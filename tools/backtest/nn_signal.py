#!/usr/bin/env python3
"""
A neural network on the same question, and the one thing it can do that the rules cannot.

Every rule tested so far answers yes or no: fire, or stand aside. A network answers with a
probability, and that opens the only avenue left unexamined — a model can be 50% accurate
overall and still be 56% accurate on the tenth of cases it is most sure about. If that
band exists it is exactly what a bot needs, because a bot can simply decline the rest.
That is the question here, not whether a network beats RSI on average.

The protocol matters more than the architecture, because the way to get a convincing and
false answer is well known by now:

  * NO LOOK-AHEAD. Features at bar i use bars <= i. The label reads bar i+hold. Series are
    broken at time gaps, and a window never spans one.
  * THREE-WAY TEMPORAL SPLIT, per symbol, never shuffled: the first 50% trains, the next
    10% validates, the last 40% is sealed. Early stopping and — critically — the
    confidence threshold are both chosen on validation. Choosing the threshold on sealed
    data is precisely the error that produced every false positive in this project.
  * THE SEALED SET IS SCORED ONCE. Its number is the answer whatever it says.
  * EVERYTHING IS SCALE-FREE. Returns are divided by ATR, so 78 instruments at different
    price levels pool into one dataset instead of the network learning which symbol is
    which.

Breakeven at the 91.6% payout actually paid is 52.20%. A sealed accuracy whose lower
confidence bound sits above that, at a trade frequency worth having, is a real finding. A
sealed accuracy of 50.5% is also a real finding, and the more likely one given that the
measured autocorrelation of these series is 0.0247 where breakeven alone needs 0.127.
"""
from __future__ import annotations

import argparse
import gzip
import json
import math
import sys

import numpy as np

PAYOUT = 0.916
BREAKEVEN = 1.0 / (1.0 + PAYOUT)


def wilson(wins, n, z=1.96):
    if n == 0:
        return (0.0, 0.0)
    p = wins / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (max(0.0, c - h), min(1.0, c + h))


# ---- data ---------------------------------------------------------------------------


def load_runs(path, period=60, min_len=200):
    """Contiguous runs of bars per symbol. A gap starts a new run rather than being
    bridged, because a feature or a label computed across one compares prices that were
    never adjacent."""
    opener = gzip.open if path.endswith(".gz") else open
    with opener(path, "rt", encoding="utf-8") as fh:
        raw = json.load(fh)
    runs = []
    for symbol, rows in raw.items():
        cur = []
        for r in rows:
            if cur and r[0] - cur[-1][0] != period:
                if len(cur) >= min_len:
                    runs.append((symbol, np.asarray(cur, dtype=np.float64)))
                cur = []
            cur.append(r)
        if len(cur) >= min_len:
            runs.append((symbol, np.asarray(cur, dtype=np.float64)))
    return runs


def rsi_series(close, period):
    d = np.diff(close, prepend=close[0])
    gain = np.where(d > 0, d, 0.0)
    loss = np.where(d < 0, -d, 0.0)
    ag = np.empty_like(close)
    al = np.empty_like(close)
    ag[:period] = gain[1:period + 1].mean() if len(gain) > period else 0.0
    al[:period] = loss[1:period + 1].mean() if len(loss) > period else 0.0
    for i in range(period, len(close)):
        ag[i] = (ag[i - 1] * (period - 1) + gain[i]) / period
        al[i] = (al[i - 1] * (period - 1) + loss[i]) / period
    rs = np.where(al > 0, ag / np.maximum(al, 1e-12), 100.0)
    return np.where(al > 0, 100.0 - 100.0 / (1.0 + rs), 100.0)


def ema_series(x, period):
    k = 2.0 / (period + 1.0)
    out = np.empty_like(x)
    out[0] = x[0]
    for i in range(1, len(x)):
        out[i] = x[i] * k + out[i - 1] * (1.0 - k)
    return out


FEATURE_NAMES = (
    [f"ret{k}" for k in range(1, 11)]
    + ["body0", "upper0", "lower0", "body1", "upper1", "lower1",
       "body2", "upper2", "lower2"]
    + ["range_atr", "range1_atr", "rsi5", "rsi7", "rsi14",
       "z_last", "ema5_dist", "ema20_dist", "vol_ratio",
       "greens10", "streak", "tod_sin", "tod_cos", "gap_atr"]
)


def features(arr, period=60):
    """
    Feature matrix, row i computed from bars <= i only.

    Everything is a ratio or divided by ATR, so instruments at different price levels and
    volatilities land on the same scale and the network cannot simply memorise symbols.
    """
    t, o, h, l, c = (arr[:, k] for k in range(5))
    n = len(c)
    rng = np.maximum(h - l, 1e-12)
    body = np.abs(c - o) / rng
    upper = (h - np.maximum(o, c)) / rng
    lower = (np.minimum(o, c) - l) / rng

    tr = np.maximum(h - l, np.maximum(np.abs(h - np.roll(c, 1)),
                                      np.abs(l - np.roll(c, 1))))
    tr[0] = h[0] - l[0]
    atr = ema_series(tr, 14)
    atr = np.maximum(atr, 1e-12)

    logc = np.log(np.maximum(c, 1e-12))
    ret1 = np.diff(logc, prepend=logc[0])
    scale = atr / np.maximum(c, 1e-12)            # ATR as a fraction of price
    scale = np.maximum(scale, 1e-9)

    cols = []
    for k in range(1, 11):
        r = logc - np.roll(logc, k)
        r[:k] = 0.0
        cols.append(r / (scale * math.sqrt(k)))
    for back in range(3):
        for arr_ in (body, upper, lower):
            v = np.roll(arr_, back)
            v[:back] = arr_[0]
            cols.append(v)
    cols.append(rng / atr)
    r1 = np.roll(rng, 1) / atr
    r1[0] = rng[0] / atr[0]
    cols.append(r1)
    for p in (5, 7, 14):
        cols.append(rsi_series(c, p) / 100.0)

    vol20 = np.array([ret1[max(0, i - 19):i + 1].std() for i in range(n)])
    vol5 = np.array([ret1[max(0, i - 4):i + 1].std() for i in range(n)])
    cols.append(ret1 / np.maximum(vol20, 1e-12))
    cols.append((c - ema_series(c, 5)) / atr)
    cols.append((c - ema_series(c, 20)) / atr)
    cols.append(vol5 / np.maximum(vol20, 1e-12))

    green = (c > o).astype(np.float64)
    cols.append(np.array([green[max(0, i - 9):i + 1].mean() for i in range(n)]))
    streak = np.zeros(n)
    for i in range(1, n):
        same = (green[i] == green[i - 1])
        streak[i] = (streak[i - 1] + 1.0) if same else 0.0
    cols.append(np.minimum(streak, 10.0) / 10.0)

    minute = (t % 86400) / 86400.0
    cols.append(np.sin(2 * math.pi * minute))
    cols.append(np.cos(2 * math.pi * minute))
    gap = (o - np.roll(c, 1)) / atr
    gap[0] = 0.0
    cols.append(gap)

    X = np.column_stack(cols)
    return np.nan_to_num(X, nan=0.0, posinf=0.0, neginf=0.0), c


def build(runs, hold, warmup=40):
    """Rows of (features, label, split_key) with the temporal split done per run."""
    Xs, ys, parts, syms = [], [], [], []
    for symbol, arr in runs:
        X, c = features(arr)
        n = len(c)
        hi = n - hold
        if hi <= warmup:
            continue
        idx = np.arange(warmup, hi)
        future = c[idx + hold]
        now = c[idx]
        keep = future != now                    # a tie carries no direction
        idx, future, now = idx[keep], future[keep], now[keep]
        if len(idx) == 0:
            continue
        y = (future > now).astype(np.float64)

        # Split by POSITION IN TIME within this run, never shuffled.
        frac = idx / float(n)
        part = np.where(frac < 0.50, 0, np.where(frac < 0.60, 1, 2))
        Xs.append(X[idx])
        ys.append(y)
        parts.append(part)
        syms.extend([symbol] * len(idx))
    return (np.vstack(Xs), np.concatenate(ys), np.concatenate(parts),
            np.array(syms))


# ---- a small MLP, written out so there is nothing hidden in it -----------------------


class MLP:
    def __init__(self, n_in, hidden=(64, 32), seed=7, l2=1e-4):
        rng = np.random.default_rng(seed)
        sizes = [n_in, *hidden, 1]
        self.W = [rng.normal(0, math.sqrt(2.0 / sizes[i]), (sizes[i], sizes[i + 1]))
                  for i in range(len(sizes) - 1)]
        self.b = [np.zeros(sizes[i + 1]) for i in range(len(sizes) - 1)]
        self.l2 = l2
        self.mW = [np.zeros_like(w) for w in self.W]
        self.vW = [np.zeros_like(w) for w in self.W]
        self.mb = [np.zeros_like(x) for x in self.b]
        self.vb = [np.zeros_like(x) for x in self.b]
        self.t = 0

    def forward(self, X):
        acts = [X]
        a = X
        for i in range(len(self.W) - 1):
            a = np.maximum(a @ self.W[i] + self.b[i], 0.0)
            acts.append(a)
        z = a @ self.W[-1] + self.b[-1]
        p = 1.0 / (1.0 + np.exp(-np.clip(z[:, 0], -30, 30)))
        return p, acts, z

    def step(self, X, y, lr=1e-3):
        m = len(y)
        p, acts, _ = self.forward(X)
        dz = ((p - y) / m)[:, None]
        gW, gb = [None] * len(self.W), [None] * len(self.b)
        for i in range(len(self.W) - 1, -1, -1):
            gW[i] = acts[i].T @ dz + self.l2 * self.W[i]
            gb[i] = dz.sum(axis=0)
            if i > 0:
                dz = (dz @ self.W[i].T) * (acts[i] > 0)
        self.t += 1
        b1, b2, eps = 0.9, 0.999, 1e-8
        for i in range(len(self.W)):
            self.mW[i] = b1 * self.mW[i] + (1 - b1) * gW[i]
            self.vW[i] = b2 * self.vW[i] + (1 - b2) * gW[i] ** 2
            mh = self.mW[i] / (1 - b1 ** self.t)
            vh = self.vW[i] / (1 - b2 ** self.t)
            self.W[i] -= lr * mh / (np.sqrt(vh) + eps)
            self.mb[i] = b1 * self.mb[i] + (1 - b1) * gb[i]
            self.vb[i] = b2 * self.vb[i] + (1 - b2) * gb[i] ** 2
            mhb = self.mb[i] / (1 - b1 ** self.t)
            vhb = self.vb[i] / (1 - b2 ** self.t)
            self.b[i] -= lr * mhb / (np.sqrt(vhb) + eps)
        return p

    def predict(self, X):
        return self.forward(X)[0]


def log_loss(p, y):
    p = np.clip(p, 1e-9, 1 - 1e-9)
    return float(-(y * np.log(p) + (1 - y) * np.log(1 - p)).mean())


def accuracy_table(p, y, label, thresholds):
    """Accuracy of the directional call at increasing confidence cut-offs."""
    conf = np.abs(p - 0.5)
    call = (p > 0.5).astype(np.float64)
    out = []
    for thr in thresholds:
        sel = conf >= thr
        n = int(sel.sum())
        if n < 50:
            out.append((thr, n, None, None, None))
            continue
        acc = float((call[sel] == y[sel]).mean())
        lo, hi = wilson(int((call[sel] == y[sel]).sum()), n)
        out.append((thr, n, acc, lo, hi))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="D:/tmp/nn-data/candles60.json.gz")
    ap.add_argument("--hold", type=int, default=5, help="bars held; 5 x 60s = 300s")
    ap.add_argument("--epochs", type=int, default=60)
    ap.add_argument("--batch", type=int, default=512)
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--hidden", default="64,32")
    ap.add_argument("--seeds", type=int, default=3,
                    help="independent initialisations; their mean is the prediction")
    args = ap.parse_args()

    runs = load_runs(args.data)
    X, y, part, syms = build(runs, args.hold)
    tr, va, se = part == 0, part == 1, part == 2
    print("=" * 96)
    print(f"NEURAL NETWORK ON {args.hold}-BAR DIRECTION  "
          f"({args.hold * 60}s expiry)")
    print("=" * 96)
    print(f"  runs                 {len(runs)}  from "
          f"{len(set(s for s, _ in runs))} symbols")
    print(f"  features             {X.shape[1]}")
    print(f"  train / val / sealed {tr.sum()} / {va.sum()} / {se.sum()}")
    print(f"  base rate (train)    {y[tr].mean()*100:.2f}% up")
    print(f"  breakeven to beat    {BREAKEVEN*100:.2f}%  (at {PAYOUT*100:.1f}% payout)")

    # Standardise with TRAIN statistics only.
    mu, sd = X[tr].mean(axis=0), X[tr].std(axis=0)
    sd = np.where(sd < 1e-9, 1.0, sd)
    Xs = (X - mu) / sd

    hidden = tuple(int(v) for v in args.hidden.split(","))
    rng = np.random.default_rng(11)
    val_preds, sealed_preds = [], []

    for seed in range(args.seeds):
        net = MLP(Xs.shape[1], hidden=hidden, seed=7 + seed * 101)
        Xtr, ytr = Xs[tr], y[tr]
        best, best_state, patience = float("inf"), None, 0
        for epoch in range(args.epochs):
            order = rng.permutation(len(ytr))
            for k in range(0, len(order), args.batch):
                b = order[k:k + args.batch]
                net.step(Xtr[b], ytr[b], lr=args.lr)
            vl = log_loss(net.predict(Xs[va]), y[va])
            if vl < best - 1e-5:
                best, patience = vl, 0
                best_state = ([w.copy() for w in net.W], [c.copy() for c in net.b])
            else:
                patience += 1
                if patience >= 8:
                    break
        if best_state:
            net.W, net.b = best_state
        print(f"  seed {seed}: best validation log loss {best:.5f} "
              f"(chance = {log_loss(np.full(va.sum(), y[tr].mean()), y[va]):.5f})")
        val_preds.append(net.predict(Xs[va]))
        sealed_preds.append(net.predict(Xs[se]))

    pv = np.mean(val_preds, axis=0)
    ps = np.mean(sealed_preds, axis=0)

    thresholds = [0.0, 0.01, 0.02, 0.03, 0.05, 0.075, 0.10, 0.15]
    print("\n" + "-" * 96)
    print("VALIDATION — the confidence cut-off is chosen HERE, never on sealed data")
    print("-" * 96)
    print(f"  {'cut-off':>9}{'n':>8}{'accuracy':>11}{'95% CI':>16}   verdict")
    vt = accuracy_table(pv, y[va], "val", thresholds)
    for thr, n, acc, lo, hi in vt:
        if acc is None:
            print(f"  {thr:>9.3f}{n:>8}       too few")
            continue
        verdict = ("above breakeven" if acc > BREAKEVEN else "below")
        print(f"  {thr:>9.3f}{n:>8}{acc*100:>10.2f}%"
              f"   [{lo*100:4.1f},{hi*100:4.1f}]   {verdict}")

    usable = [r for r in vt if r[2] is not None and r[1] >= 200]
    chosen = max(usable, key=lambda r: r[2])[0] if usable else 0.0
    print(f"\n  chosen cut-off: {chosen:.3f}  (best validation accuracy with n>=200)")

    print("\n" + "-" * 96)
    print("SEALED — scored once, at that cut-off. This is the answer.")
    print("-" * 96)
    st = accuracy_table(ps, y[se], "sealed", thresholds)
    print(f"  {'cut-off':>9}{'n':>8}{'accuracy':>11}{'95% CI':>16}   "
          f"{'edge/trade':>11}   verdict")
    for thr, n, acc, lo, hi in st:
        if acc is None:
            print(f"  {thr:>9.3f}{n:>8}       too few")
            continue
        edge = acc * PAYOUT - (1 - acc)
        if lo > BREAKEVEN:
            verdict = "CLEARS breakeven"
        elif acc > BREAKEVEN:
            verdict = "above, CI straddles"
        else:
            verdict = "below breakeven"
        mark = "  <== chosen" if abs(thr - chosen) < 1e-9 else ""
        print(f"  {thr:>9.3f}{n:>8}{acc*100:>10.2f}%"
              f"   [{lo*100:4.1f},{hi*100:4.1f}]{edge*100:>+10.2f}%   "
              f"{verdict}{mark}")

    row = next((r for r in st if abs(r[0] - chosen) < 1e-9), None)
    print("\n" + "=" * 96)
    print("ANSWER")
    print("=" * 96)
    if row is None or row[2] is None:
        print("  The chosen cut-off leaves too few sealed trades to judge.")
        return 0
    thr, n, acc, lo, hi = row
    total_sealed = int(se.sum())
    print(f"""
  At the cut-off chosen on validation, the network calls {n} of {total_sealed} sealed
  bars ({n/total_sealed*100:.1f}% of them) and gets {acc*100:.2f}% right,
  95% CI [{lo*100:.2f}, {hi*100:.2f}], against the {BREAKEVEN*100:.2f}% needed.""")
    if lo > BREAKEVEN:
        print("""
  Its interval clears breakeven on data it was neither trained nor tuned on. That is a
  real finding and the next step is a ledger-verified live run, sized small.""")
    elif acc > BREAKEVEN:
        print("""
  It sits above breakeven but the interval still includes losing, so a live run would be
  measuring it rather than profiting from it. Worth running small, expecting nothing.""")
    else:
        print(f"""
  It is below breakeven, so selectivity does not rescue it: being more certain does not
  make the network more right. Combined with the rule results, the 1,800-rule sweep, and
  a measured autocorrelation of 0.0247 where breakeven alone needs 0.127, the conclusion
  is about the price series rather than about any model: there is no exploitable
  short-horizon signal in this feed for a network to find.""")
    return 0


if __name__ == "__main__":
    sys.exit(main())
