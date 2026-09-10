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
        #: asset -> (epoch seconds, price)
        self._quotes: dict[str, tuple[float, float]] = {}
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
        return None

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
            held = self._feed.get(key)
            if held is not None:
                holder_alive = held[1]
                alive = False
                try:
                    alive = bool(holder_alive())
                except Exception:
                    alive = False  # an unanswerable session counts as gone

                # Whoever holds it — including this same caller. `get_candles` subscribes
                # on every read, so without this the pair was re-subscribed on every poll:
                # production logs showed the same `depth/follow` fired once or twice a
                # second per pair, for pairs that were already streaming.
                if alive:
                    return

            # Load whatever previous runs left on disk before the first tick arrives, so
            # a pair is analysable immediately instead of after a fresh warm-up.
            if key not in self._bars:
                self._bars[key] = self._store.load(asset, period_seconds)

            self._feed[key] = (user_id, is_alive)

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
            raise

    async def release_feeds(self, user_id: str) -> None:
        """Drops this session's claims when it disconnects, so another can take over."""
        async with self._lock:
            for key, held in list(self._feed.items()):
                if held[0] == user_id:
                    self._feed.pop(key, None)

    # ---- writing ----------------------------------------------------------

    def apply_candle(self, asset: str, period_seconds: int, row: dict[str, Any]) -> None:
        """Folds one streamed update into the shared series."""
        ts = _num(row.get("time") or row.get("from") or row.get("timestamp"))
        close = _num(row.get("close") or row.get("price") or row.get("value"))
        if ts is None or close is None:
            return

        key = (asset, period_seconds)
        start = int(ts) - (int(ts) % period_seconds)
        bucket = self._bars.setdefault(key, {})
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

    def apply_quote(self, asset: str, row: dict[str, Any]) -> None:
        price = _num(row.get("price") or row.get("close") or row.get("value"))
        if price is None:
            return
        ts = _num(row.get("time") or row.get("timestamp")) or time.time()
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
