#!/usr/bin/env python3
"""
Run every candidate rule over the recorded candles and print what each actually won.

Usage:
    python3 run.py                  # 60s bars, 1-bar hold (a 60s option)
    python3 run.py --period 60 --hold 5     # 60s bars, 5-bar hold (a 300s option)
    python3 run.py --split          # first half vs second half, the honesty check
"""
from __future__ import annotations

import argparse
import sys

from backtest import ASSUMED_PAYOUT, CANDLE_DIR, Series, evaluate, load_series, wilson
from strategies import ALL


def describe_data(series_list, period):
    bars = sum(len(s) for s in series_list)
    symbols = len({s.symbol for s in series_list})
    hours = bars * period / 3600
    print(f"Data: {symbols} symbols, {len(series_list)} contiguous runs, {bars} bars "
          f"({hours:.0f} symbol-hours) at {period}s")
    breakeven = 1.0 / (1.0 + ASSUMED_PAYOUT)
    print(f"Breakeven win rate at {ASSUMED_PAYOUT*100:.0f}% payout: {breakeven*100:.1f}%")
    print()


def halve(series_list):
    """Split each run in time. A rule that works on one half and not the other is fitted."""
    first, second = [], []
    for s in series_list:
        mid = len(s) // 2
        if mid < 120:
            continue
        first.append(Series(s.symbol, s.period, s.times[:mid], s.o[:mid], s.h[:mid],
                            s.l[:mid], s.c[:mid]))
        second.append(Series(s.symbol, s.period, s.times[mid:], s.o[mid:], s.h[mid:],
                             s.l[mid:], s.c[mid:]))
    return first, second


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=CANDLE_DIR)
    ap.add_argument("--period", type=int, default=60)
    ap.add_argument("--hold", type=int, default=1,
                    help="bars held; 1 on 60s bars = a 60s option, 5 = a 300s option")
    ap.add_argument("--split", action="store_true",
                    help="also report first half vs second half")
    args = ap.parse_args()

    series_list = load_series(args.dir, period=args.period)
    if not series_list:
        print(f"No candle data at {args.dir} for period {args.period}s.", file=sys.stderr)
        return 1

    describe_data(series_list, args.period)
    print(f"Hold: {args.hold} bar(s) = {args.hold * args.period}s option")
    print("-" * 118)

    rows = []
    for name, rule in ALL:
        rows.append(evaluate(series_list, rule, args.hold, name))

    # Sorted by the lower bound, not the headline: that is the number worth acting on.
    rows.sort(key=lambda r: wilson(r.wins, r.decided)[0], reverse=True)
    for r in rows:
        print(r.line())

    print("-" * 118)
    print("Read the LOWER CI bound, not the win rate. 'PROFITABLE' means even the")
    print("pessimistic end of the interval clears breakeven on this sample.")

    if args.split:
        print()
        print("=" * 118)
        print("IN-SAMPLE vs OUT-OF-SAMPLE (first half of each run vs second half)")
        print("A rule whose two halves disagree was fitted to noise, whatever its total says.")
        print("=" * 118)
        first, second = halve(series_list)
        if not first:
            print("Runs too short to split.")
            return 0
        for name, rule in ALL:
            a = evaluate(first, rule, args.hold, name)
            b = evaluate(second, rule, args.hold, name)
            if a.decided < 20 or b.decided < 20:
                continue
            gap = (a.win_rate - b.win_rate) * 100
            flag = "  <-- UNSTABLE" if abs(gap) > 8 else ""
            print(f"{name:<34} first={a.win_rate*100:5.1f}% (n={a.decided:>4})   "
                  f"second={b.win_rate*100:5.1f}% (n={b.decided:>4})   "
                  f"gap={gap:+5.1f}pp{flag}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
