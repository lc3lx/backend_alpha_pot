#!/usr/bin/env python3
"""
Measure what a binary-options entry rule actually wins, on the candles this gateway
recorded.

Why this exists: the live trader scores setups out of 100 using numbers nobody measured
(40 for a trend, 15 for a continuing bar, 10 for a wick). A score built that way cannot
be right or wrong, only plausible, and it was choosing real trades. This replaces the
guess with a count.

Method, and its limits, stated up front:
  * A decision at bar i uses bars 0..i ONLY. The outcome reads bars after i. Any rule
    that peeks forward scores perfectly here and loses money live, so the split is
    enforced by construction, not by care.
  * Entry is the close of bar i. A 60s option settles at the close of bar i+1, a 300s
    option at the close of bar i+5 — the same thing the broker compares.
  * An unchanged close is a tie: stake returned, counted apart from wins and losses
    because it is neither.
  * A win rate from forty trades is noise. Every line below carries its sample size and
    a 95% Wilson interval, and the LOWER bound is what should be believed.

The deeper limit no amount of arithmetic fixes: these are OTC symbols, whose prices the
broker generates rather than observes. An edge measured on ten hours of one broker's
synthetic feed is not a fact about markets, and nothing here should be read as a promise
about tomorrow.
"""
from __future__ import annotations

import json
import math
import os
import sys
from dataclasses import dataclass, field

CANDLE_DIR = "/home/web/backend/brokers/var/candles/quotex"

#: Payout assumed when turning a win rate into money. The live log showed 85-92%.
ASSUMED_PAYOUT = 0.85


# ---- indicators (no look-ahead: each value uses only bars up to its own index) -------


def ema(values, period):
    out = [None] * len(values)
    if len(values) < period:
        return out
    seed = sum(values[:period]) / period
    out[period - 1] = seed
    k = 2.0 / (period + 1)
    for i in range(period, len(values)):
        out[i] = values[i] * k + out[i - 1] * (1.0 - k)
    return out


def rsi(closes, period=14):
    out = [None] * len(closes)
    if len(closes) < period + 1:
        return out
    gains = [0.0] * len(closes)
    losses = [0.0] * len(closes)
    for i in range(1, len(closes)):
        d = closes[i] - closes[i - 1]
        gains[i] = d if d > 0 else 0.0
        losses[i] = -d if d < 0 else 0.0
    avg_g = sum(gains[1 : period + 1]) / period
    avg_l = sum(losses[1 : period + 1]) / period
    out[period] = 100.0 if avg_l == 0 else 100.0 - 100.0 / (1.0 + avg_g / avg_l)
    for i in range(period + 1, len(closes)):
        avg_g = (avg_g * (period - 1) + gains[i]) / period
        avg_l = (avg_l * (period - 1) + losses[i]) / period
        out[i] = 100.0 if avg_l == 0 else 100.0 - 100.0 / (1.0 + avg_g / avg_l)
    return out


def stdev(values):
    if len(values) < 2:
        return 0.0
    mean = sum(values) / len(values)
    return math.sqrt(sum((v - mean) ** 2 for v in values) / (len(values) - 1))


# ---- data ---------------------------------------------------------------------------


@dataclass
class Series:
    symbol: str
    period: int
    times: list
    o: list
    h: list
    l: list
    c: list

    def __len__(self):
        return len(self.c)


def load_series(directory=CANDLE_DIR, period=60, min_bars=200):
    """Every symbol with enough contiguous history at this bar size."""
    out = []
    if not os.path.isdir(directory):
        return out
    for name in sorted(os.listdir(directory)):
        if not name.endswith(f"_{period}.json"):
            continue
        try:
            with open(os.path.join(directory, name), encoding="utf-8") as fh:
                bars = json.load(fh).get("bars", {})
        except (OSError, ValueError):
            continue
        if len(bars) < min_bars:
            continue
        times = sorted(int(k) for k in bars)
        # A gap means the two sides are not adjacent in time; an outcome read across one
        # would compare prices minutes or hours apart and call it a settled option.
        rows = [(t, bars[str(t)]) for t in times]
        o, h, l, c, kept = [], [], [], [], []
        for idx, (t, row) in enumerate(rows):
            if idx > 0 and t - kept[-1] != period:
                # Restart the run rather than stitch across the hole.
                if len(c) >= min_bars:
                    out.append(Series(name[: -len(f"_{period}.json")], period, kept, o, h, l, c))
                o, h, l, c, kept = [], [], [], [], []
            try:
                o.append(float(row[0]))
                h.append(float(row[1]))
                l.append(float(row[2]))
                c.append(float(row[3]))
                kept.append(t)
            except (TypeError, ValueError, IndexError):
                continue
        if len(c) >= min_bars:
            out.append(Series(name[: -len(f"_{period}.json")], period, kept, o, h, l, c))
    return out


# ---- scoring ------------------------------------------------------------------------


def wilson(wins, n, z=1.96):
    """95% interval for a proportion. Small samples get the wide answer they deserve."""
    if n == 0:
        return (0.0, 0.0)
    p = wins / n
    d = 1 + z * z / n
    centre = (p + z * z / (2 * n)) / d
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (max(0.0, centre - half), min(1.0, centre + half))


@dataclass
class Result:
    name: str
    wins: int = 0
    losses: int = 0
    ties: int = 0
    per_symbol: dict = field(default_factory=dict)

    @property
    def decided(self):
        return self.wins + self.losses

    @property
    def win_rate(self):
        return self.wins / self.decided if self.decided else 0.0

    def expectancy(self, payout=ASSUMED_PAYOUT):
        """Money per unit staked. Ties return the stake, so they dilute rather than cost."""
        total = self.decided + self.ties
        if total == 0:
            return 0.0
        return (self.wins * payout - self.losses) / total

    def line(self):
        lo, hi = wilson(self.wins, self.decided)
        edge = self.expectancy()
        # Breakeven is where payout stops covering the losses: 1/(1+payout).
        breakeven = 1.0 / (1.0 + ASSUMED_PAYOUT)
        verdict = "PROFITABLE" if lo > breakeven else ("thin" if self.win_rate > breakeven else "loses")
        return (
            f"{self.name:<34} n={self.decided:>5}  win={self.win_rate*100:5.1f}%  "
            f"95%CI=[{lo*100:4.1f},{hi*100:4.1f}]  edge={edge*100:+5.1f}%/trade  "
            f"ties={self.ties:<4} {verdict}"
        )


def evaluate(series_list, rule, hold_bars, name, min_history=60):
    """
    Run one rule over every series.

    `rule(ctx) -> "call" | "put" | None`, where ctx exposes only history up to bar i.
    """
    res = Result(name)
    for s in series_list:
        n = len(s)
        for i in range(min_history, n - hold_bars):
            side = rule(Ctx(s, i))
            if side is None:
                continue
            entry = s.c[i]
            settle = s.c[i + hold_bars]
            if settle == entry:
                res.ties += 1
                bucket = res.per_symbol.setdefault(s.symbol, [0, 0])
            elif (settle > entry) == (side == "call"):
                res.wins += 1
                bucket = res.per_symbol.setdefault(s.symbol, [0, 0])
                bucket[0] += 1
            else:
                res.losses += 1
                bucket = res.per_symbol.setdefault(s.symbol, [0, 0])
                bucket[1] += 1
    return res


class Ctx:
    """History up to and including bar i. Reading past i is impossible by construction."""

    __slots__ = ("s", "i", "_cache")

    def __init__(self, series, i):
        self.s = series
        self.i = i
        self._cache = {}

    @property
    def closes(self):
        return self.s.c[: self.i + 1]

    def close(self, back=0):
        return self.s.c[self.i - back]

    def open(self, back=0):
        return self.s.o[self.i - back]

    def high(self, back=0):
        return self.s.h[self.i - back]

    def low(self, back=0):
        return self.s.l[self.i - back]

    def ema(self, period):
        key = ("ema", period)
        if key not in self._cache:
            self._cache[key] = ema(self.closes, period)[-1]
        return self._cache[key]

    def rsi(self, period=14):
        key = ("rsi", period)
        if key not in self._cache:
            self._cache[key] = rsi(self.closes, period)[-1]
        return self._cache[key]

    def body_fraction(self, back=0):
        rng = self.high(back) - self.low(back)
        if rng <= 0:
            return 0.0
        return abs(self.close(back) - self.open(back)) / rng

    def upper_wick(self, back=0):
        rng = self.high(back) - self.low(back)
        if rng <= 0:
            return 0.0
        return (self.high(back) - max(self.open(back), self.close(back))) / rng

    def lower_wick(self, back=0):
        rng = self.high(back) - self.low(back)
        if rng <= 0:
            return 0.0
        return (min(self.open(back), self.close(back)) - self.low(back)) / rng

    def greens(self, lookback=10):
        return sum(1 for k in range(lookback) if self.close(k) > self.open(k))

    def reds(self, lookback=10):
        return sum(1 for k in range(lookback) if self.close(k) < self.open(k))

    def volatility(self, lookback=20):
        """Standard deviation of recent returns — the scale a move should be judged on."""
        key = ("vol", lookback)
        if key not in self._cache:
            cs = self.s.c[max(0, self.i - lookback) : self.i + 1]
            rets = [(cs[k] - cs[k - 1]) / cs[k - 1] for k in range(1, len(cs)) if cs[k - 1]]
            self._cache[key] = stdev(rets)
        return self._cache[key]

    def z_move(self, lookback=20):
        """How many standard deviations the last bar moved. Scale-free across symbols."""
        vol = self.volatility(lookback)
        if vol <= 0 or not self.close(1):
            return 0.0
        return ((self.close() - self.close(1)) / self.close(1)) / vol
