"""
End-to-end checks against the HTTP surface .NET will call, using a fake broker so the
tests run without a live venue.
"""

import pytest
from fastapi.testclient import TestClient

from app import main
from app.brokers.base import BlockedError, BrokerSession, CaptchaRequired
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


class FakeBroker(BrokerSession):
    broker = "fake"
    fail_with: Exception | None = None

    async def connect(self, *, ssid=None, email=None, password=None, account_type=AccountType.REAL):
        if FakeBroker.fail_with is not None:
            raise FakeBroker.fail_with
        self.lifecycle = LifecycleState.CONNECTED
        self.account_type = account_type

    async def disconnect(self):
        self.lifecycle = LifecycleState.DISCONNECTED

    @property
    def transport_connected(self):
        return self.lifecycle is LifecycleState.CONNECTED

    async def get_balance(self):
        return Balance(demo=0.0, real=250.0, current_type=self.account_type)

    async def change_account(self, account_type):
        self.account_type = account_type

    async def list_assets(self):
        return [TradingAsset(symbol="EURUSD", is_open=True, payout=85)]

    async def get_candles(self, asset, period_seconds, count=200):
        return [Candle(timestamp=i * 60, open=1, high=2, low=0.5, close=1.5) for i in range(3)]

    async def get_quote(self, asset):
        return Quote(asset=asset, timestamp=1.0, price=1.23)

    async def subscribe(self, asset, period_seconds=60):
        return None

    def has_fresh_candles(self, asset, period_seconds):
        return True

    async def place_order(self, asset, amount, duration_seconds, direction):
        return OrderResponse(
            order_id="o1", asset=asset, direction=direction, amount=amount, placed_at=0.0
        )

    async def wait_outcome(self, order_id, timeout_seconds):
        return Outcome(order_id=order_id, result=TradeResult.WIN, profit_loss=8.5)


@pytest.fixture(autouse=True)
def _fake_adapter():
    main.ADAPTERS["fake"] = FakeBroker
    FakeBroker.fail_with = None
    main.registry._sessions.clear()
    main.registry._throttles.clear()
    yield
    main.ADAPTERS.pop("fake", None)


@pytest.fixture
def client():
    return TestClient(main.app)


def _connect(client, user="u1"):
    return client.post(
        "/sessions/connect",
        json={"user_id": user, "broker": "fake", "ssid": "s", "account_type": "Real"},
    )


def test_health_lists_brokers(client):
    body = client.get("/health").json()
    assert body["ok"] is True
    assert "quotex" in body["brokers"]


def test_connect_then_read_market_data(client):
    assert _connect(client).status_code == 200

    candles = client.get("/market/candles", params={"user_id": "u1", "broker": "fake", "asset": "EURUSD"})
    assert candles.status_code == 200
    assert len(candles.json()) == 3

    balance = client.get("/account/balance", params={"user_id": "u1", "broker": "fake"})
    assert balance.json()["real"] == 250.0


def test_market_calls_need_a_session(client):
    res = client.get("/market/candles", params={"user_id": "nobody", "broker": "fake", "asset": "X"})
    assert res.status_code == 409


def test_unknown_broker_is_rejected(client):
    res = client.post("/sessions/connect", json={"user_id": "u1", "broker": "nope"})
    assert res.status_code == 400


def test_captcha_maps_to_its_own_status(client):
    # 428 keeps "a human check is needed" distinct from a credential failure, so the
    # caller can retry rather than telling the user their password is wrong.
    FakeBroker.fail_with = CaptchaRequired("captcha")
    assert _connect(client).status_code == 428


def test_ip_block_maps_to_its_own_status(client):
    FakeBroker.fail_with = BlockedError("403 forbidden")
    assert _connect(client).status_code == 451


def test_a_refused_connect_throttles_the_next_attempt(client):
    FakeBroker.fail_with = BlockedError("403")
    assert _connect(client).status_code == 451

    # An IP block is not per-account: a different user must not be allowed to retry
    # straight away and deepen it.
    assert _connect(client, user="u2").status_code == 429


def test_place_order_and_outcome(client):
    _connect(client)
    order = client.post(
        "/orders",
        json={
            "user_id": "u1", "broker": "fake", "asset": "EURUSD",
            "amount": 10, "duration_seconds": 300, "direction": "CALL",
        },
    )
    assert order.status_code == 200
    assert order.json()["order_id"] == "o1"

    res = client.get(
        "/orders/outcome",
        params={"user_id": "u1", "broker": "fake", "order_id": "o1"},
    )
    assert res.json()["result"] == "Win"
