#!/usr/bin/env python3
"""
Candidate entry rules, and the one the live trader currently uses, measured side by side.

The point of putting them in one file is that they are judged against identical bars by
identical arithmetic. The live rule is included unchanged so its real number is on the
same page as everything proposed to replace it.
"""
from __future__ import annotations

# ---- the rule currently trading real money ------------------------------------------


def live_momentum(ctx):
    """
    What target5000_trader.py does today, reduced to its decision.

    It buys continuation: six or more red bars in the last ten and price under the 20 EMA
    becomes a Put. The 40/15/10/15 scoring around it does not change WHICH side is taken,
    only whether the setup clears 55, so the entry logic is this.
    """
    reds, greens = ctx.reds(10), ctx.greens(10)
    c, e20 = ctx.close(), ctx.ema(20)
    if e20 is None:
        return None
    if reds >= 6 and c <= e20:
        return "put"
    if greens >= 6 and c >= e20:
        return "call"
    return None


# ---- baselines that must be beaten --------------------------------------------------


def always_call(ctx):
    """
    The floor. Any rule that cannot beat 'always buy' is finding nothing.

    On a symbol with drift this scores above 50%, which is exactly why a bare win rate
    means little without it on the same page.
    """
    return "call"


def follow_last_bar(ctx):
    """One bar of momentum, no filters. The cheapest possible edge claim."""
    return "call" if ctx.close() > ctx.open() else "put"


def fade_last_bar(ctx):
    """The mirror. If follow loses, this wins, minus the spread that is not modelled."""
    return "put" if ctx.close() > ctx.open() else "call"


# ---- mean reversion, which is what a synthetic feed usually rewards -----------------


def reversion_z(ctx):
    """
    Fade a move that is large relative to the symbol's own recent volatility.

    Judging a move by standard deviations rather than pips is what lets one threshold
    apply to USDZAR and EURUSD at once; a fixed pip trigger is a different rule on every
    symbol, which is how a filter comes to look tuned when it is merely mismatched.
    """
    z = ctx.z_move(20)
    if z >= 1.5:
        return "put"
    if z <= -1.5:
        return "call"
    return None


def reversion_z_strong(ctx):
    """Same idea, rarer and more extreme. Fewer trades, and the test says whether better."""
    z = ctx.z_move(20)
    if z >= 2.2:
        return "put"
    if z <= -2.2:
        return "call"
    return None


def reversion_rsi(ctx):
    """Classic oversold/overbought fade on a short RSI."""
    r = ctx.rsi(7)
    if r is None:
        return None
    if r >= 80:
        return "put"
    if r <= 20:
        return "call"
    return None


def reversion_rsi_wick(ctx):
    """
    RSI extreme CONFIRMED by a rejection wick.

    The wick is the part that says the extreme was already pushed back against, rather
    than that it merely exists — a trend can sit at RSI 85 for an hour and punish every
    fade taken without it.
    """
    r = ctx.rsi(7)
    if r is None:
        return None
    if r >= 78 and ctx.upper_wick() >= 0.35:
        return "put"
    if r <= 22 and ctx.lower_wick() >= 0.35:
        return "call"
    return None


def reversion_stacked(ctx):
    """
    Three independent conditions agreeing: stretched move, RSI extreme, rejection wick.

    Stacking filters raises the win rate on any fixed sample almost automatically, which
    is the trap — it also shrinks the sample until the number means nothing. The interval
    in the output is what keeps that honest.
    """
    z = ctx.z_move(20)
    r = ctx.rsi(7)
    if r is None:
        return None
    if z >= 1.5 and r >= 70 and ctx.upper_wick() >= 0.25:
        return "put"
    if z <= -1.5 and r <= 30 and ctx.lower_wick() >= 0.25:
        return "call"
    return None


# ---- trend continuation, done properly ----------------------------------------------


def trend_pullback(ctx):
    """
    Trade WITH an established trend, entering on a pause rather than on extension.

    The live rule does the opposite: it enters after six of ten bars have already gone its
    way, which is where a short-horizon move is most likely to have finished.
    """
    e20, e50 = ctx.ema(20), ctx.ema(50)
    if e20 is None or e50 is None:
        return None
    c = ctx.close()
    uptrend = e20 > e50 and c > e50
    downtrend = e20 < e50 and c < e50
    if uptrend and ctx.close() < ctx.open() and ctx.lower_wick() >= 0.25:
        return "call"
    if downtrend and ctx.close() > ctx.open() and ctx.upper_wick() >= 0.25:
        return "put"
    return None


def ema_cross_filtered(ctx):
    """Momentum with a volatility floor: ignore signals while the symbol is barely moving."""
    e20, e50 = ctx.ema(20), ctx.ema(50)
    if e20 is None or e50 is None:
        return None
    if ctx.volatility(20) <= 0:
        return None
    if ctx.body_fraction() < 0.5:
        return None
    if e20 > e50 and ctx.close() > ctx.open():
        return "call"
    if e20 < e50 and ctx.close() < ctx.open():
        return "put"
    return None


ALL = [
    ("live: momentum continuation", live_momentum),
    ("baseline: always call", always_call),
    ("baseline: follow last bar", follow_last_bar),
    ("baseline: fade last bar", fade_last_bar),
    ("reversion: z>=1.5", reversion_z),
    ("reversion: z>=2.2", reversion_z_strong),
    ("reversion: rsi 20/80", reversion_rsi),
    ("reversion: rsi + wick", reversion_rsi_wick),
    ("reversion: z + rsi + wick", reversion_stacked),
    ("trend: pullback entry", trend_pullback),
    ("trend: ema + body filter", ema_cross_filtered),
]
