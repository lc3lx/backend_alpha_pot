#!/usr/bin/env python3
"""
Exhaustive search for a binary-options edge, with the one guard that makes a search
mean anything: the winner is chosen on data it is then NOT scored on.

Why that guard decides everything. Test 500 rules against the same bars and the best
will look excellent even if every rule is worthless — with 500 coin-flip rules, roughly
25 clear p<0.05 by luck alone, and the top one looks like a discovery. Reporting that
number is how a backtest comes to promise 70% and a live account delivers 46%.

So the protocol here is fixed before any result is seen:
  1. Split every symbol's history in time: the FIRST 60% trains, the LAST 40% is sealed.
  2. Sweep the whole grid on the training part only.
  3. Take the single best rule by training win rate.
  4. Score it once on the sealed part. THAT number is the answer.
  5. Report the gap. A large drop is the signature of a rule fitted to noise.

Also reported: how many rules clear breakeven in training versus how many would be
expected to by chance, which says whether the search found signal or found its own
multiplicity.
"""
from __future__ import annotations

import argparse
import itertools
import math
import sys

from backtest import ASSUMED_PAYOUT, CANDLE_DIR, Series, Ctx, load_series, wilson


# ---- the grid -----------------------------------------------------------------------
#
# Every rule is one point in a product of independent choices, so the search covers
# combinations a hand-written strategy list would never reach — including the ones that
# contradict the author's expectations, which is the point of sweeping rather than
# proposing.

DIRECTIONS = ("reversion", "momentum")
Z_THRESHOLDS = (0.0, 0.8, 1.2, 1.6, 2.0, 2.5)
RSI_PERIODS = (5, 7, 14)
RSI_BANDS = (None, (30, 70), (25, 75), (20, 80), (15, 85))
WICK_MINIMA = (0.0, 0.2, 0.35, 0.5)
TREND_FILTERS = ("none", "with_ema20", "against_ema20", "ema20_50_aligned")
BODY_MINIMA = (0.0, 0.4, 0.6)


def build_rule(direction, z_min, rsi_period, rsi_band, wick_min, trend_filter, body_min):
    """Compile one grid point into a callable entry rule."""

    def rule(ctx):
        # --- trigger: a stretched move, measured in the symbol's own volatility ---
        z = ctx.z_move(20)
        if z_min > 0 and abs(z) < z_min:
            return None

        # Which way the raw trigger points before any filter.
        if z > 0:
            raw = "put" if direction == "reversion" else "call"
        elif z < 0:
            raw = "call" if direction == "reversion" else "put"
        else:
            return None

        # --- RSI band ---
        if rsi_band is not None:
            r = ctx.rsi(rsi_period)
            if r is None:
                return None
            low, high = rsi_band
            if direction == "reversion":
                # Fade only from an extreme, and only the side the extreme implies.
                if raw == "put" and r < high:
                    return None
                if raw == "call" and r > low:
                    return None
            else:
                # Ride only while NOT already stretched to the opposite extreme.
                if raw == "call" and r > high:
                    return None
                if raw == "put" and r < low:
                    return None

        # --- rejection wick, on the side that would confirm the entry ---
        if wick_min > 0:
            wick = ctx.upper_wick() if raw == "put" else ctx.lower_wick()
            if wick < wick_min:
                return None

        # --- candle body: ignore indecisive bars ---
        if body_min > 0 and ctx.body_fraction() < body_min:
            return None

        # --- trend context ---
        if trend_filter != "none":
            e20 = ctx.ema(20)
            if e20 is None:
                return None
            above = ctx.close() > e20
            if trend_filter == "with_ema20":
                if (raw == "call") != above:
                    return None
            elif trend_filter == "against_ema20":
                if (raw == "call") == above:
                    return None
            elif trend_filter == "ema20_50_aligned":
                e50 = ctx.ema(50)
                if e50 is None:
                    return None
                up = e20 > e50
                if (raw == "call") != up:
                    return None

        return raw

    return rule


def grid():
    for combo in itertools.product(
        DIRECTIONS, Z_THRESHOLDS, RSI_PERIODS, RSI_BANDS,
        WICK_MINIMA, TREND_FILTERS, BODY_MINIMA,
    ):
        direction, z_min, rsi_period, rsi_band, wick_min, trend_filter, body_min = combo
        # Collapse duplicates: the RSI period is meaningless with no band, so sweeping it
        # would re-test one rule three times and inflate the multiplicity count.
        if rsi_band is None and rsi_period != RSI_PERIODS[0]:
            continue
        name = (
            f"{direction[:3]} z>={z_min} "
            f"rsi{rsi_period}{rsi_band if rsi_band else '-'} "
            f"wick>={wick_min} {trend_filter} body>={body_min}"
        )
        yield name, build_rule(*combo)


# ---- scoring ------------------------------------------------------------------------


def score(series_list, rule, hold, min_history=60):
    """Wins, losses, ties for one rule. Decisions use history only; outcomes read ahead."""
    wins = losses = ties = 0
    for s in series_list:
        c = s.c
        for i in range(min_history, len(c) - hold):
            side = rule(Ctx(s, i))
            if side is None:
                continue
            entry, settle = c[i], c[i + hold]
            if settle == entry:
                ties += 1
            elif (settle > entry) == (side == "call"):
                wins += 1
            else:
                losses += 1
    return wins, losses, ties


def split_series(series_list, train_fraction=0.6, min_each=150):
    """Split each symbol in TIME. Never at random: adjacent bars are not independent."""
    train, test = [], []
    for s in series_list:
        cut = int(len(s) * train_fraction)
        if cut < min_each or len(s) - cut < min_each:
            continue
        train.append(Series(s.symbol, s.period, s.times[:cut], s.o[:cut], s.h[:cut],
                            s.l[:cut], s.c[:cut]))
        test.append(Series(s.symbol, s.period, s.times[cut:], s.o[cut:], s.h[cut:],
                           s.l[cut:], s.c[cut:]))
    return train, test


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=CANDLE_DIR)
    ap.add_argument("--period", type=int, default=60)
    ap.add_argument("--hold", type=int, default=1)
    ap.add_argument("--universe", choices=("otc", "real", "all"), default="all")
    ap.add_argument("--min-trades", type=int, default=200,
                    help="a rule with fewer training trades is not evaluable, not promising")
    args = ap.parse_args()

    breakeven = 1.0 / (1.0 + ASSUMED_PAYOUT)

    everything = load_series(args.dir, period=args.period)
    if args.universe == "otc":
        series_list = [s for s in everything if "_otc" in s.symbol]
    elif args.universe == "real":
        series_list = [s for s in everything if "_otc" not in s.symbol]
    else:
        series_list = everything

    if not series_list:
        print(f"No data for universe={args.universe} period={args.period}", file=sys.stderr)
        return 1

    train, test = split_series(series_list)
    if not train:
        print("Not enough history per symbol to split.", file=sys.stderr)
        return 1

    tr_bars = sum(len(s) for s in train)
    te_bars = sum(len(s) for s in test)
    print("=" * 108)
    print(f"UNIVERSE: {args.universe}   period={args.period}s   hold={args.hold} bar(s) "
          f"= {args.hold*args.period}s option")
    print(f"symbols={len({s.symbol for s in train})}  train_bars={tr_bars}  "
          f"sealed_bars={te_bars}")
    print(f"breakeven at {ASSUMED_PAYOUT*100:.0f}% payout = {breakeven*100:.1f}% win rate")
    print("=" * 108)

    rows = []
    for name, rule in grid():
        w, l, t = score(train, rule, args.hold)
        if w + l < args.min_trades:
            continue
        rows.append((name, rule, w, l, t))

    if not rows:
        print("No rule produced enough training trades.", file=sys.stderr)
        return 1

    tested = len(rows)
    clearing = sum(1 for _, _, w, l, _ in rows if w / (w + l) > breakeven)

    # How many SHOULD clear by luck if no rule has an edge. Each rule is a coin flip
    # against breakeven; with ~50% true rate, clearing 54.1% on n trades has a small
    # probability, and tested * that probability is the number to compare against.
    expected_lucky = 0.0
    for _, _, w, l, _ in rows:
        n = w + l
        # Normal approximation to P(win_rate > breakeven | true rate 0.5).
        se = math.sqrt(0.25 / n)
        zscore = (breakeven - 0.5) / se
        expected_lucky += 0.5 * math.erfc(zscore / math.sqrt(2))

    rows.sort(key=lambda r: r[2] / (r[2] + r[3]), reverse=True)

    print(f"\nRules evaluated: {tested}")
    print(f"Rules clearing breakeven IN TRAINING: {clearing}")
    print(f"Expected to clear by pure luck:       {expected_lucky:.1f}")
    if clearing <= expected_lucky * 1.5:
        print("  -> indistinguishable from chance: the search found its own multiplicity,")
        print("     not an edge.")
    else:
        print("  -> more than chance predicts; the sealed-data test below decides.")

    print("\nTop 10 by TRAINING win rate (the number that is NOT trustworthy):")
    print("-" * 108)
    for name, _, w, l, t in rows[:10]:
        n = w + l
        lo, hi = wilson(w, n)
        print(f"  {w/n*100:5.1f}%  n={n:<6} CI=[{lo*100:4.1f},{hi*100:4.1f}]  {name}")

    print("\n" + "=" * 108)
    print("THE ONLY HONEST NUMBER: best training rule, scored once on sealed data")
    print("=" * 108)
    best_name, best_rule, bw, bl, bt = rows[0]
    train_rate = bw / (bw + bl)
    tw, tl, tt = score(test, best_rule, args.hold)
    if tw + tl == 0:
        print(f"Best rule fired no trades on sealed data: {best_name}")
        return 0
    test_rate = tw / (tw + tl)
    lo, hi = wilson(tw, tw + tl)
    edge = (tw * ASSUMED_PAYOUT - tl) / (tw + tl + tt) if (tw + tl + tt) else 0.0

    print(f"rule:      {best_name}")
    print(f"training:  {train_rate*100:5.1f}%  (n={bw+bl})")
    print(f"SEALED:    {test_rate*100:5.1f}%  (n={tw+tl})  95%CI=[{lo*100:4.1f},{hi*100:4.1f}]")
    print(f"drop:      {(train_rate-test_rate)*100:+5.1f} percentage points")
    print(f"edge:      {edge*100:+5.1f}% of stake per trade at {ASSUMED_PAYOUT*100:.0f}% payout")
    print()
    if lo > breakeven:
        print(f"VERDICT: clears breakeven on sealed data even at the pessimistic bound.")
    elif test_rate > breakeven:
        print("VERDICT: above breakeven on sealed data, but the interval includes losing.")
        print("         Not a basis for risking money.")
    else:
        print(f"VERDICT: below the {breakeven*100:.1f}% needed to break even. This rule loses")
        print("         money at this payout, and it was the best of "
              f"{tested} searched.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
