#!/usr/bin/env python3
"""
"Place as many trades as possible so the day ends profitable."

Those two halves pull in opposite directions, and the arithmetic says so sharply enough
that it is worth seeing rather than being told.

With a negative expectation per trade, the number of trades is not a lever that lifts the
result — it is the dial that decides how CERTAIN the result becomes. Variance is what
makes a profitable day possible at all; more trades removes variance, and with it the only
thing that was ever producing green days. The measured sequence from this account says it
plainly:

      25 trades   +$98.50
     164 trades   -$737.75
     612 trades   -$352.75

The win rate over 612 settled trades was 50.8%, against the 51.9% needed to break even at
the 92-93% payouts traded. So this computes two things exactly:

  1. P(the day ends green) as a function of trade count, at the measured rate. If the aim
     is a green day, this names the trade count that maximises it — and it is small, not
     large.

  2. What a martingale does to that curve. This matters because a martingale genuinely
     DOES make most days green, which is why it is so persuasive, and it is what the
     previous bot on this account ran. It turns "a small loss most days" into "a small
     profit most days, and occasionally the whole account". Expectation does not improve
     by a cent; only its shape changes. That bot settled 1,583 trades at 45.9% for
     -$25,278.
"""
from __future__ import annotations

import math
import random

MEASURED_WIN_RATE = 0.508    # 612 live demo trades, this account
PAYOUT = 0.925               # the 92-93% assets actually traded
STAKE = 25.0
BREAKEVEN = 1.0 / (1.0 + PAYOUT)


def p_day_green(n, win_rate=MEASURED_WIN_RATE, payout=PAYOUT):
    """
    Exact probability that n flat-stake trades finish above zero.

    Green needs wins*payout > losses, i.e. w > n/(1+payout).
    """
    need = n / (1.0 + payout)
    total = 0.0
    for w in range(n + 1):
        if w > need:
            total += math.comb(n, w) * win_rate ** w * (1 - win_rate) ** (n - w)
    return total


def martingale_day(n_trades, win_rate, payout, stake, multiplier, cap, bankroll,
                   trials=60_000, seed=11):
    """
    A day of martingale: stake grows after each loss, resets on a win.

    Reports how often the day ends green AND how often the bankroll is destroyed, because
    quoting the first without the second is the whole illusion.
    """
    rng = random.Random(seed)
    green = ruined = 0
    finals = []
    for _ in range(trials):
        pnl = 0.0
        current = stake
        busted = False
        for _ in range(n_trades):
            if current > bankroll + pnl:       # cannot fund the next step
                busted = True
                break
            if rng.random() < win_rate:
                pnl += current * payout
                current = stake                # reset
            else:
                pnl -= current
                current = min(current * multiplier, cap)
            if bankroll + pnl <= 0:
                busted = True
                break
        if busted:
            ruined += 1
        if pnl > 0:
            green += 1
        finals.append(pnl)
    finals.sort()
    return {
        "green": green / trials,
        "ruined": ruined / trials,
        "mean": sum(finals) / trials,
        "median": finals[trials // 2],
        "worst_1pct": finals[trials // 100],
    }


def main():
    ev = MEASURED_WIN_RATE * PAYOUT - (1 - MEASURED_WIN_RATE)
    print("=" * 96)
    print('"MAXIMUM TRADES SO THE DAY ENDS GREEN" — WHAT THE ARITHMETIC SAYS')
    print("=" * 96)
    print(f"""
Measured on this account, not assumed:
  win rate over 612 settled demo trades   {MEASURED_WIN_RATE*100:.1f}%
  payout on the assets traded             {PAYOUT*100:.1f}%
  breakeven win rate                      {BREAKEVEN*100:.1f}%
  expectation per trade                   {ev*100:+.2f}% of stake  (${ev*STAKE:+.2f} at ${STAKE:.0f})
""")

    print("-" * 96)
    print("1. CHANCE THE DAY ENDS GREEN, BY NUMBER OF TRADES  (flat stake)")
    print("-" * 96)
    print(f"  {'trades':>8}{'P(green)':>12}{'expected P&L':>16}")
    for n in (1, 5, 10, 25, 50, 100, 200, 400, 600, 1000):
        p = p_day_green(n)
        print(f"  {n:>8}{p*100:>11.1f}%{n*ev*STAKE:>15.0f}$")
    print(f"""
  The column goes DOWN as trades go up. More trades does not buy a green day; it
  buys certainty, and what is certain here is the loss. One trade is a near coin
  flip. A thousand is a near-guaranteed loss of about
  ${abs(1000*ev*STAKE):,.0f}.

  So if a green day is the goal and the edge is negative, the trade count that
  maximises it is the SMALLEST one, not the largest. That is the opposite of the
  instruction, and it is not a matter of opinion.
""")

    print("-" * 96)
    print("2. WHAT A MARTINGALE DOES  (how a losing system produces green days)")
    print("-" * 96)
    print("""
  This is the honest answer to "make the day end green by any means": a martingale
  does it, most days. It is also exactly what the previous bot on this account did,
  at 1.5x per loss up to $1,441 stakes, finishing 1,583 trades at 45.9% for -$25,278.
""")
    bankroll = 10_000.0
    for mult, cap, label in ((1.5, 750.0, "1.5x, cap $750 (the old bot)"),
                             (2.0, 1600.0, "2.0x, cap $1,600 (classic)")):
        r = martingale_day(200, MEASURED_WIN_RATE, PAYOUT, STAKE, mult, cap, bankroll)
        print(f"  {label}  —  200 trades on a ${bankroll:,.0f} bankroll")
        print(f"    day ends green           {r['green']*100:5.1f}%   <-- the selling point")
        print(f"    bankroll destroyed       {r['ruined']*100:5.1f}%")
        print(f"    mean day                 ${r['mean']:+,.0f}")
        print(f"    median day               ${r['median']:+,.0f}")
        print(f"    worst 1% of days         ${r['worst_1pct']:+,.0f}")
        print()
    print("""  Read those two lines together. A high green-day rate beside a mean that is
  negative is not a working system — it is a system that pays out small and often,
  then takes everything back at once. The expectation per trade never changed:
  only the shape of the distribution did.
""")

    print("-" * 96)
    print("3. WHY 'USE AI' DOES NOT CHANGE THIS")
    print("-" * 96)
    rho70 = math.sin(0.20 * math.pi)
    print(f"""
  A model can only extract predictability the data contains. The ceiling on sign
  accuracy for any predictor is 1/2 + arcsin(rho)/pi, a property of the series, so
  it constrains a neural network exactly as it constrains RSI:

      measured autocorrelation, Quotex OTC     0.0247
      implies a maximum accuracy of            50.79%
      needed to break even at 92.5% payout     {1.0/(1.0+PAYOUT)*100:.1f}%  (rho {math.sin((BREAKEVEN-0.5)*math.pi):.3f})
      needed for 70%                           rho {rho70:.3f}

  The gap is not an engineering gap. Direct tests agree: all 30 patterns of the last
  one to four bars give P(up) inside 47.8-51.9%, variance ratio is 0.96-1.00, run
  lengths are 0.96-1.03 of a fair coin, and 1,800 searched rules produced fewer
  breakeven-clearing results than chance alone predicts. There is no signal there for
  a model to find.
""")

    print("-" * 96)
    print("4. WHAT WOULD ACTUALLY PRODUCE A GREEN DAY")
    print("-" * 96)
    print(f"""
  Honestly, and in order of how much each is worth:

  a) Stop trading the OTC symbols. They are independently generated streams — they
     cross-correlate at most 0.155, where real pairs sharing a leg reach 0.905. There
     is no market underneath them to have an opinion about.

  b) On real pairs during market hours, genuine short-horizon reversion exists: on
     independent interbank EUR/USD data a fade rule won 57.4% and held 58.0% out of
     sample. But it died at 1-2 pips of adverse pricing, and the broker sets the
     strike — so it is not reachable through this counterparty.

  c) Fewest trades, highest payout, flat stake. That maximises P(green) at
     {p_day_green(5)*100:.0f}% for five trades. It is a coin flip dressed up, and
     across many days it still loses {abs(ev)*100:.1f}% of turnover.

  d) The only configuration that reliably ends days green is the martingale in part 2,
     and its cost is the account. That has already happened once here.
""")


if __name__ == "__main__":
    main()
