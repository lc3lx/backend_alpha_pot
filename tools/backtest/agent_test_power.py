#!/usr/bin/env python3
"""
What a "+$500 on demo" run would actually demonstrate.

The question behind the request is a good one: is this agent worth using to trade? The
proposed experiment cannot answer it, and the reason is worth more than the run would be.

Two separate things get computed:

  1. The chance of reaching +$500 at the win rate MEASURED on this broker's candles. A
     negative-expectation walk still reaches a modest target fairly often before it
     drains — that is what makes it dangerous. A profitable demo run would be evidence of
     luck presented as evidence of skill, and the user has said the result will decide
     whether real money follows.

  2. The sample size needed to tell a 50% rule from a 54.1% rule — breakeven at an 85%
     payout. This is the number that makes a short run uninformative no matter how it
     lands.

Inputs are measured, not assumed: 50.3% comes from 30,722 OTC bars across 57 symbols,
and the payout from the broker's own asset list.
"""
from __future__ import annotations

import math
import random

MEASURED_WIN_RATE = 0.503   # OTC, 30,722 bars, 57 symbols
PAYOUT = 0.85
STAKE = 50.0
START = 10_000.0
TARGET = 500.0              # profit sought
TRIALS = 200_000
MAX_TRADES = 400            # a realistic session: ~33 hours at one 5-minute trade each


def gamblers_ruin(win_rate, target, ruin, stake, payout, max_trades, trials, seed=7):
    """Monte Carlo of the exact proposal: flat stakes until the target, ruin, or fatigue."""
    rng = random.Random(seed)
    win_amount = stake * payout
    hits = ruins = neither = 0
    finals = []
    for _ in range(trials):
        pnl = 0.0
        for _ in range(max_trades):
            pnl += win_amount if rng.random() < win_rate else -stake
            if pnl >= target:
                hits += 1
                break
            if pnl <= -ruin:
                ruins += 1
                break
        else:
            neither += 1
        finals.append(pnl)
    finals.sort()
    return {
        "hit_target": hits / trials,
        "hit_ruin": ruins / trials,
        "neither": neither / trials,
        "mean": sum(finals) / len(finals),
        "median": finals[len(finals) // 2],
        "p10": finals[int(0.10 * len(finals))],
        "p90": finals[int(0.90 * len(finals))],
    }


def trades_needed(p_null, p_alt, alpha=0.05, power=0.80):
    """
    Trades required to distinguish two win rates, at 5% false-positive and 80% power.

    Standard two-proportion sizing. This is the floor: it assumes the rule's true rate is
    fixed, which a market rule's is not.
    """
    z_a = 1.959963985
    z_b = 0.841621234
    p_bar = (p_null + p_alt) / 2
    num = (z_a * math.sqrt(2 * p_bar * (1 - p_bar)) +
           z_b * math.sqrt(p_null * (1 - p_null) + p_alt * (1 - p_alt))) ** 2
    return math.ceil(num / (p_alt - p_null) ** 2)


def main():
    breakeven = 1.0 / (1.0 + PAYOUT)
    ev = MEASURED_WIN_RATE * STAKE * PAYOUT - (1 - MEASURED_WIN_RATE) * STAKE

    print("=" * 92)
    print("WOULD A '+$500 ON DEMO' RUN ANSWER THE QUESTION?")
    print("=" * 92)
    print(f"""
Measured inputs, not guesses:
  win rate on this broker's OTC candles   {MEASURED_WIN_RATE*100:.1f}%  (30,722 bars, 57 symbols)
  payout                                  {PAYOUT*100:.0f}%
  breakeven win rate                      {breakeven*100:.1f}%
  stake                                   ${STAKE:.0f} flat, no martingale
  expected value per trade                ${ev:+.2f}
""")

    print("-" * 92)
    print("1. CHANCE OF REACHING +$500")
    print("-" * 92)
    for ruin in (1_000.0, 2_000.0, 5_000.0):
        r = gamblers_ruin(MEASURED_WIN_RATE, TARGET, ruin, STAKE, PAYOUT,
                          MAX_TRADES, TRIALS)
        print(f"\n  stopping also at -${ruin:,.0f}, up to {MAX_TRADES} trades:")
        print(f"    reaches +$500 first      {r['hit_target']*100:5.1f}%")
        print(f"    reaches -${ruin:,.0f} first   {r['hit_ruin']*100:5.1f}%")
        print(f"    neither within {MAX_TRADES}      {r['neither']*100:5.1f}%")
        print(f"    mean outcome             ${r['mean']:+,.0f}")
        print(f"    median outcome          ${r['median']:+,.0f}")
        print(f"    10th-90th percentile     ${r['p10']:+,.0f} to ${r['p90']:+,.0f}")

    print(f"""
  So the target IS reached a meaningful share of the time — on a rule measured to lose
  ${-ev:.2f} per trade. That is the trap: a run that hits +$500 looks like competence and
  is luck, and the mean outcome stays negative throughout.
""")

    print("-" * 92)
    print("2. HOW MANY TRADES WOULD SETTLE IT")
    print("-" * 92)
    print(f"""
  To distinguish a 50.3% rule from the {breakeven*100:.1f}% needed to break even,
  at 5% false-positive rate and 80% power:

      {trades_needed(MEASURED_WIN_RATE, breakeven):,} trades

  At one 5-minute trade at a time that is about
  {trades_needed(MEASURED_WIN_RATE, breakeven) * 5 / 60 / 24:.0f} days of continuous trading.
  To show a genuinely good 60% rule apart from breakeven still needs
  {trades_needed(breakeven, 0.60):,} trades.

  A session of 20-50 trades has almost no power to separate any of these. Whatever it
  returns — profit or loss — is noise, and reading it either way is the mistake.
""")

    print("-" * 92)
    print("3. THE TEST THAT WAS ALREADY RUN")
    print("-" * 92)
    print(f"""
  The backtests in this directory are that experiment, at a scale live trading cannot
  reach: 35,325 simulated entries on recorded candles, plus an exhaustive 1,800-rule
  search with a sealed holdout. Result: 49.6-51.4%, nothing clearing {breakeven*100:.1f}%.

  And the account that traded live for weeks settled 1,583 trades at 45.9% for
  -$25,278 — consistent with a negative edge, not with an unlucky patch.

  The question "is this agent useful for trading" therefore already has a measured
  answer, from more evidence than any demo run could gather.
""")


if __name__ == "__main__":
    main()
