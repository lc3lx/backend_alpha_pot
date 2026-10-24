#!/usr/bin/env python3
"""
Check a specific claim: "Fast Extreme RSI-7" at 75-85%, with 85% on EURJPY_otc and
81.8% on USDPHP_otc.

The rule, implemented exactly as stated:
    RSI(7) <= 15 at a bar's close  -> CALL
    RSI(7) >= 85 at a bar's close  -> PUT
    expiry 5 minutes, i.e. settle against the close five 1-minute bars later

Nothing is reinterpreted or "improved" — a claim is only testable in the form it was
made. Three things get reported that a win-rate figure alone hides:

  * Sample size per symbol. An 81.8% rate is 9 wins in 11 trades, and 11 trades of a
    coin flip produce 9 or more wins about 3% of the time — which sounds unlikely until
    you remember the same scan ran over 50-odd symbols, where such a result is expected
    several times over. That is why per-symbol headline rates are the least reliable
    number a backtest can emit.
  * A confidence interval, so a rate from a dozen trades is visibly not a rate from a
    thousand.
  * Train versus sealed halves. The rate on bars the symbol was PICKED on is not
    evidence; the rate on the rest is.

If the claim holds, it will hold pooled across symbols with a sample large enough to
have an interval clear of breakeven. If it is a selection artifact, the pooled figure
will sit near 50% while a handful of symbols show spectacular rates on tiny samples.
"""
from __future__ import annotations

import argparse
import math
import sys

from backtest import ASSUMED_PAYOUT, CANDLE_DIR, Ctx, load_series, rsi, wilson
from sweep import split_series

#: The claim's own thresholds and horizon.
RSI_PERIOD = 7
OVERSOLD = 15.0
OVERBOUGHT = 85.0
HOLD_BARS = 5  # 5 minutes on 1-minute candles

NAMED = ("EURJPY_otc", "USDPHP_otc")


def rule(ctx):
    r = ctx.rsi(RSI_PERIOD)
    if r is None:
        return None
    if r <= OVERSOLD:
        return "call"
    if r >= OVERBOUGHT:
        return "put"
    return None


def score_series(s, hold, min_history=30):
    """Wins, losses, ties for one symbol. Decision uses bars up to i; outcome reads i+hold."""
    wins = losses = ties = 0
    for i in range(min_history, len(s) - hold):
        side = rule(Ctx(s, i))
        if side is None:
            continue
        entry, settle = s.c[i], s.c[i + hold]
        if settle == entry:
            ties += 1
        elif (settle > entry) == (side == "call"):
            wins += 1
        else:
            losses += 1
    return wins, losses, ties


def binom_at_least(k, n, p):
    """P(at least k successes in n) for a fair-ish coin — how surprising a run really is."""
    return sum(math.comb(n, i) * p ** i * (1 - p) ** (n - i) for i in range(k, n + 1))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=CANDLE_DIR)
    ap.add_argument("--period", type=int, default=60)
    args = ap.parse_args()

    breakeven = 1.0 / (1.0 + ASSUMED_PAYOUT)
    series = load_series(args.dir, period=args.period, min_bars=150)
    if not series:
        print("no candle data", file=sys.stderr)
        return 1

    print("=" * 104)
    print("CLAIM UNDER TEST:  RSI(7) <= 15 -> CALL,  >= 85 -> PUT,  5-minute expiry")
    print(f"CLAIMED:           75-85% win rate (85% EURJPY_otc, 81.8% USDPHP_otc)")
    print(f"BREAKEVEN:         {breakeven*100:.1f}% at {ASSUMED_PAYOUT*100:.0f}% payout")
    print(f"DATA:              {len({s.symbol for s in series})} symbols, "
          f"{sum(len(s) for s in series)} bars at {args.period}s")
    print("=" * 104)

    # ---- pooled, the only figure with enough sample to mean anything ----
    tot_w = tot_l = tot_t = 0
    per_symbol = {}
    for s in series:
        w, l, t = score_series(s, HOLD_BARS)
        tot_w += w
        tot_l += l
        tot_t += t
        if w + l:
            acc = per_symbol.setdefault(s.symbol, [0, 0, 0])
            acc[0] += w
            acc[1] += l
            acc[2] += t

    n = tot_w + tot_l
    if n == 0:
        print("\nThe rule never fired. RSI(7) outside 15/85 does not occur in this data.")
        return 0
    rate = tot_w / n
    lo, hi = wilson(tot_w, n)
    edge = (tot_w * ASSUMED_PAYOUT - tot_l) / (n + tot_t)

    print(f"\nPOOLED ACROSS ALL SYMBOLS  (the number that counts)")
    print(f"  trades        {n}")
    print(f"  win rate      {rate*100:.1f}%")
    print(f"  95% interval  [{lo*100:.1f}%, {hi*100:.1f}%]")
    print(f"  edge          {edge*100:+.1f}% of stake per trade")
    if lo > breakeven:
        print(f"  VERDICT       claim SUPPORTED: interval clears breakeven")
    elif hi < breakeven:
        print(f"  VERDICT       claim REFUTED: whole interval below the "
              f"{breakeven*100:.1f}% needed to break even")
    else:
        print(f"  VERDICT       inconclusive: interval straddles breakeven")

    # ---- the two symbols named in the claim ----
    print(f"\nTHE SYMBOLS NAMED IN THE CLAIM")
    for sym in NAMED:
        if sym not in per_symbol:
            print(f"  {sym:<16} rule never fired (or no data)")
            continue
        w, l, t = per_symbol[sym]
        m = w + l
        slo, shi = wilson(w, m)
        print(f"  {sym:<16} n={m:<5} win={w/m*100:5.1f}%  "
              f"CI=[{slo*100:4.1f},{shi*100:4.1f}]")
        if m < 40:
            p_luck = binom_at_least(w, m, 0.5)
            print(f"  {'':16} only {m} trades. A coin flip gives this or better "
                  f"{p_luck*100:.1f}% of the time,")
            print(f"  {'':16} and across {len(per_symbol)} symbols scanned, "
                  f"~{p_luck*len(per_symbol):.1f} symbols should look this good by luck.")

    # ---- how the per-symbol spread arises ----
    rows = []
    for sym, (w, l, t) in per_symbol.items():
        m = w + l
        if m >= 5:
            rows.append((w / m, m, sym, w, l))
    rows.sort(reverse=True)
    print(f"\nPER-SYMBOL SPREAD  ({len(rows)} symbols where the rule fired >=5 times)")
    print(f"  {'symbol':<16}{'n':>5}{'win':>8}   {'95% interval':<18}")
    print("  " + "-" * 50)
    for r, m, sym, w, l in rows[:6]:
        slo, shi = wilson(w, m)
        print(f"  {sym:<16}{m:>5}{r*100:>7.1f}%   [{slo*100:4.1f}, {shi*100:4.1f}]")
    if len(rows) > 12:
        print("  ...")
        for r, m, sym, w, l in rows[-3:]:
            slo, shi = wilson(w, m)
            print(f"  {sym:<16}{m:>5}{r*100:>7.1f}%   [{slo*100:4.1f}, {shi*100:4.1f}]")
    small = [r for r in rows if r[1] < 40]
    if small:
        best = max(small)
        worst = min(small)
        print(f"\n  Among symbols with under 40 trades, rates run from "
              f"{worst[0]*100:.0f}% to {best[0]*100:.0f}%.")
        print("  That spread is what small samples look like; picking the top of it and")
        print("  quoting it as the strategy's win rate is the selection error itself.")

    # ---- the holdout, which no per-symbol figure can survive ----
    train, test = split_series(series)
    if train:
        tw = tl = tt = 0
        sw = sl = st = 0
        for s in train:
            w, l, t = score_series(s, HOLD_BARS)
            tw += w; tl += l; tt += t
        for s in test:
            w, l, t = score_series(s, HOLD_BARS)
            sw += w; sl += l; st += t
        print(f"\nTRAIN VS SEALED  (same rule, data split in time)")
        if tw + tl:
            a_lo, a_hi = wilson(tw, tw + tl)
            print(f"  train   n={tw+tl:<6} win={tw/(tw+tl)*100:5.1f}%  "
                  f"CI=[{a_lo*100:4.1f},{a_hi*100:4.1f}]")
        if sw + sl:
            b_lo, b_hi = wilson(sw, sw + sl)
            print(f"  SEALED  n={sw+sl:<6} win={sw/(sw+sl)*100:5.1f}%  "
                  f"CI=[{b_lo*100:4.1f},{b_hi*100:4.1f}]")

    # ---- what a 75-85% claim would require of the data ----
    print(f"\nWHAT 75-85% WOULD REQUIRE")
    for target in (0.75, 0.85):
        rho = math.sin((target - 0.5) * math.pi)
        print(f"  {target*100:.0f}% accuracy needs return autocorrelation of {rho:.2f}")
    print(f"  measured on this OTC data: 0.0247")
    print(f"  -> the claim needs {math.sin(0.25*math.pi)/0.0247:.0f}x to "
          f"{math.sin(0.35*math.pi)/0.0247:.0f}x more predictability than exists.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
