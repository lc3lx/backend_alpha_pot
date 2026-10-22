#!/usr/bin/env python3
"""
Place demo trades on Quotex, optimising only what measurement says can be optimised.

What this does NOT claim. Direction on these symbols is unpredictable: pooled return
autocorrelation is 0.0247 where breaking even needs 0.127, and 1,800 searched rules all
failed out of sample. So nothing here forecasts up or down, and no setup is called
"strong". Any such label would be invented.

What can genuinely be improved is the arithmetic the trade sits inside:

  * Payout. Breakeven is 1/(1+payout) — 54.1% at 85%, but 51.8% at 93%. Trading only the
    highest-payout open symbols removes 2.3 points of required accuracy without
    predicting anything. This is the one real lever, and the previous bot ignored it,
    taking 85-89% payouts while needing every point it could get.
  * Stake discipline. Flat size. The bot this replaces multiplied the stake 1.5x per loss
    up to $1,441, which converts a small negative edge into a fast total loss.
  * Rule stability. Of everything tested, 'fade a decisive bar' was the only rule whose
    out-of-sample result matched its training result exactly (51.4% / 51.4% on OTC at a
    five-minute hold). It is still below breakeven. It is chosen for not degrading, which
    is the most that can honestly be said for any of them.

Expected outcome, stated before the run so it cannot be reinterpreted afterwards: a small
loss. At 51% actual against 51.8% breakeven on a $50 stake, that is about -$0.40 per
trade. A profit over a handful of trades would be luck — reaching +$500 at these odds
happens about 17% of the time while still losing money in expectation.

Demo only. The script refuses to run if the broker reports a live account.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

GATEWAY = "http://127.0.0.1:8100"
BROKER = "quotex"

#: Below this payout the required accuracy climbs past anything observed. 93% needs
#: 51.8%, 85% needs 54.1%, and the measured rate is ~51%.
MIN_PAYOUT = 92


def gateway_token():
    token = os.environ.get("BROKER_GATEWAY_TOKEN")
    if not token:
        sys.exit("BROKER_GATEWAY_TOKEN not set; source scaralpha.env first")
    return token


def api(path, token, payload=None, timeout=30):
    url = f"{GATEWAY}{path}"
    data = None
    headers = {"X-Gateway-Token": token}
    if payload is not None:
        data = json.dumps(payload).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers,
                                 method="POST" if payload is not None else "GET")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            body = r.read().decode()
            return json.loads(body) if body else {}
    except urllib.error.HTTPError as e:
        detail = e.read().decode()[:500]
        raise RuntimeError(f"{path} -> {e.code}: {detail}") from e


def load_bars(candle_dir, symbol, period=60):
    path = os.path.join(candle_dir, f"{symbol}_{period}.json")
    if not os.path.exists(path):
        return None
    try:
        with open(path, encoding="utf-8") as fh:
            bars = json.load(fh).get("bars", {})
    except (OSError, ValueError):
        return None
    times = sorted(int(k) for k in bars)
    if len(times) < 30:
        return None
    rows = []
    for t in times:
        row = bars[str(t)]
        try:
            rows.append((t, float(row[0]), float(row[1]), float(row[2]), float(row[3])))
        except (TypeError, ValueError, IndexError):
            continue
    return rows or None


def signal(rows, max_age_seconds=180):
    """
    Fade a decisive bar: body at least 60% of its range.

    Returns (side, body_fraction) or None. The body fraction is reported for transparency
    about what triggered, not as a confidence score — it is not one.
    """
    t, o, h, l, c = rows[-1]
    if time.time() - t > max_age_seconds:
        return None
    rng = h - l
    if rng <= 0:
        return None
    body = abs(c - o) / rng
    if body < 0.60:
        return None
    return ("put" if c > o else "call"), body


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--user-id", required=True)
    ap.add_argument("--candle-dir",
                    default="/home/web/backend/brokers/var/candles/quotex")
    ap.add_argument("--stake", type=float, default=50.0)
    ap.add_argument("--duration", type=int, default=300)
    ap.add_argument("--max-trades", type=int, default=3,
                    help="placed per invocation; small by design")
    ap.add_argument("--dry-run", action="store_true",
                    help="analyse and rank, place nothing")
    args = ap.parse_args()

    token = gateway_token()

    balance = api(f"/account/balance?user_id={args.user_id}&broker={BROKER}", token)
    current = str(balance.get("current_type", "")).lower()
    print(f"account: {balance}")
    if "demo" not in current:
        sys.exit(f"REFUSING: broker reports account type {current!r}, not demo. "
                 "This script places trades with a measured negative expectation and "
                 "will not do so on a live account.")
    print(f"confirmed demo, balance ${balance.get('demo', 0):,.2f}\n")

    assets = api(f"/market/assets?user_id={args.user_id}&broker={BROKER}", token)
    rows = assets if isinstance(assets, list) else assets.get("assets", [])
    eligible = [a for a in rows
                if a.get("is_open") and (a.get("payout") or 0) >= MIN_PAYOUT]
    eligible.sort(key=lambda a: -(a.get("payout") or 0))
    print(f"open assets at payout >= {MIN_PAYOUT}%: {len(eligible)}")

    candidates = []
    for a in eligible:
        sym = a.get("symbol")
        payout = a.get("payout") or 0
        bars = load_bars(args.candle_dir, sym)
        if bars is None:
            continue
        sig = signal(bars)
        if sig is None:
            continue
        side, body = sig
        breakeven = 100.0 / (100.0 + payout)
        candidates.append({
            "symbol": sym, "payout": payout, "side": side,
            "body": body, "breakeven": breakeven,
        })

    if not candidates:
        print("\nNo symbol currently shows the entry condition on a fresh bar.")
        print("Nothing placed. That is a normal outcome, not a failure.")
        return 0

    # Ranked by payout, because payout is the only ranking key that is actually
    # informative. Body fraction breaks ties and is NOT a probability.
    candidates.sort(key=lambda c: (-c["payout"], -c["body"]))

    print(f"\n{'symbol':<16}{'payout':>7}{'breakeven':>11}{'side':>6}{'body':>7}")
    print("-" * 50)
    for c in candidates:
        print(f"{c['symbol']:<16}{c['payout']:>6}%{c['breakeven']*100:>10.1f}%"
              f"{c['side']:>6}{c['body']*100:>6.0f}%")

    chosen = candidates[: args.max_trades]
    print(f"\nplacing {len(chosen)} trade(s) at ${args.stake:.0f} flat, "
          f"{args.duration}s expiry")
    print("expected: a small loss. ~51% measured against "
          f"{chosen[0]['breakeven']*100:.1f}% breakeven.\n")

    if args.dry_run:
        print("--dry-run: nothing placed.")
        return 0

    placed = []
    for c in chosen:
        payload = {
            "user_id": args.user_id,
            "broker": BROKER,
            "asset": c["symbol"],
            "amount": args.stake,
            "duration_seconds": args.duration,
            "direction": c["side"].upper(),
        }
        try:
            res = api("/orders", token, payload, timeout=40)
            oid = res.get("order_id") or res.get("id")
            print(f"  PLACED {c['symbol']:<14} {c['side'].upper():<5} "
                  f"${args.stake:.0f}  payout={c['payout']}%  id={oid}")
            placed.append((oid, c))
        except RuntimeError as exc:
            print(f"  FAILED {c['symbol']:<14} {exc}")

    if not placed:
        print("\nNothing was accepted by the broker.")
        return 1

    print(f"\n{len(placed)} order(s) live. Settlement in {args.duration}s.")
    print("Outcomes:")
    for oid, c in placed:
        if not oid:
            continue
        try:
            out = api(
                f"/orders/outcome?user_id={args.user_id}&broker={BROKER}"
                f"&order_id={oid}&timeout_seconds={args.duration + 90}",
                token, timeout=args.duration + 120,
            )
            print(f"  {c['symbol']:<14} {str(out.get('result')):<8} "
                  f"pnl={out.get('profit_loss')}")
        except RuntimeError as exc:
            print(f"  {c['symbol']:<14} outcome unavailable: {exc}")

    print("\nWhatever these show, a handful of trades cannot distinguish a 51% rule from")
    print("a 54% one; that takes ~2,779 trades. Read them as noise.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
