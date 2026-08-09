# PHASE 6 Report — Frontend Integration

**Date:** 2026-08-05  
**Status:** Implemented + verified (build + backend regression)

---

## 1. Frontend audit

See `PHASE6_FRONTEND_AUDIT.md` (pre-implementation mapping of screens → mocks → real endpoints).

Key finding: the webapp was UI-complete with **in-memory mocks only** — no HTTP client, no Telegram.WebApp, no JWT storage, no `VITE_*` env.

---

## 2. Existing screens preserved

Preserved layouts/routes/components for:

- Splash, Onboarding, Login, Signup (visual shell kept)
- Dashboard (`/home`), AI Bot (`/bot`), Trading, History, Account
- Bottom navigation / FAB
- Strategy / pair / chart sheets (same components)

**Not redesigned:** colors, typography, spacing, navigation, CSS architecture.

---

## 3. Mock services replaced (active runtime)

| Service | Now uses |
|---|---|
| `authService` | `POST /api/auth/telegram` |
| `accountService` | `GET /api/me`, `GET /api/account/status`, `POST /api/binolla/connect` |
| `dashboardService` | me + account status + balance + trades |
| `homeService` | strategies + market assets/candles + RSI signal + balance |
| `tradingService` | binolla/status/balance + market price + RSI signal + trades |
| `tradeService` | `GET/POST /api/trades` (+ candles for detail) |

Mock files remain as **copy/layout seeds** only (labels, icons, duration chips). Fake balances/trades/signals are no longer authoritative in active paths.

Notifications remain local (no backend notifications API in Phases 1–5).

---

## 4. Authentication integration

```
Telegram.WebApp.initData
  → POST /api/auth/telegram
  → JWT stored in sessionStorage (tokenStore)
  → Authorization: Bearer <JWT>
```

- Never uses `initDataUnsafe` for auth
- Never stores Binolla SSID
- Never logs JWT/initData
- Splash bootstraps Telegram + auth + account status routing
- Login/Signup submit now call Telegram auth (email/password obsolete)
- Activation key flow redirects away (obsolete)
- `BotLayout` enforces `requiresAuth` via token presence

Env: `VITE_API_BASE_URL` (see `.env.development` / `.env.example`).  
Optional local-only: `VITE_DEV_TELEGRAM_INIT_DATA` (not for production).

---

## 5. Binolla connection integration

- Connect via Account → **Edit Profile / Link Binolla**
- Field accepts Binolla **SSID** (sent once to `POST /api/binolla/connect`)
- SSID is **not** persisted in frontend snapshot/storage
- After connect, UI refreshes from `GET /api/account/status`

---

## 6. Account status integration

`GET /api/account/status` drives:

- Binolla connected
- Account type
- Referral status
- Bot access

Displayed on Account details/badges.

---

## 7. Referral status behavior (honest)

Production backend currently returns:

- `referralStatus: Unknown`
- `botAccess: ReferralVerificationRequired`

Frontend **does not** convert this to Eligible.  
Account badges and disclaimers show **Referral verification required**.

---

## 8. Strategy integration

`GET /api/strategies` populates the strategy sheet:

- RSI → Active / Enabled / selectable
- EMA/MACD/AI → Coming Soon / disabled (button disabled)

Frontend cannot enable disabled strategies (`homeService.updateRuntime` rejects disabled ids).

---

## 9. RSI signal integration

`GET /api/strategies/rsi/signal/{asset}?period=60` feeds:

- Bot engine signal stats
- Trading signal card

Displays Call / Put / **None** honestly.  
**No frontend RSI calculation.**  
**No automatic trade from RSI.**

---

## 10. Market integration

- Assets → trading pair sheet
- Price → trading card price
- Candles → bot chart sheet + trade detail chart
- On failure: show unavailable/empty — **no fake candles**

Trading card decorative PNG slot retained for layout; live price comes from API.

---

## 11. Trade integration

Manual Demo trades via `POST /api/trades`:

- `asset`, `direction` (CALL/PUT), `amount`, `durationSeconds`, `strategyId: "rsi"`
- Unique `Idempotency-Key` per intentional trade
- Backend remains authoritative for access/strategy/Demo/rate limits
- Errors surfaced via alert with mapped API codes

---

## 12. Trade history integration

`GET /api/trades?page=&pageSize=` powers History + dashboard recent markets.  
Statuses/PnL come from backend DTOs only.

---

## 13. Error handling

Central `apiClient` maps codes including:

`BINOLLA_NOT_CONNECTED`, `BINOLLA_SESSION_EXPIRED`, `REFERRAL_VERIFICATION_REQUIRED`, `REFERRAL_NOT_ELIGIBLE`, `MARKET_UNAVAILABLE`, `INSUFFICIENT_BALANCE`, `RATE_LIMITED`, `INVALID_TRADE`, `STRATEGY_DISABLED`, `UNAUTHORIZED`

401 clears token.

---

## 14. Token handling

- `sessionStorage` keys: `scaralpha.jwt`, `scaralpha.userId`
- Cleared on logout / 401
- All protected requests go through `apiRequest`

---

## 15. Idempotency handling

`createIdempotencyKey()` (`crypto.randomUUID`) attached as `Idempotency-Key` on each `POST /api/trades`.

---

## 16. Tests

Frontend has **no** `test`/`lint` scripts in `package.json`.

Verified:

| Command | Result |
|---|---|
| `npm run typecheck` | **Passed** |
| `npm run build` | **Passed** |
| `dotnet test ScarAlpha.sln -c Release` | **Passed** (10 Binolla + 42 API) |

---

## 17. Build result

`npm run build` → Vite production build succeeded.

---

## 18. Backend regression result

All Phase 1–5 tests remain green (52 total).

API contracts unchanged — `API.md` not modified.

---

## 19. Remaining limitations

1. **Referral still Unknown in production** until external provider exists.
2. **Trading requires Allowed access** — users will see pending referral until verification exists (honest).
3. **No frontend unit/e2e suite** yet (project had none).
4. **Notifications** still local/in-memory.
5. **Dashboard P/L aggregates** cleared to `—` (backend has no aggregate endpoint).
6. **Bot start/pause/stop** remain UI runtime only — do **not** auto-place trades.
7. Outside Telegram, auth needs `VITE_DEV_TELEGRAM_INIT_DATA` or real Mini App context.

---

## Success criteria checklist

AUTH:

- [x] Telegram Mini App auth wired
- [x] JWT flow works
- [x] protected requests send JWT
- [x] 401/403 handled via client

BINOLLA:

- [x] link via connect API
- [x] account status from backend
- [x] SSID not stored/logged/exposed
- [x] session-expired mapped

ACCESS:

- [x] subscription/activation removed from active menu/path
- [x] no paid-plan requirement
- [x] referral shown honestly (Unknown → verification required)
- [x] frontend cannot bypass access

STRATEGIES / RSI / TRADING / MARKET / HISTORY / UI / QUALITY:

- [x] as implemented above
- [x] build + typecheck + backend tests green

**STOP** — awaiting review. No Real trading, no auto RSI trading, no EMA/MACD/AI, no Redis, no deploy, no redesign.
