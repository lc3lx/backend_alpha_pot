"""
Quotex adapter over the QuotexAPI library (ChipaDevTeam).

The library is imported lazily so the gateway still starts, serves health, and runs its
tests on a machine where QuotexAPI is not installed — only Quotex calls fail, and they
fail with a clear message instead of an import error at boot.

Everything Quotex-specific stops here. The rest of the service, and all of .NET, sees only
the neutral types in `app.models`.
"""

from __future__ import annotations

import os
import time
from typing import Any

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
    # What the vendored ChipaDevTeam repo actually exports today: package `QuotexAPI`,
    # class `QuotexAPI`, re-exported from `QuotexAPI.client`.
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


class QuotexSession(BrokerSession):
    broker = "quotex"

    def __init__(self, user_id: str) -> None:
        super().__init__(user_id)
        self._client: Any = None
        #: (asset, period) -> last time candles were confirmed present.
        self._warm: dict[tuple[str, int], float] = {}

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

        client = cls(email=email or "", password=password or "")

        # Route through a proxy when one is configured. Brokers commonly refuse datacentre
        # IPs outright, and the failure looks identical to bad credentials — which is why
        # this is wired in rather than left for the operator to discover.
        proxy = _proxy_settings()
        if proxy:
            for attr in ("proxies", "proxy"):
                if hasattr(client, attr):
                    setattr(client, attr, proxy)
                    break

        if ssid:
            # Prefer a session token: it skips the login form entirely, so it works even
            # while the broker is demanding a CAPTCHA or refusing this server's IP.
            setattr(client, "session_data", ssid)

        try:
            ok, reason = await client.connect()
        except Exception as exc:
            self.lifecycle = LifecycleState.FAULTED
            raise _translate(exc)

        if not ok:
            self.lifecycle = LifecycleState.AUTH_FAILED
            raise _translate_reason(str(reason))

        self._client = client
        self.lifecycle = LifecycleState.CONNECTED

        # Select the balance as part of connecting, never as a later switch.
        await self.change_account(account_type)

    async def disconnect(self) -> None:
        client, self._client = self._client, None
        self.lifecycle = LifecycleState.DISCONNECTED
        if client is None:
            return
        try:
            close = getattr(client, "close", None)
            if close is not None:
                result = close()
                if hasattr(result, "__await__"):
                    await result
        except Exception:
            # A failing close on an already-dead socket is not worth surfacing.
            pass

    @property
    def transport_connected(self) -> bool:
        if self._client is None:
            return False
        check = getattr(self._client, "check_connect", None)
        try:
            return bool(check()) if callable(check) else True
        except Exception:
            return False

    def _require(self) -> Any:
        if self._client is None:
            raise NotConnected("Quotex session is not connected.")
        return self._client

    # ---- account -----------------------------------------------------------

    async def get_balance(self) -> Balance:
        client = self._require()
        amount = float(await client.get_balance())
        # Quotex exposes the ACTIVE balance only, so the inactive side stays unknown
        # rather than being reported as zero — a zero would read as a drained account.
        if self.account_type is AccountType.DEMO:
            return Balance(demo=amount, real=0.0, current_type=AccountType.DEMO)
        return Balance(demo=0.0, real=amount, current_type=AccountType.REAL)

    async def change_account(self, account_type: AccountType) -> None:
        client = self._require()
        mode = "PRACTICE" if account_type is AccountType.DEMO else "REAL"
        change = getattr(client, "change_account", None)
        if change is not None:
            result = change(mode)
            if hasattr(result, "__await__"):
                await result
        self.account_type = account_type

    # ---- market data -------------------------------------------------------

    async def list_assets(self) -> list[TradingAsset]:
        client = self._require()
        raw = await client.get_all_asset_name()
        assets: list[TradingAsset] = []
        for entry in raw or []:
            symbol = entry[0] if isinstance(entry, (list, tuple)) else str(entry)
            name = entry[1] if isinstance(entry, (list, tuple)) and len(entry) > 1 else None
            is_open = True
            try:
                check = await client.check_asset_open(symbol)
                # (id, name, open) on this library; be tolerant of shape drift.
                is_open = bool(check[2]) if isinstance(check, (list, tuple)) and len(check) > 2 else bool(check)
            except Exception:
                is_open = False
            assets.append(TradingAsset(symbol=symbol, name=name, is_open=is_open))
        return assets

    async def get_candles(self, asset: str, period_seconds: int, count: int = 200) -> list[Candle]:
        client = self._require()
        end = time.time()
        raw = await client.get_candles(asset, end, count * period_seconds, period_seconds)

        candles: list[Candle] = []
        for row in raw or []:
            if not isinstance(row, dict):
                continue
            start = row.get("time") or row.get("from")
            close = row.get("close")
            if start is None or close is None:
                continue
            candles.append(
                Candle(
                    timestamp=int(start),
                    open=float(row.get("open", close)),
                    high=float(row.get("high", close)),
                    low=float(row.get("low", close)),
                    close=float(close),
                    volume=_maybe_float(row.get("volume")),
                )
            )

        candles.sort(key=lambda c: c.timestamp)

        # Drop the forming bar. Letting it through is what makes an indicator disagree
        # with the broker's own chart, and no downstream calibration can undo it.
        cutoff = int(end) - (int(end) % period_seconds)
        closed = [c for c in candles if c.timestamp + period_seconds <= cutoff]

        if closed:
            self._warm[(asset, period_seconds)] = time.monotonic()
        return closed

    async def get_quote(self, asset: str) -> Quote | None:
        client = self._require()
        realtime = getattr(client, "get_realtime_candles", None)
        if realtime is None:
            return None
        try:
            data = await realtime(asset, 60)
        except Exception:
            return None
        if not data:
            return None
        latest_ts = max(data.keys())
        row = data[latest_ts]
        price = row.get("price") if isinstance(row, dict) else None
        if price is None and isinstance(row, dict):
            price = row.get("close")
        if price is None:
            return None
        return Quote(asset=asset, timestamp=float(latest_ts), price=float(price))

    async def subscribe(self, asset: str, period_seconds: int = 60) -> None:
        client = self._require()
        start = getattr(client, "start_candles_stream", None)
        if start is None:
            return
        result = start(asset, period_seconds)
        if hasattr(result, "__await__"):
            await result
        self._warm[(asset, period_seconds)] = time.monotonic()

    def has_fresh_candles(self, asset: str, period_seconds: int) -> bool:
        seen = self._warm.get((asset, period_seconds))
        if seen is None:
            return False
        # One bar of slack: past that the stream is not keeping the series current.
        return (time.monotonic() - seen) < (period_seconds + 15)

    # ---- trading -----------------------------------------------------------

    async def place_order(
        self, asset: str, amount: float, duration_seconds: int, direction: str
    ) -> OrderResponse:
        client = self._require()
        side = "call" if direction.strip().upper() == "CALL" else "put"

        ok, info = await client.buy(amount, asset, side, duration_seconds)
        if not ok:
            raise _translate_reason(str(info))

        order_id = str((info or {}).get("id") or (info or {}).get("ticket") or "")
        if not order_id:
            raise NotConnected("Quotex accepted the order but returned no id to track it by.")

        now = time.time()
        return OrderResponse(
            order_id=order_id,
            asset=asset,
            direction=direction.strip().upper(),
            amount=amount,
            open_price=_maybe_float((info or {}).get("openPrice")),
            placed_at=now,
            expiry_at=now + duration_seconds,
            account_type=self.account_type,
        )

    async def wait_outcome(self, order_id: str, timeout_seconds: float) -> Outcome:
        client = self._require()
        try:
            profit, status = await client.check_win(order_id)
        except Exception as exc:
            raise _translate(exc)

        pnl = float(profit or 0.0)
        # Trust the broker's own verdict when it gives one; fall back to the sign of the
        # P/L. Guessing "loss" when the outcome is merely unknown would report a winning
        # trade as a loss, which is worse than admitting we do not know.
        text = str(status or "").lower()
        if "win" in text:
            result = TradeResult.WIN
        elif "loose" in text or "lose" in text or "loss" in text:
            result = TradeResult.LOSS
        elif pnl > 0:
            result = TradeResult.WIN
        elif pnl < 0:
            result = TradeResult.LOSS
        elif text in {"equal", "draw", "tie"}:
            result = TradeResult.TIE
        else:
            result = TradeResult.UNKNOWN

        return Outcome(
            order_id=order_id,
            result=result,
            profit_loss=pnl,
            closed_at=time.time(),
        )


def _maybe_float(value: Any) -> float | None:
    try:
        return float(value) if value is not None else None
    except (TypeError, ValueError):
        return None


def _translate(exc: Exception) -> Exception:
    return _translate_reason(str(exc))


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
    if "credential" in low or "password" in low or "unauthor" in low or "invalid" in low:
        return AuthError(reason)
    return AuthError(reason or "Quotex refused the connection.")


def _proxy_settings() -> dict[str, str] | None:
    """
    Proxy for outbound broker traffic, from BROKER_PROXY (or BINOLLA_AUTH_PROXY, so one
    value can serve both).

    Accepts either shape:
        http://user:pass@host:port
        host:port:user:pass          (what proxy vendors usually hand out)
    """
    raw = (os.environ.get("BROKER_PROXY") or os.environ.get("BINOLLA_AUTH_PROXY") or "").strip()
    if not raw:
        return None

    url = raw
    if "://" not in raw:
        parts = raw.split(":")
        if len(parts) >= 4:
            host, port, user, *rest = parts
            # Keep the remainder as the password so one containing ':' survives.
            url = f"http://{user}:{':'.join(rest)}@{host}:{port}"
        elif len(parts) == 2:
            url = f"http://{raw}"

    return {"http": url, "https": url}
