"""
The two answers every screen waits on: which pairs exist, and how much money is there.

Both used to be wrong in the same quiet way. The instrument list arrives in batches and
each one replaced the last, so the app was served whatever the final small batch held —
five assets, two of them currency pairs. The balance was fetched with a blocking round
trip on a value the broker pushes anyway, which is what made every page mount wait, and
what turned a healthy account into "unable to verify balance" before a trade.
"""

import asyncio
import queue
import time
from types import SimpleNamespace

import pytest

from app.models import AccountType
from app.quotex_protocol import ASSET_TTL_SECONDS, QuotexLiveData


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


def row(index, symbol, name, payout=85, is_open=True):
    """One instrument as Quotex sends it: a positional array, open flag at index 14."""
    entry = [index, symbol, name, "currency", 0, payout] + [0] * 9
    entry.append(is_open)
    return entry


# ---- the instrument list --------------------------------------------------


def test_a_later_batch_adds_to_the_list_instead_of_replacing_it(live):
    live.on_event("instruments/list", [
        row(1, "EURUSD_otc", "EUR/USD OTC"),
        row(2, "GBPUSD_otc", "GBP/USD OTC"),
        row(3, "USDJPY_otc", "USD/JPY OTC"),
    ])
    live.on_event("instruments/list", [row(4, "AUDCAD_otc", "AUD/CAD OTC")])

    symbols = {asset.symbol for asset in live.assets}
    assert symbols == {"EURUSD_otc", "GBPUSD_otc", "USDJPY_otc", "AUDCAD_otc"}


def test_a_batch_is_authoritative_for_the_symbols_it_names(live):
    # Merging must not preserve a stale open flag: a market that just closed has to stop
    # being offered, or the app keeps sending orders the broker will refuse.
    live.on_event("instruments/list", [row(1, "EURUSD", "EUR/USD", is_open=True)])
    live.on_event("instruments/list", [row(1, "EURUSD", "EUR/USD", is_open=False)])

    assert [asset.is_open for asset in live.assets] == [False]


def test_an_empty_batch_does_not_erase_the_list(live):
    live.on_event("instruments/list", [row(1, "EURUSD_otc", "EUR/USD OTC")])
    live.on_event("instruments/list", [])
    assert len(live.assets) == 1


async def test_a_stale_list_is_served_at_once_and_refreshed_behind_the_caller(live):
    live.on_event("instruments/list", [row(1, "EURUSD_otc", "EUR/USD OTC")])
    live.assets_at = time.monotonic() - (ASSET_TTL_SECONDS + 1)
    live.connection._ws.sent.clear()

    started = time.monotonic()
    assets = await live.list_assets()
    elapsed = time.monotonic() - started

    assert [asset.symbol for asset in assets] == ["EURUSD_otc"]
    assert elapsed < 0.5, "a stale list must not make the caller wait for the new one"
    await asyncio.sleep(0.05)
    assert any("instruments/get" in str(frame) for frame in live.connection._ws.sent)


async def test_a_fresh_list_costs_no_round_trip(live):
    live.on_event("instruments/list", [row(1, "EURUSD_otc", "EUR/USD OTC")])
    live.connection._ws.sent.clear()

    await live.list_assets()
    await asyncio.sleep(0.05)
    assert live.connection._ws.sent == []


# ---- balance --------------------------------------------------------------


async def test_a_known_balance_is_returned_without_asking_the_broker(live):
    live.balances = (250.0, 10_000.0)
    live.balance_at = time.monotonic()
    live.connection._ws.sent.clear()

    started = time.monotonic()
    balance = await live.get_balance(AccountType.REAL)
    elapsed = time.monotonic() - started

    assert balance.real == 250.0
    assert elapsed < 0.2
    assert live.connection._ws.sent == []


async def test_a_stale_balance_is_still_served_at_once(live):
    # The alternative is what used to happen: the caller waits, and a pre-trade check
    # that waits long enough is reported to the user as a broker that cannot be reached.
    live.balances = (250.0, 10_000.0)
    live.balance_at = time.monotonic() - 3600

    started = time.monotonic()
    balance = await live.get_balance(AccountType.REAL)
    elapsed = time.monotonic() - started

    assert balance.real == 250.0
    assert elapsed < 0.5
    await asyncio.sleep(0.05)
    assert any("s_balance/list" in str(frame) for frame in live.connection._ws.sent)


async def test_a_pushed_balance_update_is_what_the_next_caller_sees(live):
    live.on_event("balance", {"liveBalance": 412.5, "demoBalance": 9_000.0})
    balance = await live.get_balance(AccountType.REAL)
    assert balance.real == 412.5
    assert balance.demo == 9_000.0
