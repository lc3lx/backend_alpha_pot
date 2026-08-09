# PHASE 4 Report — Business Model Refactor

**Date:** 2026-08-05  
**Status:** Complete  
**Tests:** `dotnet test ScarAlpha.sln -c Release` — **39 passed** (Binolla 10, API 29)

---

## 1. Old business model

- Frontend mocks assumed subscription / activation keys / paid access / expiry.
- `Subscription` entity and `subscriptions` table were scaffolded in Phase 2 but **never enforced** in backend authorization.
- No activation endpoints existed server-side.
- Binolla connect was treated as “connect trading account for any authenticated user.”

---

## 2. New business model

```
User registers on Binolla via our referral link
        ↓
User opens Telegram Mini App
        ↓
Telegram initData → JWT → Application User
        ↓
POST /api/binolla/connect → link Binolla session
        ↓
Server-side referral eligibility check
        ↓
Eligible → FREE bot access (no subscription, no keys, no expiry)
        ↓
Only RSI strategy enabled for manual Demo trades
```

**Identity layers:**
- Application identity: Telegram → JWT → `UserId`
- Trading identity: `UserId` → `BinollaLink` → encrypted SSID → `IBinollaSessionManager`

---

## 3. Subscription logic removed / deprecated

| Item | Action |
|---|---|
| `Subscription` entity / `subscriptions` table | **Retained** (migration history, no data loss) |
| Subscription in authorization path | **Never existed**; confirmed absent |
| Activation keys | **Not implemented** server-side |
| Paid plan / expiry checks | **Not implemented**; not added |
| Trade access | Now uses `IBotAccessService` (referral + Binolla link) |

---

## 4. Binolla account linking flow

`POST /api/binolla/connect` (authenticated):

1. Validate SSID; reject `Real` account type.
2. Encrypt SSID (AES-GCM); never log or return SSID.
3. Connect per-user Binolla session via `IBinollaSessionManager`.
4. Force Demo account; fetch balance.
5. Run `IReferralEligibilityService.CheckAsync`.
6. Upsert `BinollaLink` with `ReferralStatus`, `ReferralCheckedAt`, connection metadata.
7. Return safe `BinollaConnectResponse` with `access` and `referralStatus`.

---

## 5. Referral verification mechanism

**Interface:** `IReferralEligibilityService`  
**Implementation:** `ReferralEligibilityService` (`ScarAlpha.Infrastructure.Access`)

Production default: returns `Unknown` with detail explaining external verification is required.

Test-only configuration (integration tests only, **not for production**):
- `Referral:Mode=TestEligible`
- `Referral:Mode=TestNotEligible`

---

## 6. Referral verification and Binolla protocol

**Investigation result:** The current Binolla WebSocket client/protocol does **NOT** expose reliable referral/affiliate attribution.

Not found in wire messages:
- affiliate ID, referrer ID, partner ID
- referral attribution flag
- account registration source
- stable account identifier suitable for referral lookup

SSID is a session token only — it does **not** prove referral ownership.

**Therefore:** Referral verification is **not faked**. Production users receive `referralStatus: Unknown` and `botAccess: ReferralVerificationRequired` until an external affiliate API or allowlist sync is integrated.

---

## 7. Free access rules

Access decision (`IBotAccessService`):

| Condition | `botAccess` |
|---|---|
| No Binolla link / session | `BinollaNotConnected` |
| Session auth failed / expired | `SessionExpired` |
| `ReferralStatus = Unknown` | `ReferralVerificationRequired` |
| `ReferralStatus = NotEligible` | `NotEligible` |
| `ReferralStatus = Eligible` + connected | `Allowed` |

No subscription, activation key, or expiry is checked. Price = 0 by design (no fake `plan=FREE` or `expiresAt=2099`).

---

## 8. Strategy registry

**Interface:** `IStrategyRegistry`  
**Implementation:** `StrategyRegistry` (`ScarAlpha.Infrastructure.Strategies`)

| ID | Name | Status | Enabled |
|---|---|---|---|
| `rsi` | RSI | Active | true |
| `ema` | EMA | ComingSoon | false |
| `macd` | MACD | ComingSoon | false |
| `ai` | AI | ComingSoon | false |

`StrategyAppService.EnsureStrategyEnabled` blocks disabled strategies on `POST /api/trades`.

---

## 9. RSI status

| Layer | State |
|---|---|
| Server catalog | RSI registered as **Active** / enabled |
| Signal generation | **Not implemented** — no RSI indicator or auto-trade pipeline in backend |
| Trade execution | Manual `POST /api/trades` with `strategyId: "rsi"` only |
| Frontend | Mock strategy UI (unchanged in Phase 4) |

RSI is honestly catalogued for the product model; automated RSI execution is **out of scope** for Phase 4.

---

## 10. Coming Soon strategies

EMA, MACD, and AI are registered with `status: ComingSoon` and `enabled: false`. Requests with those `strategyId` values receive `STRATEGY_DISABLED` (403). No fake implementations were added.

---

## 11. API changes

| Endpoint | Change |
|---|---|
| `POST /api/binolla/connect` | Returns `access`, `referralStatus`; links account for free-access model |
| `GET /api/account/status` | **New** — `binollaConnected`, `referralStatus`, `botAccess` |
| `GET /api/strategies` | **New** — server strategy catalog |
| `POST /api/trades` | Requires `strategyId`; gated by `IBotAccessService` |

New error codes: `REFERRAL_NOT_ELIGIBLE`, `REFERRAL_VERIFICATION_REQUIRED`, `BOT_ACCESS_DENIED`, `STRATEGY_DISABLED`, `STRATEGY_NOT_FOUND`.

See `API.md` for request/response shapes.

---

## 12. Database changes

Migration `20260805085322_BinollaLinkReferralFields` adds to `binolla_links`:

- `BinollaAccountIdentifier` (nullable, max 128) — reserved for future stable account ID if protocol exposes one
- `ReferralStatus` (int enum, default `Unknown`)
- `ReferralCheckedAt` (nullable timestamp)

`subscriptions` table **not dropped**.

---

## 13. Security model

| Concern | Enforcement |
|---|---|
| User identity | JWT → `UserId` |
| Binolla identity | `BinollaLink` + encrypted SSID + session manager |
| Referral | Server-side `IReferralEligibilityService` only; client cannot set status |
| Strategy permission | Server-side `IStrategyRegistry` only |
| SSID secrecy | Encrypted at rest; never in API responses or logs |
| Real trading | Still blocked (`REAL_TRADING_DISABLED`) |

---

## 14. Tests

**Phase 4 tests** (`Phase4Tests.cs`):
- Strategy catalog (RSI active, EMA/MACD coming soon)
- Connect with test-eligible → `Allowed`
- Account status reflects linked state
- Disabled strategy (`macd`) blocked
- Production-like Unknown referral → `ReferralVerificationRequired`
- Not-eligible user blocked from trading

**Regression:** Existing Phase 1–3 tests updated with `Referral:Mode=TestEligible` in test host only.

**Result:** 39/39 passed.

---

## 15. Remaining gaps

1. **Referral verification** — requires Binolla affiliate/partner API or operational allowlist sync; protocol alone is insufficient.
2. **Binolla account identifier** — no stable account ID available from current protocol; field reserved on `BinollaLink`.
3. **RSI automation** — no server-side signal generation or auto-trade pipeline; manual trades only.
4. **Frontend** — still uses subscription/activation mocks; refactor deferred to a later phase.
5. **Production access** — until external referral verification exists, users will see `ReferralVerificationRequired` (by design, not faked).

---

## Files added / changed (summary)

**New:**
- `Application/Abstractions/AccessAndStrategy.cs`
- `Application/Services/AccountAppService.cs`
- `Application/Services/StrategyAppService.cs`
- `Infrastructure/Access/ReferralEligibilityService.cs`
- `Infrastructure/Access/BotAccessService.cs`
- `Infrastructure/Strategies/StrategyRegistry.cs`
- `ScarAlpha.Api.Tests/Phase4Tests.cs`
- `PHASE4_AUDIT.md`, `PHASE4_REPORT.md`

**Updated:**
- `BinollaAppService`, `TradeAppService`, `Dtos`, `ApiEndpoints`, `Program.cs`, `DependencyInjection.cs`
- `BinollaLink` entity, `DomainEnums`, repositories, `AppDbContext`
- `ApiIntegrationTests`, `Phase3Tests` (test referral mode)
- `API.md`

**Not modified:** `bot_telegram_webapp/` (frontend), Real trading, Redis, deployment.

---

## Success criteria checklist

- [x] Subscription not required for access
- [x] Activation keys not required
- [x] No paid plan / expiry
- [x] Binolla account as trading identity
- [x] Telegram as Mini App auth identity
- [x] Binolla linked to User via `BinollaLink`
- [x] SSID encrypted, never returned/logged
- [x] Referral check server-side, not faked
- [x] Unknown → `ReferralVerificationRequired`
- [x] Eligible users get free access (when verifiable)
- [x] Non-eligible users blocked
- [x] RSI only active strategy
- [x] Other strategies Coming Soon / disabled
- [x] Backend blocks disabled strategies
- [x] Frontend unchanged
- [x] Real trading disabled
- [x] All tests pass
- [x] Documentation complete

**STOP** — await review before frontend integration or referral API integration.
