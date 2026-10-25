#!/usr/bin/env python3
"""
Pick the best live configuration by measurement, before risking another 2,000 trades.

What 1,010 verified live trades established: fading a decisive bar at a 60-second expiry
wins 50.8% against a 51.3% breakeven. Half a point short. The question is whether any
reachable change closes half a point, and three candidates have evidence behind them
rather than intuition:

  1. The user's own RSI-7 rule. Tested earlier over 1,004 entries it won 52.8%, holding
     52.6% in training and 52.2% sealed. That is better than the rule actually traded
     live, and the stability is what makes it worth a second look. Its weakness is
     frequency: RSI(7) outside 15/85 is rare, so it fires perhaps twenty times a day.

  2. A 300-second expiry. For this same fade rule the earlier split showed 50.6%/49.4%
     at one bar but 51.4%/51.4% at five — better AND identical across the holdout, which
     is the signature of something real rather than fitted.

  3. Payout discipline. Breakeven is 1/(1+payout): 51.3% at 95%, 51.9% at 92.5%. Trading
     only the very top of the payout table lowers the bar without touching the rule.

Everything is scored on the first 60% of each symbol's history and then once on the
sealed 40%. The ranking uses the SEALED column; the training column is shown only to
expose how far each candidate degrades, because the ones that degrade most are exactly
the ones a backtest would otherwise recommend.
"""
from __future__ import annotations

import argparse
import sys

from backtest import ASSUMED_PAYOUT, CANDLE_DIR, Ctx, load_series, wilson
from sweep import score, split_series

#: Payout actually obtainable on the top assets, measured from the live asset list.
LIVE_PAYOUT = 0.95
BREAKEVEN = 1.0 / (1.0 + LIVE_PAYOUT)


# ---- candidates ---------------------------------------------------------------------


def fade_body(threshold):
    def rule(ctx):
        if ctx.body_fraction() < threshold:
            return None
        return "put" if ctx.close() > ctx.open() else "call"
    return rule


def rsi_extreme(period, low, high):
    def rule(ctx):
        r = ctx.rsi(period)
        if r is None:
            return None
        if r <= low:
            return "call"
        if r >= high:
            return "put"
        return None
    return rule


def rsi_plus_body(period, low, high, body):
    """RSI extreme AND a decisive bar — the two independent signals agreeing."""
    def rule(ctx):
        r = ctx.rsi(period)
        if r is None or ctx.body_fraction() < body:
            return None
        if r <= low:
            return "call"
        if r >= high:
            return "put"
        return None
    return rule


def rsi_or_body(period, low, high, body):
    """Either signal fires. Trades far more often; the test says whether that costs."""
    def rule(ctx):
        r = ctx.rsi(period)
        if r is not None:
            if r <= low:
                return "call"
            if r >= high:
                return "put"
        if ctx.body_fraction() >= body:
            return "put" if ctx.close() > ctx.open() else "call"
        return None
    return rule


def fade_wick(threshold):
    """Fade a bar that was already rejected — wick on the side the entry needs."""
    def rule(ctx):
        if ctx.upper_wick() >= threshold:
            return "put"
        if ctx.lower_wick() >= threshold:
            return "call"
        return None
    return rule


CANDIDATES = [
    ("LIVE NOW: fade body>=0.60",      fade_body(0.60)),
    ("fade body>=0.75",                fade_body(0.75)),
    ("fade body>=0.90",                fade_body(0.90)),
    ("rsi7 15/85 (your rule)",         rsi_extreme(7, 15, 85)),
    ("rsi7 20/80",                     rsi_extreme(7, 20, 80)),
    ("rsi7 10/90",                     rsi_extreme(7, 10, 90)),
    ("rsi5 15/85",                     rsi_extreme(5, 15, 85)),
    ("rsi14 20/80",                    rsi_extreme(14, 20, 80)),
    ("rsi7 15/85 AND body>=0.60",      rsi_plus_body(7, 15, 85, 0.60)),
    ("rsi7 20/80 AND body>=0.60",      rsi_plus_body(7, 20, 80, 0.60)),
    ("rsi7 15/85 OR body>=0.75",       rsi_or_body(7, 15, 85, 0.75)),
    ("fade wick>=0.50",                fade_wick(0.50)),
    ("fade wick>=0.65",                fade_wick(0.65)),
]

HOLDS = (1, 3, 5)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=CANDLE_DIR)
    ap.add_argument("--period", type=int, default=60)
    ap.add_argument("--universe", choices=("otc", "real", "all"), default="otc")
    ap.add_argument("--min-sealed", type=int, default=120,
                    help="a candidate with fewer sealed trades is not evaluable")
    args = ap.parse_args()

    everything = load_series(args.dir, period=args.period, min_bars=150)
    if args.universe == "otc":
        series = [s for s in everything if "_otc" in s.symbol]
    elif args.universe == "real":
        series = [s for s in everything if "_otc" not in s.symbol]
    else:
        series = everything
    if not series:
        print("no data", file=sys.stderr)
        return 1

    train, test = split_series(series)
    print("=" * 112)
    print(f"CONFIGURATION SEARCH  universe={args.universe}  "
          f"symbols={len({s.symbol for s in series})}")
    print(f"assumed payout {LIVE_PAYOUT*100:.0f}% (top of the live table)  ->  "
          f"breakeven {BREAKEVEN*100:.2f}%")
    print(f"ranked by SEALED win rate; train shown only to expose degradation")
    print("=" * 112)

    rows = []
    for hold in HOLDS:
        for name, rule in CANDIDATES:
            tw, tl, _ = score(train, rule, hold)
            sw, sl, st = score(test, rule, hold)
            if sw + sl < args.min_sealed:
                continue
            tr_rate = tw / (tw + tl) if tw + tl else 0.0
            se_rate = sw / (sw + sl)
            lo, hi = wilson(sw, sw + sl)
            edge = (sw * LIVE_PAYOUT - sl) / (sw + sl + st)
            rows.append((se_rate, lo, hi, hold, name, tr_rate, tw + tl, sw + sl, edge))

    rows.sort(reverse=True)
    print(f"\n{'rule':<30}{'hold':>5}{'train':>8}{'SEALED':>8}"
          f"{'95% CI':>16}{'n_seal':>8}{'edge':>8}  verdict")
    print("-" * 112)
    for se, lo, hi, hold, name, tr, n_tr, n_se, edge in rows:
        drop = (tr - se) * 100
        if lo > BREAKEVEN:
            verdict = "CLEARS breakeven"
        elif se > BREAKEVEN:
            verdict = f"above, CI straddles (drop {drop:+.1f}pp)"
        else:
            verdict = f"below breakeven (drop {drop:+.1f}pp)"
        print(f"{name:<30}{hold:>5}{tr*100:>7.1f}%{se*100:>7.1f}%"
              f"   [{lo*100:4.1f},{hi*100:4.1f}]{n_se:>8}{edge*100:>+7.1f}%  {verdict}")

    print("-" * 112)
    best = rows[0]
    se, lo, hi, hold, name, tr, n_tr, n_se, edge = best
    print(f"""
BEST BY SEALED RESULT
  rule            {name}
  expiry          {hold * args.period}s
  sealed win      {se*100:.1f}%  (n={n_se})  95% CI [{lo*100:.1f}, {hi*100:.1f}]
  training win    {tr*100:.1f}%  (n={n_tr})   degradation {(tr-se)*100:+.1f}pp
  breakeven       {BREAKEVEN*100:.2f}% at {LIVE_PAYOUT*100:.0f}% payout
  edge            {edge*100:+.1f}% of stake per trade
""")
    if lo > BREAKEVEN:
        print("  Its interval clears breakeven on data it was not selected on. Worth")
        print("  testing live.")
    elif se > BREAKEVEN:
        margin = (se - BREAKEVEN) * 100
        print(f"  It sits {margin:.1f}pp above breakeven but the interval still includes")
        print("  losing, so live testing measures it rather than confirms it.")
    else:
        print("  Nothing in the search clears breakeven out of sample. Any live run is")
        print("  then a measurement, not a strategy.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
