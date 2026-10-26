#!/usr/bin/env python3
"""
Review a block of completed live trades and decide what, if anything, to change.

The obvious way to do this is the wrong way. Slice 1,000 trades by symbol, keep the
symbols that won, drop the ones that lost, and the next thousand will disappoint —
because with fifty symbols and a coin flip, several will show 60% and several 40% purely
by arithmetic. Fitting to that is the same error that produced the "85% on EURJPY_otc"
claim from four trades, and the same one that made 1,800 searched rules look like 35
discoveries when chance alone predicted 53.

So this separates the two jobs that a review has to keep apart:

  GENERATE  — the live trades suggest where a difference might be. Any slice can suggest.
  CONFIRM   — the recorded candles decide, because they are a different sample that the
              live block had no part in selecting.

A change is applied only when the live block flags it AND the candle data reproduces it
with its interval clear of breakeven. Everything else is reported as "noted, not acted
on", which is the honest resting state for most findings.

Each live slice is also tested against the multiple-comparisons count: with k slices
examined, the number that should look significant by luck is k * 0.05, and anything at or
below that is reported as noise however striking the individual number is.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from collections import defaultdict

from backtest import CANDLE_DIR, Ctx, load_series, wilson
from sweep import score, split_series


def load_live(path):
    rows = []
    try:
        with open(path, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    rows.append(json.loads(line))
                except ValueError:
                    continue
    except OSError:
        return []
    out = []
    for r in rows:
        verdict = str(r.get("ledger_verdict") or r.get("result") or "").upper()
        if "WIN" in verdict:
            won = True
        elif "LOSS" in verdict or "LOSE" in verdict:
            won = False
        else:
            continue          # ties and unknowns carry no directional information
        out.append({
            "won": won,
            "symbol": r.get("symbol", "?"),
            "payout": int(r.get("payout") or 0),
            "side": str(r.get("side", "?")).upper(),
            "stake": float(r.get("stake") or 0),
            "pnl": float(r.get("pnl") or r.get("ledger_delta") or 0),
            "ts": float(r.get("ts") or 0),
            "rule": r.get("rule"),
            "duration": int(r.get("duration") or 0),
        })
    return out


def breakeven(payout_pct):
    return 100.0 / (100.0 + payout_pct)


def slice_table(rows, keyfunc, label, min_n=30):
    """Win rate per slice, with the count of slices that chance alone would flag."""
    buckets = defaultdict(lambda: [0, 0, 0.0])
    for r in rows:
        b = buckets[keyfunc(r)]
        b[0 if r["won"] else 1] += 1
        b[2] += r["pnl"]
    table = []
    for key, (w, l, pnl) in buckets.items():
        n = w + l
        if n < min_n:
            continue
        lo, hi = wilson(w, n)
        table.append((key, w, l, n, w / n, lo, hi, pnl))
    if not table:
        print(f"\n  {label}: no slice reached {min_n} trades")
        return []
    table.sort(key=lambda t: -t[4])
    flagged = [t for t in table if t[5] > 0.519 or t[6] < 0.481]
    expected = len(table) * 0.05
    print(f"\n  {label}  ({len(table)} slices with >={min_n} trades)")
    print(f"  {'slice':<18}{'n':>6}{'win':>8}{'95% CI':>16}{'pnl':>9}")
    for key, w, l, n, rate, lo, hi, pnl in table[:6]:
        print(f"  {str(key):<18}{n:>6}{rate*100:>7.1f}%"
              f"   [{lo*100:4.1f},{hi*100:4.1f}]{pnl:>+9.2f}")
    if len(table) > 8:
        print(f"  {'...':<18}")
        for key, w, l, n, rate, lo, hi, pnl in table[-2:]:
            print(f"  {str(key):<18}{n:>6}{rate*100:>7.1f}%"
                  f"   [{lo*100:4.1f},{hi*100:4.1f}]{pnl:>+9.2f}")
    print(f"  slices with an interval clear of 50%: {len(flagged)}  "
          f"(chance alone would give ~{expected:.1f})")
    if len(flagged) <= expected:
        print("  -> nothing here beyond multiple comparisons. No change proposed.")
    return [t[0] for t in flagged]


# ---- confirmation on independent data ----------------------------------------------


def wick_rule(threshold):
    def rule(ctx):
        rng = ctx.high() - ctx.low()
        if rng <= 0:
            return None
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


#: Keyed by the names live_parallel.py writes into each log row, so a review always
#: re-tests the rule that was actually traded. Confirming a rule the run was not using
#: is worse than not confirming at all: it reads as evidence while describing nothing.
RULES = {
    "rsi7": rsi_rule(7, 15, 85),
    "rsi7-20": rsi_rule(7, 20, 80),
    "rsi5": rsi_rule(5, 15, 85),
    "rsi14": rsi_rule(14, 20, 80),
    "wick": wick_rule(0.65),
    "body-60": body_rule(0.60),
    "body-75": body_rule(0.75),
}


def confirm_on_candles(symbols, rule, hold, candle_dir, period=60):
    """
    Re-test the current rule on recorded candles, restricted to the given symbols.

    This is the step that makes a review safe: the candles are a different sample from
    the live block, so a difference the live block merely suggested has to show itself
    again here before anything is changed.
    """
    series = load_series(candle_dir, period=period, min_bars=150)
    if symbols:
        wanted = set(symbols)
        series = [s for s in series if s.symbol in wanted]
    if not series:
        return None
    train, test = split_series(series)
    if not test:
        return None
    w, l, t = score(test, rule, hold)
    if w + l < 60:
        return None
    lo, hi = wilson(w, w + l)
    return {"n": w + l, "rate": w / (w + l), "lo": lo, "hi": hi}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", default="/home/web/backtest/parallel.jsonl")
    ap.add_argument("--candle-dir", default=CANDLE_DIR)
    ap.add_argument("--rule", default=None,
                    help="override the rule read from the log rows")
    ap.add_argument("--hold", type=int, default=None,
                    help="override the hold inferred from the logged expiry")
    ap.add_argument("--period", type=int, default=60)
    ap.add_argument("--block", type=int, default=1000,
                    help="review boundary; blocks are reported separately")
    args = ap.parse_args()

    rows = load_live(args.log)
    if not rows:
        print(f"no decided trades in {args.log}", file=sys.stderr)
        return 1

    # The rule and expiry come from the trades themselves. A review that has to be told
    # what was traded can be told wrong.
    rule_name = args.rule or next((r["rule"] for r in reversed(rows) if r["rule"]), "wick")
    if rule_name not in RULES:
        print(f"unknown rule {rule_name!r}; known: {', '.join(sorted(RULES))}",
              file=sys.stderr)
        return 1
    rule = RULES[rule_name]
    if args.hold is not None:
        hold = args.hold
    else:
        durations = {r["duration"] for r in rows if r["duration"]}
        hold = (max(durations) // args.period) if durations else 1
    hold = max(1, hold)

    w = sum(1 for r in rows if r["won"])
    n = len(rows)
    lo, hi = wilson(w, n)
    pnl = sum(r["pnl"] for r in rows)
    staked = sum(r["stake"] for r in rows)
    advertised = sum(r["payout"] for r in rows) / n

    # Breakeven has to come from what was PAID, not from what the asset list advertised.
    # Quotex quotes payout per expiry, so a 92% asset traded at a 300s expiry can settle
    # at 85%, and the difference is a whole point of breakeven — more than any edge being
    # argued about here.
    paid = [r["pnl"] / r["stake"] for r in rows if r["won"] and r["stake"] > 0]
    realised = (sum(paid) / len(paid) * 100.0) if paid else advertised
    avg_payout = realised
    be = breakeven(avg_payout)

    print("=" * 88)
    print(f"REVIEW OF {n} DECIDED LIVE TRADES")
    print("=" * 88)
    print(f"  rule traded       {rule_name}  at a {hold}-bar "
          f"({hold*args.period}s) expiry")
    print(f"  win rate          {w/n*100:.2f}%   95% CI [{lo*100:.2f}, {hi*100:.2f}]")
    print(f"  payout advertised {advertised:.1f}%   actually paid {realised:.1f}%"
          f"  ->  breakeven {be*100:.2f}%")
    if paid and abs(realised - advertised) >= 1.0:
        print(f"  {'':<17} the asset list overstated payout by "
              f"{advertised-realised:+.1f}pp; breakeven is set from what was paid")
    print(f"  net               {pnl:+.2f} on {staked:.0f} staked "
          f"({pnl/staked*100:+.2f}%)")
    verdict = ("CLEARS breakeven" if lo > be
               else "below breakeven" if hi < be else "straddles breakeven")
    print(f"  verdict           {verdict}")

    # Blocks of `block` trades, so drift over time is visible rather than averaged away.
    print(f"\n  PER BLOCK OF {args.block}")
    for start in range(0, n, args.block):
        chunk = rows[start:start + args.block]
        if len(chunk) < 50:
            continue
        cw = sum(1 for r in chunk if r["won"])
        clo, chi = wilson(cw, len(chunk))
        cpnl = sum(r["pnl"] for r in chunk)
        print(f"    {start+1:>5}-{start+len(chunk):<6} n={len(chunk):<5}"
              f"win={cw/len(chunk)*100:5.1f}%  [{clo*100:4.1f},{chi*100:4.1f}]"
              f"  pnl={cpnl:+8.2f}")

    print("\n" + "-" * 88)
    print("WHERE A DIFFERENCE MIGHT BE  (generated from live trades; none acted on yet)")
    print("-" * 88)
    flagged_symbols = slice_table(rows, lambda r: r["symbol"], "by symbol", min_n=30)
    slice_table(rows, lambda r: f"{r['payout']}%", "by payout", min_n=30)
    slice_table(rows, lambda r: r["side"], "by direction", min_n=30)
    slice_table(rows, lambda r: f"h{int((r['ts'] // 3600) % 24):02d}",
                "by hour of day", min_n=40)

    print("\n" + "-" * 88)
    print("CONFIRMATION ON RECORDED CANDLES  (a different sample; this decides)")
    print("-" * 88)

    base = confirm_on_candles(None, rule, hold, args.candle_dir, args.period)
    if base:
        print(f"\n  current rule ({rule_name}), all symbols, sealed candles:")
        print(f"    n={base['n']}  win={base['rate']*100:.1f}%  "
              f"CI=[{base['lo']*100:.1f},{base['hi']*100:.1f}]  "
              f"vs breakeven {be*100:.2f}%")

    if flagged_symbols:
        sub = confirm_on_candles(flagged_symbols, rule, hold,
                                 args.candle_dir, args.period)
        print(f"\n  restricted to the {len(flagged_symbols)} symbol(s) the live block "
              f"flagged:")
        if sub is None:
            print("    not enough sealed candle data on those symbols to confirm.")
            print("    -> NOT applied. A restriction that cannot be checked is a guess.")
        else:
            print(f"    n={sub['n']}  win={sub['rate']*100:.1f}%  "
                  f"CI=[{sub['lo']*100:.1f},{sub['hi']*100:.1f}]")
            if sub["lo"] > be:
                print("    -> CONFIRMED on independent data. Worth applying.")
            elif base and sub["rate"] > base["rate"] + 0.02:
                print("    -> better than the unrestricted rule but the interval still")
                print("       includes losing. Not applied.")
            else:
                print("    -> not reproduced. The live difference was sampling noise.")
                print("       NOT applied.")
    else:
        print("\n  Nothing was flagged beyond what multiple comparisons predicts, so")
        print("  there is nothing to confirm and no change to make.")

    print("\n" + "=" * 88)
    print("DECISION")
    print("=" * 88)
    if lo > be:
        print("  The rule clears breakeven over the whole block. Keep it unchanged and")
        print("  keep measuring.")
    elif hi < be:
        print("  The rule is below breakeven with its interval clear. It should be")
        print("  replaced, not tuned — run optimise.py to choose the replacement on")
        print("  sealed data rather than on this block.")
    else:
        print("  The block cannot separate the rule from breakeven. The correct action")
        print("  is to keep the configuration and keep accumulating, because any change")
        print("  made now would be fitted to an interval that spans both outcomes.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
