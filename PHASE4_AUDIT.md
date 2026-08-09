# PHASE 4 Audit — Business Model Refactor

**Date:** 2026-08-05  
**Method:** Code inspection (backend + frontend mocks read-only).

---

## 1. Where subscription logic currently exists

| Location | State |
|---|---|
| `ScarAlpha.Domain.Entities.Subscription` | Entity exists: `ActivationKey`, `ExpiresAt`, `Status` |
| `ScarAlpha.Domain.Enums.SubscriptionStatus` | Enum exists |
| `AppDbContext` / migrations `subscriptions` table | Schema exists |
| `User.Subscriptions` navigation | Present |
| **Application services** | **No subscription repository or activation flow** |
| **API endpoints** | **None** — subscription never enforced |
| **Trade/Binolla access** | **Never checked subscription** |

Subscription was scaffolded in Phase 2 schema only. It is **not** on the active authorization path today.

---

## 2. Where activation logic currently exists

| Location | State |
|---|---|
| Frontend `bot_telegram_webapp` | Mock activation pages, keys, subscription UI |
| `features/Auth` `activate()` | Mock only |
| Backend | **No activation endpoints or services** |

---

## 3. What must be removed (from active path)

- Any future subscription/activation guards (none exist yet).
- Conceptual dependency on `Subscription` for bot access.
- Frontend contract assumptions in backend (activation keys, expiry).

**Do NOT drop** `subscriptions` table in this phase (migration history / data preservation).

---

## 4. What must be preserved

- Phase 1: `ScarAlpha.Binolla` engine, sessions, async trades.
- Phase 2: Telegram auth, JWT, encrypted SSID, PostgreSQL.
- Phase 3: Market APIs, Demo trading, idempotency, outcome worker, rate limits.
- `BinollaLink` as per-user Binolla identity store.
- Real trading blocked.

---

## 5. Current Binolla authentication capabilities

- `POST /api/binolla/connect` validates SSID via WebSocket `s_authorization`.
- SSID encrypted at rest (`AesGcmSecretProtector`).
- Per-user `IBinollaSessionManager` session.
- Balance, assets, quotes, candles, place order via engine.
- **No parsed user profile** from authorization payload (only lifecycle state change).

---

## 6. Referral/affiliate information in Binolla protocol

**Investigated:** `SessionMessageRouter`, `WireModels`, `BinollaWire`, all `s_*` events.

**Available wire data:** balances, assets, quotes, history, orders (uuid, uid per deal, amounts).

**NOT found in current client/protocol:**

- affiliate ID / referrer ID / partner ID
- referral attribution flag
- account registration source
- user profile with referral metadata
- any field tying account to our affiliate link

The `uid` on order deals is **order-scoped**, not a stable Binolla account identifier for referral checks.

**SSID does NOT prove referral ownership** — it is a session token only.

---

## 7. Technical gap preventing reliable referral verification

| Gap | Impact |
|---|---|
| No referral field in WebSocket messages | Cannot verify server-side from engine alone |
| No Binolla affiliate API integrated | Need external partner/affiliate API or admin export |
| No allowlist sync | Manual ops process required until API exists |

**Phase 4 approach:** `IReferralEligibilityService` returns `Unknown` by default.  
Tests use `Referral:Mode=TestEligible` **only in test host config** — never production default.

---

## 8. RSI strategy state

| Layer | State |
|---|---|
| Backend | **No RSI implementation** — no strategy code in `backend/` |
| Frontend | Mock strategy picker (`home.mock.ts`): Alpha Momentum, OTC Hunter, etc. RSI is an **indicator** option, not a server strategy |
| Trade engine | Manual `POST /api/trades` only — no signal generation |

**Honest status:** RSI is **catalogued server-side as Active** for product model; **no automated RSI execution pipeline** exists yet.

---

## 9. Frontend obsolete contracts (do not modify in Phase 4)

- `/activation`, subscription pages, activation keys
- `authService.activate()`, `SignupPayload.binollaAccount` email flow
- Mock subscription expiry / paid plan UI

Backend will expose `GET /api/account/status` and `GET /api/strategies` as future source of truth.
