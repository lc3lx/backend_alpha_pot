# PHASE 3 Audit — Pre-Implementation

**Date:** 2026-08-05  
**Method:** Code inspection of Phase 1 + Phase 2 sources (not report-only).

---

## 1. Current market-data capabilities

| Capability | Engine | API |
|---|---|---|
| Trading assets | `IBinollaClient.GetTradingAssetsAsync` — in-memory cache from push `s_assets/list`; open assets only; includes `PayoutPercentage` from wire | **Missing** |
| Subscribe pair | `SubscribePairAsync(pair, period)` — sends `asset/change` + alerts | **Missing** |
| Live quotes | Push `s_quotes/list` → `BinollaSessionState.LatestQuotes` — **no public client method** | **Missing** |
| Candles/history | Push `s_history/last` → `State.HistoricalData[$"{asset}:{period}"]` — **no public client method** | **Missing** |
| Chart socket | Optional; connects but **no inbound handler** (`EnableChartConnection=false`) | N/A |

Quotes/candles are event-driven into session state, not request/response. No busy-wait / `Thread.Sleep` in engine.

---

## 2. Existing Binolla client methods

`IBinollaClient`: Connect, GetBalance, ChangeAccount, PlaceOrder, WaitOutcome, SubscribePair, GetTradingAssets, Disconnect + lifecycle events.

`IBinollaSessionManager`: GetOrCreate, Get, Remove, Disconnect, ActiveSessionCount.

**Gap for Phase 3:** need safe quote/history accessors (wait after subscribe, timeouts) without fabricating data.

---

## 3. Existing API endpoints

| Path | Status |
|---|---|
| `POST /api/auth/telegram`, `GET /api/me` | OK |
| Binolla connect/status/balance/account-type/disconnect | OK (Demo-only) |
| `POST/GET /api/trades`, `GET /api/trades/{id}` | OK; list hard-capped at 100, no pagination/filter |
| `GET /health` | Liveness only — no `/health/ready` |
| Market assets/price/candles | **Missing** |

Rate limit: `trades` policy **30/min** on entire `/api/trades` group.

---

## 4. Current Trade lifecycle

Statuses (`TradeStatus`): `Pending`, `Running`, `Profit`, `Loss`, `Tie`, `Failed`.

Flow: insert Pending → PlaceOrder → Running + BinollaOrderId → enqueue outcome → Profit|Loss|Tie|Failed.

**No transition guards.** `ErrorCode` on entity not exposed on DTO. No `Unknown` / `Cancelled`.

---

## 5. Outcome worker

`TradeOutcomeWorker`: unbounded in-process channel; `WaitOutcomeAsync` then update DB.

**Critical gaps:**
- Does **not** reload Pending/Running on startup.
- If session missing → log and leave trade **Running forever**.
- Process crash after Running loses queue durability.
- No authoritative Binolla “get order by id” API exists → full post-restart reconciliation **impossible**.

---

## 6. Idempotency

- Header `Idempotency-Key` required.
- Lookup `(UserId, key)` then insert; unique index `IX_trades_UserId_IdempotencyKey`.
- Race → unique violation → re-fetch.
- **Tests:** sequential duplicate only; **no concurrent ×10 test**.

---

## 7. Session lifecycle / reconnect

States: Disconnected → Connecting → Connected | Reconnecting → Reconnected | AuthenticationFailed | SessionExpired | Faulted.

Auto-reconnect (default on) with exponential backoff; max 5 attempts. Pending open/outcome waiters **failed** on drop (not resumed). `OnReconnected` can fire more than once (inbound path quirk).

Fake transport: `SimulateDropAsync` available for tests.

---

## 8. Database Trade schema

`trades`: Id, UserId, BinollaOrderId, Asset, Direction, Amount, DurationSeconds, Status, Pnl, IdempotencyKey, ErrorCode, timestamps.  
Indexes: `UserId`; unique `(UserId, IdempotencyKey)`. **No** index on Status for recovery scans.

---

## 9. Missing functionality (Phase 3 scope)

1. Market HTTP: assets, price, candles  
2. Public quote/history on `IBinollaClient`  
3. Trade state machine + `Unknown` for irrecoverable open trades  
4. Startup recovery for Pending/Running  
5. Concurrent idempotency proof  
6. Two-user concurrent isolation (market + trade)  
7. WS disconnect/reconnect API-level tests  
8. Invalid/expired SSID controlled errors (extend coverage)  
9. Rate-limit automated test  
10. Trade list pagination/filter  
11. `/health/ready`  
12. Structured trade/session observability  
13. Outcome update idempotency (ignore terminal re-apply)

---

## 10. Risks discovered

| Risk | Severity | Notes |
|---|---|---|
| No durable outcome queue / no order query API | High | Restart → mark `Unknown` when session gone; never invent Won/Lost |
| DB link `Connected` vs live session after restart | Medium | Me may lie; status/balance already live-aware |
| Assets/quotes empty until push arrives | Medium | Subscribe + wait with timeout; return MARKET_UNAVAILABLE |
| In-memory sessions only | Medium | Expected until Redis |
| Real trading blocked at connect/account/trade | OK | Keep |
| Concurrent PlaceOrder before unique commit | Medium | Unique index + catch; prove with ×10 test |

---

## Implementation stance

- Prefer thin additions to Binolla client for quote/history wait (no engine rewrite).  
- Do not invent payouts or candles.  
- Document honest restart limits.  
- Frontend untouched.
