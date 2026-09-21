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
from collections import deque
from datetime import datetime, timedelta
from typing import Any
from uuid import uuid4

from app.brokers.base import AuthError, BrokerError, NotConnected
from app.models import AccountType, Balance, TradingAsset

#: Frame-level tracing.
TRACE_FRAMES = True

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
                    if data is None or (isinstance(data, dict) and data.get("_placeholder") is True):
                        self.pending = payload[0]
                        return None
                    return payload[0], data
                if isinstance(payload, list) and len(payload) == 1 and isinstance(payload[0], dict):
                    item = payload[0]
                    if any(k in item for k in ("liveBalance", "demoBalance", "realBalance", "balance")):
                        return "balance", item
                    return "_ack_response", item
                if isinstance(payload, dict):
                    if any(k in payload for k in ("liveBalance", "demoBalance", "realBalance", "balance")):
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
                        if any(k in payload for k in ("liveBalance", "demoBalance", "realBalance", "balance")):
                            return "balance", payload
                        if any(k in payload for k in ("id", "deal", "deals", "openPrice", "uuid", "ticket", "order_id", "status")):
                            return "_ack_response", payload
                    if isinstance(payload, list):
                        if payload and isinstance(payload[0], dict) and any(k in payload[0] for k in ("id", "deal", "deals", "openPrice", "uuid", "ticket", "order_id")):
                            return "_ack_response", payload[0]
                        return "quotes", payload
                except Exception:
                    pass
        return None


def _order_rows(payload):
    """Every order-shaped dict in a frame, whatever container the broker wrapped it in."""
    if isinstance(payload, dict):
        if "deal" in payload and isinstance(payload["deal"], dict):
            r = dict(payload)
            deal_data = r.pop("deal")
            r.update(deal_data)
            return [r]
        for key in ("deals", "orders", "positions"):
            inner = payload.get(key)
            if isinstance(inner, list):
                meta = {k: v for k, v in payload.items() if k not in ("deals", "orders", "positions")}
                rows = []
                for row in inner:
                    if isinstance(row, dict):
                        r = dict(meta)
                        r.update(row)
                        rows.append(r)
                return rows
        return [payload]
    if isinstance(payload, list):
        return [row for row in payload if isinstance(row, dict)]
    return []


def _normalize_history_rows(raw_candles: list) -> list[dict]:
    """Turn broker history (dicts, OHLC arrays, or [time, price] ticks) into candle rows."""
    normalized = []
    for row in raw_candles:
        if isinstance(row, dict):
            normalized.append(row)
            continue
        if not isinstance(row, (list, tuple)) or len(row) < 2:
            continue
        ts = row[0]
        if len(row) >= 5:
            normalized.append({
                "time": ts,
                "open": row[1],
                "close": row[2],
                "high": row[3],
                "low": row[4],
                "ticks": row[5] if len(row) > 5 else 0,
            })
        else:
            price = row[1]
            normalized.append({
                "time": ts,
                "open": price,
                "close": price,
                "high": price,
                "low": price,
            })
    return normalized


def get_expiration_time_quotex(timestamp: int, duration: int) -> int:
    """
    Calculates the exact candle close expiration timestamp for Quotex orders.
    Enforces Quotex's >=30s remaining rule and timeframe boundaries.
    """
    now = datetime.fromtimestamp(timestamp)
    if duration < 60:
        shift = 1 if now.second >= 30 else 0
        exp_time = now.replace(second=0, microsecond=0) + timedelta(minutes=shift + 1)
        return int(exp_time.timestamp())

    midnight = now.replace(hour=0, minute=0, second=0, microsecond=0)
    seconds_since_midnight = int((now - midnight).total_seconds())

    remainder = seconds_since_midnight % duration
    step = 2 if remainder > (duration / 2) else 1
    next_valid = ((seconds_since_midnight // duration) + step) * duration

    expiration_time = midnight + timedelta(seconds=next_valid)
    return int(expiration_time.timestamp())


def _find_order_id(row):
    """The broker's id for a deal, wherever this particular frame put it."""
    if not isinstance(row, dict):
        return None
    for key in ("id", "order_id", "orderId", "ticket", "dealId", "deal_id", "uuid", "orderUuid", "deal_uuid"):
        value = row.get(key)
        if value:
            return str(value)
    # Some frames wrap the deal one level down.
    for key in ("deal", "order", "data"):
        inner = row.get(key)
        if isinstance(inner, dict):
            found = _find_order_id(inner)
            if found:
                return found
    return None


def _is_settlement(row, event=None):
    """
    True when this row reports a finished trade rather than an accepted one.

    An acknowledgement and a settlement share the same id and much of the same shape,
    so the close time is what separates them — reading a settlement as an acknowledgement
    would report the previous trade's fill as this one's.
    """
    if not isinstance(row, dict):
        return False
    if not (row.get("id") or row.get("order_id") or row.get("ticket")):
        return False
    if event and event in QuotexLiveData._CLOSE_EVENTS:
        return True
    if str(row.get("status", "")).lower() == "closed" or row.get("game_state") == 1:
        return True
    close_price = row.get("closePrice") or row.get("close_price")
    try:
        if close_price is not None and float(close_price) > 0 and ("profit" in row or "profitAmount" in row or "win" in row):
            return True
    except (ValueError, TypeError):
        pass
    return False


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
        # Waiters for history/load so the chart can draw closed bars before live ticks.
        self._history_waiters: dict[tuple[str, int], asyncio.Event] = {}
        # Last history/load we sent, used when the broker replies with a bare candle
        # list and no asset name.
        self._last_history_request: tuple[str, int] | None = None
        # Set when instruments/list arrives while authenticate() is waiting. Fresh
        # captures often never get s_authorization on this transport; they get the
        # instrument dump instead, and treating that as a timeout is what made login
        # fail after a successful browser capture.
        self._instruments_during_auth = False
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
        # The last few things the broker said, excluding the quote flood. An order that is
        # never acknowledged says nothing about WHY on its own, and reproducing it costs
        # the customer a real entry — so the answer is collected before it is needed.
        self._recent_events: deque[tuple[float, str, str]] = deque(maxlen=40)
        self.last_recv_time = time.monotonic()
        self._is_running = True
        # The vendor delivers each packet through BOTH callback and recv(). Use only
        # its ordered receive queue, otherwise a binary header is consumed twice.
        self.connection._handle_message = self._ignore_callback
        self.connection._receive_messages = self.receive
        self.connection.send_raw = self.send_raw
        self.connection.send_request = self.send_request

    def is_alive(self) -> bool:
        ws = getattr(self.connection, "_ws", None)
        if ws is None or not ws.is_connected():
            return False
        if not getattr(self, "_is_running", False):
            return False
        return True

    async def _ignore_callback(self, message):
        pass

    async def send_raw(self, message):
        ws = self.connection._ws
        if ws is None or not ws.is_connected():
            raise NotConnected("Quotex transport disconnected.")
        if TRACE_FRAMES:
            print(f"[LIVE SEND] {str(message)[:250]}", flush=True)
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
        self._is_running = True
        try:
            while ws is not None and ws.is_connected():
                message = await asyncio.to_thread(ws.recv, 0.5)
                if not message:
                    continue
                self.last_recv_time = time.monotonic()
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
        finally:
            self._is_running = False

    def on_event(self, event, data):
        if event != "quotes":
            self._recent_events.append((time.time(), str(event), str(data)[:140]))

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
            cur_real, cur_demo = self.balances if self.balances is not None else (0.0, 10000.0)
            has_update = False

            # Format 1: Quotex dynamic balance push from trades/orders
            # e.g. event="s_balance", data={'uid': ..., 'isDemo': 1/0, 'balance': 2699.52}
            if "balance" in data and ("isDemo" in data or "is_demo" in data):
                try:
                    b_val = float(data["balance"])
                    raw_demo = data.get("isDemo") if "isDemo" in data else data.get("is_demo")
                    is_demo = raw_demo in (1, True, "1")
                    if math.isfinite(b_val):
                        if is_demo:
                            cur_demo = b_val
                        else:
                            cur_real = b_val
                        has_update = True
                except (ValueError, TypeError):
                    pass

            # Format 2: Quotex batch balance: {'liveBalance': ..., 'demoBalance': ...}
            live = data.get("liveBalance") if data.get("liveBalance") is not None else data.get("realBalance")
            demo = data.get("demoBalance")
            if live is not None:
                try:
                    live_val = float(live)
                    if math.isfinite(live_val):
                        cur_real = live_val
                        has_update = True
                except (ValueError, TypeError):
                    pass
            if demo is not None:
                try:
                    demo_val = float(demo)
                    if math.isfinite(demo_val):
                        cur_demo = demo_val
                        has_update = True
                except (ValueError, TypeError):
                    pass

            if has_update and math.isfinite(cur_real) and math.isfinite(cur_demo):
                self.balances = (cur_real, cur_demo)
                self.balance_at = time.monotonic()
                self.balance_ready.set()

        if event == "instruments/list":
            if not self.auth_ready.is_set():
                self._instruments_during_auth = True
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
                    for asset, period in list(self.periods):
                        if (
                            asset == symbol
                            or (symbol.endswith("_otc") and asset == symbol[:-4])
                            or (not symbol.endswith("_otc") and f"{symbol}_otc" == asset)
                        ):
                            self.market.apply_candle(asset, period, tick)
                            counterpart = symbol[:-4] if symbol.endswith("_otc") else f"{symbol}_otc"
                            self.market.apply_candle(counterpart, period, tick)
                except (ValueError, TypeError):
                    continue

        if event in {
            "history/load",
            "history/list/v2",
            "history/load/line",
            "s_history/load",
            "history/list",
            "candles/history",
        }:
            payload_dict = data if isinstance(data, dict) else {}
            asset_name = payload_dict.get("asset") or payload_dict.get("symbol")
            try:
                period = int(payload_dict.get("period") or 0)
            except (TypeError, ValueError):
                period = 0
            raw_candles = (
                payload_dict.get("data")
                or payload_dict.get("candles")
                or payload_dict.get("history")
                or []
            )
            if not raw_candles and isinstance(data, list):
                raw_candles = data
            pending = self._last_history_request
            if pending:
                if not asset_name:
                    asset_name = pending[0]
                if not period:
                    period = pending[1]
            if not period:
                period = 60
            if asset_name and isinstance(raw_candles, list) and raw_candles:
                normalized = _normalize_history_rows(raw_candles)
                if normalized:
                    result = self.market.apply_candles(asset_name, period, normalized)
                    try:
                        applied = int(result or 0)
                    except (TypeError, ValueError):
                        applied = len(normalized)
                    if asset_name.endswith("_otc"):
                        self.market.apply_candles(asset_name[:-4], period, normalized)
                    else:
                        self.market.apply_candles(f"{asset_name}_otc", period, normalized)
                    print(
                        f"[quotex] history event={event} asset={asset_name} "
                        f"period={period} rows={len(normalized)} applied={applied}",
                        flush=True,
                    )
                    self._signal_history(asset_name, period)
            elif TRACE_FRAMES:
                kind = type(data).__name__
                keys = list(payload_dict)[:8] if payload_dict else []
                print(
                    f"[quotex] history ignored event={event} asset={asset_name} "
                    f"period={period} kind={kind} keys={keys} rows="
                    f"{len(raw_candles) if isinstance(raw_candles, list) else 0}",
                    flush=True,
                )


    # ---- order acknowledgement and settlement ------------------------------
    #
    # Orders are sent by this class, so the vendor client never learns of them and cannot
    # report their outcome — its lookup answered "Order not found" for every trade the app
    # placed. Both halves are tracked here instead, off the same frame stream.

    #: Frames that may carry an acknowledgement of an order we just sent.
    #
    # Quotex replies to `orders/open` with `s_orders/open` — the `s_` prefix is the
    # server's answer, and missing it is why a correctly-shaped order still timed out:
    # the deal opened, the balance moved, and this waited twelve seconds for a name it
    # was not listening for.
    _ACK_EVENTS = frozenset({
        "s_orders/open", "orders/open", "orders/opened", "order/opened",
        "order/created", "s_order/created", "_ack_response",
    })

    #: Frames that may carry a settlement.
    _CLOSE_EVENTS = frozenset({
        "s_orders/close", "s_orders/closed", "orders/closed", "order/closed", "orders/close",
        "deals/closed", "deal/closed", "s_deals/closed", "orders/complete", "position/closed",
        "orders/closed/list", "deals", "s_deals",
    })

    #: Acks Quotex sends ONLY in answer to an order it accepted. Unlike the shared
    #: `_ack_response` bucket, the arrival of one of these while an order is pending is
    #: itself the confirmation — so it resolves the order even when the payload is thinner
    #: than expected, rather than being held to the strict shape the noisy bucket needs.
    _DEFINITIVE_ACKS = frozenset({
        "s_orders/open", "orders/open", "orders/opened", "order/opened",
        "order/created", "s_order/created",
    })

    def _absorb_order_events(self, event, data):
        if event not in self._ACK_EVENTS and event not in self._CLOSE_EVENTS:
            return
        for row in _order_rows(data):
            if event in self._ACK_EVENTS:
                self._resolve_pending_order(row, event)
            elif event in self._CLOSE_EVENTS or _is_settlement(row, event):
                self._record_settlement(row)

    def _record_settlement(self, row):
        order_id = row.get("id") or row.get("order_id") or row.get("ticket")
        if not order_id:
            return
        self.closed_orders[str(order_id)] = row
        # Trigger an immediate balance refresh on settlement so the UI reflects winnings/losses without delay
        self._refresh_in_background(self._request_balance())
        # A session runs for days. Keep the window that outcomes are actually read from
        # and drop the rest, rather than holding every deal until the process restarts.
        if len(self.closed_orders) > 500:
            for stale in list(self.closed_orders)[:200]:
                self.closed_orders.pop(stale, None)

    def _resolve_pending_order(self, row, event):
        if not self._pending_orders:
            return

        order_id = _find_order_id(row)
        rejected = bool(row.get("error")) or row.get("isSuccessful") is False
        definitive = event in self._DEFINITIVE_ACKS

        # The shared `_ack_response` bucket also carries the reply to `settings/apply`,
        # sent microseconds before the order — a payload with no id that must not be taken
        # for the order's own acknowledgement. A frame Quotex sends ONLY in answer to an
        # order (s_orders/open and its kin) needs no such proof: its arrival while an
        # order is pending IS the confirmation, whatever fields it happens to carry.
        deal_dict = row.get("deal") if isinstance(row.get("deal"), dict) else row
        if not definitive and not rejected:
            names_an_order = bool(order_id) and any(
                key in row or key in deal_dict for key in ("asset", "openPrice", "open_price", "amount", "command", "uuid")
            )
            if not names_an_order:
                return

        request_id = row.get("requestId") or row.get("request_id") or deal_dict.get("requestId") or deal_dict.get("request_id")
        row_asset = str(row.get("asset") or deal_dict.get("asset") or "")

        def hand_to(entry):
            self._pending_orders.remove(entry)
            _, _, future = entry
            if not future.done():
                # Guarantee the caller an id to track the trade by. Quotex's own deal id
                # when it sent one; the request id as a last resort, so a confirmed order
                # is never discarded merely because the ack was thin.
                res_dict = dict(row)
                found_id = _find_order_id(res_dict)
                if found_id:
                    res_dict.setdefault("id", found_id)
                else:
                    res_dict.setdefault("id", str(entry[0]))
                future.set_result(res_dict)

        # Best: the broker echoed the request id we sent.
        if request_id is not None:
            for entry in list(self._pending_orders):
                if str(request_id) == str(entry[0]):
                    return hand_to(entry)

        # Next: same instrument as a waiting order.
        if row_asset:
            for entry in list(self._pending_orders):
                if row_asset == entry[1]:
                    return hand_to(entry)

        # A definitive ack with neither — orders go out one at a time, so the oldest
        # waiter is the one this answers.
        if definitive or rejected:
            return hand_to(self._pending_orders[0])

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
        self._instruments_during_auth = False
        await self.send_event("authorization", {"session": ssid,
            "isDemo": int(account_type is AccountType.DEMO), "tournamentId": 0})
        try:
            await asyncio.wait_for(self.auth_ready.wait(), 3)
        except asyncio.TimeoutError as exc:
            if self.auth_error:
                raise self.auth_error
            # A fresh token answers with instruments/list, then (if it is dead) with
            # authorization/reject a moment later. Waiting the full 15s and disconnecting
            # is what turned a captured session into HTTP 409.
            if self._instruments_during_auth:
                await asyncio.sleep(1.2)
                if self.auth_error:
                    raise self.auth_error
                self.auth_ready.set()
            else:
                try:
                    await asyncio.wait_for(self.auth_ready.wait(), 12)
                except asyncio.TimeoutError:
                    if self._instruments_during_auth and self.auth_error is None:
                        self.auth_ready.set()
                    else:
                        raise AuthError("Quotex authorization timed out. Sign in again.") from exc
        if self.auth_error:
            raise self.auth_error

    async def get_balance(self, account_type):
        self.require_connected()

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
            if self.balances is None:
                raise NotConnected("Quotex did not deliver live balance.")
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
        if self.auth_error is not None:
            raise self.auth_error
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

    def _signal_history(self, asset: str, period: int) -> None:
        keys = {(asset, period)}
        if asset.endswith("_otc"):
            keys.add((asset[:-4], period))
        else:
            keys.add((f"{asset}_otc", period))
        for key in keys:
            ev = self._history_waiters.get(key)
            if ev is not None and not ev.is_set():
                ev.set()

    async def subscribe(self, asset, period):
        key = (asset, period)
        self.periods.add(key)
        formatted = asset if asset.endswith("_otc") else f"{asset}_otc"
        self.periods.add((formatted, period))
        if asset.endswith("_otc"):
            self.periods.add((asset[:-4], period))
        try:
            ws = getattr(self.connection, "_ws", None)
            if ws is None or not getattr(ws, "is_connected", lambda: False)():
                return

            # Historical candles FIRST, then the live stream. Sending live quotes
            # before history/load left the chart empty until ticks dribbled in.
            now_sec = int(time.time())
            browser_index = int(time.time() * 100)
            # 12 hours of 1m bars covers a multi-hour reconnect hole plus RSI warmup.
            offset = max(period * 720, 14400)
            hist_payload = {
                "asset": formatted,
                "index": browser_index,
                "time": now_sec,
                "offset": offset,
                "period": period,
            }
            waiter = asyncio.Event()
            self._history_waiters[(formatted, period)] = waiter
            self._last_history_request = (formatted, period)
            try:
                await self.send_event("history/load", hist_payload)
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
                try:
                    await asyncio.wait_for(waiter.wait(), 5.0)
                except asyncio.TimeoutError:
                    pass
            finally:
                self._history_waiters.pop((formatted, period), None)

            await self.send_event("instruments/update", {"asset": formatted, "period": period})
            await self.send_event("depth/follow", formatted)
            if formatted != asset:
                try:
                    await self.send_event("depth/follow", asset)
                except Exception:
                    pass
        except BaseException:
            self.periods.discard(key)
            raise

    def _describe_silence(self, order_payload, sent_at):
        """
        Why an order went unanswered, in the message the customer can hand back.

        Silence is the one failure that explains nothing by itself, and reproducing it
        costs a real entry — so the order as sent, and what the broker said instead, both
        travel with the error rather than having to be asked for afterwards.
        """
        names = [name for when, name, _ in self._recent_events if when >= sent_at]
        heard = ", ".join(dict.fromkeys(names)) or "nothing at all"
        known = self._asset_index.get(order_payload["asset"])
        listed = (
            "not in the instrument list" if known is None
            else ("open" if known.is_open else "listed but closed")
        )
        return (
            "Quotex did not acknowledge the order within 12s. "
            f"Sent asset={order_payload['asset']} ({listed}), "
            f"action={order_payload['action']}, amount={order_payload['amount']}, "
            f"isDemo={order_payload['isDemo']}, optionType={order_payload['optionType']}, "
            f"expiry={order_payload['time']}. Heard back: {heard}."
        )

    async def place_order(
        self, asset: str, amount: float, duration_seconds: int, direction: str, is_demo: bool
    ) -> dict[str, Any]:
        self.require_connected()
        if self.auth_error:
            raise self.auth_error

        clean_asset = self._tradable_symbol(asset)
        asset_info = self._asset_index.get(clean_asset)
        if asset_info is not None and not asset_info.is_open:
            raise BrokerError(f"Market for {clean_asset} is currently closed on Quotex.")

        # Ensure chart subscription & depth stream are active for this asset
        try:
            await self.send_event("instruments/update", {"asset": clean_asset, "period": int(duration_seconds)})
            await self.send_event("chart_notification/get", {"asset": clean_asset, "version": "1.0.0"})
            await self.send_event("depth/follow", clean_asset)
        except Exception as e:
            print(f"[ORDER WARN] pre-order stream subscription error: {e}", flush=True)

        # Milliseconds plus a counter within safe integer range.
        self._order_seq = (self._order_seq + 1) % 1000
        req_id = int(time.time() * 1000) % 10000000000 + self._order_seq
        amount_int = max(1, int(round(float(amount))))

        future = asyncio.get_running_loop().create_future()
        entry = (req_id, clean_asset, future)
        self._pending_orders.append(entry)

        now_ts = int(time.time())
        exp_time = get_expiration_time_quotex(now_ts, int(duration_seconds))

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
                "dealValue": amount_int,
                "dealPercentValue": 1,
                "isVisible": True,
                "timePeriod": int(duration_seconds),
                "gridOpacity": 8,
                "isAutoScrolling": 1,
                "isOneClickTrade": True,
                "upColor": "#0FAF59",
                "downColor": "#FF6251",
            },
            "endTime": exp_time,
        }

        order_payload = {
            "asset": clean_asset,
            "amount": amount_int,
            "time": int(duration_seconds),
            "action": direction.lower(),
            "isDemo": 1 if is_demo else 0,
            "tournamentId": 0,
            "requestId": req_id,
            "optionType": 100,
        }

        sent_at = time.time()
        max_attempts = 3
        per_attempt = 4.0
        try:
            for attempt in range(1, max_attempts + 1):
                balance_before = self.balance_at
                try:
                    await self.send_event("settings/apply", settings_payload)
                    await self.send_raw('42["tick"]')
                except Exception as e:
                    print(f"[ORDER WARN] settings/apply or tick error: {e}", flush=True)

                await self.send_event("orders/open", order_payload)

                try:
                    # Shielded, so a timeout here leaves the order still pending: a late
                    # acknowledgement on the next wait still resolves it.
                    response = await asyncio.wait_for(asyncio.shield(future), per_attempt)
                    break
                except asyncio.TimeoutError:
                    opened = self.balance_at != balance_before or any(
                        name == "s_balance" or name.startswith("s_orders")
                        for ts, name, _ in self._recent_events if ts >= sent_at
                    )
                    if opened:
                        # Something opened. Never resend — wait out the ack instead.
                        response = await asyncio.wait_for(future, 8.0)
                        break
                    if attempt >= max_attempts:
                        raise BrokerError(self._describe_silence(order_payload, sent_at))
                    # Genuinely silent: nothing opened, so it is safe to send again.
        finally:
            if entry in self._pending_orders:
                self._pending_orders.remove(entry)

        if not isinstance(response, dict):
            raise BrokerError("Quotex returned an unreadable order response.")
        if response.get("error") or response.get("isSuccessful") is False:
            msg = response.get("message") or response.get("error") or "Order rejected by Quotex"
            raise BrokerError(str(msg))
        # The resolver guarantees an id — the broker's own when it sent one, the request id
        # as a fallback — so a confirmed order is never rejected here for a thin ack.
        return response

