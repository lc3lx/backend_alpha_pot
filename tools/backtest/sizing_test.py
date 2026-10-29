#!/usr/bin/env python3
"""
"Bigger stake on the trades that look strong, smaller on the ones that look risky."

That works under exactly one condition: the thing that makes a trade LOOK strong has to
make it WIN more often. If it does, sizing by it is real edge — the Kelly criterion is
built on it. If it does not, varying the stake changes nothing about the expectation per
dollar; it only moves more dollars onto some trades than others, and with a negative
expectation that is a faster way to lose the same percentage.

So this does not argue the point, it measures it. For every live trade it reconstructs,
from the recorded candles, how strong the signal was at the moment of entry:

  * rsi-any  -> how far RSI was past its threshold (RSI 5 is a stronger oversold than 14)
  * body-60  -> how decisive the bar was (body 0.95 of the range versus 0.61)

It then sorts trades by that strength into five equal groups and reports each group's win
rate. If strength predicts winning, the rates climb from the weakest group to the strongest
and confidence-weighted sizing has something to work with. If they are flat, it does not.

Finally it replays the very same trades three ways — flat stake, stake proportional to
strength, and the inverse — so the effect of the proposal is a number in dollars.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys
from collections import defaultdict

CANDLE_DIR = "/home/web/backend/brokers/var/candles/quotex"


def wilson(wins, n, z=1.96):
    if n == 0:
        return (0.0, 0.0)
    p = wins / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (max(0.0, c - h), min(1.0, c + h))


def rsi_last(closes, period):
    if len(closes) < period + 1:
        return None
    ag = al = 0.0
    for i in range(1, period + 1):
        d = closes[i] - closes[i - 1]
        ag += max(d, 0.0)
        al += max(-d, 0.0)
    ag /= period
    al /= period
    for i in range(period + 1, len(closes)):
        d = closes[i] - closes[i - 1]
        ag = (ag * (period - 1) + max(d, 0.0)) / period
        al = (al * (period - 1) + max(-d, 0.0)) / period
    return 100.0 if al == 0 else 100.0 - 100.0 / (1.0 + ag / al)


_cache = {}


def bars_for(symbol, period=60):
    if symbol not in _cache:
        try:
            with open(os.path.join(CANDLE_DIR, f"{symbol}_{period}.json"),
                      encoding="utf-8") as fh:
                _cache[symbol] = json.load(fh).get("bars", {})
        except (OSError, ValueError):
            _cache[symbol] = {}
    return _cache[symbol]


def strength(row, period=60):
    """How strong the entry signal was.

    The value the trader logged at entry is used when present. Reconstruction from the
    candle files is only a fallback for older rows, and an unreliable one: files are
    pruned after a retention window and recent bars are revised, so rebuilt values have
    come back as no extreme at all for trades that demonstrably fired on one."""
    if row.get("strength") is not None:
        return float(row["strength"])
    bars = bars_for(row["symbol"], period)
    t = int(row.get("bar_time") or 0)
    if not t or str(t) not in bars:
        return None
    rule = row.get("rule") or ""
    if rule.startswith("body"):
        o, h, l, c = (float(v) for v in bars[str(t)][:4])
        rng = h - l
        return abs(c - o) / rng if rng > 0 else None
    # RSI: walk back over contiguous bars ending at bar_time.
    closes, k = [], t
    while str(k) in bars and len(closes) < 80:
        closes.append(float(bars[str(k)][3]))
        k -= period
    closes.reverse()
    best = 0.0
    for p, lo, hi in ((5, 15, 85), (7, 20, 80), (7, 15, 85)):
        r = rsi_last(closes, p)
        if r is None:
            continue
        # distance past whichever threshold fired, in RSI points
        best = max(best, lo - r if r <= lo else (r - hi if r >= hi else 0.0))
    return best


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", action="append", required=True)
    ap.add_argument("--groups", type=int, default=5)
    args = ap.parse_args()

    for path in args.log:
        rows = []
        with open(path, encoding="utf-8") as fh:
            for line in fh:
                try:
                    r = json.loads(line)
                except ValueError:
                    continue
                res = str(r.get("result", "")).upper()
                if "WIN" not in res and "LOSS" not in res:
                    continue
                s = strength(r)
                if s is None:
                    continue
                rows.append((s, "WIN" in res, float(r.get("stake") or 1),
                             float(r.get("pnl") or 0), r.get("rule")))
        if len(rows) < 100:
            print(f"{path}: only {len(rows)} trades with reconstructable strength")
            continue

        rows.sort(key=lambda x: x[0])
        rule = rows[0][4]
        paid = [p / s for _, w, s, p, _ in rows if w and s > 0]
        payout = sum(paid) / len(paid)
        breakeven = 1 / (1 + payout)
        n = len(rows)
        size = n // args.groups

        print("=" * 90)
        print(f"{os.path.basename(path)}   rule {rule}   {n} trades   "
              f"breakeven {breakeven*100:.2f}%")
        print("=" * 90)
        unit = "RSI pts past threshold" if not str(rule).startswith("body") \
            else "body / range"
        print(f"  {'group':<14}{'signal strength (' + unit + ')':<38}"
              f"{'n':>6}{'win':>9}{'95% CI':>16}")
        rates = []
        for g in range(args.groups):
            chunk = rows[g * size:(g + 1) * size] if g < args.groups - 1 \
                else rows[g * size:]
            w = sum(1 for _, won, *_ in chunk if won)
            lo, hi = wilson(w, len(chunk))
            rates.append(w / len(chunk))
            label = ["weakest", "weak", "middle", "strong", "strongest"][g] \
                if args.groups == 5 else f"g{g+1}"
            print(f"  {label:<14}{chunk[0][0]:>8.3f} .. {chunk[-1][0]:<25.3f}"
                  f"{len(chunk):>6}{w/len(chunk)*100:>8.2f}%"
                  f"   [{lo*100:4.1f},{hi*100:4.1f}]")

        # Does strength predict winning? Point-biserial correlation.
        xs = [r[0] for r in rows]
        ys = [1.0 if r[1] else 0.0 for r in rows]
        mx, my = sum(xs) / n, sum(ys) / n
        cov = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / n
        sx = math.sqrt(sum((x - mx) ** 2 for x in xs) / n)
        sy = math.sqrt(sum((y - my) ** 2 for y in ys) / n)
        corr = cov / (sx * sy) if sx and sy else 0.0
        z = corr * math.sqrt(n)
        p = math.erfc(abs(z) / math.sqrt(2))
        print(f"\n  correlation between strength and winning: {corr:+.4f}  "
              f"(z={z:+.2f}, p={p:.3f})")

        # Replay the same trades under three sizing schemes, same total stake.
        ranks = list(range(1, n + 1))
        mean_rank = sum(ranks) / n
        schemes = {
            "flat $1 every trade": [1.0] * n,
            "bigger on strong signals": [r / mean_rank for r in ranks],
            "bigger on weak signals": [(n + 1 - r) / mean_rank for r in ranks],
        }
        print(f"\n  same {n} trades, same total stake (${n:,}), three ways:")
        for name, stakes in schemes.items():
            pnl = sum(st * (payout if won else -1.0)
                      for st, (_, won, *_) in zip(stakes, rows))
            print(f"    {name:<28} P&L {pnl:+9.2f}   ({pnl/n*100:+.2f}% of stake)")
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
