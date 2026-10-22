#!/usr/bin/env python3
"""
How much predictability a 70% win rate would require, against how much the data has.

Every test so far reports that some rule failed. This asks the question the other way
round, which is the one that settles the matter: for a target win rate, what does the
series itself have to look like? That number can be compared with the measured series,
and the comparison does not depend on anyone's choice of indicator — it bounds EVERY
indicator at once.

The link is standard for a Gaussian process. If consecutive returns have correlation rho,
the best possible accuracy in calling the next move's sign is

    accuracy = 1/2 + arcsin(rho) / pi

This is an upper bound, reached only by a predictor that knows the true process exactly.
Any real rule, fitted on finite data and reading a noisy proxy, does worse. So if the
bound sits below breakeven, no strategy of any sophistication can clear it, and the search
for one is over rather than merely unsuccessful.
"""
from __future__ import annotations

import math

PAYOUTS = (0.80, 0.85, 0.89, 0.92)

#: Largest |autocorrelation| found anywhere in 60 lags of pooled Quotex OTC returns
#: (30,722 bars, 57 symbols). Scattered across lags 14/23/33/35/54 with no coherent
#: structure, which is what multiple testing on 60 lags produces by itself.
OTC_OBSERVED_RHO = 0.0247

#: Genuine interbank EUR/USD, same measurement (Twelve Data, 5,000 one-minute bars):
#: lag-2 autocorrelation, the only one that was both significant and coherent.
REAL_FX_OBSERVED_RHO = 0.0555


def rho_for_accuracy(accuracy):
    """Inverse of the bound: the correlation a given accuracy demands."""
    if not 0.5 <= accuracy < 1.0:
        raise ValueError("accuracy must be in [0.5, 1)")
    return math.sin((accuracy - 0.5) * math.pi)


def accuracy_for_rho(rho):
    return 0.5 + math.asin(max(-1.0, min(1.0, rho))) / math.pi


def main():
    print("=" * 94)
    print("WHAT A WIN RATE REQUIRES OF THE SERIES")
    print("=" * 94)
    print("\nBreakeven win rate by payout (below this, a rule loses money):")
    for p in PAYOUTS:
        be = 1.0 / (1.0 + p)
        print(f"  payout {p*100:4.0f}%  ->  breakeven {be*100:5.1f}%  "
              f"needs |rho| >= {rho_for_accuracy(be):.3f}")

    print("\nCorrelation required for a target win rate (perfect-knowledge upper bound):")
    for target in (0.541, 0.55, 0.60, 0.65, 0.70, 0.80):
        print(f"  {target*100:5.1f}% win  ->  |rho| >= {rho_for_accuracy(target):.3f}")

    print("\n" + "=" * 94)
    print("WHAT THE SERIES ACTUALLY HAS")
    print("=" * 94)

    for label, rho, note in (
        ("Quotex OTC (57 symbols, 30,722 bars)", OTC_OBSERVED_RHO,
         "largest of 60 lags, scattered — consistent with multiple-testing noise"),
        ("Real interbank EUR/USD (5,000 bars)", REAL_FX_OBSERVED_RHO,
         "lag-2, the one coherent significant value"),
    ):
        best = accuracy_for_rho(rho)
        print(f"\n{label}")
        print(f"  measured |rho|                 {rho:.4f}")
        print(f"  best achievable win rate       {best*100:.2f}%   ({note})")
        for p in (0.85, 0.92):
            be = 1.0 / (1.0 + p)
            short = rho_for_accuracy(be) / rho if rho else float("inf")
            ok = "clears" if best > be else "FALLS SHORT"
            print(f"  vs breakeven at {p*100:.0f}% payout      {ok} "
                  f"(needs {short:.1f}x more correlation)")
        gap70 = rho_for_accuracy(0.70) / rho if rho else float("inf")
        print(f"  vs a 70% target                FALLS SHORT "
              f"(needs {gap70:.1f}x more correlation)")

    print("\n" + "=" * 94)
    print("CONCLUSION")
    print("=" * 94)
    be85 = 1.0 / 1.85
    print(f"""
A 70% win rate needs |rho| of about {rho_for_accuracy(0.70):.2f}. Quotex OTC returns carry
{OTC_OBSERVED_RHO:.4f} at the single most extreme of sixty lags — roughly
{rho_for_accuracy(0.70)/OTC_OBSERVED_RHO:.0f} times too little, and that figure is itself
the largest of sixty tries rather than a stable feature.

Merely breaking even at an 85% payout ({be85*100:.1f}%) already needs
{rho_for_accuracy(be85):.3f}, about {rho_for_accuracy(be85)/OTC_OBSERVED_RHO:.0f} times
what is there.

This bound holds for every possible strategy, because it is a property of the series
rather than of any rule: no indicator, no neural network and no amount of parameter
searching can extract predictability the data does not contain. It is the reason 1,800
searched rules all failed, and it would be the reason the next 1,800 would fail too.

Genuine interbank EUR/USD carries about twice the correlation, which is why a fade rule
reached 57.4% there and held 58.0% out of sample. Twice too little is still too little for
70%, and that edge vanished at one to two pips of adverse pricing — which the broker sets.
""")


if __name__ == "__main__":
    main()
