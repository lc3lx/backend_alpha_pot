"""
The rolling window and its persistence.

Quotex has no history call, so this store IS the price history — if it loses bars or
grows without bound, the bot either cannot analyse a pair or slowly eats the disk.
"""

from __future__ import annotations

from app import candle_store
from app.candle_store import CandleStore, keep_count, trim


def _series(count: int, period: int = 60, start: int = 1_800_000_000) -> dict[int, list[float]]:
    return {
        start + i * period: [1.0 + i, 2.0 + i, 0.5 + i, 1.5 + i, 0.0]
        for i in range(count)
    }


def test_window_is_three_hours_on_one_minute_bars():
    # 3h / 60s = 180, under the 200-bar floor, so the floor decides.
    assert keep_count(60) == max(3 * 60, candle_store.MIN_BARS)


def test_higher_timeframes_keep_the_bar_count_floor():
    """
    Three hours of 5-minute bars is 36 — far below the indicator warm-up. A time-only
    window would leave every higher timeframe permanently untradeable, which is exactly
    the failure this floor exists to prevent.
    """
    assert keep_count(300) == candle_store.MIN_BARS
    assert keep_count(300) >= 150


def test_adding_bars_drops_the_same_number_from_the_far_end():
    period = 60
    limit = keep_count(period)

    series = _series(limit + 25, period=period)
    oldest_before = sorted(series)[:25]

    trim(series, period)

    assert len(series) == limit
    # The 25 newest survived and exactly 25 oldest went.
    for start in oldest_before:
        assert start not in series
    assert max(series) == max(_series(limit + 25, period=period))


def test_a_short_series_is_left_alone():
    series = _series(10)
    trim(series, 60)
    assert len(series) == 10


def test_bars_survive_a_restart(tmp_path):
    store = CandleStore("quotex", root=tmp_path)
    written = _series(5)

    store.save("EURUSD_otc", 60, dict(written), force=True)
    # A second store instance is the restart: nothing carries over in memory.
    reloaded = CandleStore("quotex", root=tmp_path).load("EURUSD_otc", 60)

    assert reloaded == written


def test_a_pair_never_seen_loads_empty(tmp_path):
    assert CandleStore("quotex", root=tmp_path).load("GBPUSD_otc", 60) == {}


def test_a_corrupt_file_reads_as_empty_rather_than_crashing(tmp_path):
    store = CandleStore("quotex", root=tmp_path)
    store.save("EURUSD_otc", 60, _series(3), force=True)

    path = tmp_path / "quotex" / "EURUSD_otc_60.json"
    path.write_text("{ this is not json", encoding="utf-8")

    # An unreadable cache must not stop trading; the stream refills it.
    assert store.load("EURUSD_otc", 60) == {}


def test_saving_prunes_what_is_written(tmp_path):
    period = 60
    store = CandleStore("quotex", root=tmp_path)
    series = _series(keep_count(period) + 40, period=period)

    store.save("EURUSD_otc", period, series, force=True)

    assert len(store.load("EURUSD_otc", period)) == keep_count(period)


def test_writes_are_debounced(tmp_path):
    """Streamed ticks arrive many times a second; every one must not hit the disk."""
    store = CandleStore("quotex", root=tmp_path)
    store.save("EURUSD_otc", 60, _series(3), force=True)

    store.save("EURUSD_otc", 60, _series(9), force=False)

    # The second write was inside the debounce, so the file still holds the first.
    assert len(store.load("EURUSD_otc", 60)) == 3
