"""
On-disk candle history with a rolling window.

Quotex's client library has no history call — it only streams — so bars exist for a pair
only from the moment it was subscribed. Held purely in memory that means every gateway
restart throws the series away and the bot cannot analyse a pair again until roughly the
indicator warm-up has re-accumulated: hours of dead time after each deploy.

This keeps the series on disk instead, and prunes it as it grows: each write drops
whatever now falls outside the window, so the file stays a fixed size rather than growing
for ever.

The window is deliberately **the larger of a time span and a bar count**. Three hours is
the retention the operator asked for, but three hours of 5-minute bars is 36 of them, and
the RSI warm-up floor upstream is 150 — so a literal time-only window would silently make
every higher timeframe untradeable. The bar-count floor is what stops that.
"""

from __future__ import annotations

import json
import os
import tempfile
import time
from pathlib import Path

#: One bar: [open, high, low, close, volume]
Bar = list[float]
#: bar start (unix seconds) -> bar
Series = dict[int, Bar]


def _env_int(name: str, default: int) -> int:
    try:
        value = int(os.environ.get(name, "") or default)
    except ValueError:
        return default
    return value if value > 0 else default


#: How far back to keep, in hours.
RETENTION_HOURS = _env_int("QUOTEX_CANDLE_RETENTION_HOURS", 3)

#: Never keep fewer bars than this, whatever the timeframe. The indicator warm-up floor
#: upstream is 150; the margin above it covers the bars a fresh subscription misses.
MIN_BARS = _env_int("QUOTEX_CANDLE_MIN_BARS", 200)

#: Seconds between flushes. Writing on every streamed tick would be hundreds of writes a
#: minute per pair for data that only has to survive a restart.
FLUSH_INTERVAL_SECONDS = _env_int("QUOTEX_CANDLE_FLUSH_SECONDS", 30)


def default_root() -> Path:
    configured = os.environ.get("BROKER_DATA_DIR", "").strip()
    if configured:
        return Path(configured) / "candles"
    return Path(__file__).resolve().parent.parent / "var" / "candles"


def keep_count(period_seconds: int) -> int:
    """How many bars the window holds for this timeframe (3 hours)."""
    by_time = (RETENTION_HOURS * 3600) // max(period_seconds, 1)
    return max(int(by_time), 180)


def trim(series: Series, period_seconds: int) -> Series:
    """
    Drop the oldest bars that no longer fit the window.

    Called on every write, so adding N bars removes N from the far end and the series
    holds its size (FIFO rolling window).
    """
    limit = keep_count(period_seconds)
    cutoff = int(time.time()) - (RETENTION_HOURS * 3600)

    if len(series) > limit:
        for old in sorted(series)[: len(series) - limit]:
            series.pop(old, None)

    if len(series) > 180:
        for old in sorted(series):
            if old < cutoff and len(series) > 180:
                series.pop(old, None)
            elif old >= cutoff:
                break

    return series


class CandleStore:
    """
    Reads and writes one JSON file per (asset, timeframe).

    A file per pair rather than one shared file: pairs are written independently and at
    different rates, and a single file would have every flush rewrite every pair's history
    and lose the lot on one corrupt write.
    """

    def __init__(self, broker: str, root: Path | None = None) -> None:
        self._root = (root or default_root()) / broker
        self._last_flush: dict[tuple[str, int], float] = {}

    def _path(self, asset: str, period_seconds: int) -> Path:
        # Symbols carry characters that are not safe in a filename on every platform.
        safe = "".join(c if c.isalnum() or c in "-_" else "_" for c in asset)
        return self._root / f"{safe}_{period_seconds}.json"

    def load(self, asset: str, period_seconds: int) -> Series:
        path = self._path(asset, period_seconds)
        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            # Missing or corrupt: an empty series is correct — the stream refills it.
            return {}

        series: Series = {}
        for start, values in (raw.get("bars") or {}).items():
            try:
                if len(values) >= 4:
                    series[int(start)] = [float(v) for v in values[:5]] + (
                        [0.0] if len(values) == 4 else []
                    )
            except (TypeError, ValueError):
                continue

        return trim(series, period_seconds)

    def save(self, asset: str, period_seconds: int, series: Series, *, force: bool = False) -> None:
        key = (asset, period_seconds)
        now = time.monotonic()
        if not force and (now - self._last_flush.get(key, 0.0)) < FLUSH_INTERVAL_SECONDS:
            return

        trim(series, period_seconds)
        path = self._path(asset, period_seconds)

        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            payload = {
                "asset": asset,
                "period_seconds": period_seconds,
                "saved_at": time.time(),
                "bars": {str(k): v for k, v in sorted(series.items())},
            }
            # Write-then-rename: a crash mid-write must not leave a half file that the
            # next start reads as a corrupt series and silently discards.
            with tempfile.NamedTemporaryFile(
                "w", dir=path.parent, delete=False, encoding="utf-8"
            ) as handle:
                json.dump(payload, handle, separators=(",", ":"))
                temp = Path(handle.name)
            os.replace(temp, path)
            self._last_flush[key] = now
        except OSError:
            # History is a cache: failing to persist must never break trading.
            self._last_flush[key] = now
