#!/usr/bin/env python3
"""
Run thousands of demo trades at a rate that makes 5,000 reachable, without giving up the
integrity check that made the last run trustworthy.

The per-trade version read the broker's balance before and after every single trade. That
is the strongest possible check and it caught a real fault — an outcome reported UNKNOWN
that the ledger showed as a paid win — but it costs about seventy seconds per trade, so
5,000 would take four days.

Over 1,010 trades it also established something that buys the speed back: only 2
disagreements, both benign. The reported outcomes are reliable now, so this places several
trades per cycle and reconciles the BATCH against the ledger instead of each trade:

    balance before -> place N -> collect N outcomes -> balance after
    then check  (after - before)  ==  sum(reported P&L)

A systematic mislabelling cannot hide from that — it would show up as cumulative drift
between the two running totals, which is reported every cycle. What the batch check gives
up is knowing WHICH trade in a drifting batch was wrong, and that is an acceptable trade
for a 3x rate once per-trade agreement has already been demonstrated.

Stake is sized so the bankroll survives the whole run even if the rule loses at the rate
measured so far: 5,000 trades at $2 is $10,000 of turnover, and -2% of that is -$200
against a balance near $800.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time
import urllib.error
import urllib.request

GATEWAY = "http://127.0.0.1:8100"
BROKER = "quotex"


def wilson(wins, n, z=1.96):
    if n == 0:
        return (0.0, 0.0)
    p = wins / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (max(0.0, c - h), min(1.0, c + h))


def call(path, token, payload=None, timeout=30):
    headers = {"X-Gateway-Token": token}
    data = None
    if payload is not None:
        data = json.dumps(payload).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(f"{GATEWAY}{path}", data=data, headers=headers,
                                 method="POST" if payload is not None else "GET")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            body = r.read().decode()
            return (json.loads(body) if body else {}), None
    except urllib.error.HTTPError as e:
        return None, f"HTTP {e.code}: {e.read().decode()[:160]}"
    except Exception as e:
        return None, f"{type(e).__name__}: {e}"


def balance(user_id, token):
    res, err = call(f"/account/balance?user_id={user_id}&broker={BROKER}", token)
    if res is None:
        return None, "", err
    return float(res.get("demo") or 0.0), str(res.get("current_type", "")), None


def last_bar(candle_dir, symbol, period=60):
    try:
        with open(os.path.join(candle_dir, f"{symbol}_{period}.json"), encoding="utf-8") as fh:
            bars = json.load(fh).get("bars", {})
    except (OSError, ValueError):
        return None
    times = sorted(int(k) for k in bars)
    if not times:
        return None
    t = times[-1]
    row = bars[str(t)]
    try:
        return t, float(row[0]), float(row[1]), float(row[2]), float(row[3])
    except (TypeError, ValueError, IndexError):
        return None


def decide(bar, rule, threshold, max_age=150):
    """Fade a rejected move ('wick') or a decisive one ('body'). See optimise.py."""
    t, o, h, l, c = bar
    if time.time() - t > max_age:
        return None
    rng = h - l
    if rng <= 0:
        return None
    if rule == "body":
        if abs(c - o) / rng < threshold:
            return None
        return "PUT" if c > o else "CALL"
    upper = (h - max(o, c)) / rng
    lower = (min(o, c) - l) / rng
    if upper >= threshold:
        return "PUT"
    if lower >= threshold:
        return "CALL"
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--user-id", required=True)
    ap.add_argument("--candle-dir",
                    default="/home/web/backend/brokers/var/candles/quotex")
    ap.add_argument("--stake", type=float, default=2.0)
    ap.add_argument("--duration", type=int, default=60)
    ap.add_argument("--min-payout", type=int, default=92)
    ap.add_argument("--rule", choices=("wick", "body"), default="wick")
    ap.add_argument("--threshold", type=float, default=0.65)
    ap.add_argument("--per-cycle", type=int, default=3)
    ap.add_argument("--trades", type=int, default=5000)
    ap.add_argument("--min-balance", type=float, default=250.0)
    ap.add_argument("--log", default="/home/web/backtest/batch.jsonl")
    args = ap.parse_args()

    token = os.environ.get("BROKER_GATEWAY_TOKEN")
    if not token:
        sys.exit("BROKER_GATEWAY_TOKEN not set")

    bal, kind, err = balance(args.user_id, token)
    if bal is None:
        sys.exit(f"cannot read balance: {err}")
    if "demo" not in kind.lower():
        sys.exit(f"REFUSING: account type {kind!r} is not demo")
    opening = bal

    print(f"[{time.strftime('%H:%M:%S')}] batch run: target {args.trades} trades",
          flush=True)
    print(f"  balance ${opening:,.2f}  stake ${args.stake:.2f} flat  "
          f"expiry {args.duration}s  payout>={args.min_payout}%", flush=True)
    print(f"  rule: fade {args.rule} >= {args.threshold}  "
          f"({args.per_cycle} per cycle, ledger reconciled per batch)", flush=True)
    print(f"  projected turnover ${args.trades*args.stake:,.0f}; at -2% that is "
          f"${args.trades*args.stake*0.02:,.0f}\n", flush=True)

    wins = losses = ties = unknown = 0
    placed = 0
    reported_total = 0.0      # what the outcomes claim, cumulative
    ledger_total = 0.0        # what the balance actually moved, cumulative
    errors = 0

    while placed < args.trades:
        bal_before, kind, err = balance(args.user_id, token)
        # A failed read and a real zero are different things, and conflating them ended a
        # run at 443 trades with $766 still in the account: the endpoint returned nothing,
        # it was read as $0.00, and that tripped the floor. Only a figure the broker
        # actually reported can stop the run.
        if bal_before is None or bal_before <= 0.0:
            errors += 1
            if errors >= 15:
                print("too many unusable balance reads; stopping", flush=True)
                break
            print(f"[{time.strftime('%H:%M:%S')}] balance unreadable "
                  f"({err or 'returned 0'}); retrying", flush=True)
            time.sleep(25)
            continue
        errors = 0
        if "demo" not in kind.lower():
            print(f"STOPPING: account is now {kind!r}", flush=True)
            break
        if bal_before < args.min_balance:
            print(f"STOPPING: balance ${bal_before:,.2f} reached the floor", flush=True)
            break

        assets, err = call(f"/market/assets?user_id={args.user_id}&broker={BROKER}", token)
        if assets is None:
            time.sleep(15)
            continue
        rows = assets if isinstance(assets, list) else assets.get("assets", [])
        pool = [a for a in rows
                if a.get("is_open") and (a.get("payout") or 0) >= args.min_payout]
        pool.sort(key=lambda a: -(a.get("payout") or 0))

        picks = []
        for a in pool:
            bar = last_bar(args.candle_dir, a.get("symbol"))
            if bar is None:
                continue
            side = decide(bar, args.rule, args.threshold)
            if side:
                picks.append((a.get("symbol"), a.get("payout") or 0, side))
            if len(picks) >= args.per_cycle:
                break
        if not picks:
            time.sleep(20)
            continue

        live = []
        for symbol, payout, side in picks:
            res, err = call("/orders", token, {
                "user_id": args.user_id, "broker": BROKER, "asset": symbol,
                "amount": args.stake, "duration_seconds": args.duration,
                "direction": side,
            }, timeout=45)
            if res is None:
                continue
            oid = res.get("order_id") or res.get("id")
            if oid:
                live.append((oid, symbol, payout, side))
        if not live:
            time.sleep(10)
            continue

        batch_reported = 0.0
        batch_rows = []
        for oid, symbol, payout, side in live:
            out, err = call(
                f"/orders/outcome?user_id={args.user_id}&broker={BROKER}"
                f"&order_id={oid}&timeout_seconds={args.duration + 120}",
                token, timeout=args.duration + 150)
            result = str((out or {}).get("result", "")).upper()
            pnl = float((out or {}).get("profit_loss") or 0.0)
            if "WIN" in result:
                wins += 1
            elif "LOSS" in result or "LOSE" in result:
                losses += 1
            elif "TIE" in result:
                ties += 1
            else:
                unknown += 1
            batch_reported += pnl
            placed += 1
            batch_rows.append({
                "ts": time.time(), "symbol": symbol, "payout": payout, "side": side,
                "stake": args.stake, "result": result, "pnl": pnl, "order_id": oid,
            })

        # Let the credits land, then reconcile the batch against the ledger.
        time.sleep(4)
        bal_after, _, _ = balance(args.user_id, token)
        if bal_after is not None and bal_after <= 0.0:
            bal_after = None          # unusable, not a wiped account
        batch_ledger = None if bal_after is None else bal_after - bal_before
        if batch_ledger is not None:
            ledger_total += batch_ledger
            reported_total += batch_reported

        for row in batch_rows:
            row["batch_reported"] = round(batch_reported, 2)
            row["batch_ledger"] = None if batch_ledger is None else round(batch_ledger, 2)
            try:
                with open(args.log, "a", encoding="utf-8") as fh:
                    fh.write(json.dumps(row) + "\n")
            except OSError:
                pass

        n = wins + losses
        drift = reported_total - ledger_total
        lo, hi = wilson(wins, n) if n >= 30 else (0.0, 0.0)
        ci = f" CI=[{lo*100:.1f},{hi*100:.1f}]" if n >= 30 else ""
        flag = ""
        if abs(drift) > max(5.0, args.stake * 2):
            flag = f"  DRIFT reported-ledger={drift:+.2f}"
        print(f"[{time.strftime('%H:%M:%S')}] {placed:>5}/{args.trades}  "
              f"W{wins}/L{losses}/T{ties}/U{unknown}  "
              f"win={wins/n*100 if n else 0:.1f}%{ci}  "
              f"ledger={ledger_total:+.2f}  bal=${bal_after if bal_after else 0:,.2f}{flag}",
              flush=True)
        time.sleep(3)

    final, _, _ = balance(args.user_id, token)
    n = wins + losses
    print(f"\n{'='*96}")
    print(f"BATCH RUN COMPLETE")
    print(f"{'='*96}")
    print(f"  trades placed        {placed}")
    print(f"  W / L / T / unknown  {wins} / {losses} / {ties} / {unknown}")
    if n:
        lo, hi = wilson(wins, n)
        for payout, label in ((0.95, "95%"), (0.925, "92.5%")):
            be = 1 / (1 + payout)
            state = ("CLEARS" if lo > be else ("below" if hi < be else "straddles"))
            print(f"  vs breakeven at {label:<6} {be*100:.2f}%  ->  {state}")
        print(f"  win rate             {wins/n*100:.2f}%   95% CI [{lo*100:.2f}, {hi*100:.2f}]")
    print(f"  ledger movement      {ledger_total:+.2f}")
    print(f"  reported total       {reported_total:+.2f}")
    print(f"  drift                {reported_total - ledger_total:+.2f}  "
          f"(large drift invalidates the reported figures)")
    if final is not None:
        print(f"  balance              ${opening:,.2f} -> ${final:,.2f}")
    if placed:
        turnover = placed * args.stake
        print(f"  turnover             ${turnover:,.2f}")
        print(f"  return on turnover   {ledger_total/turnover*100:+.3f}%")
    print(f"  log                  {args.log}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
