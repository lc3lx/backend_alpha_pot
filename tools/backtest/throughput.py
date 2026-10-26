#!/usr/bin/env python3
"""
How many trades per hour each candidate rule can actually produce — and at what payout.

Picking a rule on win rate alone was about to waste days. The rule that measured best,
RSI(7) outside 15/85, fires on roughly one bar in a hundred; restricted to the symbols
whose payout is high enough to be worth trading, that is a handful of trades an hour, and
5,000 of them is weeks rather than hours. Win rate and reachability have to be read
together, so this prints them side by side.

Two things are measured rather than assumed:

  * SIGNAL RATE, as signals per bar, counted on the recorded candles. Multiplied by the
    number of tradable symbols and the bars-per-hour of the chosen candle size, it gives
    trades per hour directly.

  * THE TRADABLE SET, taken from the live asset list rather than from the candle
    directory. Of 92 assets only 68 are open, and only about two dozen pay 90% or more.
    Trading a 20%-payout asset needs an 83% win rate to break even, so counting it toward
    throughput is counting trades that lose money by construction.

The output answers one question: given the hours available, which configurations can
reach the target, and what does each cost in win rate against its own breakeven.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.request

from backtest import CANDLE_DIR, Ctx, load_series, wilson
from sweep import score, split_series

GATEWAY = "http://127.0.0.1:8100"


def rsi_rule(period, low, high):
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


def wick_rule(threshold):
    def rule(ctx):
        if ctx.upper_wick() >= threshold:
            return "put"
        if ctx.lower_wick() >= threshold:
            return "call"
        return None
    return rule


def body_rule(threshold):
    def rule(ctx):
        if ctx.body_fraction() < threshold:
            return None
        return "put" if ctx.close() > ctx.open() else "call"
    return rule


def rsi_union(specs):
    """
    Any of several RSI extremes fires. Tested as one rule rather than assumed to work.

    The motive is throughput: RSI(5) alone fills about 57 trades an hour, which is three
    days for 5,000. The union of the short-period extremes roughly doubles that. The
    reason it needs testing rather than reasoning is that these rules were chosen BECAUSE
    they measured well, so their union inherits that selection; if the union's sealed rate
    sits below the individuals', the individuals were noise and the union says so.
    """
    rules = [rsi_rule(p, lo, hi) for p, lo, hi in specs]
    def rule(ctx):
        for r in rules:
            side = r(ctx)
            if side is not None:
                return side
        return None
    return rule


CANDIDATES = [
    ("rsi-any",  rsi_union([(5, 15, 85), (7, 20, 80), (7, 15, 85)])),
    ("rsi5+7-20", rsi_union([(5, 15, 85), (7, 20, 80)])),
    ("rsi7",     rsi_rule(7, 15, 85)),
    ("rsi7-20",  rsi_rule(7, 20, 80)),
    ("rsi5",     rsi_rule(5, 15, 85)),
    ("rsi14",    rsi_rule(14, 20, 80)),
    ("wick",     wick_rule(0.65)),
    ("wick-50",  wick_rule(0.50)),
    ("body-60",  body_rule(0.60)),
]


def tradable(user_id, min_payout):
    """Open symbols at or above a payout, from the live table. Empty list on failure."""
    token = os.environ.get("BROKER_GATEWAY_TOKEN")
    if not token or not user_id:
        return None
    req = urllib.request.Request(
        f"{GATEWAY}/market/assets?user_id={user_id}&broker=quotex",
        headers={"X-Gateway-Token": token})
    try:
        with urllib.request.urlopen(req, timeout=25) as r:
            d = json.loads(r.read().decode())
    except Exception as e:
        print(f"  (asset list unavailable: {type(e).__name__}); "
              f"falling back to every recorded symbol", file=sys.stderr)
        return None
    rows = d if isinstance(d, list) else d.get("assets", [])
    return [a.get("symbol") for a in rows
            if a.get("is_open") and (a.get("payout") or 0) >= min_payout]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=CANDLE_DIR)
    ap.add_argument("--period", type=int, default=60)
    ap.add_argument("--hold", type=int, default=5)
    ap.add_argument("--user-id", default=None)
    ap.add_argument("--min-payout", type=int, default=90)
    ap.add_argument("--target", type=int, default=5000)
    ap.add_argument("--min-sealed", type=int, default=100)
    args = ap.parse_args()

    payout = args.min_payout / 100.0
    breakeven = 1.0 / (1.0 + payout)

    everything = load_series(args.dir, period=args.period, min_bars=150)
    if not everything:
        print(f"no series at period {args.period}", file=sys.stderr)
        return 1

    allowed = tradable(args.user_id, args.min_payout)
    if allowed is None:
        series = everything
        set_label = f"all {len({s.symbol for s in series})} recorded symbols"
    else:
        wanted = set(allowed)
        series = [s for s in everything if s.symbol in wanted]
        have = len({s.symbol for s in series})
        set_label = (f"{len(wanted)} open at >={args.min_payout}% payout, "
                     f"{have} of them with candle history")
        if not series:
            print("none of the tradable symbols has candle history", file=sys.stderr)
            return 1

    n_symbols = len({s.symbol for s in series})
    bars_per_hour = 3600.0 / args.period

    print("=" * 108)
    print(f"THROUGHPUT AND WIN RATE TOGETHER   period={args.period}s  "
          f"hold={args.hold} bars ({args.hold*args.period}s expiry)")
    print(f"tradable set: {set_label}")
    print(f"breakeven at {args.min_payout}% payout: {breakeven*100:.2f}%")
    print("=" * 108)

    train, test = split_series(series)
    total_bars = sum(len(s) for s in series)

    rows = []
    for name, rule in CANDIDATES:
        signals = 0
        for s in series:
            for i in range(60, len(s) - args.hold):
                if rule(Ctx(s, i)) is not None:
                    signals += 1
        per_bar = signals / total_bars if total_bars else 0.0
        per_hour = per_bar * n_symbols * bars_per_hour
        tw, tl, _ = score(train, rule, args.hold)
        sw, sl, _ = score(test, rule, args.hold)
        sealed = sw / (sw + sl) if sw + sl else 0.0
        lo, hi = wilson(sw, sw + sl) if sw + sl else (0.0, 0.0)
        hours = args.target / per_hour if per_hour > 0 else float("inf")
        rows.append((per_hour, name, per_bar, sealed, lo, hi, sw + sl,
                     tw / (tw + tl) if tw + tl else 0.0, hours))

    rows.sort(reverse=True)
    print(f"\n{'rule':<10}{'signals/bar':>13}{'trades/hr':>11}{'hrs for ':>9}"
          f"{'SEALED':>9}{'95% CI':>15}{'n':>7}{'train':>8}  verdict")
    print(f"{'':<10}{'':>13}{'':>11}{args.target:>9}")
    print("-" * 108)
    for per_hour, name, per_bar, sealed, lo, hi, n, train_rate, hours in rows:
        if n < args.min_sealed:
            verdict = f"not evaluable (n={n})"
        elif lo > breakeven:
            verdict = "CLEARS breakeven"
        elif sealed > breakeven:
            verdict = f"above, CI straddles (train {train_rate*100:.1f}%)"
        else:
            verdict = "below breakeven"
        hrs = "   n/a" if hours == float("inf") else f"{hours:6.1f}"
        print(f"{name:<10}{per_bar*100:>12.2f}%{per_hour:>11.1f}{hrs:>9}"
              f"{sealed*100:>8.1f}%   [{lo*100:4.1f},{hi*100:4.1f}]{n:>7}"
              f"{train_rate*100:>7.1f}%  {verdict}")
    print("-" * 108)

    reachable = [r for r in rows if r[0] > 0 and args.target / r[0] <= 24
                 and r[6] >= args.min_sealed]
    print(f"\n  Reachable within 24 hours at this payout floor: "
          f"{', '.join(r[1] for r in reachable) if reachable else 'none'}")
    best_rate = max(rows, key=lambda r: r[3] if r[6] >= args.min_sealed else -1)
    print(f"  Highest sealed win rate: {best_rate[1]} at {best_rate[3]*100:.1f}% "
          f"-> {args.target} trades needs "
          f"{'n/a' if best_rate[8]==float('inf') else f'{best_rate[8]:.0f} hours'}")
    print("""
  Read the two columns against each other. A rule that wins more but cannot fill the
  target is not the better choice unless the target itself is negotiable, and a rule
  that fills it while measuring below breakeven only buys a faster loss.""")
    return 0


if __name__ == "__main__":
    sys.exit(main())
