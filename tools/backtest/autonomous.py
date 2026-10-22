#!/usr/bin/env python3
"""
Trade the demo account continuously, and let the sample settle the question.

Standing authorisation was given to keep trading without asking each time. The most
useful thing to do with that is not more trades — it is enough trades. Twenty-five
decided nothing: 56% with a 95% interval of [37%, 73%], a result that occurs 38% of the
time by luck alone at the rate measured on 35,325 backtested entries. Several hundred
narrows that interval to the point where it either contains breakeven or does not, which
is the empirical answer no amount of argument can supply.

Fixed before the run, so no outcome can be reinterpreted afterwards:

  * Flat stake, always. The account this replaces multiplied 1.5x per loss to $1,441
    stakes; that is the mechanism that turned a small negative edge into -$25,278.
  * Highest-payout open symbols only. Breakeven is 1/(1+payout) — 51.8% at 93% against
    54.1% at 85% — so this removes 2.3 points of required accuracy without forecasting.
  * Demo only. The run aborts if the broker ever reports a live account, and re-checks
    every cycle rather than trusting the first answer.
  * A floor. Trading stops at the configured drawdown instead of grinding the balance to
    nothing; the experiment is finished by then either way.
  * The prediction, recorded now: the rate converges toward 50-52% and the balance
    declines. If instead it holds above breakeven across several hundred trades, that is
    a real finding and the measurements in this directory were wrong about something.

Every trade is appended to a JSONL log, so the tally can be recomputed independently of
anything this process reports.
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
    centre = (p + z * z / (2 * n)) / d
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (max(0.0, centre - half), min(1.0, centre + half))


def api(path, token, payload=None, timeout=30):
    headers = {"X-Gateway-Token": token}
    data = None
    if payload is not None:
        data = json.dumps(payload).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(f"{GATEWAY}{path}", data=data, headers=headers,
                                 method="POST" if payload is not None else "GET")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        body = r.read().decode()
        return json.loads(body) if body else {}


def try_api(path, token, payload=None, timeout=30):
    """Network trouble must pause the loop, never end the run."""
    try:
        return api(path, token, payload, timeout), None
    except urllib.error.HTTPError as e:
        return None, f"HTTP {e.code}: {e.read().decode()[:200]}"
    except Exception as e:  # timeouts, connection resets, malformed JSON
        return None, f"{type(e).__name__}: {e}"


def load_bars(candle_dir, symbol, period=60):
    path = os.path.join(candle_dir, f"{symbol}_{period}.json")
    try:
        with open(path, encoding="utf-8") as fh:
            bars = json.load(fh).get("bars", {})
    except (OSError, ValueError):
        return None
    times = sorted(int(k) for k in bars)
    if len(times) < 5:
        return None
    t = times[-1]
    row = bars[str(t)]
    try:
        return t, float(row[0]), float(row[1]), float(row[2]), float(row[3])
    except (TypeError, ValueError, IndexError):
        return None


def signal(bar, max_age=150):
    """Fade a decisive bar — the only rule whose sealed result matched its training one."""
    t, o, h, l, c = bar
    if time.time() - t > max_age:
        return None
    rng = h - l
    if rng <= 0:
        return None
    if abs(c - o) / rng < 0.60:
        return None
    return "PUT" if c > o else "CALL"


class Tally:
    def __init__(self, path):
        self.path = path
        self.wins = self.losses = self.ties = 0
        self.pnl = 0.0
        self.staked = 0.0

    def record(self, row):
        result = str(row.get("result", "")).lower()
        pnl = float(row.get("pnl") or 0.0)
        if "win" in result:
            self.wins += 1
        elif "loss" in result or "lose" in result:
            self.losses += 1
        else:
            self.ties += 1
        self.pnl += pnl
        self.staked += float(row.get("stake") or 0.0)
        try:
            with open(self.path, "a", encoding="utf-8") as fh:
                fh.write(json.dumps(row) + "\n")
        except OSError:
            pass

    @property
    def decided(self):
        return self.wins + self.losses

    def line(self, breakeven):
        n = self.decided
        if n == 0:
            return "no settled trades yet"
        rate = self.wins / n
        lo, hi = wilson(self.wins, n)
        # Only once the whole interval sits on one side of breakeven does the sample
        # actually say something.
        if lo > breakeven:
            verdict = "above breakeven, interval clear"
        elif hi < breakeven:
            verdict = "BELOW breakeven, interval clear"
        else:
            need = max(0, 2779 - n)
            verdict = f"undecided, interval straddles breakeven (~{need} more to settle)"
        return (f"n={n} W{self.wins}/L{self.losses}/T{self.ties}  "
                f"win={rate*100:.1f}%  CI=[{lo*100:.1f},{hi*100:.1f}]  "
                f"pnl={self.pnl:+.2f}  {verdict}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--user-id", required=True)
    ap.add_argument("--candle-dir",
                    default="/home/web/backend/brokers/var/candles/quotex")
    ap.add_argument("--stake", type=float, default=25.0)
    ap.add_argument("--duration", type=int, default=60)
    ap.add_argument("--min-payout", type=int, default=90)
    ap.add_argument("--per-cycle", type=int, default=3)
    ap.add_argument("--target-trades", type=int, default=600)
    ap.add_argument("--max-drawdown", type=float, default=3000.0,
                    help="stop once the demo balance has fallen this far")
    ap.add_argument("--log", default="/home/web/backtest/autonomous.jsonl")
    args = ap.parse_args()

    token = os.environ.get("BROKER_GATEWAY_TOKEN")
    if not token:
        sys.exit("BROKER_GATEWAY_TOKEN not set; source scaralpha.env first")

    bal, err = try_api(f"/account/balance?user_id={args.user_id}&broker={BROKER}", token)
    if bal is None:
        sys.exit(f"cannot read balance: {err}")
    if "demo" not in str(bal.get("current_type", "")).lower():
        sys.exit(f"REFUSING: account type is {bal.get('current_type')!r}, not demo.")
    start_balance = float(bal.get("demo") or 0.0)

    tally = Tally(args.log)
    print(f"[{time.strftime('%H:%M:%S')}] autonomous demo run starting", flush=True)
    print(f"  balance ${start_balance:,.2f}  stake ${args.stake:.0f} flat  "
          f"expiry {args.duration}s  payout>={args.min_payout}%", flush=True)
    print(f"  target {args.target_trades} trades, stop at -${args.max_drawdown:,.0f}",
          flush=True)
    print("  prediction on record: converges to 50-52%, balance declines.\n", flush=True)

    cycle = 0
    consecutive_errors = 0
    while tally.decided < args.target_trades:
        cycle += 1

        bal, err = try_api(
            f"/account/balance?user_id={args.user_id}&broker={BROKER}", token)
        if bal is None:
            consecutive_errors += 1
            print(f"[{time.strftime('%H:%M:%S')}] balance unavailable ({err}); waiting",
                  flush=True)
            if consecutive_errors >= 10:
                print("too many consecutive failures; stopping.", flush=True)
                break
            time.sleep(30)
            continue
        consecutive_errors = 0

        # Re-checked every cycle: a session can be switched to live underneath us.
        if "demo" not in str(bal.get("current_type", "")).lower():
            print(f"STOPPING: account switched to {bal.get('current_type')!r}.",
                  flush=True)
            break

        current = float(bal.get("demo") or 0.0)
        if start_balance - current >= args.max_drawdown:
            print(f"STOPPING: drawdown ${start_balance - current:,.2f} reached the floor.",
                  flush=True)
            break

        assets, err = try_api(
            f"/market/assets?user_id={args.user_id}&broker={BROKER}", token)
        if assets is None:
            time.sleep(20)
            continue
        rows = assets if isinstance(assets, list) else assets.get("assets", [])
        eligible = [a for a in rows
                    if a.get("is_open") and (a.get("payout") or 0) >= args.min_payout]
        eligible.sort(key=lambda a: -(a.get("payout") or 0))

        picks = []
        for a in eligible:
            bar = load_bars(args.candle_dir, a.get("symbol"))
            if bar is None:
                continue
            side = signal(bar)
            if side is None:
                continue
            picks.append((a.get("symbol"), a.get("payout") or 0, side))
            if len(picks) >= args.per_cycle:
                break

        if not picks:
            time.sleep(25)
            continue

        live = []
        for symbol, payout, side in picks:
            res, err = try_api("/orders", token, {
                "user_id": args.user_id, "broker": BROKER, "asset": symbol,
                "amount": args.stake, "duration_seconds": args.duration,
                "direction": side,
            }, timeout=45)
            if res is None:
                print(f"[{time.strftime('%H:%M:%S')}] reject {symbol} {side}: {err}",
                      flush=True)
                continue
            oid = res.get("order_id") or res.get("id")
            if oid:
                live.append((oid, symbol, payout, side))

        for oid, symbol, payout, side in live:
            out, err = try_api(
                f"/orders/outcome?user_id={args.user_id}&broker={BROKER}"
                f"&order_id={oid}&timeout_seconds={args.duration + 120}",
                token, timeout=args.duration + 150)
            if out is None:
                print(f"[{time.strftime('%H:%M:%S')}] outcome lost {symbol}: {err}",
                      flush=True)
                continue
            tally.record({
                "ts": time.time(), "symbol": symbol, "payout": payout, "side": side,
                "stake": args.stake, "result": out.get("result"),
                "pnl": out.get("profit_loss"), "order_id": oid,
            })

        # Breakeven for the payouts actually traded, not a nominal figure.
        payouts = [p for _, p, _ in picks]
        breakeven = 100.0 / (100.0 + (sum(payouts) / len(payouts))) if payouts else 0.52
        print(f"[{time.strftime('%H:%M:%S')}] cycle {cycle}: {tally.line(breakeven)}",
              flush=True)
        time.sleep(8)

    print(f"\n[{time.strftime('%H:%M:%S')}] run finished after {cycle} cycles", flush=True)
    print(f"FINAL: {tally.line(0.519)}", flush=True)
    if tally.staked:
        print(f"staked ${tally.staked:,.2f}, net {tally.pnl:+,.2f} "
              f"({tally.pnl/tally.staked*100:+.2f}% of turnover)", flush=True)
    print(f"log: {args.log}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
