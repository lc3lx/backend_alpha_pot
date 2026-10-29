#!/usr/bin/env python3
"""
Confluence entry: trade only when independent readings agree, size up only when all do.

The specification this serves is a capital-first one — a $100 account, a $20 target, $1
stakes with $2 reserved for the clearest setups, never forced. So the rule is built to
fire rarely and to decline anything ambiguous, and the same decision function is used by
the backtest and by the live trader so the two cannot drift apart.

Four readings, each pointing CALL, PUT or nowhere:

  1. RSI(7) at an extreme          <= 20 -> CALL,  >= 80 -> PUT
  2. close outside Bollinger(20,2) below -> CALL,  above -> PUT
  3. a rejection wick on the bar   lower >= 40% of range -> CALL, upper -> PUT
  4. RSI(5) at a deeper extreme    <= 15 -> CALL,  >= 85 -> PUT

Entry needs 1, 2 and 3 to agree with no reading pointing the other way. All four agreeing
is what earns the $2 stake. Anything else — two of three, a split, an indicator that
cannot be computed — is a skip, not a smaller bet.

These are not independent in the statistical sense — RSI and Bollinger both read the same
closes — and that is the reason the backtest below is the deciding evidence rather than
the number of indicators. Agreement only helps if it raises the win rate, and that is
measured on the sealed 40% of history, never assumed.
"""
from __future__ import annotations

import math


def rsi_last(closes, period):
    if len(closes) < period + 1:
        return None
    ag = al = 0.0
    for i in range(1, period + 1):
        d = closes[i] - closes[i - 1]
        ag += max(d, 0.0)
        al += max(-d, 0.0)
    ag /= period
    al /= period
    for i in range(period + 1, len(closes)):
        d = closes[i] - closes[i - 1]
        ag = (ag * (period - 1) + max(d, 0.0)) / period
        al = (al * (period - 1) + max(-d, 0.0)) / period
    return 100.0 if al == 0 else 100.0 - 100.0 / (1.0 + ag / al)


def bollinger(closes, period=20, k=2.0):
    if len(closes) < period:
        return None
    w = closes[-period:]
    m = sum(w) / period
    sd = math.sqrt(sum((x - m) ** 2 for x in w) / period)
    return m - k * sd, m, m + k * sd


def decide(closes, o, h, l, c):
    """
    ("CALL"|"PUT", stake_units, votes) or None.

    stake_units is 1 when readings 1-3 agree, 2 when all four do.
    """
    rng = h - l
    if rng <= 0 or len(closes) < 21:
        return None
    votes = []

    r7 = rsi_last(closes, 7)
    votes.append(None if r7 is None else ("CALL" if r7 <= 20 else "PUT" if r7 >= 80 else None))

    bb = bollinger(closes)
    if bb is None:
        return None
    lo_band, _, hi_band = bb
    votes.append("CALL" if c < lo_band else "PUT" if c > hi_band else None)

    upper = (h - max(o, c)) / rng
    lower = (min(o, c) - l) / rng
    votes.append("CALL" if lower >= 0.40 else "PUT" if upper >= 0.40 else None)

    r5 = rsi_last(closes, 5)
    votes.append(None if r5 is None else ("CALL" if r5 <= 15 else "PUT" if r5 >= 85 else None))

    core = votes[:3]
    if core[0] is None or core.count(core[0]) != 3:
        return None
    side = core[0]
    opposite = "PUT" if side == "CALL" else "CALL"
    if opposite in votes:
        return None
    units = 2 if votes[3] == side else 1
    return side, units, votes
