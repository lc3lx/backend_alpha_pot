# Scar Alpha Broker Gateway

One HTTP surface over every supported broker. The .NET backend calls this instead of
speaking a broker protocol itself, so adding a venue means adding one adapter here —
nothing in the strategy layer changes.

## Why this exists

The strategy layer (`Indicators`, `RsiZoneBacktest`, `MarketRegime`, `CohortSignalCache`,
`StrategyGate`) already works on plain candles and signals, not on broker types. It is the
plumbing underneath that was Binolla-specific: 1,142 references across 93 C# files, and a
34-member `IBinollaClient` that every worker and service called directly.

This gateway is that plumbing, made broker-neutral and moved to Python — where a
maintained Quotex library already exists.

## Status

| Broker  | Adapter | Notes |
|---------|---------|-------|
| Quotex  | ✅ `app/brokers/quotex.py` | Wraps [QuotexAPI](https://github.com/ChipaDevTeam/QuotexAPI) |
| Binolla | ⏳ not yet | .NET keeps using `ScarAlpha.Binolla` until the port lands and is proven |

Binolla is deliberately last. It is live with real users, and it carries fixes that were
expensive to find — the official-close overwrite, candle residency, balance selection in
the post-auth bootstrap, credential-login backoff. Porting it before Quotex exists would
reopen all of them with nothing to fall back on.

## Contract

`app/brokers/base.py` is the interface every adapter implements. `app/models.py` holds the
neutral wire types, shaped to match what .NET already works in, so the same strategies run
unchanged whichever broker produced the data.

Two rules matter more than the rest:

- **`get_candles` returns CLOSED bars only.** A forming bar leaking through is what makes
  an indicator disagree with the broker's own chart, and no downstream calibration can
  undo it.
- **`connect` applies the account type during the handshake.** Switching afterwards races
  the broker's own setup on at least one venue, leaving the socket on the wrong balance
  while the API reports the other.

## Error codes

Adapter failures map to statuses the .NET side can act on:

| Status | Meaning | Correct response |
|--------|---------|------------------|
| `428` | CAPTCHA required | Intermittent — retry later, or have the user paste an SSID |
| `451` | Broker refused the SERVER (IP/geo/WAF) | Not per-account: pause **every** user |
| `401` | Credentials rejected | That user only |
| `429` | Throttled by this gateway | Wait; do not retry immediately |
| `409` | No live session | Connect first |

## Throttling

`app/registry.py` carries two levels of backoff, both learned from production:

- **per user** — 30s doubling to 15 min on consecutive failures
- **global** — one connect at a time, a 20s floor between attempts, and a full pause when
  a broker refuses the server's IP

A broker that refuses logins keeps refusing; retrying at full speed is what turns a
temporary block into a lasting IP ban.

## Running

```bash
cd backend/brokers
pip install -e ".[dev]"
pip install -e ./vendor/QuotexAPI      # clone the library here

export BROKER_GATEWAY_TOKEN="<shared secret with the .NET backend>"
uvicorn app.main:app --host 127.0.0.1 --port 8100
```

**Bind to `127.0.0.1` only.** This process holds live trading sessions and must never be
reachable from outside the host. The token is a second line of defence against another
local process, not a substitute for the binding.

## Tests

```bash
python -m pytest tests/ -q
```

Run without a live broker: `tests/test_gateway.py` drives the HTTP surface through a fake
adapter, and `tests/test_registry.py` pins the throttle.
