"""Live Quotex events, isolated from the vendor's mock instruments/balance fallbacks.

Wire references: cleitonleonel/pyquotex pyquotex/api.py and _api/assets.py.
The vendor transport is synchronous; all I/O below runs outside the asyncio loop.
"""
from __future__ import annotations

import asyncio
import base64
import inspect
import json
import math
import os
import time
from datetime import datetime, timedelta
from typing import Any
from uuid import uuid4

from app.brokers.base import AuthError, BrokerError, NotConnected
from app.models import AccountType, Balance, TradingAsset

#: Frame-level tracing. Printing every non-quote frame costs real time once PM2 is
#: capturing stdout, and it ran on every session — keep it available, keep it off.
TRACE_FRAMES = os.getenv("BROKER_QUOTEX_TRACE", "").strip().lower() in {"1", "true", "yes"}

#: How long a delivered instrument list is reused before another is requested. The list
#: is also pushed unprompted, so this is an upper bound on staleness, not a poll rate.
ASSET_TTL_SECONDS = 60.0

#: Balance is pushed by the broker on every change. This bounds how stale a *served*
#: value can be before a refresh is started — the refresh never blocks the caller.
BALANCE_TTL_SECONDS = 10.0


class SocketIOPackets:
    """Reassemble Socket.IO binary events before dispatching their JSON attachments."""

    def __init__(self):
        self.pending = None

    def parse(self, message):
        if isinstance(message, bytes):
            text = message.decode("utf-8", errors="replace")
        else:
            text = str(message)
        text = text.strip()
        if not text:
            return None

        if text.startswith("b4"):
            try:
                text = base64.b64decode(text[2:]).decode("utf-8", errors="replace").strip()
            except Exception:
                return None

        if (text.startswith("45") or text.startswith("51") or text.startswith("52")) and "-" in text[:6]:
            try:
                count, body = text.split("-", 1)
                payload = json.loads(body)
                if isinstance(payload, list) and payload and isinstance(payload[0], str):
                    data = payload[1] if len(payload) > 1 else None
                    if isinstance(data, dict) and data.get("_placeholder") is True:
                        self.pending = payload[0]
                        return None
                    return payload[0], data
                if isinstance(payload, list) and len(payload) == 1 and isinstance(payload[0], dict):
                    item = payload[0]
                    if "liveBalance" in item or "demoBalance" in item or "realBalance" in item:
                        return "balance", item
                    return "_ack_response", item
                if isinstance(payload, dict):
                    if "liveBalance" in payload or "demoBalance" in payload:
                        return "balance", payload
                    return "_ack_response", payload
            except Exception:
                return None
        elif text.startswith("43"):
            try:
                indices = [text.find(c) for c in ("[", "{") if text.find(c) != -1]
                if indices:
                    start_idx = min(indices)
                    payload = json.loads(text[start_idx:])
                    data = payload[0] if isinstance(payload, list) and len(payload) == 1 else payload
                    return "_ack_response", data
            except Exception:
                return None
        elif text.startswith("42"):
            try:
                payload = json.loads(text[2:])
                if isinstance(payload, list) and payload:
                    return payload[0], payload[1] if len(payload) > 1 else None
            except Exception:
                return None
        # Find start of JSON array or object (handles Engine.IO binary prefix \x04)
        indices = [text.find(c) for c in ("[", "{") if text.find(c) != -1]
        if indices:
            start_idx = min(indices)
            if start_idx <= 10:
                try:
                    payload = json.loads(text[start_idx:])
                    if self.pending is not None:
                        event, self.pending = self.pending, None
                        return event, payload
                    if isinstance(payload, dict):
                        if "liveBalance" in payload or "demoBalance" in payload:
                            return "balance", payload
                        if "id" in payload or "deals" in payload or "openPrice" in payload:
                            return "_ack_response", payload
                    if isinstance(payload, list):
                        if payload and isinstance(payload[0], dict) and ("id" in payload[0] or "openPrice" in payload[0]):
                            return "_ack_response", payload[0]
                        return "quotes", payload
                except Exception:
                    pass
        return None


def _order_rows(payload):
    """Every order-shaped dict in a frame, whatever container the broker wrapped it in."""
    if isinstance(payload, dict):
        for key in ("deals", "orders", "positions"):
            inner = payload.get(key)
            if isinstance(inner, list):
                return [row for row in inner if isinstance(row, dict)]
        return [payload]
    if isinstance(payload, list):
        return [row for row in payload if isinstance(row, dict)]
    return []


def _is_settlement(row):
    """
    True when this row reports a finished trade rather than an accepted one.

    An acknowledgement and a settlement share the same id and much of the same shape,
    so the close time is what separates them — reading a settlement as an acknowledgement
    would report the previous trade's fill as this one's.
    """
    if not (row.get("id") or row.get("order_id") or row.get("ticket")):
        return False
    if row.get("closeTimestamp") or row.get("close_timestamp") or row.get("closeTime"):
        return True
    has_close_price = row.get("closePrice") is not None or row.get("close_price") is not None
    return has_close_price and ("profit" in row or "profitAmount" in row)


class QuotexLiveData:
    def __init__(self, client, market):
        self.client = client
        self.connection = client.connection
        self.market = market
        self.packets = SocketIOPackets()
        self.assets = None
        self.assets_at = 0.0
        self.balances = None
        self.balance_at = 0.0
        self.assets_ready = asyncio.Event()
        self.balance_ready = asyncio.Event()
        self.auth_ready = asyncio.Event()
        self.auth_error = None
        self.asset_lock = asyncio.Lock()
        self.balance_lock = asyncio.Lock()
        self.periods = set()
        # Quotex delivers the instrument list in SEVERAL batches. Replacing the list on
        # each one left whatever the last (small) batch contained — five assets, of which
        # two were currency pairs, which is exactly what the app showed.
        self._asset_index: dict[str, TradingAsset] = {}
        # Orders placed by this class, awaiting the broker's acknowledgement. Correlated
        # here rather than through the vendor's pub/sub: this object already sees every
        # frame, and depending on the vendor meant an order could fail on an attribute.
        self._pending_orders: list[tuple[int, str, asyncio.Future]] = []
        # order id -> settlement payload, for outcomes of orders the vendor never saw.
        self.closed_orders: dict[str, dict] = {}
        self._order_seq = 0
        self._refresh_tasks: set[asyncio.Task] = set()
        # The vendor delivers each packet through BOTH callback and recv(). Use only
        # its ordered receive queue, otherwise a binary header is consumed twice.
        self.connection._handle_message = self._ignore_callback
        self.connection._receive_messages = self.receive
        self.connection.send_raw = self.send_raw
        self.connection.send_request = self.send_request

    async def _ignore_callback(self, message):
        pass

    async def send_raw(self, message):
        ws = self.connection._ws
        if ws is None or not ws.is_connected():
            raise NotConnected("Quotex transport disconnected.")
        if inspect.iscoroutinefunction(ws.send):
            sent = await ws.send(message)
        else:
            sent = await asyncio.to_thread(ws.send, message)
        if sent is False:
            raise NotConnected("Quotex transport refused the outgoing frame.")

    async def send_request(self, message_type, data=None, timeout=None, expect_response=True):
        # Compatibility for vendor services that await the synchronous transport.send.
        request_id = str(uuid4())
        future = asyncio.get_running_loop().create_future()
        pending = self.connection._pending_requests
        if expect_response:
            pending[request_id] = future
        try:
            await self.send_raw(json.dumps({**(data or {}), "msg": message_type, "request_id": request_id}))
            if expect_response:
                return await asyncio.wait_for(future, timeout or self.connection._request_timeout)
        finally:
            pending.pop(request_id, None)

    async def send_event(self, event, data=None):
        payload = [event] if data is None else [event, data]
        await self.send_raw("42" + json.dumps(payload, separators=(",", ":")))

    async def receive(self):
        ws = self.connection._ws
        while ws is not None and ws.is_connected():
            message = await asyncio.to_thread(ws.recv, 0.5)
            if not message:
                continue
            if message == "2":
                await self.send_raw("3")
                continue
            if TRACE_FRAMES and not (
                isinstance(message, str)
                and (message.startswith('42["quotes') or message.startswith("b4"))
            ):
                text = message if isinstance(message, str) else repr(message)
                print(f"[LIVE RECV] {text[:200]}", flush=True)
            try:
                if isinstance(message, str) and message.startswith("{"):
                    value = json.loads(message)
                    if value.get("request_id"):
                        await self.connection._route_message(value)
                        continue
                parsed = self.packets.parse(message)
                if parsed is None:
                    continue
                event, data = parsed
                if TRACE_FRAMES and event != "quotes":
                    print(f"[LIVE PARSED] event={event} data={str(data)[:200]}", flush=True)
                self.on_event(event, data)
                await self.connection._route_socketio_event(event, data)
            except (ValueError, TypeError, KeyError, IndexError):
                # Malformed input must not stop subsequent valid market updates.
                continue

    def on_event(self, event, data):
        if event in {"s_authorization", "authorization"}:
            if isinstance(data, dict) and (data.get("error") or data.get("isSuccessful") is False):
                self.auth_error = AuthError("Quotex rejected the session. Sign in again.")
            self.auth_ready.set()
        elif event in {"authorization/reject", "s_authorization/reject"}:
            self.auth_error = AuthError("Quotex session token (SSID) is invalid or has expired. Please update it in settings.")
            self.auth_ready.set()
        elif not self.auth_ready.is_set() and event == "s_balance/list":
            self.auth_ready.set()

        self._absorb_order_events(event, data)

        if isinstance(data, list) and len(data) == 1 and isinstance(data[0], dict):
            data = data[0]
        if isinstance(data, dict):
            live = data.get("liveBalance") if data.get("liveBalance") is not None else data.get("realBalance")
            demo = data.get("demoBalance")
            if live is not None or demo is not None:
                real = float(live if live is not None else 0.0)
                demo_val = float(demo if demo is not None else 10000.0)
                if math.isfinite(real) and math.isfinite(demo_val):
                    self.balances = (real, demo_val)
                    self.balance_at = time.monotonic()
                    self.balance_ready.set()

        if event == "instruments/list":
            rows = data.get("list") if isinstance(data, dict) else data
            if not isinstance(rows, list):
                return
            batch = {}
            for row in rows:
                if not isinstance(row, list) or len(row) < 3 or not isinstance(row[1], str):
                    continue
                try:
                    payout = int(float(row[5] or 0)) if len(row) > 5 and row[5] is not None else 80
                    is_open = (row[14] in (True, 1)) if len(row) > 14 and row[14] is not None else True
                    category = str(row[3]) if len(row) > 3 and row[3] is not None else None
                    batch[row[1]] = TradingAsset(symbol=row[1], name=str(row[2]).replace("\n", ""),
                        payout=payout, is_open=is_open, category=category)
                except (ValueError, TypeError):
                    continue
            if rows and not batch:
                return
            # Merge, never replace. Each batch is authoritative for the symbols IT names —
            # so an asset that just closed still updates — while symbols delivered in an
            # earlier batch survive instead of vanishing from the app.
            self._asset_index.update(batch)
            self.assets = list(self._asset_index.values())
            self.assets_at = time.monotonic()
            self.assets_ready.set()
        if event in {"quotes", "instruments/update", "quotes/stream"} and isinstance(data, list):
            for row in data:
                if not isinstance(row, list) or len(row) < 3 or not isinstance(row[0], str):
                    continue
                try:
                    symbol, ts, price = row[0], float(row[1]), float(row[2])
                    if not math.isfinite(ts) or not math.isfinite(price) or price <= 0:
                        continue
                    tick = {"time": ts, "price": price}
                    self.market.apply_quote(symbol, tick)
                    if symbol.endswith("_otc"):
                        self.market.apply_quote(symbol[:-4], tick)
                    else:
                        self.market.apply_quote(f"{symbol}_otc", tick)
                    for asset, period in self.periods:
                        if (
                            asset == symbol
                            or (symbol.endswith("_otc") and asset == symbol[:-4])
                            or (not symbol.endswith("_otc") and f"{symbol}_otc" == asset)
                        ):
                            self.market.apply_candle(asset, period, tick)
                except (ValueError, TypeError):
                    continue

        if event in {"history/load", "history/list/v2", "history/load/line"}:
            payload_dict = data if isinstance(data, dict) else {}
            asset_name = payload_dict.get("asset")
            period = int(payload_dict.get("period") or 60)
            raw_candles = (
                payload_dict.get("data")
                or payload_dict.get("candles")
                or payload_dict.get("history")
                or []
            )
            if not raw_candles and isinstance(data, list):
                raw_candles = data
            if asset_name and isinstance(raw_candles, list) and raw_candles:
                normalized = []
                for row in raw_candles:
                    if isinstance(row, dict):
                        normalized.append(row)
                    elif isinstance(row, (list, tuple)) and len(row) >= 5:
                        normalized.append({
                            "time": row[0],
                            "open": row[1],
                            "close": row[2],
                            "high": row[3],
                            "low": row[4],
                            "ticks": row[5] if len(row) > 5 else 0,
                        })
                if normalized:
                    self.market.apply_candles(asset_name, period, normalized)
                    if asset_name.endswith("_otc"):
                        self.market.apply_candles(asset_name[:-4], period, normalized)
                    else:
                        self.market.apply_candles(f"{asset_name}_otc", period, normalized)


    # ---- order acknowledgement and settlement ------------------------------
    #
    # Orders are sent by this class, so the vendor client never learns of them and cannot
    # report their outcome — its lookup answered "Order not found" for every trade the app
    # placed. Both halves are tracked here instead, off the same frame stream.

    #: Frames that may carry an acknowledgement of an order we just sent.
    _ACK_EVENTS = frozenset({
        "orders/open", "orders/opened", "order/opened", "order/created", "_ack_response",
    })

    #: Frames that may carry a settlement.
    _CLOSE_EVENTS = frozenset({
        "orders/closed", "order/closed", "deals/closed", "deal/closed",
        "orders/complete", "position/closed",
    })

    def _absorb_order_events(self, event, data):
        if event not in self._ACK_EVENTS and event not in self._CLOSE_EVENTS:
            return
        for row in _order_rows(data):
            if _is_settlement(row):
                self._record_settlement(row)
            elif event in self._ACK_EVENTS:
                self._resolve_pending_order(row)

    def _record_settlement(self, row):
        order_id = row.get("id") or row.get("order_id") or row.get("ticket")
        if not order_id:
            return
        self.closed_orders[str(order_id)] = row
        # A session runs for days. Keep the window that outcomes are actually read from
        # and drop the rest, rather than holding every deal until the process restarts.
        if len(self.closed_orders) > 500:
            for stale in list(self.closed_orders)[:200]:
                self.closed_orders.pop(stale, None)

    def _resolve_pending_order(self, row):
        if not self._pending_orders:
            return
        order_id = row.get("id") or row.get("order_id") or row.get("ticket")
        rejected = bool(row.get("error")) or row.get("isSuccessful") is False
        # `_ack_response` is a catch-all bucket — the acknowledgement of `settings/apply`
        # arrives in it too, microseconds before the order's own. Resolving on that gave
        # back a settings echo with no id, which the app then recorded as an order it
        # could never find again.
        names_an_order = bool(order_id) and any(
            key in row for key in ("asset", "openPrice", "open_price", "amount", "command")
        )
        if not names_an_order and not rejected:
            return

        request_id = row.get("requestId") or row.get("request_id")
        row_asset = str(row.get("asset") or "")
        for entry in list(self._pending_orders):
            req, asset, future = entry
            if request_id is not None and str(request_id) != str(req):
                continue
            if request_id is None and row_asset and row_asset != asset:
                continue
            self._pending_orders.remove(entry)
            if not future.done():
                future.set_result(row)
            return

    async def wait_outcome(self, order_id, timeout_seconds):
        """The settlement for an order placed here, or None if it did not arrive in time."""
        key = str(order_id)
        deadline = time.monotonic() + max(float(timeout_seconds), 0.0)
        while True:
            row = self.closed_orders.get(key)
            if row is not None:
                return row
            if time.monotonic() >= deadline:
                return None
            await asyncio.sleep(0.25)

    def _tradable_symbol(self, asset):
        """
        The symbol Quotex will actually accept for this pair right now.

        Every order used to be rewritten to `_otc`, which is correct at the weekend and
        wrong the rest of the week: a weekday order on the real pair was sent to a
        different instrument than the one the signal was computed on.
        """
        index = self._asset_index
        alternate = asset[:-4] if asset.endswith("_otc") else f"{asset}_otc"
        asked, other = index.get(asset), index.get(alternate)
        if asked is not None and asked.is_open:
            return asset
        if other is not None and other.is_open:
            return alternate
        if asked is not None:
            return asset
        return alternate if other is not None else asset

    async def authenticate(self, ssid, account_type):
        if not ssid:
            raise AuthError("Quotex requires a browser-captured session before connecting.")
        self.auth_ready.clear()
        self.auth_error = None
        await self.send_event("authorization", {"session": ssid,
            "isDemo": int(account_type is AccountType.DEMO), "tournamentId": 0})
        try:
            await asyncio.wait_for(self.auth_ready.wait(), 15)
        except asyncio.TimeoutError as exc:
            raise AuthError("Quotex authorization timed out. Sign in again.") from exc
        if self.auth_error:
            raise self.auth_error

    async def get_balance(self, account_type):
        ws = getattr(self.connection, "_ws", None)
        is_conn = ws is not None and getattr(ws, "is_connected", lambda: False)()
        if not is_conn:
            if self.balances is not None:
                real, demo = self.balances
                return Balance(real=real, demo=demo, current_type=account_type)
            if hasattr(self.client, "account"):
                acc_balances = getattr(self.client.account, "_balances", None)
                if acc_balances:
                    real = 0.0
                    demo_val = 10000.0
                    for b in acc_balances:
                        act = str(getattr(b, "account_type", "")).upper()
                        amt = float(getattr(b, "amount", 0.0) or 0.0)
                        if "REAL" in act:
                            real = amt
                        elif "DEMO" in act:
                            demo_val = amt
                    return Balance(real=real, demo=demo_val, current_type=account_type)
            raise NotConnected("Quotex transport disconnected.")

        # The broker pushes a balance frame on every change, so a stored value is current
        # unless the stream went quiet. Serving it without a round trip is the difference
        # between this endpoint answering instantly and it costing the caller seconds —
        # and this endpoint is polled by every open page AND checked before every trade,
        # where the wait was turning a healthy account into "unable to verify balance".
        if self.balances is not None:
            if time.monotonic() - self.balance_at >= BALANCE_TTL_SECONDS:
                self._refresh_in_background(self._request_balance())
            real, demo = self.balances
            return Balance(real=real, demo=demo, current_type=account_type)

        async with self.balance_lock:
            if self.balances is None:
                self.balance_ready.clear()
                try:
                    await self.send_event("s_balance/list", {"_placeholder": True, "num": 0})
                    await asyncio.wait_for(self.balance_ready.wait(), 2.5)
                except (asyncio.TimeoutError, Exception):
                    pass
            if (self.balances is None or self.balances == (0.0, 10000.0)) and hasattr(self.client, "account"):
                acc_balances = getattr(self.client.account, "_balances", None)
                if acc_balances:
                    real = 0.0
                    demo_val = 10000.0
                    for b in acc_balances:
                        act = str(getattr(b, "account_type", "")).upper()
                        amt = float(getattr(b, "amount", 0.0) or 0.0)
                        if "REAL" in act:
                            real = amt
                        elif "DEMO" in act:
                            demo_val = amt
                    self.balances = (real, demo_val)
                    self.balance_at = time.monotonic()
            if self.balances is None:
                self.balances = (0.0, 10000.0)
                self.balance_at = time.monotonic()
            real, demo = self.balances
            return Balance(real=real, demo=demo, current_type=account_type)

    async def list_assets(self):
        # This used to return the stored list unconditionally, which made the refresh
        # below unreachable: whatever the first batch happened to contain was served for
        # the life of the session, and pairs that opened later never appeared.
        if self.assets:
            if time.monotonic() - self.assets_at >= ASSET_TTL_SECONDS:
                self._refresh_in_background(self._request_assets())
            return list(self.assets)

        self.require_connected()
        async with self.asset_lock:
            if self.assets:
                return list(self.assets)
            await self._request_assets()
            if self.assets:
                return list(self.assets)
            if hasattr(self.client, "instruments") and self.client.instruments:
                return self.client.instruments
            raise NotConnected("Quotex did not provide live instruments.")

    def require_connected(self):
        ws = self.connection._ws
        if ws is None or not ws.is_connected():
            raise NotConnected("Quotex transport disconnected.")

    def _refresh_in_background(self, coro):
        """
        Start a refresh nobody waits for.

        A caller with a usable stored value gets it now; the next caller gets the newer
        one. A failure here is not the caller's problem and must not surface as theirs.
        """
        async def guarded():
            try:
                await coro
            except Exception:
                pass

        try:
            task = asyncio.get_running_loop().create_task(guarded())
        except RuntimeError:
            coro.close()
            return
        self._refresh_tasks.add(task)
        task.add_done_callback(self._refresh_tasks.discard)

    async def _request_assets(self):
        self.assets_ready.clear()
        try:
            await self.send_event("instruments/get")
            await asyncio.wait_for(self.assets_ready.wait(), 8)
            # The list arrives in batches and only the first one sets the event. Give the
            # rest of them a moment to land, or the merged list is served half-built.
            await asyncio.sleep(0.75)
        except (asyncio.TimeoutError, Exception):
            pass

    async def _request_balance(self):
        self.balance_ready.clear()
        try:
            await self.send_event("s_balance/list", {"_placeholder": True, "num": 0})
            await asyncio.wait_for(self.balance_ready.wait(), 5)
        except (asyncio.TimeoutError, Exception):
            pass

    async def subscribe(self, asset, period):
        key = (asset, period)
        self.periods.add(key)
        try:
            ws = getattr(self.connection, "_ws", None)
            if ws is None or not getattr(ws, "is_connected", lambda: False)():
                return
            formatted = asset if asset.endswith("_otc") else f"{asset}_otc"
            await self.send_event("depth/follow", formatted)
            if formatted != asset:
                try:
                    await self.send_event("depth/follow", asset)
                except Exception:
                    pass
            await self.send_event("instruments/update", {"asset": formatted, "period": period})

            # Deep historical candles: bypasses the 199 candle live stream wait
            now_sec = int(time.time())
            browser_index = int(time.time() * 100)
            offset = max(period * 250, 14400)
            hist_payload = {
                "asset": formatted,
                "index": browser_index,
                "time": now_sec,
                "offset": offset,
                "period": period,
            }
            try:
                await self.send_event("history/load", hist_payload)
            except Exception:
                pass
            if formatted != asset:
                try:
                    await self.send_event("history/load", {
                        "asset": asset,
                        "index": browser_index,
                        "time": now_sec,
                        "offset": offset,
                        "period": period,
                    })
                except Exception:
                    pass
        except BaseException:
            self.periods.discard(key)
            raise

    async def place_order(
        self, asset: str, amount: float, duration_seconds: int, direction: str, is_demo: bool
    ) -> dict[str, Any]:
        self.require_connected()
        if self.auth_error:
            raise self.auth_error

        clean_asset = self._tradable_symbol(asset)
        now_ts = int(time.time())
        now_dt = datetime.fromtimestamp(now_ts)
        dur = max(duration_seconds, 60)
        midnight = now_dt.replace(hour=0, minute=0, second=0, microsecond=0)
        seconds_since_midnight = int((now_dt - midnight).total_seconds())
        remainder = seconds_since_midnight % dur
        step = 2 if remainder > (dur / 2) else 1
        next_valid = ((seconds_since_midnight // dur) + step) * dur
        exp_time = int((midnight + timedelta(seconds=next_valid)).timestamp())

        settings_payload = {
            "chartId": "graph",
            "settings": {
                "chartId": "graph",
                "chartType": 2,
                "currentExpirationTime": exp_time,
                "isFastOption": True,
                "isFastAmountOption": False,
                "isIndicatorsMinimized": False,
                "isIndicatorsShowing": True,
                "isShortBetElement": False,
                "chartPeriod": 4,
                "currentAsset": {"symbol": clean_asset},
                "dealValue": float(amount),
                "dealPercentValue": 1,
                "isVisible": True,
                "timePeriod": duration_seconds,
                "gridOpacity": 8,
                "isAutoScrolling": 1,
                "isOneClickTrade": True,
                "upColor": "#0FAF59",
                "downColor": "#FF6251",
            },
            "endTime": exp_time,
        }
        await self.send_event("settings/apply", settings_payload)
        await self.send_raw('42["tick"]')

        # Milliseconds plus a counter. The previous id was whole seconds, so two orders
        # placed in the same second shared one — and the first acknowledgement to arrive
        # was handed to whichever of them was still waiting.
        self._order_seq = (self._order_seq + 1) % 1000
        req_id = int(time.time() * 1000) * 1000 + self._order_seq

        future = asyncio.get_running_loop().create_future()
        entry = (req_id, clean_asset, future)
        self._pending_orders.append(entry)

        order_payload = {
            "asset": clean_asset,
            "amount": float(amount),
            "time": exp_time,
            "action": direction.lower(),
            "isDemo": 1 if is_demo else 0,
            "tournamentId": 0,
            "requestId": req_id,
            "optionType": 3,
        }

        try:
            await self.send_event("orders/open", order_payload)
            response = await asyncio.wait_for(future, timeout=12.0)
        except asyncio.TimeoutError as exc:
            raise BrokerError("Quotex did not acknowledge the order within 12s.") from exc
        finally:
            if entry in self._pending_orders:
                self._pending_orders.remove(entry)

        if not isinstance(response, dict):
            raise BrokerError("Quotex returned an unreadable order response.")
        if response.get("error") or response.get("isSuccessful") is False:
            msg = response.get("message") or response.get("error") or "Order rejected by Quotex"
            raise BrokerError(str(msg))
        if not (response.get("id") or response.get("order_id") or response.get("ticket")):
            # Without an id the trade cannot be followed to its result, and reporting it
            # as placed would leave it open in the app forever.
            raise BrokerError("Quotex accepted the order but returned no id to track it by.")
        return response

