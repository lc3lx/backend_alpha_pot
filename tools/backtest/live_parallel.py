#!/usr/bin/env python3
"""
Trade every symbol that signals, at the same time, instead of one at a time.

This exists because the rule that measured best is a rare one. RSI(7) leaving 15/85 fires
perhaps twenty times a day on a single symbol, so the sequential runner would need weeks
to settle 5,000 trades on it. Breadth fixes what rate cannot: fifty-odd OTC symbols are
open at once, each produces a closed bar every minute, and the ones that signal are
independent of each other. Scanning all of them and entering every signal in the same
second turns a rare rule into a fast one without loosening the rule itself.

Three things had to change to stay honest about it:

  * ENTRY TIMING. The backtest enters at the close of the deciding bar. Mid-bar, that
    price no longer exists, so a signal is only taken within `--max-age` seconds of the
    bar actually closing. A stale signal is skipped rather than entered late, because an
    entry at a drifted price is measuring a different rule than the one that was tested.

  * THE LEDGER CHECK. Balance-before / balance-after cannot isolate a trade when forty
    are in flight. It is replaced by a continuous identity that holds at every instant,
    which is strictly stronger than the per-batch version:

        expected = opening - (stake in flight) + (settled P&L)

    Quotex debits the stake on placement and returns stake+profit on settlement, so this
    must track the real balance at all times. Any systematic mislabelling shows up as
    drift between the two, reported every cycle, with no quiet moment needed.

  * CONCURRENT EXPOSURE. Forty open trades at $2 is $80 at risk simultaneously, which the
    bankroll has to be able to lose without the floor being hit mid-wave. `--max-inflight`
    bounds it, and the floor is checked against in-flight exposure rather than balance
    alone.

Unchanged from the sequential runner, because they are what make the numbers worth
having: flat stake and no martingale, demo re-verified every cycle, timeouts recorded as
UNKNOWN and excluded rather than counted as losses, and a failed balance read treated as
a failed read instead of as a zero.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys
import threading
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

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


# ---- indicators: identical arithmetic to backtest.py, so live measures the same rule --


def rsi_last(closes, period):
    """Wilder's RSI at the final close. Returns None when there is too little history."""
    if len(closes) < period + 1:
        return None
    avg_g = avg_l = 0.0
    for i in range(1, period + 1):
        d = closes[i] - closes[i - 1]
        avg_g += d if d > 0 else 0.0
        avg_l += -d if d < 0 else 0.0
    avg_g /= period
    avg_l /= period
    for i in range(period + 1, len(closes)):
        d = closes[i] - closes[i - 1]
        g = d if d > 0 else 0.0
        l = -d if d < 0 else 0.0
        avg_g = (avg_g * (period - 1) + g) / period
        avg_l = (avg_l * (period - 1) + l) / period
    if avg_l == 0:
        return 100.0
    return 100.0 - 100.0 / (1.0 + avg_g / avg_l)


# ---- the deciding bar ----------------------------------------------------------------


def window(candle_dir, symbol, period, want=80, min_bars=40):
    """
    The last `want` CLOSED bars, plus how long ago the newest of them closed.

    The newest row in the file is usually the bar still forming; including it would decide
    on a partial bar, which is not what was measured. It is dropped, and the age returned
    is measured from the moment the deciding bar actually closed.

    `min_bars` is a COUNT of bars and is deliberately not derived from `period`, which is
    a size in seconds. Tying the two would make a 300s scan wait for 302 bars of 5-minute
    history — a day and a night — before it could trade at all.
    """
    path = os.path.join(candle_dir, f"{symbol}_{period}.json")
    try:
        with open(path, encoding="utf-8") as fh:
            bars = json.load(fh).get("bars", {})
    except (OSError, ValueError):
        return None
    times = sorted(int(k) for k in bars)
    if len(times) < 3:
        return None

    now = time.time()
    if now - times[-1] < period:
        times = times[:-1]          # newest row is still forming
    if not times:
        return None
    closed_at = times[-1] + period
    age = now - closed_at

    # Walk back from the deciding bar for as long as the bars are adjacent in time. An
    # indicator must not be computed across a hole — it would read prices from either side
    # of it as consecutive — but requiring the whole 80-bar window to be clean threw away
    # symbols that had one gap an hour ago and were perfectly usable now. Taking the
    # contiguous tail keeps the same guarantee and roughly doubles the symbols in play.
    sel = [times[-1]]
    for t in reversed(times[:-1]):
        if sel[-1] - t != period or len(sel) >= want:
            break
        sel.append(t)
    sel.reverse()
    if len(sel) < min_bars:
        return None

    o = h = l = c = None
    closes = []
    for t in sel:
        row = bars[str(t)]
        try:
            o, h, l, c = float(row[0]), float(row[1]), float(row[2]), float(row[3])
        except (TypeError, ValueError, IndexError):
            return None
        closes.append(c)
    return {"bar_time": sel[-1], "age": age, "closes": closes,
            "o": o, "h": h, "l": l, "c": c}


# ---- rules ---------------------------------------------------------------------------


def make_rule(name):
    """
    Entry rules, keyed by the name used in optimise.py so the two cannot drift apart.

    `rsi7` is the live default: on the sealed 40% of 46,226 bars it won 59.4% over 187
    entries at a five-bar hold, the only candidate whose interval cleared breakeven. The
    caveat is recorded where it belongs — it IMPROVED 12pp from its training rate, which
    is the signature of variance as much as of edge, so this run is a measurement of it
    and not a bet on it.
    """
    def rsi_rule(period, low, high):
        def rule(w):
            r = rsi_last(w["closes"], period)
            if r is None:
                return None
            if r <= low:
                return "CALL"
            if r >= high:
                return "PUT"
            return None
        return rule

    def wick_rule(threshold):
        def rule(w):
            rng = w["h"] - w["l"]
            if rng <= 0:
                return None
            if (w["h"] - max(w["o"], w["c"])) / rng >= threshold:
                return "PUT"
            if (min(w["o"], w["c"]) - w["l"]) / rng >= threshold:
                return "CALL"
            return None
        return rule

    def body_rule(threshold):
        def rule(w):
            rng = w["h"] - w["l"]
            if rng <= 0 or abs(w["c"] - w["o"]) / rng < threshold:
                return None
            return "PUT" if w["c"] > w["o"] else "CALL"
        return rule

    table = {
        "rsi7": rsi_rule(7, 15, 85),
        "rsi7-20": rsi_rule(7, 20, 80),
        "rsi5": rsi_rule(5, 15, 85),
        "rsi14": rsi_rule(14, 20, 80),
        "wick": wick_rule(0.65),
        "body-60": body_rule(0.60),
        "body-75": body_rule(0.75),
    }
    if name not in table:
        sys.exit(f"unknown rule {name!r}; choose from {', '.join(sorted(table))}")
    return table[name]


# ---- book: the numbers every thread shares -------------------------------------------


class Book:
    """Counters and the ledger identity, under one lock."""

    def __init__(self, opening, stake):
        self.lock = threading.Lock()
        self.opening = opening
        self.stake = stake
        self.wins = self.losses = self.ties = self.unknown = 0
        self.placed = 0
        self.inflight = 0
        self.settled_pnl = 0.0

    def on_place(self):
        with self.lock:
            self.placed += 1
            self.inflight += 1

    def on_settle(self, result, pnl):
        with self.lock:
            self.inflight -= 1
            if "WIN" in result:
                self.wins += 1
            elif "LOSS" in result or "LOSE" in result:
                self.losses += 1
            elif "TIE" in result:
                self.ties += 1
            else:
                self.unknown += 1
                return                       # an unsettled trade's P&L is not known
            self.settled_pnl += pnl

    def expected(self):
        with self.lock:
            return self.opening - self.inflight * self.stake + self.settled_pnl

    def snapshot(self):
        with self.lock:
            return (self.placed, self.wins, self.losses, self.ties, self.unknown,
                    self.inflight, self.settled_pnl)


def trade(args, token, book, log_lock, symbol, payout, side, bar_time):
    """Place one option and wait for it alone. Runs in its own thread."""
    res, err = call("/orders", token, {
        "user_id": args.user_id, "broker": BROKER, "asset": symbol,
        "amount": args.stake, "duration_seconds": args.duration,
        "direction": side,
    }, timeout=45)
    if res is None:
        return
    oid = res.get("order_id") or res.get("id")
    if not oid:
        return
    book.on_place()
    placed_at = time.time()

    out, err = call(
        f"/orders/outcome?user_id={args.user_id}&broker={BROKER}"
        f"&order_id={oid}&timeout_seconds={args.duration + 120}",
        token, timeout=args.duration + 150)
    result = str((out or {}).get("result", "")).upper()
    pnl = float((out or {}).get("profit_loss") or 0.0)
    book.on_settle(result, pnl)

    row = {"ts": placed_at, "symbol": symbol, "payout": payout, "side": side,
           "stake": args.stake, "duration": args.duration, "rule": args.rule,
           "bar_time": bar_time, "result": result, "pnl": pnl, "order_id": oid}
    with log_lock:
        try:
            with open(args.log, "a", encoding="utf-8") as fh:
                fh.write(json.dumps(row) + "\n")
        except OSError:
            pass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--user-id", required=True)
    ap.add_argument("--candle-dir",
                    default="/home/web/backend/brokers/var/candles/quotex")
    ap.add_argument("--period", type=int, default=60, help="candle size used to decide")
    ap.add_argument("--stake", type=float, default=2.0)
    ap.add_argument("--duration", type=int, default=300,
                    help="expiry in seconds; 300 matches the 5-bar hold that measured best")
    ap.add_argument("--min-payout", type=int, default=85)
    ap.add_argument("--rule", default="rsi7")
    ap.add_argument("--max-age", type=float, default=25.0,
                    help="skip a signal this many seconds after its bar closed")
    ap.add_argument("--max-inflight", type=int, default=40)
    ap.add_argument("--workers", type=int, default=24)
    ap.add_argument("--trades", type=int, default=5000)
    ap.add_argument("--min-balance", type=float, default=250.0)
    ap.add_argument("--log", default="/home/web/backtest/parallel.jsonl")
    args = ap.parse_args()

    token = os.environ.get("BROKER_GATEWAY_TOKEN")
    if not token:
        sys.exit("BROKER_GATEWAY_TOKEN not set")

    rule = make_rule(args.rule)
    hold = args.duration // args.period

    bal, kind, err = balance(args.user_id, token)
    if bal is None:
        sys.exit(f"cannot read balance: {err}")
    if "demo" not in kind.lower():
        sys.exit(f"REFUSING: account type {kind!r} is not demo")

    book = Book(bal, args.stake)
    log_lock = threading.Lock()
    seen = {}                 # symbol -> bar_time already traded, so one entry per bar
    errors = 0
    cycles = 0

    print(f"[{time.strftime('%H:%M:%S')}] parallel run: target {args.trades} trades",
          flush=True)
    print(f"  balance ${bal:,.2f}  stake ${args.stake:.2f} flat  "
          f"expiry {args.duration}s ({hold} x {args.period}s bars)", flush=True)
    print(f"  rule {args.rule}  payout>={args.min_payout}%  "
          f"signal fresh within {args.max_age:.0f}s of bar close", flush=True)
    print(f"  up to {args.max_inflight} trades open at once "
          f"(${args.max_inflight*args.stake:,.0f} exposure)", flush=True)
    print(f"  ledger identity checked every cycle: "
          f"opening - inflight*stake + settled\n", flush=True)

    pool = ThreadPoolExecutor(max_workers=args.workers)
    try:
        while True:
            placed, wins, losses, ties, unknown, inflight, settled = book.snapshot()
            if placed >= args.trades and inflight == 0:
                break
            if placed >= args.trades:
                time.sleep(5)
                continue

            bal_now, kind, err = balance(args.user_id, token)
            # A failed read and a real zero are different things. Conflating them ended an
            # earlier run at 443 trades with $766 still in the account.
            if bal_now is None or bal_now <= 0.0:
                errors += 1
                if errors >= 15:
                    print("too many unusable balance reads; stopping", flush=True)
                    break
                print(f"[{time.strftime('%H:%M:%S')}] balance unreadable "
                      f"({err or 'returned 0'}); retrying", flush=True)
                time.sleep(20)
                continue
            errors = 0
            if "demo" not in kind.lower():
                print(f"STOPPING: account is now {kind!r}", flush=True)
                break
            # The floor has to account for money already at risk, or a losing wave can
            # take the balance through it while the check still reads clear.
            if bal_now - inflight * args.stake < args.min_balance:
                print(f"STOPPING: ${bal_now:,.2f} with "
                      f"${inflight*args.stake:,.2f} at risk reached the floor", flush=True)
                break

            room = min(args.max_inflight - inflight, args.trades - placed)
            if room <= 0:
                time.sleep(3)
                continue

            assets, err = call(
                f"/market/assets?user_id={args.user_id}&broker={BROKER}", token)
            if assets is None:
                time.sleep(10)
                continue
            rows = assets if isinstance(assets, list) else assets.get("assets", [])
            pool_assets = [a for a in rows
                           if a.get("is_open")
                           and (a.get("payout") or 0) >= args.min_payout]
            pool_assets.sort(key=lambda a: -(a.get("payout") or 0))

            picks, scanned, stale = [], 0, 0
            for a in pool_assets:
                symbol = a.get("symbol")
                if not symbol:
                    continue
                w = window(args.candle_dir, symbol, args.period)
                if w is None:
                    continue
                scanned += 1
                if seen.get(symbol) == w["bar_time"]:
                    continue
                side = rule(w)
                if side is None:
                    continue
                if w["age"] > args.max_age:
                    stale += 1
                    seen[symbol] = w["bar_time"]   # do not chase it next cycle either
                    continue
                seen[symbol] = w["bar_time"]
                picks.append((symbol, a.get("payout") or 0, side, w["bar_time"]))
                if len(picks) >= room:
                    break

            for symbol, payout, side, bar_time in picks:
                pool.submit(trade, args, token, book, log_lock,
                            symbol, payout, side, bar_time)

            cycles += 1
            placed, wins, losses, ties, unknown, inflight, settled = book.snapshot()
            n = wins + losses
            drift = bal_now - book.expected()
            lo, hi = wilson(wins, n) if n >= 30 else (0.0, 0.0)
            ci = f" CI=[{lo*100:.1f},{hi*100:.1f}]" if n >= 30 else ""
            # A trade whose outcome never arrived is excluded from settled P&L, so it
            # legitimately moves the identity by up to stake*(1+payout). Allowing for that
            # is what keeps the flag meaningful instead of permanently lit.
            tolerance = max(5.0, args.stake * 3) + unknown * args.stake * 2.0
            flag = f"  DRIFT {drift:+.2f} (tol {tolerance:.0f})" if abs(drift) > tolerance else ""
            if picks or cycles % 10 == 0:
                print(f"[{time.strftime('%H:%M:%S')}] {placed:>5}/{args.trades}  "
                      f"+{len(picks)} new, {inflight} open  "
                      f"W{wins}/L{losses}/T{ties}/U{unknown}  "
                      f"win={wins/n*100 if n else 0:.1f}%{ci}  "
                      f"pnl={settled:+.2f}  bal=${bal_now:,.2f}  "
                      f"[scan {scanned}, stale {stale}]{flag}", flush=True)

            # Wake just after the next bar closes. Signals exist only in the seconds after
            # a close, so an unaligned loop spends its time scanning mid-bar and then
            # discarding everything it finds as stale.
            nap = args.period - (time.time() % args.period) + 1.5
            time.sleep(min(nap, args.period))
    except KeyboardInterrupt:
        print("\ninterrupted; waiting for open trades to settle", flush=True)
    finally:
        pool.shutdown(wait=True)

    placed, wins, losses, ties, unknown, inflight, settled = book.snapshot()
    final, _, _ = balance(args.user_id, token)
    n = wins + losses
    print(f"\n{'='*96}")
    print("PARALLEL RUN COMPLETE")
    print(f"{'='*96}")
    print(f"  rule                 {args.rule} at {args.duration}s expiry")
    print(f"  trades placed        {placed}")
    print(f"  W / L / T / unknown  {wins} / {losses} / {ties} / {unknown}")
    if n:
        lo, hi = wilson(wins, n)
        print(f"  win rate             {wins/n*100:.2f}%   95% CI "
              f"[{lo*100:.2f}, {hi*100:.2f}]")
        for payout, label in ((0.95, "95%"), (0.925, "92.5%"), (0.85, "85%")):
            be = 1 / (1 + payout)
            state = "CLEARS" if lo > be else ("below" if hi < be else "straddles")
            print(f"    vs breakeven at {label:<6} {be*100:.2f}%  ->  {state}")
    print(f"  settled P&L          {settled:+.2f}")
    if final is not None:
        print(f"  balance              ${book.opening:,.2f} -> ${final:,.2f} "
              f"({final-book.opening:+,.2f})")
        print(f"  ledger drift         {final - book.expected():+.2f}  "
              f"(of which up to {unknown*args.stake*2:.2f} is the {unknown} unsettled)")
    if placed:
        turnover = placed * args.stake
        print(f"  turnover             ${turnover:,.2f}")
        print(f"  return on turnover   {settled/turnover*100:+.3f}%")
    print(f"  log                  {args.log}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
