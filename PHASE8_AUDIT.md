# PHASE 8 Audit — Production Hardening & Security

**Date:** 2026-08-05  
**Method:** Code inspection of Phases 1–7 reports + live implementation in backend and `bot_telegram_webapp`.  
**Rule:** Do not trust reports blindly; findings below are from source.

---

## 1. Architecture findings

| ID | Severity | Finding |
|---|---|---|
| A1 | INFO | Multi-user Binolla sessions are in-process (`BinollaSessionManager` ConcurrentDictionary). Documented single-instance limitation; no Redis by design. |
| A2 | INFO | Trade outcome worker is in-process; restart marks open trades Unknown honestly. |
| A3 | MEDIUM | Idempotency gate is in-memory; DB unique `(UserId, IdempotencyKey)` is the durable safeguard. Acceptable for single-instance. |
| A4 | HIGH | `EnsureAllowed` (AdminApproved) is called **only** from `TradeAppService.PlaceTradeAsync`. Market, RSI signal, and Binolla balance require only a live Binolla session. **Conflicts with Phase 8 access model** (Telegram + Binolla + AdminApproved for bot use). |

---

## 2. Security findings

| ID | Severity | Finding |
|---|---|---|
| S1 | HIGH | `appsettings.json` commits local JWT secret, encryption key, Telegram placeholder, DB password. Must be overridden externally in production; JWT Bearer also falls back to a hardcoded default secret. |
| S2 | MEDIUM | Balance DTO exposes `RealBalance` while Real trading is disabled. |
| S3 | LOW | Serilog request logging does not log bodies; Binolla layer avoids SSID logs. Good. |
| S4 | INFO | Health endpoints expose DB boolean only — acceptable. |

---

## 3. Authorization findings

| ID | Severity | Finding |
|---|---|---|
| Z1 | HIGH | Pending/rejected users with live sessions can call market + RSI + balance. |
| Z2 | HIGH | Admin role is promoted from config on login but **never demoted** when Telegram ID is removed; long-lived JWT (7d) keeps Admin claim. |
| Z3 | MEDIUM | Admin endpoints trust JWT role only; no DB role re-check on approve/reject. |
| Z4 | INFO | Users cannot set `AdminApproved` via any user API; connect never auto-approves. Secure. |
| Z5 | INFO | Trade get/list always scoped by `userId` — no IDOR observed. |

---

## 4. Concurrency findings

| ID | Severity | Finding |
|---|---|---|
| C1 | MEDIUM | Concurrent admin Approve + Reject = last-write-wins; both fields are always updated together (no split state from that path). |
| C2 | MEDIUM | `GetOrCreateAsync` returns an existing Connected session **without** applying a new SSID → DB `EncryptedSsid` can desync from live session. |
| C3 | INFO | Per-user session keys prevent A/B session leakage across users. |

---

## 5. Session findings

| ID | Severity | Finding |
|---|---|---|
| SE1 | MEDIUM | SSID rotation while Connected is broken (see C2). |
| SE2 | INFO | Idle eviction, disconnect, dispose paths exist. |
| SE3 | INFO | Multi-instance session stickiness is NOT VERIFIED / not supported. |

---

## 6. Trading findings

| ID | Severity | Finding |
|---|---|---|
| T1 | INFO | Demo-only enforced at connect, account-type, and place-trade. |
| T2 | LOW | Amount has no upper bound (only `> 0`); relies on Demo balance. |
| T3 | LOW | `IsUniqueViolation` string-matches `"unique"`/`"duplicate"` broadly. |
| T4 | INFO | Trade state machine ignores invalid transitions; restart → Unknown is honest. |

---

## 7. Admin findings

| ID | Severity | Finding |
|---|---|---|
| AD1 | INFO | Dual gate: `AdminOnly` policy + `EnsureAdmin()`. |
| AD2 | MEDIUM | Approval state machine allows Rejected→Approved and Approved→Rejected (intentional re-review). Must keep `AdminApproved` ↔ `ApprovalStatus` synchronized (already set together). |
| AD3 | LOW | Admin list has no pagination. |

---

## 8. Frontend findings

| ID | Severity | Finding |
|---|---|---|
| F1 | HIGH | Fake seed notifications (`SEED_NOTIFICATIONS`) always shown — misleading approval/trade messages. |
| F2 | MEDIUM | `SessionExpired` mapped to `accountStatus: 'pending'` → wrong “waiting for approval” notice. |
| F3 | MEDIUM | `/admin` route is JWT-only; non-admins can open page (API still 403). |
| F4 | MEDIUM | No client soft-gate on trade buttons; relies on backend (backend must enforce — and will after Z1 fix). |
| F5 | INFO | SSID not in storage; JWT in sessionStorage only; no `initDataUnsafe`; no token console logs. Secure. |

---

## 9. Database findings

| ID | Severity | Finding |
|---|---|---|
| D1 | INFO | Unique: User.TelegramUserId, BinollaLink.UserId, Trade.(UserId,IdempotencyKey). |
| D2 | MEDIUM | No DB check constraint tying `AdminApproved` to `ApprovalStatus` (application must keep them synced). |

---

## 10. Production-readiness findings

| ID | Severity | Finding |
|---|---|---|
| P1 | HIGH | Weak default secrets + JWT Bearer fallback. |
| P2 | MEDIUM | Rate limiting only on trades; auth/connect unlimited. |
| P3 | MEDIUM | Telegram `auth_date` in the future is accepted; MaxAuthAgeHours default 24h. |
| P4 | LOW | No HTTPS redirect/HSTS in app (acceptable behind reverse proxy — document). |
| P5 | INFO | Single-instance Binolla sessions + in-memory idempotency gate — document as remaining risk. |

---

## 11. Conflict check

No product-model conflict requiring STOP.  
Access-gate gap (A4/Z1) is a **hardening fix**: extend `EnsureAllowed` to market/RSI/balance while keeping connect/status/me available for pending users.

**Proceed to fixes.**
