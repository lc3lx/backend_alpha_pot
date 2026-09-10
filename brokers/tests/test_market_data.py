"""
The shared market store.

The property under test is the one that matters operationally: a pair is subscribed ONCE
for the whole gateway, and every account reads the same series. Per-session accumulation
multiplied broker subscriptions, memory and disk writes by the user count, and made each
new account wait out its own warm-up while identical bars already existed elsewhere.
"""

from __future__ import annotations

import time

import pytest

from app.market_data import MarketData


def candle(ts: int, close: float) -> dict[str, float]:
    return {"time": ts, "open": close, "high": close + 1, "low": close - 1, "close": close}


@pytest.fixture
def market(tmp_path, monkeypatch):
    monkeypatch.setenv("BROKER_DATA_DIR", str(tmp_path))
    store = MarketData("quotex")
    # The store resolves its directory at construction, so point it at the temp path.
    from app.candle_store import CandleStore

    store._store = CandleStore("quotex", root=tmp_path)  # noqa: SLF001
    return store


async def test_one_subscription_serves_every_account(market):
    calls: list[str] = []

    async def subscribe_a() -> None:
        calls.append("a")

    async def subscribe_b() -> None:
        calls.append("b")

    await market.ensure_feed("EURUSD_otc", 60, "user-a", subscribe_a, lambda: True)
    await market.ensure_feed("EURUSD_otc", 60, "user-b", subscribe_b, lambda: True)

    # The second account rides the first account's stream instead of opening its own.
    assert calls == ["a"]
    assert market.feeder("EURUSD_otc", 60) == "user-a"


async def test_a_second_account_sees_the_first_accounts_bars(market):
    await market.ensure_feed("EURUSD_otc", 60, "user-a", lambda: None, lambda: True)
    market.apply_candle("EURUSD_otc", 60, candle(1_800_000_000, 1.1))
    market.apply_candle("EURUSD_otc", 60, candle(1_800_000_060, 1.2))

    # No warm-up of its own: the history is already there the moment it asks.
    await market.ensure_feed("EURUSD_otc", 60, "user-b", lambda: None, lambda: True)
    assert len(market.bars("EURUSD_otc", 60)) == 2


async def test_a_dropped_feed_is_taken_over(market):
    alive = {"a": True}
    calls: list[str] = []

    await market.ensure_feed(
        "EURUSD_otc", 60, "user-a", lambda: calls.append("a"), lambda: alive["a"]
    )
    market.apply_candle("EURUSD_otc", 60, candle(1_800_000_000, 1.1))

    # The feeding session drops. Without a takeover the pair would go silent for everyone.
    alive["a"] = False
    await market.ensure_feed(
        "EURUSD_otc", 60, "user-b", lambda: calls.append("b"), lambda: True
    )

    assert calls == ["a", "b"]
    assert market.feeder("EURUSD_otc", 60) == "user-b"
    # And the history carries straight on rather than restarting.
    assert len(market.bars("EURUSD_otc", 60)) == 1


async def test_a_failed_subscribe_does_not_hold_the_pair(market):
    async def fails() -> None:
        raise RuntimeError("stream refused")

    with pytest.raises(RuntimeError):
        await market.ensure_feed("EURUSD_otc", 60, "user-a", fails, lambda: True)

    # Left claimed, the pair would look fed while nothing streamed.
    assert market.feeder("EURUSD_otc", 60) is None


async def test_closing_one_account_frees_its_pairs(market):
    await market.ensure_feed("EURUSD_otc", 60, "user-a", lambda: None, lambda: True)
    await market.ensure_feed("GBPUSD_otc", 60, "user-a", lambda: None, lambda: True)

    await market.release_feeds("user-a")

    assert market.feeder("EURUSD_otc", 60) is None
    assert market.feeder("GBPUSD_otc", 60) is None


async def test_bars_are_merged_not_duplicated_within_one_bar(market):
    market.apply_candle("EURUSD_otc", 60, {"time": 1_800_000_010, "close": 1.10, "high": 1.11, "low": 1.09})
    market.apply_candle("EURUSD_otc", 60, {"time": 1_800_000_040, "close": 1.12, "high": 1.13, "low": 1.05})

    bars = market.bars("EURUSD_otc", 60)
    assert len(bars) == 1

    values = bars[1_800_000_000]
    assert values[1] == pytest.approx(1.13)  # highest high kept
    assert values[2] == pytest.approx(1.05)  # lowest low kept
    assert values[3] == pytest.approx(1.12)  # newest close wins


async def test_freshness_reflects_the_last_update(market):
    assert market.is_fresh("EURUSD_otc", 60) is False
    market.apply_candle("EURUSD_otc", 60, candle(int(time.time()), 1.1))
    assert market.is_fresh("EURUSD_otc", 60) is True


async def test_a_quote_falls_back_to_the_newest_bar(market):
    market.apply_candle("EURUSD_otc", 60, candle(1_800_000_000, 1.23))

    quote = market.quote("EURUSD_otc")
    assert quote is not None
    assert quote[1] == pytest.approx(1.23)
