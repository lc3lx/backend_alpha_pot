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

    connects = 0

    async def connect(self, *, ssid=None, email=None, password=None, account_type=AccountType.REAL):
        if FakeBroker.fail_with is not None:
            raise FakeBroker.fail_with
        FakeBroker.connects += 1
        self.lifecycle = LifecycleState.CONNECTED
        self.account_type = account_type
        self.ssid = ssid

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


def test_quotex_accounts_connect_and_disconnect_independently(client, monkeypatch):
    class IsolatedQuotex(FakeBroker):
        broker = 'quotex'

        async def get_balance(self):
            return Balance(real=11 if self.user_id == 'first' else 22,
                           demo=0, current_type=self.account_type)

    monkeypatch.setitem(main.ADAPTERS, 'quotex', IsolatedQuotex)
    for uid in ('first', 'second'):
        response = client.post('/sessions/connect', json={
            'user_id': uid, 'broker': 'quotex', 'ssid': uid, 'account_type': 'Real'})
        assert response.status_code == 200
    for uid, amount in [('first', 11), ('second', 22)]:
        response = client.get('/account/balance', params={'user_id': uid, 'broker': 'quotex'})
        assert response.json()['real'] == amount
    client.post('/sessions/disconnect', params={'user_id': 'second', 'broker': 'quotex'})
    first = client.get('/sessions/status', params={'user_id': 'first', 'broker': 'quotex'})
    assert first.json()['transport_connected'] is True


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


def test_second_user_does_not_inherit_another_accounts_balance(client):
    assert _connect(client).status_code == 200
    candles = client.get("/market/candles", params={"user_id": "u2", "broker": "fake", "asset": "EURUSD"})
    assert candles.status_code == 200
    balance = client.get("/account/balance", params={"user_id": "u2", "broker": "fake"})
    assert balance.status_code == 409
    status = client.get("/sessions/status", params={"user_id": "u2", "broker": "fake"})
    assert status.status_code == 409


class FakeQuotex(FakeBroker):
    broker = "quotex"


def test_second_quotex_login_does_not_steal_the_live_socket(client):
    previous = main.ADAPTERS.get("quotex")
    main.ADAPTERS["quotex"] = FakeQuotex
    try:
        first = client.post(
            "/sessions/connect",
            json={"user_id": "u1", "broker": "quotex", "ssid": "s", "account_type": "Real"},
        )
        assert first.status_code == 200
        second = client.post(
            "/sessions/connect",
            json={"user_id": "u2", "broker": "quotex", "ssid": "s2", "account_type": "Real"},
        )
        assert second.status_code == 200
        assert client.get("/sessions/status", params={"user_id": "u1", "broker": "quotex"}).status_code == 200
        assert client.get("/account/balance", params={"user_id": "u1", "broker": "quotex"}).json()["real"] == 250.0
        assert client.get("/account/balance", params={"user_id": "u2", "broker": "quotex"}).status_code == 200
    finally:
        if previous is None:
            main.ADAPTERS.pop("quotex", None)
        else:
            main.ADAPTERS["quotex"] = previous
        main.registry._sessions.clear()


def test_unknown_broker_is_rejected(client):
    res = client.post("/sessions/connect", json={"user_id": "u1", "broker": "nope"})
    assert res.status_code == 400


def test_missing_credentials_does_not_leave_a_login_in_flight(client):
    res = client.post('/sessions/connect', json={'user_id': 'u1', 'broker': 'fake'})
    assert res.status_code == 409
    assert not main.registry.throttle('fake').is_connecting('u1')
    assert _connect(client).status_code == 200


async def test_twenty_users_keep_distinct_balances_during_concurrent_reads(monkeypatch):
    import asyncio
    import httpx
    import time

    class IsolatedBroker(FakeBroker):
        async def get_balance(self):
            await asyncio.sleep(0.001)
            return Balance(real=float(self.user_id[1:]), demo=1000, current_type=self.account_type)

    monkeypatch.setitem(main.ADAPTERS, 'fake', IsolatedBroker)
    # Other synchronous tests create different event loops.
    monkeypatch.setattr(main, '_handshake_locks', {})
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=main.app), base_url='http://test') as http:
        connected = await asyncio.gather(*(http.post('/sessions/connect', json={
            'user_id': f'u{i}', 'broker': 'fake', 'ssid': f's{i}'}) for i in range(20)))
        assert all(r.status_code == 200 for r in connected)
        latencies = []

        async def reader(i):
            for _ in range(100):
                start = time.perf_counter()
                r = await http.get('/account/balance', params={'user_id': f'u{i}', 'broker': 'fake'})
                latencies.append(time.perf_counter() - start)
                assert r.status_code == 200
                assert r.json()['real'] == i

        await asyncio.gather(*(reader(i) for i in range(20)))
        await http.post('/sessions/disconnect', params={'user_id': 'u0', 'broker': 'fake'})
        remaining = await asyncio.gather(*(http.get('/account/balance', params={
            'user_id': f'u{i}', 'broker': 'fake'}) for i in range(1, 20)))
        assert all(r.status_code == 200 and r.json()['real'] == i
                   for i, r in enumerate(remaining, 1))
        print(f'20 simulated accounts, 2000 balance reads, p95={sorted(latencies)[1899]*1000:.1f}ms')


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


def test_connecting_again_with_the_same_session_reuses_the_socket(client):
    FakeBroker.connects = 0
    assert _connect(client).status_code == 200
    assert _connect(client).status_code == 200
    # Tearing down a healthy socket on every sign-in would drop the pair feeds it owns
    # and cost a fresh handshake for nothing.
    assert FakeBroker.connects == 1


@pytest.mark.parametrize('disconnected', [False, True])
def test_new_credentials_reconnect_a_dead_session_but_preserve_a_live_one(client, disconnected):
    """Live authenticated sessions stay up; dead sessions use the new credentials.

    Quotex protocol tests separately require private authorization before reporting
    a transport as usable; public market frames alone are insufficient.
    """
    FakeBroker.connects = 0
    assert _connect(client).status_code == 200
    if disconnected:
        main.registry.get('u1', 'fake').lifecycle = LifecycleState.DISCONNECTED

    replaced = client.post(
        "/sessions/connect",
        json={"user_id": "u1", "broker": "fake", "ssid": "captured-in-a-browser", "account_type": "Real"},
    )
    assert replaced.status_code == 200
    assert FakeBroker.connects == (2 if disconnected else 1)
