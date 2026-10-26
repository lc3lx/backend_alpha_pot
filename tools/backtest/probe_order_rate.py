#!/usr/bin/env python3
"""
How many orders a second will Quotex actually accept?

Placing every signal in the same instant lost about half of them: the same assets both
succeeded and failed, each failure reporting that nothing but quote traffic came back, so
the broker was dropping orders rather than rejecting them for anything to do with the
instrument. That is a rate limit, and the gateway's retry could not help because the
retries collided too.

Guessing a safe gap would mean either throwing away throughput or keeping the losses, so
this measures it: several small batches, each with a fixed delay between sends, counting
how many were acknowledged. The output is the delay to pace sends at.

Every accepted order is a real demo trade, so the batches are small and the stake is the
minimum. Expiry is short to keep exposure brief; it has nothing to do with the ack path
being measured.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

GATEWAY = "http://127.0.0.1:8100"
BROKER = "quotex"


def call(path, token, payload=None, timeout=60):
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
        return None, f"HTTP {e.code}: {e.read().decode()[:120]}"
    except Exception as e:
        return None, f"{type(e).__name__}: {e}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--user-id", required=True)
    ap.add_argument("--stake", type=float, default=1.0)
    ap.add_argument("--duration", type=int, default=60)
    ap.add_argument("--batch", type=int, default=6, help="orders per gap setting")
    ap.add_argument("--gaps", default="0,0.5,1.0,2.0,3.0",
                    help="seconds between consecutive sends, comma separated")
    ap.add_argument("--min-payout", type=int, default=85)
    ap.add_argument("--settle-wait", type=int, default=20,
                    help="pause between batches so one batch cannot shadow the next")
    args = ap.parse_args()

    token = os.environ.get("BROKER_GATEWAY_TOKEN")
    if not token:
        sys.exit("BROKER_GATEWAY_TOKEN not set")

    bal, err = call(f"/account/balance?user_id={args.user_id}&broker={BROKER}", token)
    if bal is None:
        sys.exit(f"cannot read balance: {err}")
    if "demo" not in str(bal.get("current_type", "")).lower():
        sys.exit(f"REFUSING: account is {bal.get('current_type')!r}, not demo")
    print(f"demo balance ${float(bal.get('demo') or 0):,.2f}", flush=True)

    assets, err = call(f"/market/assets?user_id={args.user_id}&broker={BROKER}", token)
    rows = assets if isinstance(assets, list) else (assets or {}).get("assets", [])
    pool = [a["symbol"] for a in rows
            if a.get("is_open") and (a.get("payout") or 0) >= args.min_payout
            and a.get("symbol")]
    if len(pool) < args.batch:
        sys.exit(f"only {len(pool)} assets open at >={args.min_payout}%; "
                 f"need {args.batch}")
    print(f"{len(pool)} assets open at >={args.min_payout}% payout\n", flush=True)

    gaps = [float(g) for g in args.gaps.split(",")]
    results = []

    for gap in gaps:
        # Different instruments each round, so a per-asset cooldown cannot be mistaken
        # for a global rate limit.
        picks = pool[:args.batch]
        pool = pool[args.batch:] + picks
        sent, ok = 0, 0
        errors = []

        def place(symbol, delay):
            time.sleep(delay)
            t0 = time.time()
            res, e = call("/orders", token, {
                "user_id": args.user_id, "broker": BROKER, "asset": symbol,
                "amount": args.stake, "duration_seconds": args.duration,
                "direction": "call",
            }, timeout=90)
            took = time.time() - t0
            oid = (res or {}).get("order_id") or (res or {}).get("id")
            return symbol, bool(oid), took, e

        with ThreadPoolExecutor(max_workers=args.batch) as pool_ex:
            futs = [pool_ex.submit(place, s, i * gap)
                    for i, s in enumerate(picks)]
            for f in futs:
                symbol, accepted, took, e = f.result()
                sent += 1
                if accepted:
                    ok += 1
                else:
                    errors.append(f"{symbol}: {(e or 'no id')[:70]}")

        rate = ok / sent if sent else 0.0
        results.append((gap, ok, sent, rate))
        print(f"  gap {gap:>4.1f}s  ->  {ok}/{sent} acknowledged "
              f"({rate*100:.0f}%)", flush=True)
        for line in errors[:3]:
            print(f"      {line}", flush=True)
        time.sleep(args.settle_wait)

    print(f"\n{'='*70}")
    print("ORDER PACING")
    print(f"{'='*70}")
    for gap, ok, sent, rate in results:
        bar = "#" * int(rate * 40)
        print(f"  {gap:>4.1f}s  {ok}/{sent}  {rate*100:>3.0f}%  {bar}")
    clean = [r for r in results if r[3] >= 0.95]
    if clean:
        g = min(r[0] for r in clean)
        print(f"\n  Smallest gap that lost nothing: {g}s "
              f"-> about {60.0/max(g,0.001):.0f} orders a minute.")
    else:
        best = max(results, key=lambda r: r[3])
        print(f"\n  Nothing tested was lossless. Best was {best[0]}s at "
              f"{best[3]*100:.0f}%; the limit is beyond the range probed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
