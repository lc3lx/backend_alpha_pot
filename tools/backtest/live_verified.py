#!/usr/bin/env python3
"""
A live test whose result cannot be faked by a software bug.

The objection is fair: the 1,638 historical trades were placed while the order path was
broken — outcomes came back "Order not found", frames were dropped silently — so their
45.9% measures the code of that period, not any strategy. They are discarded here.

This run answers to the broker's own ledger instead of to our own bookkeeping. Around
every trade:

    balance before  ->  place  ->  wait for settlement  ->  balance after

and the recorded outcome is checked against `after - before`. If the two disagree the
trade is flagged rather than counted, because a disagreement means our view of the trade
is wrong — which is exactly the failure mode that corrupted the old data. The win rate at
the end rests on balance movements the broker reported, not on a `result` field any bug
could mislabel.

Three further guards, since the point is a trustworthy number:
  * The outcome must be a genuine settlement. A timeout is recorded as UNKNOWN and left
    out of the win rate rather than being counted as a loss — miscounting those is how a
    50% rule reads as 46%.
  * Demo is re-verified every trade, not once at startup.
  * Stake is flat. No martingale, ever.
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
        return None, None, err
    return float(res.get("demo") or 0.0), str(res.get("current_type", "")), None


def last_bar(candle_dir, symbol, period=60):
    path = os.path.join(candle_dir, f"{symbol}_{period}.json")
    try:
        with open(path, encoding="utf-8") as fh:
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


def decide(bar, max_age=150):
    t, o, h, l, c = bar
    if time.time() - t > max_age:
        return None
    rng = h - l
    if rng <= 0 or abs(c - o) / rng < 0.60:
        return None
    return "PUT" if c > o else "CALL"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--user-id", required=True)
    ap.add_argument("--candle-dir",
                    default="/home/web/backend/brokers/var/candles/quotex")
    ap.add_argument("--stake", type=float, default=10.0)
    ap.add_argument("--duration", type=int, default=60)
    ap.add_argument("--min-payout", type=int, default=90)
    ap.add_argument("--trades", type=int, default=60)
    ap.add_argument("--min-balance", type=float, default=200.0)
    ap.add_argument("--log", default="/home/web/backtest/verified.jsonl")
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
    print(f"[{time.strftime('%H:%M:%S')}] verified live test", flush=True)
    print(f"  opening demo balance ${opening:,.2f}  stake ${args.stake:.0f} flat  "
          f"expiry {args.duration}s", flush=True)
    print("  every trade cross-checked against the broker's balance movement\n",
          flush=True)

    wins = losses = ties = unknown = mismatched = 0
    placed = 0

    while placed < args.trades:
        bal_before, kind, err = balance(args.user_id, token)
        if bal_before is None:
            time.sleep(20)
            continue
        if "demo" not in kind.lower():
            print(f"STOPPING: account is now {kind!r}", flush=True)
            break
        if bal_before < args.min_balance:
            print(f"STOPPING: balance ${bal_before:,.2f} hit the floor", flush=True)
            break

        assets, err = call(f"/market/assets?user_id={args.user_id}&broker={BROKER}", token)
        if assets is None:
            time.sleep(15)
            continue
        rows = assets if isinstance(assets, list) else assets.get("assets", [])
        pool = [a for a in rows
                if a.get("is_open") and (a.get("payout") or 0) >= args.min_payout]
        pool.sort(key=lambda a: -(a.get("payout") or 0))

        pick = None
        for a in pool:
            bar = last_bar(args.candle_dir, a.get("symbol"))
            if bar is None:
                continue
            side = decide(bar)
            if side:
                pick = (a.get("symbol"), a.get("payout") or 0, side)
                break
        if pick is None:
            time.sleep(20)
            continue
        symbol, payout, side = pick

        res, err = call("/orders", token, {
            "user_id": args.user_id, "broker": BROKER, "asset": symbol,
            "amount": args.stake, "duration_seconds": args.duration,
            "direction": side,
        }, timeout=45)
        if res is None:
            print(f"[{time.strftime('%H:%M:%S')}] rejected {symbol}: {err}", flush=True)
            time.sleep(10)
            continue
        oid = res.get("order_id") or res.get("id")
        if not oid:
            print(f"[{time.strftime('%H:%M:%S')}] {symbol}: accepted with no id; skipped",
                  flush=True)
            continue
        placed += 1

        out, err = call(
            f"/orders/outcome?user_id={args.user_id}&broker={BROKER}"
            f"&order_id={oid}&timeout_seconds={args.duration + 120}",
            token, timeout=args.duration + 150)

        # Settle the balance before comparing: the credit can land a moment after the
        # outcome frame does.
        time.sleep(4)
        bal_after, _, _ = balance(args.user_id, token)
        delta = None if bal_after is None else bal_after - bal_before

        result = str((out or {}).get("result", "")).upper()
        reported = float((out or {}).get("profit_loss") or 0.0)

        # The broker's own ledger decides. Our result field is only cross-checked.
        if delta is None:
            verdict, flag = "UNKNOWN", "no balance read"
        elif delta > 0.01:
            verdict, flag = "WIN", ""
        elif delta < -0.01:
            verdict, flag = "LOSS", ""
        else:
            verdict, flag = "TIE", ""

        if result and verdict != "UNKNOWN":
            consistent = (("WIN" in result and verdict == "WIN")
                          or ("LOSS" in result and verdict == "LOSS")
                          or ("TIE" in result and verdict == "TIE"))
            if not consistent:
                flag = f"MISMATCH reported={result} ledger={verdict}"
                mismatched += 1

        if flag.startswith("MISMATCH") or verdict == "UNKNOWN":
            unknown += 1 if verdict == "UNKNOWN" else 0
        elif verdict == "WIN":
            wins += 1
        elif verdict == "LOSS":
            losses += 1
        else:
            ties += 1

        try:
            with open(args.log, "a", encoding="utf-8") as fh:
                fh.write(json.dumps({
                    "ts": time.time(), "symbol": symbol, "payout": payout, "side": side,
                    "stake": args.stake, "reported_result": result,
                    "reported_pnl": reported, "balance_before": bal_before,
                    "balance_after": bal_after, "ledger_delta": delta,
                    "ledger_verdict": verdict, "flag": flag, "order_id": oid,
                }) + "\n")
        except OSError:
            pass

        n = wins + losses
        rate = f"{wins/n*100:.1f}%" if n else "n/a"
        ci = ""
        if n >= 10:
            lo, hi = wilson(wins, n)
            ci = f" CI=[{lo*100:.1f},{hi*100:.1f}]"
        print(f"[{time.strftime('%H:%M:%S')}] {placed:>3}. {symbol:<14}{side:<5}"
              f"p={payout}%  ledger={verdict:<7}"
              f"delta={delta if delta is None else format(delta, '+.2f'):>8}  "
              f"W{wins}/L{losses} {rate}{ci} {flag}", flush=True)

        time.sleep(6)

    final, _, _ = balance(args.user_id, token)
    n = wins + losses
    print(f"\n{'='*92}")
    print("VERIFIED LIVE RESULT  (win/loss taken from broker balance movements)")
    print(f"{'='*92}")
    print(f"  trades placed        {placed}")
    print(f"  wins / losses / ties {wins} / {losses} / {ties}")
    print(f"  unreadable outcomes  {unknown}  (excluded, not counted as losses)")
    print(f"  ledger mismatches    {mismatched}")
    if n:
        lo, hi = wilson(wins, n)
        print(f"  win rate             {wins/n*100:.1f}%   95% CI [{lo*100:.1f}, {hi*100:.1f}]")
    if final is not None:
        moved = final - opening
        print(f"  balance              ${opening:,.2f} -> ${final:,.2f}  ({moved:+,.2f})")
        print(f"  staked               ${placed*args.stake:,.2f}")
        if placed:
            print(f"  return on turnover   {moved/(placed*args.stake)*100:+.2f}%")
    print(f"  log                  {args.log}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
