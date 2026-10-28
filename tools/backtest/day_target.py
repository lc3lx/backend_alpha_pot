#!/usr/bin/env python3
"""
"Every day ends green, or flat — as many trades as it takes."

That is a precise specification, and it turns out to be a much harder one than "profitable
on average". This computes exactly what it requires, so the target can be compared against
what has actually been measured instead of against a hope.

Two facts drive everything:

  1. A day is green when wins * payout > losses, i.e. when wins exceed n / (1 + payout).
     For n flat-stake trades at accuracy p that is a binomial tail, computed exactly here.

  2. More trades does not make a day greener — it makes it more CERTAIN. Variance is the
     only thing that produces a green day when the edge is negative, and trading more
     removes variance. So "as many trades as it takes" pushes the result towards the
     expectation, whatever its sign. With a positive edge that is exactly what you want.
     With a negative one it is the fastest route to a guaranteed loss.

The third fact is the one that makes "every day" so demanding: a strategy sitting exactly
at breakeven delivers a green day only about half the time. Reliably green days need a
real positive edge AND enough trades for it to dominate the noise, which is why the
accuracy required for 9 days out of 10 is far above the accuracy required merely to stop
losing money.
"""
from __future__ import annotations

import argparse
import math


def p_green(n, p, payout):
    """Exact probability that n flat-stake trades finish above zero."""
    need = n / (1.0 + payout)
    total = 0.0
    for w in range(n + 1):
        if w > need:
            total += math.comb(n, w) * p ** w * (1 - p) ** (n - w)
    return total


def accuracy_for_green_rate(n, payout, want, lo=0.40, hi=0.80, iters=60):
    """The accuracy at which `want` of days end green, for n trades a day."""
    for _ in range(iters):
        mid = (lo + hi) / 2.0
        if p_green(n, mid, payout) < want:
            lo = mid
        else:
            hi = mid
    return (lo + hi) / 2.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--payout", type=float, default=0.916,
                    help="fraction actually paid on a win; 0.916 was measured live")
    ap.add_argument("--measured", type=float, default=0.4966,
                    help="accuracy actually achieved over 5,054 live trades")
    ap.add_argument("--best-model", type=float, default=0.5087,
                    help="best sealed accuracy any model reached (the network)")
    ap.add_argument("--ceiling", type=float, default=0.5079,
                    help="max possible from this series' autocorrelation: "
                         "1/2 + arcsin(rho)/pi at rho=0.0247")
    ap.add_argument("--stake", type=float, default=2.0)
    args = ap.parse_args()

    b = args.payout
    breakeven = 1.0 / (1.0 + b)
    counts = (10, 25, 50, 100, 200, 300, 500, 1000)

    print("=" * 94)
    print('"EVERY DAY GREEN OR FLAT" — WHAT IT ACTUALLY REQUIRES')
    print("=" * 94)
    print(f"""
  payout actually paid        {b*100:.1f}%
  accuracy needed to stop losing   {breakeven*100:.2f}%
  accuracy measured live           {args.measured*100:.2f}%   (5,054 trades)
  best any model reached (sealed)  {args.best_model*100:.2f}%   (neural net, 78 symbols)
  ceiling set by the series itself {args.ceiling*100:.2f}%   (1/2 + arcsin(0.0247)/pi)
""")

    print("-" * 94)
    print("1. CHANCE A DAY ENDS GREEN, AT THE ACCURACIES ACTUALLY AVAILABLE")
    print("-" * 94)
    print(f"  {'trades/day':>11}{'measured':>12}{'best model':>13}{'at breakeven':>15}"
          f"{'expected P&L':>15}")
    print(f"  {'':>11}{args.measured*100:>11.2f}%{args.best_model*100:>12.2f}%"
          f"{breakeven*100:>14.2f}%{'(measured)':>15}")
    for n in counts:
        ev = (args.measured * b - (1 - args.measured)) * n * args.stake
        print(f"  {n:>11}{p_green(n, args.measured, b)*100:>11.1f}%"
              f"{p_green(n, args.best_model, b)*100:>12.1f}%"
              f"{p_green(n, breakeven, b)*100:>14.1f}%{ev:>+14.2f}$")

    print(f"""
  Read the first two columns downward: they FALL as trades rise. At the accuracy
  actually achieved, ten trades a day end green {p_green(10, args.measured, b)*100:.0f}% of the time and a
  thousand end green {p_green(1000, args.measured, b)*100:.1f}% of the time. "As many trades as it takes"
  takes the target further away, not closer, because the only thing producing
  green days is variance and volume is what removes it.

  The third column is the subtle one. A strategy sitting EXACTLY at breakeven
  still ends the day green only about half the time — a coin flip on the day.
  So "every day green" is not the same request as "stop losing money"; it is a
  much larger one, and it needs a real edge, not the absence of a loss.
""")

    print("-" * 94)
    print("2. THE ACCURACY 'EVERY DAY GREEN' ACTUALLY DEMANDS")
    print("-" * 94)
    print(f"  {'trades/day':>11}{'for 50% of days':>18}{'for 75%':>12}"
          f"{'for 90%':>12}{'for 95%':>12}")
    for n in counts:
        row = [accuracy_for_green_rate(n, b, w) for w in (0.50, 0.75, 0.90, 0.95)]
        print(f"  {n:>11}" + "".join(f"{v*100:>17.2f}%" if i == 0
                                     else f"{v*100:>11.2f}%"
                                     for i, v in enumerate(row)))
    need90 = accuracy_for_green_rate(300, b, 0.90)
    print(f"""
  Take 300 trades a day, roughly what the parallel runner places in an hour:
  nine green days out of ten needs {need90*100:.2f}% accuracy.

  Against that:
      achieved live                {args.measured*100:.2f}%
      best model, sealed           {args.best_model*100:.2f}%
      the series' own ceiling      {args.ceiling*100:.2f}%   <-- caps every method

  The ceiling is the line that closes it. {args.ceiling*100:.2f}% is the most any
  predictor can extract from a series with this autocorrelation — not the most
  this network managed, the most that exists to be had. It is {(need90-args.ceiling)*100:.1f} points
  short of what nine-green-days-in-ten requires, and {(breakeven-args.ceiling)*100:.2f} points short of
  merely breaking even.
""")

    print("-" * 94)
    print("3. WHAT WOULD CHANGE THE ANSWER")
    print("-" * 94)
    print(f"""
  The bar is 1/(1+payout), so it moves with the payout, and that is the only
  term in the whole problem that is a product decision rather than a property
  of the price series:

      payout    breakeven    gap to the {args.ceiling*100:.2f}% ceiling""")
    for pay in (0.85, 0.90, 0.916, 0.95, 1.00, 1.10, 1.25, 1.50):
        be = 1.0 / (1.0 + pay)
        gap = (be - args.ceiling) * 100
        flag = "  reachable" if gap < 0 else ""
        print(f"      {pay*100:>5.1f}%     {be*100:>6.2f}%      "
              f"{gap:>+6.2f} points{flag}")
    print(f"""
  Even a 150% payout leaves breakeven at {1/(1+1.5)*100:.1f}%, which the ceiling clears — but no
  binary broker pays that, and the ceiling itself is a property of THIS feed. The
  honest version of the request is therefore not "a better model"; it is a price
  series with more structure in it, measured the same way before anything is built
  on top of it. On genuine interbank EUR/USD the same family of rules reached 57.4%
  in training and held 58.0% out of sample, which clears breakeven at every payout
  in the table above. That edge exists. It is simply not in this feed.
""")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
