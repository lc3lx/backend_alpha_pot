# PHASE 6 Frontend Audit — Before Integration

**Date:** 2026-08-05  
**App:** `bot_telegram_webapp`  
**Method:** Full source inspection (routes, services, mocks, hooks). No code changes in this audit step.

---

## 1. Stack snapshot

| Item | State |
|---|---|
| Framework | Vite + React + React Router |
| State | Local `useState` / in-memory service singletons |
| HTTP client | **None** (no axios/fetch wrappers) |
| Env | **No** `VITE_*` files |
| Telegram.WebApp | **Not integrated** |
| JWT storage | **None** (`AuthSession` returned then discarded) |
| Tests | **None** (`npm run test` / lint not configured) |
| Scripts | `dev`, `build` (`tsc -b && vite build`), `preview`, `typecheck` |

---

## 2. Routes (active)

| Route | Screen | Layout |
|---|---|---|
| `/` | Splash | Auth |
| `/onboarding` | Onboarding | Auth |
| `/login` | Login (email/password mock) | Auth |
| `/signup` | Signup (email + telegram text + binolla text) | Auth |
| `/activation` | Activation key (obsolete product model) | Auth |
| `/home` | Dashboard | Bot |
| `/bot` | AI Bot Home (strategy/config sheets) | Bot |
| `/trading` | Trading | Bot |
| `/trading/:tradeId` | Trade detail | Bot |
| `/history` | Trade history | Bot |
| `/notifications` | Notifications | Bot |
| `/settings` | Account | Bot |
| `/settings/edit-profile` | Edit profile | Bot |
| `/settings/change-password` | Change password | Bot |
| `/settings/subscription` | Subscription (obsolete) | Bot |
| `/settings/activation-history` | Activation history (obsolete) | Bot |

**Note:** Splash currently skips auth and navigates to `/home` after a timer.

---

## 3. Screen → Current mock/service → Real backend endpoint

```text
Splash
→ useSplashBootstrap (timer only, no API)
→ POST /api/auth/telegram
→ GET /api/me
→ GET /api/account/status
→ route by botAccess / binollaConnected

Onboarding
→ ONBOARDING_MOCK_CONTENT (static copy)
→ Keep as static UX; optional status from GET /api/account/status

Login / Signup
→ authService.login / authService.signup (email/password mocks)
→ REPLACE active flow with Telegram initData → POST /api/auth/telegram
→ Do NOT keep email/password as authorization

Activation
→ authService.activate (activation key mock)
→ OBSOLETE — remove from active access flow
→ Access decided by GET /api/account/status

Dashboard (/home)
→ dashboardService + dashboard.mock (fake balance, fake recent trades)
→ GET /api/me
→ GET /api/binolla/balance
→ GET /api/account/status
→ GET /api/trades?page=1&pageSize=…

AI Bot Home (/bot)
→ homeService + home.mock (fake strategies/indicators/balance/signal)
→ GET /api/strategies
→ GET /api/market/assets
→ GET /api/strategies/rsi/signal/{asset}?period=60
→ GET /api/binolla/balance
→ GET /api/account/status
→ No auto-trade from RSI

Trading (/trading)
→ tradingService + trading.mock + tradeService.placeTrade (in-memory)
→ GET /api/binolla/status
→ GET /api/binolla/balance
→ GET /api/market/price/{asset}
→ GET /api/market/candles/{asset}?period=60
→ GET /api/strategies/rsi/signal/{asset}?period=60
→ POST /api/trades (Idempotency-Key, strategyId, asset, direction, amount, durationSeconds)
→ Manual Demo only; RSI signal display only

Trade detail
→ tradeService.getTradeDetail (in-memory + fake candles image)
→ GET /api/trades/{id} (if used) / list mapping from GET /api/trades
→ candles from GET /api/market/candles/{asset} for display only if needed

History (/history)
→ tradeService.listTrades (SEED_TRADES)
→ GET /api/trades?page=&pageSize=

Account (/settings)
→ accountService.fetchAccountSnapshot (mock profile + subscription)
→ GET /api/me
→ GET /api/account/status
→ GET /api/binolla/status
→ POST /api/binolla/connect (Binolla link UI)
→ POST /api/binolla/disconnect

Subscription / Activation History
→ accountService mock subscription/activation history
→ OBSOLETE product model — remove from active navigation/access path
→ Show referral / botAccess from GET /api/account/status instead

Notifications
→ activityService (in-memory only)
→ No backend notifications API in Phase 1–5
→ Keep local/in-memory for now (not fake trading data)

Binolla connect (missing dedicated screen today; fields on signup/account)
→ Must wire to POST /api/binolla/connect
→ Refresh GET /api/account/status
```

---

## 4. Obsolete UI (remove from ACTIVE flow only)

| Screen / concept | Action |
|---|---|
| Email/password login as auth | Bypass; use Telegram auth |
| Signup email/password | Bypass; Telegram identity |
| Activation key page | Remove from required path |
| Subscription required / buy plan | Remove from required path |
| Change password as security gate | Keep page if linked, but not required for bot access |
| Mock `subscriptionActive` checks | Never use for authorization |

---

## 5. Integration architecture (planned)

```text
src/shared/api/
  apiClient.ts          — fetch + Bearer JWT + error mapping
  types.ts              — typed DTOs matching backend exactly
  tokenStore.ts         — sessionStorage JWT (no SSID)
  endpoints:
    authApi.ts
    accountApi.ts
    binollaApi.ts
    strategiesApi.ts
    marketApi.ts
    tradesApi.ts

src/shared/telegram/
  telegramWebApp.ts     — read Telegram.WebApp.initData safely

Existing *Service.ts adapters
  → call real APIs instead of mocks
  → preserve return shapes expected by hooks/UI
```

---

## 6. Risks / constraints

1. Backend production referral is `Unknown` → UI must show **Referral verification required** honestly.
2. Trading requires `botAccess: Allowed` — frontend cannot bypass.
3. Charts currently use static PNG — replace data path with real candles without redesigning layout if possible.
4. No frontend test suite — verify with `npm run build` + `npm run typecheck` + backend `dotnet test`.
5. Preserve visual design; only swap data sources.

---

## 7. Audit complete — ready to implement

Implementation may begin after this document exists. Do not invent endpoints or response fields beyond `backend/API.md` and actual DTOs.
