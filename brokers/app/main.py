"""
Broker gateway: one HTTP surface over every supported broker.

The .NET backend calls this instead of speaking a broker protocol itself. Adding a venue
means adding one adapter here — nothing in the strategy layer changes, because everything
crossing this boundary is already broker-neutral.

Bound to localhost by default: it holds live trading sessions and must never be reachable
from outside the host.
"""

from __future__ import annotations

import os
from typing import Annotated

from fastapi import Depends, FastAPI, HTTPException, Header
from pydantic import BaseModel

from app.brokers.base import (
    AuthError,
    BlockedError,
    BrokerSession,
    CaptchaRequired,
    NotConnected,
)
from app.brokers.quotex import QuotexSession
from app.models import (
    AccountType,
    Balance,
    Candle,
    OrderResponse,
    Outcome,
    Quote,
    SessionStatus,
    TradingAsset,
)
from app.registry import SessionRegistry

#: Adapters by broker id. Binolla joins this once its port lands; until then the .NET
#: side keeps using its own C# implementation, so nothing in production depends on a
#: half-finished adapter.
ADAPTERS: dict[str, type[BrokerSession]] = {
    "quotex": QuotexSession,
}

app = FastAPI(title="Scar Alpha Broker Gateway", version="0.1.0")
registry = SessionRegistry()

#: Shared secret with the .NET backend. Localhost binding is the real boundary; this stops
#: another local process from driving somebody's trading session.
_TOKEN = os.environ.get("BROKER_GATEWAY_TOKEN", "")


def require_token(x_gateway_token: Annotated[str | None, Header()] = None) -> None:
    if not _TOKEN:
        return
    if x_gateway_token != _TOKEN:
        raise HTTPException(status_code=401, detail="Bad gateway token.")


Guarded = Depends(require_token)


class ConnectRequest(BaseModel):
    user_id: str
    broker: str
    ssid: str | None = None
    email: str | None = None
    password: str | None = None
    account_type: AccountType = AccountType.REAL


class OrderRequestBody(BaseModel):
    user_id: str
    broker: str
    asset: str
    amount: float
    duration_seconds: int
    direction: str


def _adapter(broker: str) -> type[BrokerSession]:
    cls = ADAPTERS.get(broker.strip().lower())
    if cls is None:
        raise HTTPException(status_code=400, detail=f"Unknown broker '{broker}'.")
    return cls


def _session(user_id: str, broker: str) -> BrokerSession:
    session = registry.get(user_id, broker.strip().lower())
    if session is None:
        raise HTTPException(status_code=409, detail="No live session; connect first.")
    return session


def _fail(exc: Exception) -> HTTPException:
    """
    Translate adapter errors into codes the .NET side already understands, so its existing
    handling for blocks, CAPTCHAs and auth failures applies to every broker unchanged.
    """
    # Already-shaped responses (unknown broker, no session) pass through untouched —
    # wrapping them turned every 400/409 into a 502 and hid what was actually wrong.
    if isinstance(exc, HTTPException):
        return exc
    if isinstance(exc, CaptchaRequired):
        return HTTPException(status_code=428, detail=f"CAPTCHA_REQUIRED: {exc}")
    if isinstance(exc, BlockedError):
        return HTTPException(status_code=451, detail=f"BROKER_IP_BLOCKED: {exc}")
    if isinstance(exc, AuthError):
        return HTTPException(status_code=401, detail=f"BROKER_AUTH_FAILED: {exc}")
    if isinstance(exc, NotConnected):
        return HTTPException(status_code=409, detail=f"NOT_CONNECTED: {exc}")
    return HTTPException(status_code=502, detail=f"BROKER_ERROR: {exc}")


@app.get("/health")
async def health() -> dict[str, object]:
    return {
        "ok": True,
        "brokers": sorted(ADAPTERS),
        "sessions": registry.count,
    }


@app.post("/sessions/connect", response_model=SessionStatus, dependencies=[Guarded])
async def connect(body: ConnectRequest) -> SessionStatus:
    broker = body.broker.strip().lower()
    cls = _adapter(broker)
    throttle = registry.throttle(broker)

    # Ask before trying. A broker that is refusing logins keeps refusing, and the caller
    # retries on its own schedule — without this the gateway relays the storm.
    if not throttle.can_attempt(body.user_id):
        raise HTTPException(
            status_code=429,
            detail="A connect attempt is already running or was just refused. Wait before retrying.",
        )

    await registry.remove(body.user_id, broker)
    session = cls(body.user_id)
    try:
        await session.connect(
            ssid=body.ssid,
            email=body.email,
            password=body.password,
            account_type=body.account_type,
        )
    except Exception as exc:
        throttle.mark_failed(body.user_id, blocked_by_broker=isinstance(exc, BlockedError))
        raise _fail(exc)

    throttle.mark_succeeded(body.user_id)
    await registry.put(session)
    return _status(session)


@app.post("/sessions/disconnect", dependencies=[Guarded])
async def disconnect(user_id: str, broker: str) -> dict[str, bool]:
    await registry.remove(user_id, broker.strip().lower())
    return {"ok": True}


@app.get("/sessions/status", response_model=SessionStatus, dependencies=[Guarded])
async def status(user_id: str, broker: str) -> SessionStatus:
    return _status(_session(user_id, broker))


@app.get("/market/assets", response_model=list[TradingAsset], dependencies=[Guarded])
async def assets(user_id: str, broker: str) -> list[TradingAsset]:
    try:
        return await _session(user_id, broker).list_assets()
    except Exception as exc:
        raise _fail(exc)


@app.get("/market/candles", response_model=list[Candle], dependencies=[Guarded])
async def candles(
    user_id: str, broker: str, asset: str, period_seconds: int = 60, count: int = 200
) -> list[Candle]:
    try:
        return await _session(user_id, broker).get_candles(asset, period_seconds, count)
    except Exception as exc:
        raise _fail(exc)


@app.get("/market/quote", response_model=Quote | None, dependencies=[Guarded])
async def quote(user_id: str, broker: str, asset: str) -> Quote | None:
    try:
        return await _session(user_id, broker).get_quote(asset)
    except Exception as exc:
        raise _fail(exc)


@app.post("/market/subscribe", dependencies=[Guarded])
async def subscribe(
    user_id: str, broker: str, asset: str, period_seconds: int = 60
) -> dict[str, bool]:
    try:
        await _session(user_id, broker).subscribe(asset, period_seconds)
        return {"ok": True}
    except Exception as exc:
        raise _fail(exc)


@app.get("/account/balance", response_model=Balance, dependencies=[Guarded])
async def balance(user_id: str, broker: str) -> Balance:
    try:
        return await _session(user_id, broker).get_balance()
    except Exception as exc:
        raise _fail(exc)


@app.post("/orders", response_model=OrderResponse, dependencies=[Guarded])
async def place_order(body: OrderRequestBody) -> OrderResponse:
    try:
        return await _session(body.user_id, body.broker).place_order(
            body.asset, body.amount, body.duration_seconds, body.direction
        )
    except Exception as exc:
        raise _fail(exc)


@app.get("/orders/outcome", response_model=Outcome, dependencies=[Guarded])
async def outcome(
    user_id: str, broker: str, order_id: str, timeout_seconds: float = 120.0
) -> Outcome:
    try:
        return await _session(user_id, broker).wait_outcome(order_id, timeout_seconds)
    except Exception as exc:
        raise _fail(exc)


def _status(session: BrokerSession) -> SessionStatus:
    return SessionStatus(
        user_id=session.user_id,
        broker=session.broker,
        lifecycle=session.lifecycle,
        transport_connected=session.transport_connected,
        account_type=session.account_type,
    )
