# PHASE 3 Report — Market Data + Demo Trading Hardening

**Status: COMPLETE**

Scope held: no frontend changes, no Real trading, no AI strategies, no Redis, no production deploy.

Initial audit: `PHASE3_AUDIT.md` (written before implementation).

---

## 1. Initial audit (summary)

Verified gaps vs reports:

- Engine had assets/subscribe; quotes/candles only in session state (no public client API).
- No market HTTP endpoints; trade list unpaginated; outcome worker not durable across restart.
- No Binolla “get order by id” → full post-crash Won/Lost recovery impossible.
- Idempotency: unique index + sequential test only (no concurrent ×10).
- `/health` only; no ready check.

---

## 2. Files changed / added

### Engine (`ScarAlpha.Binolla`)
- `IBinollaClient`: `GetLatestQuoteAsync`, `GetHistoryAsync`
- `BinollaSession`: implement wait-after-subscribe; assets wait if empty; remove `OnReconnected` spam on every inbound

### Domain / Application
- `TradeStatus`: +`Unknown`, +`Cancelled`
- `TradeStateMachine` — allowed transitions + terminal idempotency
- `MarketAppService`, hardened `TradeAppService` (balance check, idempotency gate, pagination)
- `MeAppService` — live session for `connected`
- DTOs: market + paginated trades + `errorCode`

### Infrastructure / API
- `IdempotencyGate`, `TradeOutcomeWorker` recovery, trade status index migration
- Market endpoints, `/health/ready`, configurable trade rate limit + `RATE_LIMITED` JSON
- Smoke: optional `--trade` / `BINOLLA_SMOKE_TRADE=1`

### Tests
- Engine: market data + reconnect
- API: Phase 3 suite (market, concurrent idempotency, isolation, recovery, rate limit, outcome)

---

## 3. Market API endpoints

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/api/market/assets` | Live session → `GetTradingAssetsAsync`; payout null if ≤0 |
| `GET` | `/api/market/price/{asset}` | Subscribe + latest push quote |
| `GET` | `/api/market/candles/{asset}?period=60` | Subscribe + push history candles |

Errors: `BINOLLA_NOT_CONNECTED`, `BINOLLA_SESSION_EXPIRED`, `BINOLLA_CONNECTION_FAILED`, `MARKET_UNAVAILABLE`.

No fabricated payouts/OHLC.

---

## 4. Trade lifecycle

```text
Pending → Running | Failed | Cancelled
Running → Profit | Loss | Tie | Failed | Unknown
```

Terminal states ignore further transitions (duplicate outcome safe).  
API names keep Phase 2: Profit/Loss/Tie (= Won/Lost/Draw).

HTTP place-order does **not** wait for binary outcome.

---

## 5. Outcome worker

- Channel still in-process for live outcomes.
- On start: `RecoverOpenTradesAsync`:
  - Pending without order id → `Failed`
  - Running/Pending with order id + live session → re-enqueue `WaitOutcomeAsync`
  - Running with order id + **no** session → `Unknown` (`RECOVERY_NO_SESSION`)
- Never invents Won/Lost without Binolla data.

---

## 6. Restart recovery behavior

| Scenario | Result |
|---|---|
| Order placed, outcome still queued, process dies, session gone | Trade → `Unknown` |
| Same, but session still in-process after soft recover | Re-enqueue wait |
| Pending never opened | `Failed` |

**Limitation:** Binolla protocol exposes no authoritative historical order query. Documented honestly; `Unknown` is the safe status.

---

## 7. Idempotency

- Unique `(UserId, IdempotencyKey)` + in-process `IdempotencyGate` (serialize same key).
- Sequential duplicate → one trade / one order.
- **10 concurrent** same key → exactly **1** `PlaceOrderAsync` and **1** DB row (proven in tests).

---

## 8. Two-user isolation

Concurrent User A/B: assets, price, balance, trade — all OK; A cannot GET B’s trade (`404`); no SSID in bodies.

---

## 9. WebSocket reconnect

Engine test: drop via `FakeWebSocketTransport.SimulateDropAsync` → auto-reconnect → Connected/Reconnected → `GetBalanceAsync` works. No deadlock. Pending waiters still fail on drop (by design).

---

## 10. Invalid / expired SSID

- Engine: invalid SSID → `BinollaAuthenticationException` / `AuthenticationFailed` (existing + retained).
- API market: auth exception → `BINOLLA_SESSION_EXPIRED` (401).
- Disconnected → `BINOLLA_NOT_CONNECTED` (409).

---

## 11. Rate limiting

- Policy `trades` on `/api/trades` group.
- Default: **30 requests / 60 seconds** per JWT `sub` (config: `RateLimiting:Trades:PermitLimit` / `WindowSeconds`).
- Burst test with PermitLimit=2 → HTTP **429** + `RATE_LIMITED`.

---

## 12. Trade history security

- `GET /api/trades?page=&pageSize=&status=&asset=` — newest first, paginated, own user only.
- `GET /api/trades/{id}` — ownership filter; other user → **404** (consistent).

---

## 13. Health checks

| Path | Meaning |
|---|---|
| `GET /health` | Process up |
| `GET /health/ready` | DB `CanConnect`; does **not** require Binolla sessions |

---

## 14. Observability

Structured logs (no SSID/JWT/initData/keys): connect, market timing, trade requested/accepted/failed, idempotency duplicate, outcome, recovery, rate limit. Identifiers: UserId, TradeId, BinollaOrderId.

---

## 15. Full test matrix

| Suite | Result |
|---|---|
| `ScarAlpha.Binolla.Tests` | **10/10 passed** |
| `ScarAlpha.Api.Tests` | **23/23 passed** |

```powershell
dotnet test ScarAlpha.sln -c Release
```

Covered: market assets/quote/candles; disconnected/expired; Demo trade + outcome; concurrent idempotency ×10; A/B isolation; recovery → Unknown; rate limit; reconnect; Phase 1+2 regressions.

---

## 16. Live Demo smoke

```powershell
$env:BINOLLA_SSID = '<auth frame>'
dotnet run --project ScarAlpha.Binolla.Smoke -c Release
# optional one Demo trade:
dotnet run --project ScarAlpha.Binolla.Smoke -c Release -- --trade
```

**This environment:** `BINOLLA_SSID` not set → smoke skipped (not failed). Deterministic suites are the gate.

---

## 17. Known limitations

- In-process sessions + outcome channel (no Redis).
- Restart without live session → `Unknown`, not PnL.
- Quote/candle waits use short `Task.Delay` polling after subscribe (finite timeout; no busy-spin).
- Real trading still blocked.
- Frontend still on mocks (unchanged).

---

## 18. Risks before frontend integration

1. Map frontend `TradeRecord` / `up|down` / rich UI fields to lean API DTOs.
2. Handle `Unknown` trades in UI.
3. After API process restart, users must reconnect Binolla (encrypted SSID in DB; session not restored automatically).
4. Assets/quotes may briefly return `MARKET_UNAVAILABLE` until Binolla push arrives.
5. Multi-instance deploy still unsafe without sticky sessions / Redis (future phase).

**Phase 3 stopped for review. Do not start frontend integration, Real trading, AI, Redis, or deploy automatically.**
