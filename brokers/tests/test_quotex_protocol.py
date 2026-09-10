import asyncio
import base64
import json
import queue
from types import SimpleNamespace

import pytest

from app.brokers.base import AuthError, NotConnected
from app.models import AccountType
from app.quotex_protocol import QuotexLiveData, SocketIOPackets


class Socket:
    def __init__(self):
        self.sent = []
        self.frames = queue.Queue()
        self.connected = True
        self.accept = True
        self.respond = lambda message: None

    def is_connected(self):
        return self.connected

    def send(self, message):
        self.sent.append(message)
        self.respond(message)
        return self.accept

    def recv(self, timeout):
        try:
            return self.frames.get(timeout=timeout)
        except queue.Empty:
            return None


@pytest.fixture
def live():
    async def route(*args):
        pass
    connection = SimpleNamespace(_ws=Socket(), _pending_requests={},
        _request_timeout=0.01, _route_socketio_event=route, _route_message=route)
    market = SimpleNamespace(apply_quote=lambda *a: None, apply_candle=lambda *a: None)
    return QuotexLiveData(SimpleNamespace(connection=connection), market)


def test_binary_header_keeps_event_until_attachment_arrives():
    parser = SocketIOPackets()
    assert parser.parse('451-["instruments/list",{"_placeholder":true,"num":0}]') is None
    assert parser.parse('42["s_authorization"]') == ("s_authorization", None)
    assert parser.parse(b'[[1,"EURUSD"]]') == ("instruments/list", [[1, "EURUSD"]])
    assert parser.pending is None


def test_binary_polling_attachment_and_zero_balances():
    parser = SocketIOPackets()
    parser.parse('451-["s_balance/list",{"_placeholder":true,"num":0}]')
    wire = "b4" + base64.b64encode(b'{"liveBalance":0,"demoBalance":0}').decode()
    assert parser.parse(wire) == ("s_balance/list", {"liveBalance": 0, "demoBalance": 0})


async def test_failed_sync_send_raises_immediately_without_awaiting_bool(live):
    live.connection._ws.accept = False
    with pytest.raises(NotConnected):
        await live.send_request("switch_account")
    assert live.connection._pending_requests == {}


async def test_request_cancellation_cleans_pending_future(live):
    task = asyncio.create_task(live.send_request("test", timeout=10))
    while not live.connection._ws.sent:
        await asyncio.sleep(0)
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert live.connection._pending_requests == {}


async def test_real_account_authorizes_before_any_balance_request(live):
    ws = live.connection._ws
    async def send(event, data=None):
        assert event == "authorization"
        assert data["isDemo"] == 0
        assert data["session"] == "token"
        live.on_event("s_authorization", None)
    live.send_event = send
    await live.authenticate("token", AccountType.REAL)
    assert ws.sent == []


async def test_rejected_session_cannot_become_connected(live):
    async def send(*args):
        live.on_event("authorization/reject", {})
    live.send_event = send
    with pytest.raises(AuthError):
        await live.authenticate("expired", AccountType.DEMO)


async def test_live_balances_are_shared_by_concurrent_polls_and_include_zero(live):
    sends = []
    async def send(*args):
        sends.append(args)
        await asyncio.sleep(0)
        live.on_event("s_balance/list", {"liveBalance": 0, "demoBalance": 124.25})
    live.send_event = send
    balances = await asyncio.gather(*(live.get_balance(AccountType.REAL) for _ in range(8)))
    assert len(sends) == 1
    assert all(b.real == 0 and b.demo == 124.25 for b in balances)


async def test_balance_timeout_never_returns_vendor_defaults(live, monkeypatch):
    async def timeout(*args):
        args[0].close()
        raise asyncio.TimeoutError
    monkeypatch.setattr(asyncio, "wait_for", timeout)
    with pytest.raises(NotConnected, match="live balance"):
        await live.get_balance(AccountType.DEMO)
    assert live.balances is None


async def test_assets_come_from_wire_and_are_not_requested_per_pair(live):
    row = [0, "USDINR_otc", "USD/INR OTC", None, None, 87] + [None] * 8 + [True]
    sends = []
    async def send(*args):
        sends.append(args)
        live.on_event("instruments/list", [row])
    live.send_event = send
    results = await asyncio.gather(*(live.list_assets() for _ in range(10)))
    assert len(sends) == 1
    assert all(r[0].symbol == "USDINR_otc" and r[0].payout == 87 for r in results)


async def test_receive_reassembles_and_delivers_once(live):
    events = []
    async def route(event, data):
        events.append(event)
        live.connection._ws.connected = False
    live.connection._route_socketio_event = route
    ws = live.connection._ws
    ws.frames.put('451-["s_balance/list",{"_placeholder":true,"num":0}]')
    ws.frames.put(b'{"liveBalance":27,"demoBalance":100}')
    await live.receive()
    assert live.balances == (27, 100)
    assert events == ["s_balance/list"]


async def test_disconnected_socket_does_not_serve_cached_balance(live):
    live.on_event("balance", {"liveBalance": 5, "demoBalance": 8})
    live.connection._ws.connected = False
    with pytest.raises(NotConnected):
        await live.get_balance(AccountType.REAL)


async def test_ticks_feed_each_subscribed_period(live):
    candles, quotes = [], []
    live.market.apply_quote = lambda *a: quotes.append(a)
    live.market.apply_candle = lambda *a: candles.append(a)
    await live.subscribe("EURUSD", 60)
    live.on_event("quotes", [["EURUSD", 1800000000, 1.2, 1]])
    assert len(quotes) == len(candles) == 1
    assert candles[0][:2] == ("EURUSD", 60)
    assert json.loads(live.connection._ws.sent[0][2:])[0] == "instruments/update"


@pytest.mark.parametrize("reject", [False, True])
async def test_session_connect_uses_wire_auth_and_balance_and_closes_on_failure(monkeypatch, reject):
    from app.brokers import quotex
    from app.models import LifecycleState

    class Client:
        instance = None

        def __init__(self, **kwargs):
            Client.instance = self
            self.closed = False
            self.connection = SimpleNamespace(_ws=Socket(), _pending_requests={}, _request_timeout=1,
                _route_socketio_event=self.route, _route_message=self.route)
            self.connection._ws.respond = self.respond

        async def route(self, *args):
            pass

        def respond(self, message):
            event, *data = json.loads(message[2:])
            if event == 'authorization':
                assert data[0]['isDemo'] == 0
                response = 'authorization/reject' if reject else 's_authorization'
                self.connection._ws.frames.put('42' + json.dumps([response]))
            elif event == 's_balance/list':
                self.connection._ws.frames.put('{"liveBalance":42.5,"demoBalance":0}')
            else:
                pytest.fail('Unexpected bootstrap event: ' + event)

        async def connect(self):
            self.receiver = asyncio.create_task(self.connection._receive_messages())

        async def disconnect(self):
            self.closed = True
            self.connection._ws.connected = False
            self.receiver.cancel()
            try:
                await self.receiver
            except asyncio.CancelledError:
                pass

        async def get_balance(self):
            pytest.fail('Vendor balance fallback must never be used')

        async def switch_account(self, *args):
            pytest.fail('Constructor already selected the account')

    monkeypatch.setattr(quotex, '_load_client_cls', lambda: Client)
    monkeypatch.setattr(quotex, 'install_websocket_transport', lambda: False)
    session = quotex.QuotexSession('protocol-test')
    if reject:
        with pytest.raises(AuthError):
            await session.connect(ssid='token', account_type=AccountType.REAL)
        assert Client.instance.closed
        assert not session.transport_connected
        assert session.lifecycle is LifecycleState.AUTH_FAILED
    else:
        try:
            await session.connect(ssid='token', account_type=AccountType.REAL)
            assert session.transport_connected
            assert (await session.get_balance()).real == 42.5
        finally:
            await session.disconnect()


def test_account_type_case_insensitive():
    assert AccountType("REAL") is AccountType.REAL
    assert AccountType("real") is AccountType.REAL
    assert AccountType("DEMO") is AccountType.DEMO
    assert AccountType("demo") is AccountType.DEMO


async def test_get_balance_sends_placeholder_payload(live):
    sends = []
    async def send(event, data=None):
        sends.append((event, data))
        live.on_event("s_balance/list", {"liveBalance": 100.0, "demoBalance": 50.0})
    live.send_event = send
    bal = await live.get_balance(AccountType.REAL)
    assert len(sends) == 1
    assert sends[0] == ("s_balance/list", {"_placeholder": True, "num": 0})
    assert bal.real == 100.0
