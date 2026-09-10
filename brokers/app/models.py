"""
Broker-neutral wire types.

These mirror the shapes the .NET side already works in (`RsiCandle`, `QuoteData`,
`BalanceInfo`, `OrderResponse`, `TradeOutcome`), so the strategy layer keeps operating on
exactly the same data whichever broker produced it. Anything broker-specific stays inside
that broker's adapter and never reaches this module.
"""

from __future__ import annotations

from enum import Enum

from pydantic import BaseModel, Field


class AccountType(str, Enum):
    DEMO = "Demo"
    REAL = "Real"


class LifecycleState(str, Enum):
    DISCONNECTED = "Disconnected"
    CONNECTING = "Connecting"
    CONNECTED = "Connected"
    RECONNECTING = "Reconnecting"
    RECONNECTED = "Reconnected"
    SESSION_EXPIRED = "SessionExpired"
    AUTH_FAILED = "AuthenticationFailed"
    FAULTED = "Faulted"


class TradeResult(str, Enum):
    WIN = "Win"
    LOSS = "Loss"
    TIE = "Tie"
    UNKNOWN = "Unknown"


class Candle(BaseModel):
    """One OHLC bar. `timestamp` is the bar's START, in unix seconds."""

    timestamp: int
    open: float
    high: float
    low: float
    close: float
    volume: float | None = None


class Quote(BaseModel):
    asset: str
    timestamp: float
    price: float


class Balance(BaseModel):
    demo: float = 0.0
    real: float = 0.0
    current_type: AccountType = AccountType.REAL

    @property
    def current(self) -> float:
        return self.demo if self.current_type is AccountType.DEMO else self.real


class TradingAsset(BaseModel):
    symbol: str
    name: str | None = None
    is_open: bool = False
    payout: int = 0
    #: Broker's own grouping (currency, crypto, stock, ...). Carried through so the
    #: strategy layer can drop everything that is not FX without guessing from the
    #: symbol alone.
    category: str | None = None


class OrderRequest(BaseModel):
    asset: str
    amount: float
    duration_seconds: int
    direction: str = Field(description="CALL or PUT")


class OrderResponse(BaseModel):
    order_id: str
    asset: str
    direction: str
    amount: float
    open_price: float | None = None
    placed_at: float
    expiry_at: float | None = None
    account_type: AccountType = AccountType.REAL


class Outcome(BaseModel):
    order_id: str
    result: TradeResult
    profit_loss: float
    close_price: float | None = None
    closed_at: float | None = None


class SessionStatus(BaseModel):
    user_id: str
    broker: str
    lifecycle: LifecycleState
    transport_connected: bool
    account_type: AccountType
    subscribed_pairs: int = 0
    cached_history_keys: int = 0
