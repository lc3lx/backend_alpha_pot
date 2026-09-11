"""
Broker gateway: one HTTP surface over every supported broker.

The .NET backend calls this instead of speaking a broker protocol itself. Adding a venue
means adding one adapter here — nothing in the strategy layer changes, because everything
crossing this boundary is already broker-neutral.

Bound to localhost by default: it holds live trading sessions and must never be reachable
from outside the host.
"""

from __future__ import annotations

import asyncio
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
from app.market_data import all_stores
from app.proxy import check_stickiness, configure_process_proxy, mask
from app.quotex_ws import install as install_websocket_transport
from app.registry import SessionRegistry

#: Adapters by broker id. Binolla joins this once its port lands; until then the .NET
#: side keeps using its own C# implementation, so nothing in production depends on a
#: half-finished adapter.
ADAPTERS: dict[str, type[BrokerSession]] = {
    "quotex": QuotexSession,
}

app = FastAPI(title="Scar Alpha Broker Gateway", version="0.1.0")
registry = SessionRegistry()

#: Set once, at import, before any broker client exists — a client that builds its HTTP
#: session before this runs would go out on the server's own IP and be refused.
_PROXY = configure_process_proxy()

#: Swapped in at import so the very first session already uses it — the library binds its
#: transport when a connection service is built, and a later swap would leave that
#: session on the slow polling path.
_WEBSOCKET = install_websocket_transport()

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


def _market_session(user_id: str, broker: str) -> BrokerSession:
    b_clean = broker.strip().lower()
    session = registry.get(user_id, b_clean)
    if session is not None and getattr(session, "transport_connected", False):
        return session
    fallback = registry.get_any(b_clean)
    if fallback is not None:
        return fallback
    if session is not None:
        return session
    raise HTTPException(status_code=409, detail="No live session; connect first.")


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


@app.get("/proxy/check", dependencies=[Guarded])
async def proxy_check() -> dict[str, object]:
    """
    Whether the proxy holds one exit IP.

    `sticky: false` is fatal for Quotex however good the rest of the code is: Cloudflare
    binds its clearance cookie to an IP, so an upgrade from a different one is refused,
    and a session opened on one IP is unknown on the next.
    """
    return await asyncio.to_thread(check_stickiness)


@app.get("/health")
async def health() -> dict[str, object]:
    return {
        "ok": True,
        "brokers": sorted(ADAPTERS),
        "sessions": registry.count,
        # Whether broker traffic is actually leaving through the proxy is the single most
        # common cause of "the broker refuses everything", so it is visible here rather
        # than only discoverable from a log. Host and port only — never the credentials.
        "proxy": mask(_PROXY),
        # Shared history, so "is one feed really serving everyone?" is answerable without
        # reading logs: series and feeds should stay flat as accounts are added.
        "market": {broker: store.stats() for broker, store in all_stores().items()},
        # Which transport Quotex is on. "polling" means every frame is a separate HTTPS
        # round trip, which is the difference between a subscribe costing a millisecond
        # and costing a second.
        "quotex_transport": "websocket-with-polling-fallback" if _WEBSOCKET else "polling",
        "quotex_live_transports": {
            mode: sum(1 for session in registry.all_sessions()
                if session.broker == "quotex" and session.transport_connected
                and getattr(session, "transport_mode", None) == mode)
            for mode in ("websocket", "polling")
        },
    }


#: How long a second caller waits for an in-flight connect before giving up on joining
#: it. Comfortably longer than a healthy handshake, short enough not to hold a request.
_JOIN_WAIT_SECONDS = 45.0


async def _await_session(user_id: str, broker: str, account_type: AccountType) -> BrokerSession | None:
    """Waits for an in-flight connect to produce a usable session, or None if it does not."""
    deadline = asyncio.get_running_loop().time() + _JOIN_WAIT_SECONDS

    while asyncio.get_running_loop().time() < deadline:
        session = registry.get(user_id, broker)
        if (
            session is not None
            and session.transport_connected
            and session.account_type == account_type
        ):
            return session

        # The attempt finished without producing one — failed, or on another balance.
        if not registry.throttle(broker).is_connecting(user_id):
            return session if session is not None and session.transport_connected else None

        await asyncio.sleep(0.3)

    return None


@app.post("/sessions/connect", response_model=SessionStatus, dependencies=[Guarded])
async def connect(
    body: ConnectRequest | None = None,
    user_id: str | None = None,
    broker: str | None = None,
    account_type: AccountType | None = None,
) -> SessionStatus:
    if body is None:
        if not user_id or not broker:
            raise HTTPException(status_code=422, detail="Missing user_id or broker in connect request")
        body = ConnectRequest(
            user_id=user_id,
            broker=broker,
            account_type=account_type or AccountType.REAL,
        )

    broker = body.broker.strip().lower()
    cls = _adapter(broker)
    throttle = registry.throttle(broker)

    # A session that is already live needs no login at all. Reconnecting a healthy
    # account was the single biggest source of refused sign-ins: background restores
    # asked for one on every status poll, each took the slot, and the person actually
    # clicking "log in" was turned away.
    existing = registry.get(body.user_id, broker)
    # Reuse a live session — unless the caller brought a session the existing one was not
    # built from. If the caller asks for a different account type (Real <-> Demo),
    # switch it directly on the existing session in-memory without disconnecting.
    if existing is not None and existing.transport_connected:
        if existing.account_type != body.account_type:
            try:
                await existing.change_account(body.account_type)
            except Exception as change_err:
                print(f"change_account on existing session: {change_err}")
        if not (body.ssid and body.ssid != getattr(existing, "ssid", None)):
            return _status(existing)

    # A connect for this same account is already under way — from another device, or
    # from a background restore. WAIT for it rather than refusing: one account has one
    # session, and whoever asked second wants exactly the session the first will produce.
    # Refusing here is what told a user signing in on their laptop that a sign-in was
    # "already running", while their phone was quietly completing the very same one.
    if throttle.is_connecting(body.user_id):
        joined = await _await_session(body.user_id, broker, body.account_type)
        if joined is not None:
            return _status(joined)

    # Ask before trying. A broker that is refusing logins keeps refusing, and the caller
    # retries on its own schedule — without this the gateway relays the storm.
    if not throttle.can_attempt(body.user_id):
        # Say which of the several reasons applies; "wait a moment" told a user nothing
        # about whether the problem was theirs, the broker's, or just timing.
        raise HTTPException(status_code=429, detail=throttle.describe(body.user_id))

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
        return await _market_session(user_id, broker).list_assets()
    except Exception as exc:
        raise _fail(exc)


@app.get("/market/candles", response_model=list[Candle], dependencies=[Guarded])
async def candles(
    user_id: str, broker: str, asset: str, period_seconds: int = 60, count: int = 200
) -> list[Candle]:
    try:
        return await _market_session(user_id, broker).get_candles(asset, period_seconds, count)
    except Exception as exc:
        raise _fail(exc)


@app.get("/market/quote", response_model=Quote | None, dependencies=[Guarded])
async def quote(user_id: str, broker: str, asset: str) -> Quote | None:
    try:
        return await _market_session(user_id, broker).get_quote(asset)
    except Exception as exc:
        raise _fail(exc)


@app.post("/market/subscribe", dependencies=[Guarded])
async def subscribe(
    user_id: str, broker: str, asset: str, period_seconds: int = 60
) -> dict[str, bool]:
    # Answers immediately and opens the stream behind the request.
    #
    # Warming is a background chore, and the caller does not use the result — but a
    # subscribe costs the broker a full round trip, and the warm-up worker asks for one
    # pair at a time. Waiting on each turned warming a 25-pair list into a 25-second
    # crawl that blocked an API thread the whole way.
    session = _market_session(user_id, broker)

    async def open_stream() -> None:
        try:
            await session.subscribe(asset, period_seconds)
        except Exception as exc:  # noqa: BLE001 - background work has nobody to raise to
            print(f"subscribe failed {broker}:{asset}@{period_seconds}s: {exc}")

    asyncio.create_task(open_stream())
    return {"ok": True}


@app.post("/market/subscribe/sync", dependencies=[Guarded])
async def subscribe_sync(
    user_id: str, broker: str, asset: str, period_seconds: int = 60
) -> dict[str, bool]:
    """Subscribe and wait for it — for a caller that needs the stream open before it acts."""
    try:
        await _market_session(user_id, broker).subscribe(asset, period_seconds)
        return {"ok": True}
    except Exception as exc:
        raise _fail(exc)


@app.get("/account/balance", response_model=Balance, dependencies=[Guarded])
async def balance(user_id: str, broker: str) -> Balance:
    try:
        return await _session(user_id, broker).get_balance()
    except Exception as exc:
        raise _fail(exc)


@app.post("/account/account-type", response_model=SessionStatus, dependencies=[Guarded])
async def change_account_type(
    user_id: str, broker: str, account_type: AccountType
) -> SessionStatus:
    try:
        sess = _session(user_id, broker)
        await sess.change_account(account_type)
        return _status(sess)
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


@app.on_event("shutdown")
async def _persist_on_shutdown() -> None:
    """
    Write the shared history out before exiting.

    A restart is exactly when the newest bars matter most: without this the last minute
    of every series is lost to the write debounce, and on a venue with no history call
    that gap cannot be fetched back.
    """
    for store in all_stores().values():
        store.flush()
