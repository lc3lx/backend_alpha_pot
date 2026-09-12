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
import time
from uuid import uuid4

from app.brokers.base import AuthError, NotConnected
from app.models import AccountType, Balance, TradingAsset


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

        if text.startswith("45") and "-" in text:
            try:
                count, body = text[2:].split("-", 1)
                payload = json.loads(body)
                if isinstance(payload, list) and payload and isinstance(payload[0], str):
                    data = payload[1] if len(payload) > 1 else None
                    if isinstance(data, dict) and data.get("_placeholder") is True:
                        self.pending = payload[0]
                        return None
                    return payload[0], data
                if isinstance(payload, list) and len(payload) == 1 and isinstance(payload[0], dict):
                    return "balance", payload[0]
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
            if start_idx <= 4:
                try:
                    payload = json.loads(text[start_idx:])
                    if self.pending is not None:
                        event, self.pending = self.pending, None
                        return event, payload
                    if isinstance(payload, dict) and ("liveBalance" in payload or "demoBalance" in payload):
                        return "balance", payload
                    if isinstance(payload, list):
                        return "quotes", payload
                except Exception:
                    pass
        return None


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
                self.on_event(event, data)
                await self.connection._route_socketio_event(event, data)
            except (ValueError, TypeError, KeyError, IndexError):
                # Malformed input must not stop subsequent valid market updates.
                continue

    def on_event(self, event, data):
        if event in {"s_authorization", "authorization", "authorization/reject"}:
            if event == "authorization/reject" or (isinstance(data, dict) and
                    (data.get("error") or data.get("isSuccessful") is False)):
                self.auth_error = AuthError("Quotex rejected the session. Sign in again.")
            self.auth_ready.set()
        elif not self.auth_ready.is_set() and event in {"instruments/list", "balance", "quotes", "s_balance/list"}:
            self.auth_ready.set()

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
            assets = []
            for row in rows:
                if not isinstance(row, list) or len(row) < 3 or not isinstance(row[1], str):
                    continue
                try:
                    payout = int(float(row[5] or 0)) if len(row) > 5 and row[5] is not None else 80
                    is_open = (row[14] in (True, 1)) if len(row) > 14 and row[14] is not None else True
                    category = str(row[3]) if len(row) > 3 and row[3] is not None else None
                    assets.append(TradingAsset(symbol=row[1], name=str(row[2]).replace("\n", ""),
                        payout=payout, is_open=is_open, category=category))
                except (ValueError, TypeError):
                    continue
            if rows and not assets:
                return
            self.assets = assets
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

        async with self.balance_lock:
            if self.balances is None or time.monotonic() - self.balance_at >= 15:
                self.balance_ready.clear()
                try:
                    await self.send_event("s_balance/list", {"_placeholder": True, "num": 0})
                    await asyncio.wait_for(self.balance_ready.wait(), 1.5)
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
        if self.assets:
            return list(self.assets)
        self.require_connected()
        async with self.asset_lock:
            if self.assets and (time.monotonic() - self.assets_at < 60):
                return list(self.assets)
            if not self.assets:
                self.assets_ready.clear()
                try:
                    await self.send_event("instruments/get")
                    await asyncio.wait_for(self.assets_ready.wait(), 8)
                except (asyncio.TimeoutError, Exception):
                    pass
            if self.assets:
                return list(self.assets)
            if hasattr(self.client, "instruments") and self.client.instruments:
                return self.client.instruments
            raise NotConnected("Quotex did not provide live instruments.")

    def require_connected(self):
        ws = self.connection._ws
        if ws is None or not ws.is_connected():
            raise NotConnected("Quotex transport disconnected.")

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
        except BaseException:
            self.periods.discard(key)
            raise
