"""
Placing a Quotex order and following it to its result.

Orders go out on the live socket rather than through the vendored client, so the vendor
has no record of them: its outcome lookup answered "Order not found" for every trade this
app ever placed, and the acknowledgement was matched by whichever frame happened to
arrive first. Both halves are correlated here now, and these are the assertions that
distinguish a working order from the several ways it silently was not one.
"""

import asyncio
import queue
from types import SimpleNamespace

import pytest

from app.brokers.base import BrokerError
from app.models import TradingAsset
from app.quotex_protocol import QuotexLiveData


class Socket:
    def __init__(self):
        self.sent = []
        self.frames = queue.Queue()
        self.connected = True

    def is_connected(self):
        return self.connected

    def send(self, message):
        self.sent.append(message)
        return True

    def recv(self, timeout):
        try:
            return self.frames.get(timeout=timeout)
        except queue.Empty:
            return None


@pytest.fixture
def live():
    async def route(*args):
        pass

    connection = SimpleNamespace(
        _ws=Socket(), _pending_requests={}, _request_timeout=0.01,
        _route_socketio_event=route, _route_message=route,
    )
    market = SimpleNamespace(
        apply_quote=lambda *a: None, apply_candle=lambda *a: None,
        apply_candles=lambda *a: None,
    )
    return QuotexLiveData(SimpleNamespace(connection=connection), market)


def request_id_of(live_data):
    """The id the pending order is waiting on."""
    assert live_data._pending_orders, "no order is pending"
    return live_data._pending_orders[0][0]


# ---- acknowledgement ------------------------------------------------------


async def test_an_order_is_acknowledged_by_its_own_reply(live):
    task = asyncio.create_task(
        live.place_order("EURUSD_otc", 5.0, 60, "call", is_demo=True)
    )
    await asyncio.sleep(0.05)
    req = request_id_of(live)

    live.on_event("orders/open", {
        "id": "deal-1", "asset": "EURUSD_otc", "openPrice": 1.2345, "requestId": req,
    })
    result = await asyncio.wait_for(task, 1)

    assert result["id"] == "deal-1"


async def test_the_settings_acknowledgement_is_not_mistaken_for_the_order(live):
    # `settings/apply` is sent microseconds before the order and its reply lands in the
    # same catch-all bucket. Treating it as the acknowledgement returned a payload with no
    # id, which the app stored as an order it could then never find.
    task = asyncio.create_task(
        live.place_order("EURUSD_otc", 5.0, 60, "call", is_demo=True)
    )
    await asyncio.sleep(0.05)
    req = request_id_of(live)

    live.on_event("_ack_response", {"chartId": "graph", "settings": {"chartType": 2}})
    await asyncio.sleep(0.05)
    assert not task.done()

    live.on_event("_ack_response", {"id": "deal-2", "asset": "EURUSD_otc", "requestId": req})
    assert (await asyncio.wait_for(task, 1))["id"] == "deal-2"


async def test_two_orders_in_the_same_second_get_their_own_replies(live):
    # The request id used to be whole seconds, so a bot placing two entries at once gave
    # both the same id and the first reply satisfied whichever was still waiting.
    first = asyncio.create_task(live.place_order("EURUSD_otc", 5.0, 60, "call", is_demo=True))
    await asyncio.sleep(0.05)
    second = asyncio.create_task(live.place_order("GBPUSD_otc", 7.0, 60, "put", is_demo=True))
    await asyncio.sleep(0.05)

    ids = [entry[0] for entry in live._pending_orders]
    assert len(set(ids)) == 2, "two concurrent orders shared one request id"

    live.on_event("orders/open", {"id": "b", "asset": "GBPUSD_otc", "requestId": ids[1]})
    live.on_event("orders/open", {"id": "a", "asset": "EURUSD_otc", "requestId": ids[0]})

    assert (await asyncio.wait_for(first, 1))["id"] == "a"
    assert (await asyncio.wait_for(second, 1))["id"] == "b"


async def test_a_refusal_is_raised_rather_than_returned_as_an_order(live):
    task = asyncio.create_task(live.place_order("EURUSD_otc", 5.0, 60, "call", is_demo=True))
    await asyncio.sleep(0.05)
    req = request_id_of(live)

    live.on_event("orders/open", {"error": "Not enough money", "requestId": req})
    with pytest.raises(BrokerError, match="Not enough money"):
        await asyncio.wait_for(task, 1)


async def test_an_accepted_order_with_no_id_is_a_failure(live):
    # It cannot be followed to a result, and reporting it as placed leaves the trade open
    # in the app forever.
    task = asyncio.create_task(live.place_order("EURUSD_otc", 5.0, 60, "call", is_demo=True))
    await asyncio.sleep(0.05)
    req = request_id_of(live)

    live.on_event("orders/open", {"asset": "EURUSD_otc", "amount": 5.0, "requestId": req})
    await asyncio.sleep(0.05)
    # Nothing named an order, so nothing resolved it.
    assert not task.done()
    task.cancel()


async def test_placing_an_order_needs_nothing_from_the_vendor_client(live):
    # The previous implementation reached for `connection.subscribe`, which the vendored
    # client does not have to provide — an order could fail on an attribute error before
    # a single frame went out.
    assert not hasattr(live.connection, "subscribe")
    task = asyncio.create_task(live.place_order("EURUSD_otc", 5.0, 60, "call", is_demo=True))
    await asyncio.sleep(0.05)
    assert any("orders/open" in str(frame) for frame in live.connection._ws.sent)
    task.cancel()


# ---- which instrument the order is actually sent on -----------------------


def test_a_weekday_pair_is_not_rewritten_to_its_otc_twin(live):
    # Every order used to have `_otc` appended. On a weekday that sends the trade to a
    # different instrument than the one the signal was computed on.
    live._asset_index["EURUSD"] = TradingAsset(
        symbol="EURUSD", name="EUR/USD", payout=85, is_open=True, category="currency")
    live._asset_index["EURUSD_otc"] = TradingAsset(
        symbol="EURUSD_otc", name="EUR/USD OTC", payout=85, is_open=False, category="currency")

    assert live._tradable_symbol("EURUSD") == "EURUSD"


def test_a_closed_pair_falls_back_to_the_open_otc_one(live):
    live._asset_index["EURUSD"] = TradingAsset(
        symbol="EURUSD", name="EUR/USD", payout=85, is_open=False, category="currency")
    live._asset_index["EURUSD_otc"] = TradingAsset(
        symbol="EURUSD_otc", name="EUR/USD OTC", payout=85, is_open=True, category="currency")

    assert live._tradable_symbol("EURUSD") == "EURUSD_otc"


def test_an_unknown_pair_is_sent_as_asked(live):
    # Guessing at a symbol nobody has listed is how a valid order becomes an invalid one.
    assert live._tradable_symbol("XAUUSD") == "XAUUSD"


# ---- settlement -----------------------------------------------------------


async def test_the_result_of_an_order_is_read_from_the_stream(live):
    live.on_event("deals/closed", {
        "id": "deal-9", "profit": 4.25, "closePrice": 1.2400, "closeTimestamp": 1800000060,
    })
    row = await live.wait_outcome("deal-9", 1)
    assert row["profit"] == 4.25


async def test_an_undecided_order_reports_nothing_rather_than_a_loss(live):
    assert await live.wait_outcome("deal-absent", 0.3) is None


def test_an_acknowledgement_is_not_stored_as_a_settlement(live):
    # The two frames share an id and much of their shape. Reading the acknowledgement as
    # the result would settle a trade the moment it opened, at zero profit.
    live.on_event("orders/open", {"id": "deal-3", "asset": "EURUSD_otc", "openPrice": 1.2})
    assert "deal-3" not in live.closed_orders


def test_settled_deals_do_not_accumulate_without_limit(live):
    for index in range(700):
        live.on_event("orders/closed", {
            "id": f"deal-{index}", "profit": 1.0, "closeTimestamp": 1800000000 + index,
        })
    assert len(live.closed_orders) <= 500


# ---- an order that goes unanswered ----------------------------------------


async def test_silence_is_reported_with_what_was_sent_and_what_came_back(live, monkeypatch):
    # Silence explains nothing by itself, and reproducing it costs a real entry. The
    # order as sent, and whatever the broker said instead, belong in the error.
    monkeypatch.setattr("app.quotex_protocol.asyncio.wait_for", _immediate_timeout)
    live.on_event("instruments/list", [])

    with pytest.raises(BrokerError) as caught:
        await live.place_order("EURUSD_otc", 5.0, 60, "call", is_demo=True)

    message = str(caught.value)
    assert "asset=EURUSD_otc" in message
    assert "not in the instrument list" in message
    assert "action=call" in message
    assert "isDemo=1" in message


async def test_the_silence_report_names_the_frames_that_did_arrive(live, monkeypatch):
    async def timeout_after_noise(awaitable, timeout=None):
        live.on_event("s_balance/list", {"liveBalance": 10.0})
        live.on_event("instruments/update", {"asset": "EURUSD_otc"})
        awaitable.close() if hasattr(awaitable, "close") else None
        raise asyncio.TimeoutError

    monkeypatch.setattr("app.quotex_protocol.asyncio.wait_for", timeout_after_noise)

    with pytest.raises(BrokerError) as caught:
        await live.place_order("EURUSD_otc", 5.0, 60, "call", is_demo=True)

    message = str(caught.value)
    assert "s_balance/list" in message
    assert "instruments/update" in message


async def _immediate_timeout(awaitable, timeout=None):
    raise asyncio.TimeoutError
