"""
One shared price history for the whole gateway.

## Why this is process-wide and not per account

A pair's candles are a property of the MARKET, not of whoever is watching it. EURUSD_otc
prints the same bar for every account on the venue.

Held per session, as this was, the cost multiplied by the number of users: 100 accounts
watching 20 pairs meant 2,000 subscriptions on the broker, 2,000 copies of the same bars
in memory, and 2,000 files rewritten on disk — for 20 distinct series. Worse, each account
started from an EMPTY history when it connected, so a user who signed in an hour after
everyone else had to wait for the warm-up all over again while the identical data already
sat in another session's memory.

Here one subscription per (asset, timeframe) feeds one series, and every account reads it.
A new account inherits the full history instantly, and the load on the broker stops
growing with the user count.

## What is still per session

The broker connection itself. Only one live session at a time is elected as the FEED for
a given pair — whichever subscribed first and is still connected. If it drops, the next
account to ask takes over and the series continues from where it was, on disk and in
memory, with no gap for anyone else.
"""

from __future__ import annotations

import asyncio
import time
from typing import Any, Callable

from app.candle_store import CandleStore, Series, trim


class MarketData:
    """
    The gateway's shared candle and quote store for one broker.

    Every method is safe to call from several sessions at once: the only mutation that
    matters is appending to a bar dict, and Python's GIL makes those individual dict
    writes atomic. The subscribe path takes a lock because electing a feed must not
    happen twice for the same pair.
    """

    def __init__(self, broker: str) -> None:
        self.broker = broker
        self._store = CandleStore(broker)
        #: (asset, timeframe) -> {bar start -> [open, high, low, close, volume]}
        self._bars: dict[tuple[str, int], Series] = {}
        #: (asset, timeframe) -> monotonic time of the last update actually received.
        self._last_tick: dict[tuple[str, int], float] = {}
        #: (asset, timeframe) -> the session feeding it: (user id, liveness check).
        #:
        #: The liveness check is supplied by the feeding session itself, so this store
        #: never has to know what a session is or reach into a registry to ask.
        self._feed: dict[tuple[str, int], tuple[str, Callable[[], bool]]] = {}
        self._feed_started: dict[tuple[str, int], float] = {}
        #: asset -> (epoch seconds, price)
        self._quotes: dict[str, tuple[float, float]] = {}
        #: (asset, timeframe) -> monotonic time of the last history/load we asked for.
        #: Without a cooldown a gappy series would fire history/load on every chart poll.
        self._history_reload_at: dict[tuple[str, int], float] = {}
        self._history_reload_cooldown = 30.0
        # Short series (under the 200-bar backtest window) get one older page
        # at a time. A page that adds nothing waits 15 minutes instead of looping.
        self._backfill_at: dict[tuple[str, int], float] = {}
        self._backfill_seen: dict[tuple[str, int], int] = {}
        self._backfill_stall_until: dict[tuple[str, int], float] = {}
        self._lock = asyncio.Lock()

    # ---- reading ----------------------------------------------------------

    def bars(self, asset: str, period_seconds: int) -> Series:
        """The live series. Callers read, never mutate."""
        return self._bars.get((asset, period_seconds), {})

    def is_fresh(self, asset: str, period_seconds: int) -> bool:
        seen = self._last_tick.get((asset, period_seconds))
        if seen is None:
            return False
        # One bar of slack; past that the stream is not keeping the series current.
        return (time.monotonic() - seen) < (period_seconds + 15)

    def last_bar_age(self, asset: str, period_seconds: int) -> float | None:
        bucket = self._series(asset, period_seconds)
        if not bucket:
            return None
        return time.time() - max(bucket)

    def is_series_current(self, asset: str, period_seconds: int) -> bool:
        age = self.last_bar_age(asset, period_seconds)
        if age is None:
            return False
        return age <= (period_seconds * 3)

    def _series(self, asset: str, period_seconds: int) -> Series:
        bucket = self._bars.get((asset, period_seconds))
        if bucket:
            return bucket
        alt = asset[:-4] if asset.endswith("_otc") else f"{asset}_otc"
        return self._bars.get((alt, period_seconds), {})

    def needs_history(self, asset: str, period_seconds: int, min_bars: int = 50) -> bool:
        """
        True when the stored series cannot support RSI: too few bars, last bar stale,
        or a hole inside the recent window. Live ticks on top of a 4-hour gap look
        "fresh" but leave the bot with two minutes of contiguous history.
        """
        bucket = self._series(asset, period_seconds)
        if len(bucket) < min_bars:
            return True
        now = time.time()
        last = max(bucket)
        if now - last > period_seconds * 3:
            return True
        lookback = now - max(period_seconds * 200, 10800)
        stamps = sorted(k for k in bucket if k >= lookback)
        if len(stamps) < min_bars:
            return True
        gap_limit = max(period_seconds * 2, 120)
        for i in range(1, len(stamps)):
            if stamps[i] - stamps[i - 1] > gap_limit:
                return True
        return False

    def should_reload_history(self, asset: str, period_seconds: int) -> bool:
        if not self.needs_history(asset, period_seconds):
            return False
        last = self._history_reload_at.get((asset, period_seconds), 0.0)
        # A frozen last bar is a dead feed, not a gappy one. 30s used to skip
        # resubscribe while RSI stayed on the same closed minute for an hour.
        cooldown = 8.0 if not self.is_series_current(asset, period_seconds) else self._history_reload_cooldown
        return (time.monotonic() - last) >= cooldown

    def backfill_end(self, asset: str, period_seconds: int, target: int = 200) -> int | None:
        """Unix end for the next older history page, or None when this series can wait.

        One page every 3 minutes. If the previous page did not add bars, wait 15 minutes.
        """
        bucket = self._bars.get((asset, period_seconds), {})
        if len(bucket) >= target:
            return None
        now = time.monotonic()
        key = (asset, period_seconds)
        if now < self._backfill_stall_until.get(key, 0.0):
            return None
        last = self._backfill_at.get(key, 0.0)
        if now - last < 180.0:
            return None
        seen = self._backfill_seen.get(key)
        if seen is not None and len(bucket) <= seen:
            self._backfill_stall_until[key] = now + 900.0
            self._backfill_seen[key] = len(bucket)
            return None
        self._backfill_at[key] = now
        self._backfill_seen[key] = len(bucket)
        if not bucket:
            return int(time.time())
        return int(min(bucket))

    def mark_history_reload(self, asset: str, period_seconds: int) -> None:
        now = time.monotonic()
        self._history_reload_at[(asset, period_seconds)] = now
        alt = asset[:-4] if asset.endswith("_otc") else f"{asset}_otc"
        self._history_reload_at[(alt, period_seconds)] = now

    def quote(self, asset: str) -> tuple[float, float] | None:
        hit = self._quotes.get(asset)
        if hit is not None:
            return hit

        # Fall back to the newest streamed bar's close, so a pair with candles but no
        # separate quote feed still has a price.
        for (symbol, _period), bucket in self._bars.items():
            if symbol == asset and bucket:
                start = max(bucket)
                return (float(start), bucket[start][3])
        alt = asset[:-4] if asset.endswith("_otc") else f"{asset}_otc"
        hit = self._quotes.get(alt)
        if hit is not None:
            return hit
        return None

    def live_quotes(self) -> list[tuple[str, float, float]]:
        """Every pair's latest tick (or forming 1m close). The monitor board reads this."""
        now = time.time()
        merged: dict[str, tuple[float, float]] = {}
        for (symbol, period), bucket in self._bars.items():
            if period != 60 or not bucket:
                continue
            start = max(bucket)
            ts = float(start) if (now - start) <= 180 else now
            merged[symbol] = (ts, float(bucket[start][3]))
        for asset, (ts, price) in self._quotes.items():
            tick_ts = float(ts) if (now - ts) <= 180 else now
            merged[asset] = (tick_ts, float(price))
        return [(asset, ts, price) for asset, (ts, price) in merged.items()]

    def has_feed(self, asset: str, period_seconds: int) -> bool:
        return (asset, period_seconds) in self._feed

    def feeder(self, asset: str, period_seconds: int) -> str | None:
        held = self._feed.get((asset, period_seconds))
        return held[0] if held else None

    # ---- feeding ----------------------------------------------------------

    async def ensure_feed(
        self,
        asset: str,
        period_seconds: int,
        user_id: str,
        subscribe: Callable[[], Any],
        is_alive: Callable[[], bool],
    ) -> None:
        """
        Make sure someone is streaming this pair, subscribing through `subscribe` only if
        nobody already is.

        `is_alive` reports whether THIS caller's session is connected, and is remembered
        alongside the claim. A feed whose session has since dropped is taken over by the
        next caller rather than leaving the pair silently dead for everyone.
        """
        key = (asset, period_seconds)

        async with self._lock:
            # Give a newly requested feed time to deliver its first bars. Readers
            # arriving together must not each start the same subscription.
            held = self._feed.get(key)
            if held is not None:
                holder_alive = held[1]
                alive = False
                try:
                    alive = bool(holder_alive())
                except Exception:
                    alive = False  # an unanswerable session counts as gone

                # A connected socket with a last bar older than 3 periods is dead.
                # Skipping it was what froze RSI while health still said websocket:1.
                if (
                    alive
                    and (time.monotonic() - self._feed_started.get(key, 0) < 10.0
                         or (self.is_series_current(asset, period_seconds)
                             and not self.should_reload_history(asset, period_seconds)))
                ):
                    return

            # Load whatever previous runs left on disk before the first tick arrives, so
            # a pair is analysable immediately instead of after a fresh warm-up.
            if key not in self._bars:
                self._bars[key] = self._store.load(asset, period_seconds)

            self._feed[key] = (user_id, is_alive)
            self._feed_started[key] = time.monotonic()
            if self.needs_history(asset, period_seconds):
                self.mark_history_reload(asset, period_seconds)

        try:
            result = subscribe()
            if hasattr(result, "__await__"):
                await result
        except Exception:
            # Release the claim so the next account can try; leaving it held would make
            # the pair look fed while nothing was streaming.
            async with self._lock:
                held = self._feed.get(key)
                if held is not None and held[0] == user_id:
                    self._feed.pop(key, None)
                    self._feed_started.pop(key, None)
            raise

    async def release_feeds(self, user_id: str) -> None:
        """Drops this session's claims when it disconnects, so another can take over."""
        async with self._lock:
            for key, held in list(self._feed.items()):
                if held[0] == user_id:
                    self._feed.pop(key, None)
                    self._feed_started.pop(key, None)

    # ---- writing ----------------------------------------------------------

    def apply_candle(self, asset: str, period_seconds: int, row: dict[str, Any]) -> None:
        """Folds one streamed update into the shared series."""
        ts = _epoch_seconds(_num(row.get("time") or row.get("from") or row.get("timestamp")))
        close = _num(row.get("close") or row.get("price") or row.get("value"))
        if ts is None or close is None:
            return

        key = (asset, period_seconds)
        start = int(ts) - (int(ts) % period_seconds)
        bucket = self._bars.setdefault(key, {})
        now = time.time()
        last = max(bucket) if bucket else None
        # Vendor ticks sometimes repeat the last bar's clock for tens of minutes.
        # Those must open a new wall-clock minute or RSI freezes and every chart waits 5s.
        if last is not None and start <= last and (now - last) > (period_seconds * 2):
            ts = now
            start = int(ts) - (int(ts) % period_seconds)
        high = _num(row.get("high")) or close
        low = _num(row.get("low")) or close
        volume = _num(row.get("volume")) or 0.0

        existing = bucket.get(start)
        if existing is None:
            bucket[start] = [_num(row.get("open")) or close, high, low, close, volume]
        else:
            # A bar is revised repeatedly while it forms: keep the extremes, take the
            # newest close.
            existing[1] = max(existing[1], high)
            existing[2] = min(existing[2], low)
            existing[3] = close
            existing[4] = max(existing[4], volume)

        # Rolling window: whatever no longer fits drops off the far end, so the series
        # holds its size instead of growing for ever.
        trim(bucket, period_seconds)
        self._last_tick[key] = time.monotonic()
        # Debounced inside the store — this fires per streamed tick.
        self._store.save(asset, period_seconds, bucket)

    def apply_candles(self, asset: str, period_seconds: int, rows: list[dict[str, Any]]) -> int:
        """Folds a batch of historical candles into the shared series."""
        if not rows:
            return 0
        key = (asset, period_seconds)
        bucket = self._bars.setdefault(key, {})
        valid = 0
        for row in rows:
            ts = _epoch_seconds(_num(row.get("time") or row.get("from") or row.get("timestamp")))
            close = _num(row.get("close") or row.get("price") or row.get("value"))
            if ts is None or close is None:
                continue
            start = int(ts) - (int(ts) % period_seconds)
            open_p = _num(row.get("open")) or close
            high = _num(row.get("high")) or close
            low = _num(row.get("low")) or close
            volume = _num(row.get("volume") or row.get("ticks")) or 0.0

            existing = bucket.get(start)
            if existing is None:
                bucket[start] = [open_p, high, low, close, volume]
                valid += 1
            else:
                existing[1] = max(existing[1], high)
                existing[2] = min(existing[2], low)
                existing[3] = close
                existing[4] = max(existing[4], volume)
                valid += 1

        if valid > 0:
            trim(bucket, period_seconds)
            self._last_tick[key] = time.monotonic()
            self._store.save(asset, period_seconds, bucket, force=True)
        return valid

    def apply_quote(self, asset: str, row: dict[str, Any]) -> None:
        price = _num(row.get("price") or row.get("close") or row.get("value"))
        if price is None:
            return
        ts = _epoch_seconds(_num(row.get("time") or row.get("timestamp"))) or time.time()
        if time.time() - ts > 120:
            ts = time.time()
        self._quotes[asset] = (float(ts), price)

    def flush(self) -> None:
        """Writes every series out. Called on shutdown and on session close."""
        for (asset, period_seconds), bucket in list(self._bars.items()):
            self._store.save(asset, period_seconds, bucket, force=True)

    def stats(self) -> dict[str, object]:
        return {
            "series": len(self._bars),
            "bars": sum(len(b) for b in self._bars.values()),
            "feeds": len(self._feed),
        }


def _num(value: Any) -> float | None:
    try:
        return float(value) if value is not None else None
    except (TypeError, ValueError):
        return None


def _epoch_seconds(value: float | None) -> float | None:
    if value is None:
        return None
    # Quotex history sometimes arrives in milliseconds.
    if value >= 1_000_000_000_000:
        return value / 1000.0
    return value


#: One store per broker, shared by every session on it.
_stores: dict[str, MarketData] = {}


def market_data(broker: str) -> MarketData:
    store = _stores.get(broker)
    if store is None:
        store = MarketData(broker)
        _stores[broker] = store
    return store


def all_stores() -> dict[str, MarketData]:
    return dict(_stores)
