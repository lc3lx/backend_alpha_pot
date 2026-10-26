#!/usr/bin/env python3
"""
Run the review automatically each time a log crosses a 1,000-decided-trade boundary.

The instruction was to review every thousand trades and correct what the review finds.
Doing that by hand means the review happens when someone remembers, which is both late
and selective — and reviewing when a run looks bad while skipping it when a run looks
good is how a losing configuration survives. A boundary that fires on a count cannot be
chosen after seeing the number.

Each review is written to its own file, named for the rule and the count, and never
overwritten. The sequence of them is the record of whether a configuration held up or
decayed, which is the thing the single latest review cannot show.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))


def decided(path):
    """Settled trades in a log. Ties and timeouts carry no directional information."""
    n = 0
    try:
        with open(path, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    r = json.loads(line)
                except ValueError:
                    continue
                v = str(r.get("ledger_verdict") or r.get("result") or "").upper()
                if "WIN" in v or "LOSS" in v or "LOSE" in v:
                    n += 1
    except OSError:
        return 0
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", action="append", required=True,
                    help="a live log to watch; repeatable")
    ap.add_argument("--block", type=int, default=1000)
    ap.add_argument("--out-dir", default="/home/web/backtest/reviews")
    ap.add_argument("--interval", type=int, default=120)
    args = ap.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    done = {}
    for path in args.log:
        # Treat blocks already on disk as reviewed, so a restart does not re-emit history.
        done[path] = decided(path) // args.block

    print(f"[{time.strftime('%H:%M:%S')}] watching "
          f"{', '.join(os.path.basename(p) for p in args.log)} "
          f"for every {args.block} decided trades", flush=True)
    for path, k in done.items():
        print(f"  {os.path.basename(path)}: {decided(path)} decided, "
              f"{k} block(s) already past", flush=True)

    while True:
        for path in args.log:
            n = decided(path)
            block = n // args.block
            if block <= done[path]:
                continue
            done[path] = block
            tag = os.path.basename(path).replace(".jsonl", "")
            out = os.path.join(args.out_dir, f"{tag}_{block*args.block}.txt")
            print(f"[{time.strftime('%H:%M:%S')}] {tag} reached {n} decided; "
                  f"writing {out}", flush=True)
            try:
                res = subprocess.run(
                    [sys.executable, os.path.join(HERE, "review.py"), "--log", path],
                    capture_output=True, text=True, timeout=1800)
                with open(out, "w", encoding="utf-8") as fh:
                    fh.write(res.stdout)
                    if res.stderr.strip():
                        fh.write("\n--- stderr ---\n" + res.stderr)
            except Exception as e:
                print(f"  review failed: {type(e).__name__}: {e}", flush=True)
        time.sleep(args.interval)


if __name__ == "__main__":
    sys.exit(main())
