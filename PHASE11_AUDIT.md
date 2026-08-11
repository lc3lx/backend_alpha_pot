# PHASE 11 — AUDIT (Step 0)

**Date:** 2026-08-12  
**Scope:** Live Binolla market + pairs + charts + Demo trading (no Real / Auto / redesign)  
**Prior evidence:** Live Binolla login on production VPS succeeded; token JSON normalized to UUID; `isUnsupportedCountry=false`.

---

## Product gate (unchanged)

```text
Telegram → Binolla login/SSID → Admin approval → FREE Demo
→ live assets/quotes/candles → RSI signal → manual Demo trade → history
```

Out of scope (do not build): Real trading, Auto trading, Auto RSI, EMA/MACD/AI, Redis, UI redesign, payments.

---

## What already works (code inspection)

### Binolla engine (`ScarAlpha.Binolla`)
| Capability | Status | Notes |
|---|---|---|
| Per-user WebSocket session | Present | `BinollaSession` + `BinollaSessionManager` (in-process) |
| Assets list from wire | Present | `s_assets/list` → `State.Assets` (open instruments) |
| Pair subscribe + quotes | Present | `SubscribePairAsync` / `GetLatestQuoteAsync` |
| History / candles | Present | `GetHistoryAsync` from pushed history |
| Demo place order | Present | `PlaceOrderAsync` |
| Outcome wait | Present | `WaitOutcomeAsync` → TradeOutcomeWorker |
| Chart socket | Disabled by config | `EnableChartConnection = false` — candles via trading socket history (intentional) |

### API (`MarketAppService` / `TradeAppService` / `RsiSignalAppService`)
| Endpoint | Live Binolla? | Gate |
|---|---|---|
| `GET /api/market/assets` | YES — session assets | JWT + Allowed |
| `GET /api/market/price/{asset}` | YES — subscribe + quote | JWT + Allowed |
| `GET /api/market/candles/{asset}` | YES — history | JWT + Allowed |
| `GET /api/strategies/rsi/signal/{asset}` | YES — same history → RSI | JWT + Allowed |
| `POST /api/trades` | YES — PlaceOrderAsync | JWT + Allowed + Demo + Idempotency-Key |
| `GET /api/trades` | Local DB, user-scoped | JWT |
| `GET /api/binolla/balance` | YES — Demo balance; Real masked 0 | JWT + Allowed |
| Session restore | YES — Approved links with EncryptedSsid | Hosted worker + lazy restore |

**No static asset catalog in market service.** Assets come from the connected session.

### Frontend Trading
| Piece | Live? | Notes |
|---|---|---|
| Chart OHLC | Live when Allowed | `marketApi.candles` → `CandlestickChart` (no invented OHLC) |
| Price / balance / RSI | Live when Allowed | Poll ~8s via `tradingService` / `useTradingData` |
| Place trade / history | Live | `tradesApi` |
| UI chrome | Local copy | `trading.mock.ts` labels/durations only — not market prices |
| Home Start/Pause/Stop | Coming Soon | No fake auto-bot |
| Asset fallback | **Gap** | If assets API fails, code may fall back to hardcoded `EURUSD_otc` — must not invent pairs |

### Persistence
| Item | Status |
|---|---|
| EF migrations | Present (Initial + admin approval + trades indexes) |
| Npgsql | Supported; DI defaults to Npgsql if provider unset |
| Env examples | Still default `DATABASE_PROVIDER=InMemory` — **launch risk** |
| `/health` | Liveness only |
| `/health/ready` | DB `CanConnectAsync` |

### Auth / security (code)
- Multi-user isolation: session dictionary keyed by `userId`
- Trades/market always use current user’s session
- SSID encrypted at rest; Real trading rejected

---

## Gaps for Phase 11 (actionable)

1. **Production DB:** ~~VPS scripts/examples still push InMemory~~ → **Addressed in repo:** Production defaults + `start-backend-pm2.sh` refuse InMemory; **live VPS env still needs operator confirmation**.
2. **Debug instrumentation (session 660ec2):** ~~must remove~~ → **Removed** (API/FE/capture/AgentDebugLog).
3. **FE asset fallback:** ~~hardcoded `EURUSD_otc`~~ → **Fixed** — no invented pairs.
4. **Live E2E verification:** Still requires operator Demo JWT on VPS for assets/quotes/candles/trade/restore PASS.
5. **Trade recovery:** No Binolla order-query API → open trades without live session stay `Unknown` (honest; keep).

---

## Rebuild policy

**Do NOT rebuild** market/trade/RSI/session stack — already wired to live Binolla.

**DONE in Phase 11 code pass:**
- Harden production persistence + secrets (repo/scripts)
- Strip debug
- Fix FE fallback that invents a pair
- Document verification + honest `PHASE11_REPORT.md`

---

## Verdict after Step 0 (+ code pass)

```text
IMPLEMENTATION: MOSTLY COMPLETE (live path present in code)
VERIFICATION: PARTIALLY VERIFIED — see PHASE11_REPORT.md
```
