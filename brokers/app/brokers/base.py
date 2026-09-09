"""
The contract every broker adapter implements.

Derived from the surface the .NET application already depends on (`IBinollaClient`), so a
broker that satisfies this can be dropped in without the strategy layer knowing which one
it is talking to. The methods are deliberately the *narrow* set the bot actually calls —
34 members on the C# interface collapse to these once the events and duplicate overloads
are folded into polling and one signature.
"""

from __future__ import annotations

import abc

from app.models import (
    AccountType,
    Balance,
    Candle,
    LifecycleState,
    Outcome,
    OrderResponse,
    Quote,
    TradingAsset,
)


class BrokerError(Exception):
    """Base for adapter failures the gateway should translate, not leak."""


class AuthError(BrokerError):
    """Credentials or session token rejected by the broker."""


class BlockedError(BrokerError):
    """Broker refused the SERVER — IP/geo/WAF. Not per-account, so pause everyone."""


class CaptchaRequired(BrokerError):
    """Broker demanded a human check. Intermittent; a later attempt usually passes."""


class NotConnected(BrokerError):
    """Operation needs a live session and there is none."""


class BrokerSession(abc.ABC):
    """
    One user's live connection to one broker.

    Implementations own their own reconnect logic. The gateway never reaches inside; it
    only observes `lifecycle` and calls these methods.
    """

    broker: str

    def __init__(self, user_id: str) -> None:
        self.user_id = user_id
        self.lifecycle: LifecycleState = LifecycleState.DISCONNECTED
        self.account_type: AccountType = AccountType.REAL

    # ---- lifecycle ---------------------------------------------------------

    @abc.abstractmethod
    async def connect(
        self,
        *,
        ssid: str | None = None,
        email: str | None = None,
        password: str | None = None,
        account_type: AccountType = AccountType.REAL,
    ) -> None:
        """
        Open a session.

        `account_type` must be applied as part of the initial handshake wherever the
        broker allows it. Switching afterwards races the broker's own setup on at least
        one venue and silently leaves the socket on the wrong balance — the API then
        reports one account while every order goes to the other.
        """

    @abc.abstractmethod
    async def disconnect(self) -> None: ...

    @property
    @abc.abstractmethod
    def transport_connected(self) -> bool: ...

    # ---- account -----------------------------------------------------------

    @abc.abstractmethod
    async def get_balance(self) -> Balance: ...

    @abc.abstractmethod
    async def change_account(self, account_type: AccountType) -> None: ...

    # ---- market data -------------------------------------------------------

    @abc.abstractmethod
    async def list_assets(self) -> list[TradingAsset]: ...

    @abc.abstractmethod
    async def get_candles(self, asset: str, period_seconds: int, count: int = 200) -> list[Candle]:
        """
        Closed bars, oldest first, for `asset`.

        MUST return only bars the broker considers final. A forming bar leaking in here is
        what makes an indicator disagree with the broker's own chart, and no amount of
        calibration downstream can correct it.
        """

    @abc.abstractmethod
    async def get_quote(self, asset: str) -> Quote | None: ...

    @abc.abstractmethod
    async def subscribe(self, asset: str, period_seconds: int = 60) -> None:
        """
        Keep `asset` streaming so its candles stay resident.

        Residency is what makes a scan at bar close cost nothing. Fetching on demand
        serialises behind the broker's one-subscription-at-a-time behaviour and turns a
        bar-close scan into a multi-second crawl.
        """

    @abc.abstractmethod
    def has_fresh_candles(self, asset: str, period_seconds: int) -> bool: ...

    # ---- trading -----------------------------------------------------------

    @abc.abstractmethod
    async def place_order(
        self, asset: str, amount: float, duration_seconds: int, direction: str
    ) -> OrderResponse: ...

    @abc.abstractmethod
    async def wait_outcome(self, order_id: str, timeout_seconds: float) -> Outcome: ...
