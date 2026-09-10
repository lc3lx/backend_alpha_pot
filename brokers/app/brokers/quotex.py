"""
Quotex adapter over the QuotexAPI library (ChipaDevTeam).

The library is imported lazily so the gateway still starts, serves health, and runs its
tests on a machine where QuotexAPI is not installed — only Quotex calls fail, and they
fail with a clear message instead of an import error at boot.

Everything Quotex-specific stops here. The rest of the service, and all of .NET, sees only
the neutral types in `app.models`.

**On candles.** This library has no history call. Its `DataService` streams candles and
quotes and nothing more, so bars are accumulated here as they arrive and persisted by
`app.candle_store` so a restart does not throw the series away — without that, every
deploy left each pair untradeable for hours while the warm-up re-accumulated.

What remains true is that a pair subscribed for the FIRST time has no past, and cannot be
analysed until enough bars have streamed in. `has_fresh_candles` answers honestly, and
`get_candles` returns only bars that genuinely closed.
"""

from __future__ import annotations

import asyncio
import inspect
import time
from typing import Any

from app.candle_store import CandleStore, trim
from app.proxy import configure_process_proxy, proxy_url
from app.brokers.base import (
    AuthError,
    BlockedError,
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


class QuotexSession(BrokerSession):
    broker = "quotex"

    def __init__(self, user_id: str) -> None:
        super().__init__(user_id)
        self._client: Any = None
        self._data: Any = None
        self._store = CandleStore(self.broker)
        #: (asset, timeframe) -> {bar_start: [open, high, low, close, volume]}
        self._bars: dict[tuple[str, int], dict[int, list[float]]] = {}
        #: (asset, timeframe) -> monotonic time of the last bar update actually received.
        self._last_tick: dict[tuple[str, int], float] = {}
        #: asset -> (epoch seconds, price), from the quote stream.
        self._quotes: dict[str, tuple[float, float]] = {}

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

        cls = _load_client_cls()
        self.lifecycle = LifecycleState.CONNECTING

        # Before the client is constructed, not after: its transport reads the proxy from
        # the environment when it builds its session, and there is no object to set it on
        # afterwards.
        configure_process_proxy()

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

        try:
            await _maybe_await(client.connect())
        except Exception as exc:
            self.lifecycle = LifecycleState.FAULTED
            raise _translate(exc)

        # `connect()` returns None and signals failure by raising, so the session is only
        # proven live by a call that needs authentication. Doing it here means a bad
        # password fails at login instead of surfacing later as an empty market.
        try:
            await _maybe_await(client.get_balance())
        except Exception as exc:
            if not await self._explicit_login(client, ssid, email, password):
                self.lifecycle = LifecycleState.AUTH_FAILED
                raise _translate(exc)

        self._client = client
        self._data = _find_data_service(client)
        self.account_type = account_type
        self.lifecycle = LifecycleState.CONNECTED

        # Confirm the balance rather than trusting the constructor flag alone.
        try:
            await self.change_account(account_type)
        except Exception:
            # Already on the right balance, or the library has no switch. The constructor
            # flag stands; a wrong balance would show up in get_balance either way.
            pass

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
        for (asset, period_seconds), bucket in self._bars.items():
            self._store.save(asset, period_seconds, bucket, force=True)

        client, self._client = self._client, None
        self._data = None
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

    # ---- account -----------------------------------------------------------

    async def get_balance(self) -> Balance:
        client = self._require()
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
        self.account_type = account_type

    # ---- market data -------------------------------------------------------

    async def list_assets(self) -> list[TradingAsset]:
        client = self._require()
        raw = await _maybe_await(client.get_assets())

        assets: list[TradingAsset] = []
        for entry in raw or []:
            symbol = _attr(entry, "symbol", "name", "id")
            if not symbol:
                continue
            is_open = _attr(entry, "is_open", "is_active", "open", default=True)
            payout = _attr(entry, "payout", "payout_percentage", "profit", default=0)
            assets.append(
                TradingAsset(
                    symbol=str(symbol),
                    name=str(_attr(entry, "name", "description", default=symbol)),
                    is_open=bool(is_open),
                    payout=int(float(payout or 0)),
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

        bucket = self._bars.get((asset, period_seconds), {})
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
        hit = self._quotes.get(asset)
        if hit is None:
            # Fall back to the newest streamed bar's close, so a pair with candles but no
            # separate quote feed still has a price.
            for (sym, _tf), bucket in self._bars.items():
                if sym == asset and bucket:
                    start = max(bucket)
                    return Quote(asset=asset, timestamp=float(start), price=bucket[start][3])
            return None
        ts, price = hit
        return Quote(asset=asset, timestamp=ts, price=price)

    async def subscribe(self, asset: str, period_seconds: int = 60) -> None:
        client = self._require()
        key = (asset, period_seconds)
        if key in self._bars:
            return  # already streaming

        data = self._data or _find_data_service(client)
        if data is None:
            raise NotConnected(
                "This Quotex client exposes no candle stream, so no price history can be "
                "built. The bot cannot analyse Quotex pairs without it."
            )
        self._data = data
        # Start from what survived the last run. This is the whole point of persisting:
        # a pair with history on disk is analysable immediately instead of after hours
        # of re-accumulation.
        self._bars[key] = self._store.load(asset, period_seconds)

        try:
            await _maybe_await(data.subscribe_candles(asset, period_seconds))
            data.on_candle(asset, self._make_candle_handler(asset, period_seconds))

            subscribe_quotes = getattr(data, "subscribe_quotes", None)
            on_quote = getattr(data, "on_quote", None)
            if subscribe_quotes is not None and on_quote is not None:
                await _maybe_await(subscribe_quotes(asset))
                on_quote(asset, self._make_quote_handler(asset))
        except Exception as exc:
            # Do not keep an empty bucket for a pair that never subscribed: it would read
            # as "streaming but quiet" forever.
            self._bars.pop(key, None)
            raise _translate(exc)

    def has_fresh_candles(self, asset: str, period_seconds: int) -> bool:
        seen = self._last_tick.get((asset, period_seconds))
        if seen is None:
            return False
        # One bar of slack: past that the stream is not keeping the series current.
        return (time.monotonic() - seen) < (period_seconds + 15)

    def _make_candle_handler(self, asset: str, period_seconds: int) -> Any:
        key = (asset, period_seconds)

        def handle(payload: Any) -> None:
            row = payload if isinstance(payload, dict) else getattr(payload, "__dict__", {})
            ts = _num(row.get("time") or row.get("from") or row.get("timestamp"))
            close = _num(row.get("close") or row.get("price") or row.get("value"))
            if ts is None or close is None:
                return

            start = int(ts) - (int(ts) % period_seconds)
            bucket = self._bars.setdefault(key, {})
            high = _num(row.get("high")) or close
            low = _num(row.get("low")) or close
            volume = _num(row.get("volume")) or 0.0

            existing = bucket.get(start)
            if existing is None:
                bucket[start] = [
                    _num(row.get("open")) or close, high, low, close, volume
                ]
            else:
                # A bar is revised repeatedly while it forms; keep the extremes and take
                # the newest close.
                existing[1] = max(existing[1], high)
                existing[2] = min(existing[2], low)
                existing[3] = close
                existing[4] = max(existing[4], volume)

            # Rolling window: whatever no longer fits drops off the far end, so the
            # series holds its size instead of growing for ever.
            trim(bucket, period_seconds)
            self._last_tick[key] = time.monotonic()
            # Debounced inside the store — this fires per streamed tick, and writing
            # each one would be hundreds of writes a minute per pair.
            self._store.save(asset, period_seconds, bucket)

        return handle

    def _make_quote_handler(self, asset: str) -> Any:
        def handle(payload: Any) -> None:
            row = payload if isinstance(payload, dict) else getattr(payload, "__dict__", {})
            price = _num(row.get("price") or row.get("close") or row.get("value"))
            if price is None:
                return
            ts = _num(row.get("time") or row.get("timestamp")) or time.time()
            self._quotes[asset] = (float(ts), price)

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


