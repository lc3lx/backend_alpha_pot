#!/usr/bin/env python3
"""
Backtest the confluence rule, then simulate the actual mission: $100 to $120.

Two questions, answered on data the rule was not built on:

  1. Does agreement raise the win rate? Each tier — three readings agreeing ($1) and all
     four ($2) — is scored on the first 60% of each run and, separately, on the sealed 40%.
     A tier that clears breakeven in training and not sealed was fitted, not found.

  2. What are the odds of the mission itself? Given the sealed win rates, the measured
     payout and the stake rule, a Monte Carlo replays the account from $100 until it hits
     the $120 target or the capital floor, and reports how often each happens and how
     many trades it takes. That is the honest form of "can you get me to $120".
"""
from __future__ import annotations

import argparse
import gzip
import json
import math
import random
import sys

from confluence import decide

PAYOUT = 0.915


def wilson(w, n, z=1.96):
    if n == 0:
        return (0.0, 0.0)
    p = w / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (max(0.0, c - h), min(1.0, c + h))


def runs(path, period=60, min_len=150):
    with gzip.open(path, "rt", encoding="utf-8") as fh:
        raw = json.load(fh)
    out = []
    for sym, rows in raw.items():
        cur = []
        for r in rows:
            if cur and r[0] - cur[-1][0] != period:
                if len(cur) >= min_len:
                    out.append((sym, cur))
                cur = []
            cur.append(r)
        if len(cur) >= min_len:
            out.append((sym, cur))
    return out


def score(all_runs, hold, part):
    tiers = {1: [0, 0], 2: [0, 0]}
    bars = 0
    for sym, rows in all_runs:
        n = len(rows)
        cut = int(n * 0.6)
        lo, hi = (40, cut) if part == "train" else (cut, n - hold)
        for i in range(lo, hi):
            bars += 1
            closes = [r[4] for r in rows[max(0, i - 79):i + 1]]
            t, o, h, l, c = rows[i]
            d = decide(closes, o, h, l, c)
            if d is None:
                continue
            side, units, _ = d
            fut = rows[i + hold][4]
            if fut == c:
                continue
            won = (fut > c) == (side == "CALL")
            tiers[units][0 if won else 1] += 1
    return tiers, bars


def simulate(p1, p2, share2, start=100.0, target=120.0, floor=80.0,
             trials=40000, seed=5):
    rng = random.Random(seed)
    hit = ruin = 0
    lengths = []
    for _ in range(trials):
        bal, k = start, 0
        while floor < bal < target and k < 5000:
            two = rng.random() < share2
            stake, p = (2.0, p2) if two else (1.0, p1)
            bal += stake * PAYOUT if rng.random() < p else -stake
            k += 1
        if bal >= target:
            hit += 1
        elif bal <= floor:
            ruin += 1
        lengths.append(k)
    lengths.sort()
    return hit / trials, ruin / trials, lengths[len(lengths) // 2]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="D:/tmp/nn-data/candles60.json.gz")
    args = ap.parse_args()
    rr = runs(args.data)
    be = 1 / (1 + PAYOUT)
    print("=" * 92)
    print(f"CONFLUENCE RULE   {len(rr)} runs   breakeven {be*100:.2f}% at {PAYOUT*100:.1f}%")
    print("=" * 92)
    best = None
    for hold in (1, 3, 5):
        tr, _ = score(rr, hold, "train")
        se, bars = score(rr, hold, "sealed")
        print(f"\n  hold {hold} bar(s) = {hold*60}s expiry")
        print(f"  {'tier':<16}{'train':>16}{'SEALED':>16}{'95% CI':>16}{'n':>6}")
        for units, name in ((1, "3 agree ($1)"), (2, "all 4 ($2)")):
            tw, tl = tr[units]
            sw, sl = se[units]
            lo, hi = wilson(sw, sw + sl)
            trr = tw / (tw + tl) * 100 if tw + tl else 0
            ser = sw / (sw + sl) * 100 if sw + sl else 0
            print(f"  {name:<16}{trr:>15.1f}%{ser:>15.1f}%"
                  f"   [{lo*100:4.1f},{hi*100:4.1f}]{sw+sl:>6}")
        n_all = sum(sum(v) for v in se.values())
        print(f"  signals per 1,000 bars (sealed): {n_all / bars * 1000:.1f}")
        p1 = se[1][0] / max(1, sum(se[1]))
        p2 = se[2][0] / max(1, sum(se[2]))
        share2 = sum(se[2]) / max(1, n_all)
        if best is None or (p1 * (1 - share2) + p2 * share2) > best[1]:
            best = (hold, p1 * (1 - share2) + p2 * share2, p1, p2, share2, n_all)

    hold, blended, p1, p2, share2, n_all = best
    print("\n" + "-" * 92)
    print(f"MISSION ODDS  — best hold {hold} bar(s): $1 tier {p1*100:.1f}%, "
          f"$2 tier {p2*100:.1f}% ({share2*100:.0f}% of entries)")
    print("-" * 92)
    for floor in (80.0, 70.0, 50.0):
        h, r, med = simulate(p1, p2, share2, floor=floor)
        print(f"  floor ${floor:>3.0f}:  reach $120 {h*100:5.1f}%   "
              f"hit floor {r*100:5.1f}%   median trades {med}")
    for p in (0.50, 0.52, 0.55):
        h, r, med = simulate(p, p, share2, floor=80.0)
        print(f"  reference, every trade at {p*100:.0f}%:  reach $120 {h*100:5.1f}%   "
              f"floor $80 {r*100:5.1f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
