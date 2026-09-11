"""
Quotex adapter over the QuotexAPI library (ChipaDevTeam).

The library is imported lazily so the gateway still starts, serves health, and runs its
tests on a machine where QuotexAPI is not installed — only Quotex calls fail, and they
fail with a clear message instead of an import error at boot.

Everything Quotex-specific stops here. The rest of the service, and all of .NET, sees only
the neutral types in `app.models`.

**On candles.** This library has no history call. Its `DataService` streams candles and
quotes and nothing more, so bars are accumulated as they arrive.

That accumulation is deliberately NOT held here. A pair's candles belong to the market,
not to whoever is watching, so they live in the process-wide `app.market_data` store: one
subscription per pair feeds every account, and an account that connects later inherits the
whole history instead of warming up again from nothing. See that module for why.

What remains true is that a pair nobody has ever subscribed has no past, and cannot be
analysed until enough bars have streamed in. `has_fresh_candles` answers honestly, and
`get_candles` returns only bars that genuinely closed.
"""

from __future__ import annotations

import asyncio
import inspect
import json
import os
import time
from typing import Any

from app.market_data import market_data
from app.proxy import configure_process_proxy, proxy_url
from app.quotex_ws import install as install_websocket_transport
from app.quotex_protocol import QuotexLiveData
from app.brokers.base import (
    AuthError,
    BlockedError,
    BrokerError,
    BrokerSession,
    CaptchaRequired,
    NotConnected,
)
from app.models import (
    AccountType,
    Balance,
    Candle,
    LifecycleState,
    Outcome,
    OrderResponse,
    Quote,
    TradeResult,
    TradingAsset,
)

#: Where the client class has been found to live, newest layout first.
#:
#: The library is vendored from git rather than PyPI, so its import path is whatever that
#: repository happens to use on the day it is cloned — and it does not match the
#: distribution name (`pip install` reports `quotex-api`, while the package directory is
#: `QuotexAPI`). Pinning one path turned a rename into a 409 on every Quotex login with
#: nothing in the message naming the real cause, so every known layout is tried and the
#: failure says exactly what was attempted.
_CLIENT_PATHS: tuple[tuple[str, str], ...] = (
    ("QuotexAPI.client", "QuotexAPI"),
    ("QuotexAPI", "QuotexAPI"),
    ("QuotexAPI.stable_api", "Quotex"),
    ("QuotexAPI", "Quotex"),
    ("quotexapi.stable_api", "Quotex"),
    ("quotex_api.stable_api", "Quotex"),
    ("pyquotex.stable_api", "Quotex"),
)

def _load_client_cls() -> Any:
    import importlib

    attempted: list[str] = []
    for module_name, attr in _CLIENT_PATHS:
        attempted.append(f"{module_name}.{attr}")
        try:
            module = importlib.import_module(module_name)
        except ImportError:
            continue
        client = getattr(module, attr, None)
        if client is not None:
            return client

    raise NotConnected(
        "The Quotex client library was not found. Tried: "
        + ", ".join(attempted)
        + ". Install it with: pip install -e ./vendor/QuotexAPI"
    )


def _enums() -> Any:
    import importlib

    for name in ("QuotexAPI.enums", "quotexapi.enums", "quotex_api.enums"):
        try:
            return importlib.import_module(name)
        except ImportError:
            continue
    raise NotConnected("QuotexAPI enums module not found.")


async def _maybe_await(value: Any) -> Any:
    """
    Call results here are sometimes coroutines and sometimes plain values, depending on
    which service the client delegates to. Awaiting only what is awaitable keeps the
    adapter working across both without a branch at every call site.
    """
    if inspect.isawaitable(value):
        return await value
    return value


def _enum_member(enum_cls: Any, *names: str) -> Any:
    """
    Pick an enum member by any of several spellings.

    Broker libraries disagree on whether the practice balance is DEMO or PRACTICE, and
    getting it wrong sends a real order to the wrong balance — so the choice is made by
    matching names rather than by assuming one.
    """
    for name in names:
        member = getattr(enum_cls, name, None)
        if member is not None:
            return member
    for member in enum_cls:
        if member.name.upper() in {n.upper() for n in names}:
            return member
        if str(getattr(member, "value", "")).upper() in {n.upper() for n in names}:
            return member
    raise NotConnected(
        f"{enum_cls.__name__} has none of {names}; the library's API has changed."
    )


async def _auto_extract_ssid(email: str, password: str) -> str | None:
    """Performs automated login to Quotex via capture.mjs and extracts the SSID session token."""
    import json
    import os
    import subprocess
    from pathlib import Path
    from app.proxy import proxy_url

    script_path = Path(__file__).resolve().parents[3] / "tools" / "binolla-auth" / "capture.mjs"
    if not script_path.exists():
        script_path = Path("/home/web/backend/tools/binolla-auth/capture.mjs")
    if not script_path.exists():
        return None

    proxy = proxy_url()
    env = {
        **os.environ,
        "BINOLLA_AUTH_EMAIL": email,
        "BINOLLA_AUTH_PASSWORD": password,
    }
    auth_proxy = os.getenv("BINOLLA_AUTH_PROXY") or proxy
    if auth_proxy:
        env["BINOLLA_AUTH_PROXY"] = auth_proxy

    cmd = [
        "node",
        str(script_path),
        "--broker", "quotex",
        "--mode", "login",
        "--headless", "true",
        "--timeoutMs", "60000",
    ]

    def _run() -> str | None:
        try:
            res = subprocess.run(
                cmd,
                cwd=str(script_path.parent),
                env=env,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                timeout=75,
            )
            for line in res.stdout.strip().split("\n"):
                s = line.find("{")
                e = line.rfind("}")
                if s != -1 and e != -1 and e > s:
                    try:
                        data = json.loads(line[s:e + 1])
                        if data.get("ok") and data.get("token"):
                            return str(data["token"])
                    except Exception:
                        pass
        except Exception:
            pass
        return None

    try:
        return await asyncio.to_thread(_run)
    except Exception:
        return None



class QuotexSession(BrokerSession):
    broker = "quotex"

    def __init__(self, user_id: str) -> None:
        super().__init__(user_id)
        self._client: Any = None
        self._data: Any = None
        self._live: QuotexLiveData | None = None
        #: Shared across every session on this broker — see app.market_data.
        self._market = market_data(self.broker)
        #: Pairs this session has subscribed, so its claims can be released on close.
        self._subscribed: set[tuple[str, int]] = set()
        #: The session this connection was authenticated with, if any.
        self.ssid: str | None = None

    # ---- lifecycle ---------------------------------------------------------

    async def connect(
        self,
        *,
        ssid: str | None = None,
        email: str | None = None,
        password: str | None = None,
        account_type: AccountType = AccountType.REAL,
    ) -> None:
        if not ssid and not (email and password):
            raise AuthError("Quotex needs either an SSID or an email/password pair.")

        ssid = _normalize_ssid(ssid)

        # Automatically extract SSID from Quotex HTTP login if missing
        if not ssid and (email and password):
            ssid = await _auto_extract_ssid(email, password)

        cls = _load_client_cls()
        self.lifecycle = LifecycleState.CONNECTING

        # Before the client is constructed, not after: its transport reads the proxy from
        # the environment when it builds its session, and there is no object to set it on
        # afterwards.
        configure_process_proxy()
        install_websocket_transport()

        # The balance is chosen in the constructor, before the socket exists. Switching
        # after the handshake races the broker's own setup and can leave the session on
        # the other balance while the API reports this one.
        client = cls(
            email=email or None,
            password=password or None,
            ssid=ssid or None,
            is_demo=account_type is AccountType.DEMO,
        )

        # Belt and braces for builds that DO expose a proxy attribute. The environment
        # above is what actually carries it for this library.
        url = proxy_url()
        if url:
            _apply_proxy(client, {"http": url, "https": url})

        self._client = client
        self.account_type = account_type
        self.ssid = ssid
        connection = getattr(client, "connection", None)
        if connection is not None and hasattr(connection, "_route_socketio_event"):
            self._live = QuotexLiveData(client, self._market)
        try:
            if self._live is not None and not ssid:
                raise AuthError("Quotex requires a valid session token. Please check your credentials.")
            connected = await _maybe_await(client.connect())
            if connected is False:
                raise NotConnected("Quotex connection failed.")
            if self._live is not None:
                try:
                    await self._live.authenticate(ssid, account_type)
                except AuthError:
                    if email and password:
                        new_ssid = await _auto_extract_ssid(email, password)
                        if new_ssid:
                            self.ssid = ssid = new_ssid
                            await self._live.authenticate(ssid, account_type)
                        else:
                            raise
                    else:
                        raise
                try:
                    await self._live.get_balance(account_type)
                except Exception as b_exc:
                    _log(f"non-fatal balance fetch on connect: {b_exc}")
            else:
                if hasattr(client, "login_with_ssid") or hasattr(client, "login_with_email"):
                    if not await self._explicit_login(client, ssid, email, password):
                        raise AuthError("Quotex authentication failed.")
                try:
                    await _maybe_await(client.get_balance())
                except Exception as b_exc:
                    _log(f"non-fatal balance fetch on connect: {b_exc}")
        except BaseException as exc:
            await self.disconnect()
            self.lifecycle = LifecycleState.AUTH_FAILED if isinstance(exc, AuthError) else LifecycleState.FAULTED
            if isinstance(exc, asyncio.CancelledError):
                raise
            if isinstance(exc, BrokerError):
                raise
            raise _translate(exc) from exc

        self._data = _find_data_service(client)
        self.lifecycle = LifecycleState.CONNECTED

    async def _explicit_login(
        self, client: Any, ssid: str | None, email: str | None, password: str | None
    ) -> bool:
        """Some builds separate the socket handshake from authentication."""
        try:
            if ssid and hasattr(client, "login_with_ssid"):
                await _maybe_await(client.login_with_ssid(ssid))
                return True
            if email and password and hasattr(client, "login_with_email"):
                await _maybe_await(client.login_with_email(email, password))
                return True
        except Exception:
            return False
        return False

    async def disconnect(self) -> None:
        # Persist before tearing down: a clean shutdown is exactly when the newest bars
        # are worth keeping, and the debounce may have skipped the last minute of them.
        self._market.flush()
        # Hand back the pairs this session was feeding so another can take them over
        # rather than leaving them silently dead for everyone else.
        await self._market.release_feeds(self.user_id)
        self._subscribed.clear()

        client, self._client = self._client, None
        self._data = None
        self._live = None
        self.lifecycle = LifecycleState.DISCONNECTED
        if client is None:
            return
        for name in ("disconnect", "logout", "close"):
            method = getattr(client, name, None)
            if method is None:
                continue
            try:
                await _maybe_await(method())
            except Exception:
                # A failing close on an already-dead socket is not worth surfacing.
                pass
            break

    @property
    def transport_connected(self) -> bool:
        if self._client is None:
            return False
        try:
            connection = getattr(self._client, "connection", None)
            if connection is not None and hasattr(connection, "_ws"):
                ws = connection._ws
                if ws is None or not ws.is_connected():
                    return False
            connected = getattr(self._client, "is_connected", None)
            if connected is None:
                return True
            return bool(connected() if callable(connected) else connected)
        except Exception:
            return False

    def _require(self) -> Any:
        if self._client is None:
            raise NotConnected("Quotex session is not connected.")
        return self._client

    @property
    def transport_mode(self) -> str:
        connection = getattr(self._client, "connection", None)
        ws = getattr(connection, "_ws", None)
        if getattr(ws, "_ws", None) is not None and getattr(ws, "_delegate", None) is None:
            return "websocket"
        return "polling"

    # ---- account -----------------------------------------------------------

    async def get_balance(self) -> Balance:
        client = self._require()
        if self._live is not None:
            return await self._live.get_balance(self.account_type)
        enums = _enums()
        demo_type = _enum_member(enums.AccountType, "DEMO", "PRACTICE")

        demo = 0.0
        real = 0.0
        seen = False

        # Both balances in one call when the library offers it: reporting the inactive
        # side as zero reads as a drained account.
        listing = getattr(client, "get_all_balances", None)
        if listing is not None:
            try:
                for entry in await _maybe_await(listing()) or []:
                    amount = float(getattr(entry, "amount", 0.0) or 0.0)
                    if getattr(entry, "account_type", None) == demo_type:
                        demo = amount
                    else:
                        real = amount
                    seen = True
            except Exception:
                seen = False

        if not seen:
            current = await _maybe_await(client.get_balance())
            amount = float(getattr(current, "amount", current) or 0.0)
            if self.account_type is AccountType.DEMO:
                demo = amount
            else:
                real = amount

        return Balance(demo=demo, real=real, current_type=self.account_type)

    async def change_account(self, account_type: AccountType) -> None:
        client = self._require()
        if account_type is self.account_type:
            return
        self.account_type = account_type
        if self._live is not None:
            if self.ssid:
                try:
                    await self._live.authenticate(self.ssid, account_type)
                except Exception as ex:
                    _log(f"live change_account authenticate: {ex}")
            try:
                await self._live.send_event("switch_account", {"account_type": "demo" if account_type is AccountType.DEMO else "real"})
            except Exception:
                pass
            try:
                await self._live.get_balance(account_type)
            except Exception:
                pass
            return
        switch = getattr(client, "switch_account", None) or getattr(
            client, "change_account", None
        )
        if switch is not None:
            enums = _enums()
            target = (
                _enum_member(enums.AccountType, "DEMO", "PRACTICE")
                if account_type is AccountType.DEMO
                else _enum_member(enums.AccountType, "REAL", "LIVE")
            )
            await _maybe_await(switch(target))

    # ---- market data -------------------------------------------------------

    async def list_assets(self) -> list[TradingAsset]:
        client = self._require()
        if self._live is not None:
            return await self._live.list_assets()
        raw = await _maybe_await(client.get_assets())

        assets: list[TradingAsset] = []
        for entry in raw or []:
            symbol = _attr(entry, "symbol", "ticker", "id", "name")
            if not symbol:
                continue
            # This library's Asset model is
            # (symbol, name, asset_type, is_active, current_payout). The alternatives are
            # kept because the payout field in particular has already been renamed once,
            # and reading the wrong name does not fail loudly — it reports every pair at
            # 0% and quietly makes the whole venue look untradeable.
            is_open = _attr(entry, "is_active", "is_open", "open", "active", default=True)
            payout = _attr(
                entry, "current_payout", "payout", "payout_percentage", "profit", default=0
            )
            category = _attr(entry, "asset_type", "category", "type")
            assets.append(
                TradingAsset(
                    symbol=str(symbol),
                    name=str(_attr(entry, "name", "description", default=symbol)),
                    is_open=bool(is_open),
                    payout=int(float(payout or 0)),
                    category=str(category) if category is not None else None,
                )
            )
        return assets

    async def get_candles(
        self, asset: str, period_seconds: int, count: int = 200
    ) -> list[Candle]:
        # Streamed, not fetched: this library exposes no history call, so the series is
        # whatever has arrived since the pair was subscribed. Subscribing here (rather
        # than only in `subscribe`) means a first request starts the feed instead of
        # failing outright — it will simply return few or no bars until it fills.
        await self.subscribe(asset, period_seconds)

        bucket = self._market.bars(asset, period_seconds)
        now = time.time()
        # A bar is closed once its whole period is behind us. The forming bar is what
        # makes an indicator disagree with the broker's own chart, and no downstream
        # calibration can undo it.
        cutoff = int(now) - (int(now) % period_seconds)

        candles = [
            Candle(
                timestamp=start,
                open=values[0],
                high=values[1],
                low=values[2],
                close=values[3],
                volume=values[4] if values[4] else None,
            )
            for start, values in sorted(bucket.items())
            if start + period_seconds <= cutoff
        ]
        return candles[-count:]

    async def get_quote(self, asset: str) -> Quote | None:
        hit = self._market.quote(asset)
        if hit is None:
            return None
        ts, price = hit
        return Quote(asset=asset, timestamp=ts, price=price)

    async def subscribe(self, asset: str, period_seconds: int = 60) -> None:
        client = self._require()
        key = (asset, period_seconds)

        if self._live is not None:
            await self._market.ensure_feed(asset, period_seconds, self.user_id,
                lambda: self._live.subscribe(asset, period_seconds),
                lambda: self.transport_connected)
            self._subscribed.add(key)
            return

        data = self._data or _find_data_service(client)
        if data is None:
            raise NotConnected(
                "This Quotex client exposes no candle stream, so no price history can be "
                "built. The bot cannot analyse Quotex pairs without it."
            )
        self._data = data

        def open_stream() -> Any:
            async def run() -> None:
                await _maybe_await(data.subscribe_candles(asset, period_seconds))
                data.on_candle(asset, self._make_candle_handler(asset, period_seconds))

                subscribe_quotes = getattr(data, "subscribe_quotes", None)
                on_quote = getattr(data, "on_quote", None)
                if subscribe_quotes is not None and on_quote is not None:
                    await _maybe_await(subscribe_quotes(asset))
                    on_quote(asset, self._make_quote_handler(asset))

            return run()

        try:
            # One subscription per pair for the whole gateway, not one per account: the
            # bars are identical, and multiplying them by the user count is what put
            # thousands of redundant subscriptions on the broker.
            await self._market.ensure_feed(
                asset,
                period_seconds,
                self.user_id,
                open_stream,
                # This session's own liveness, remembered with the claim so another
                # account can take the pair over if this socket drops.
                lambda: self.transport_connected,
            )
            self._subscribed.add(key)
        except Exception as exc:
            raise _translate(exc)

    def has_fresh_candles(self, asset: str, period_seconds: int) -> bool:
        return self._market.is_fresh(asset, period_seconds)

    def _make_candle_handler(self, asset: str, period_seconds: int) -> Any:
        market = self._market

        def handle(payload: Any) -> None:
            row = payload if isinstance(payload, dict) else getattr(payload, "__dict__", {})
            market.apply_candle(asset, period_seconds, row)

        return handle

    def _make_quote_handler(self, asset: str) -> Any:
        market = self._market

        def handle(payload: Any) -> None:
            row = payload if isinstance(payload, dict) else getattr(payload, "__dict__", {})
            market.apply_quote(asset, row)

        return handle

    # ---- trading -----------------------------------------------------------

    async def place_order(
        self, asset: str, amount: float, duration_seconds: int, direction: str
    ) -> OrderResponse:
        client = self._require()
        enums = _enums()
        side = (
            _enum_member(enums.TradeDirection, "CALL", "BUY", "UP")
            if direction.strip().upper() == "CALL"
            else _enum_member(enums.TradeDirection, "PUT", "SELL", "DOWN")
        )

        try:
            trade = await _maybe_await(
                client.buy(
                    asset=asset,
                    amount=float(amount),
                    direction=side,
                    expiry=int(duration_seconds),
                )
            )
        except Exception as exc:
            raise _translate(exc)

        order_id = str(_attr(trade, "order_id", "id", "ticket", default="") or "")
        if not order_id:
            raise NotConnected("Quotex accepted the order but returned no id to track it by.")

        opened = _attr(trade, "open_time")
        placed_at = opened.timestamp() if hasattr(opened, "timestamp") else time.time()

        return OrderResponse(
            order_id=order_id,
            asset=asset,
            direction=direction.strip().upper(),
            amount=float(amount),
            open_price=_num(_attr(trade, "open_price")),
            placed_at=placed_at,
            expiry_at=placed_at + duration_seconds,
            account_type=self.account_type,
        )

    async def wait_outcome(self, order_id: str, timeout_seconds: float) -> Outcome:
        client = self._require()
        waiter = getattr(client, "wait_for_result", None)
        if waiter is None:
            raise NotConnected("This Quotex client cannot report trade outcomes.")

        try:
            trade = await _maybe_await(waiter(order_id, timeout=timeout_seconds))
        except asyncio.TimeoutError:
            # Not decided yet. Reporting a loss here would record a winning trade as lost;
            # the caller re-checks an unknown outcome and does not re-check a settled one.
            return Outcome(
                order_id=order_id,
                result=TradeResult.UNKNOWN,
                profit_loss=0.0,
                closed_at=time.time(),
            )
        except Exception as exc:
            raise _translate(exc)

        pnl = _num(_attr(trade, "profit")) or 0.0
        verdict = _attr(trade, "result")
        text = str(getattr(verdict, "name", verdict) or "").lower()

        if "win" in text:
            result = TradeResult.WIN
        elif "loss" in text or "lose" in text or "loose" in text:
            result = TradeResult.LOSS
        elif text in {"equal", "draw", "tie", "refund"}:
            result = TradeResult.TIE
        elif pnl > 0:
            result = TradeResult.WIN
        elif pnl < 0:
            result = TradeResult.LOSS
        else:
            result = TradeResult.UNKNOWN

        return Outcome(
            order_id=order_id,
            result=result,
            profit_loss=float(pnl),
            close_price=_num(_attr(trade, "close_price")),
            closed_at=time.time(),
        )


def _normalize_ssid(ssid: str | None) -> str | None:
    """
    Reduces an SSID to the bare session value the library expects.

    The browser capture sees the authorisation the site itself sends, which is a whole
    Socket.IO frame::

        42["authorization",{"session":"abc…","isDemo":0,"tournamentId":0}]

    The library builds that frame itself, from the session value and the balance the
    caller asked for. Passing the frame through would double-wrap it, and would also let a
    captured `isDemo` override the account the user chose — which is how a demo request
    ends up placing a real trade. So the value is unwrapped here, and a bare token is
    already in the right shape and passes through untouched.
    """
    if not ssid:
        return None

    text = ssid.strip()
    if not text:
        return None

    start = text.find("[")
    if start >= 0:
        try:
            frame = json.loads(text[start:])
        except (ValueError, TypeError):
            return text
        if isinstance(frame, list) and len(frame) >= 2 and isinstance(frame[1], dict):
            for field in ("session", "ssid", "token"):
                value = frame[1].get(field)
                if isinstance(value, str) and value.strip():
                    return value.strip()
    return text


def _find_data_service(client: Any) -> Any:
    """
    Locate the client's candle stream.

    Named attributes first, then a scan — the library keeps its services private and has
    already renamed things once, and a missed rename here would silently leave every
    Quotex pair with no price history at all.
    """
    for name in ("data", "_data", "data_service", "_data_service"):
        service = getattr(client, name, None)
        if service is not None and hasattr(service, "subscribe_candles"):
            return service

    for name in dir(client):
        if name.startswith("__"):
            continue
        try:
            service = getattr(client, name)
        except Exception:
            continue
        if hasattr(service, "subscribe_candles") and hasattr(service, "on_candle"):
            return service
    return None


def _attr(obj: Any, *names: str, default: Any = None) -> Any:
    for name in names:
        if isinstance(obj, dict):
            if name in obj:
                return obj[name]
            continue
        value = getattr(obj, name, None)
        if value is not None:
            return value
    return default


def _num(value: Any) -> float | None:
    try:
        return float(value) if value is not None else None
    except (TypeError, ValueError):
        return None


def _apply_proxy(client: Any, proxy: dict[str, str]) -> None:
    for attr in ("proxies", "proxy"):
        if hasattr(client, attr):
            setattr(client, attr, proxy)
            return
    config = getattr(client, "config", None)
    if config is not None:
        for attr in ("proxies", "proxy"):
            if hasattr(config, attr):
                setattr(config, attr, proxy)
                return


def _translate(exc: Exception) -> Exception:
    return _translate_reason(f"{type(exc).__name__}: {exc}")


def _translate_reason(reason: str) -> Exception:
    """
    Map a broker message onto the gateway's error kinds.

    The distinction matters downstream: a CAPTCHA is intermittent and worth retrying, an
    IP block is not per-account and must pause everyone, and bad credentials should stop
    only that user.
    """
    low = reason.lower()
    if "captcha" in low:
        return CaptchaRequired(reason)
    if "403" in low or "forbidden" in low or "region" in low or "country" in low:
        return BlockedError(reason)
    if (
        "credential" in low
        or "password" in low
        or "unauthor" in low
        or "authentication" in low
        or "sessionexpired" in low
    ):
        return AuthError(reason)
    if "connection" in low or "websocket" in low or "timeout" in low:
        return NotConnected(reason)
    return AuthError(reason or "Quotex refused the connection.")


