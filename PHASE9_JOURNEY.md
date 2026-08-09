# PHASE 9 Step 6 — User Journey Verification

**Date:** 2026-08-07  
**Method:** Code-path verification against implementation (no fabricated live Telegram E2E).

## Target flow

```text
New user → Telegram → Login → No Binolla → Connect → Pending → Approved
  → Market → RSI → Demo Trade → Trade History
```

| Step | Implementation | Verified how |
|---|---|---|
| Splash Telegram auth | `useSplashBootstrap` → `authService.loginWithTelegram` | Code + prior API tests |
| No Binolla | `botAccess=BinollaNotConnected` → Splash routes to `ROUTES.login` (Phase 9) | Code |
| Connect credentials | `POST /api/binolla/login\|signup` → ConnectAsync | API tests (mocked Playwright) |
| Pending | `AdminApprovalRequired`; Account notice; trades 403 | Phase 7/8 tests |
| Approved | Admin approve → `Allowed` | Phase 7 tests |
| Market / RSI | EnsureAllowed + live session | Phase 8 tests |
| Demo trade | `POST /api/trades` Demo-only | Phase 3/7 tests |
| History | `GET /api/trades` | Existing API tests |

## Edge cases

| Case | Behavior |
|---|---|
| Rejected | `NotEligible` → Settings notice; trades 403 |
| Disconnected (intentional) | Link Status=Disconnected; not restored |
| Expired SSID | Restore marks Disconnected; `SessionExpired`; Splash → login |
| API reboot | Session restore for approved Connected links (Step 2 tests) |
| Page refresh | JWT in sessionStorage; Splash/bootstrap re-checks status |
| Multiple tabs | Each tab has own sessionStorage JWT; share same backend sessions |

## NOT live-proven in this environment

- Real Telegram Mini App initData against production bot
- Real Playwright credential success (see `PHASE9_STEP3_LIVE_AUTH.md`)
- Multi-tab concurrent place-trade race (idempotency exists server-side)
