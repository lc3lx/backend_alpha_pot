# PHASE 8 Report — Production Hardening & End-to-End Security Audit

**Date:** 2026-08-05  
**Status:** Complete (awaiting review)  
**Scope:** Hardening only — no new product features.

Artifacts:
- `backend/PHASE8_AUDIT.md` (pre-change findings)
- `backend/PHASE8_REPORT.md` (this file)
- `backend/API.md` (Phase 7 model; access gate clarified below)

---

## 1. Audit scope

Verified Phases 1–7 reports against live code in:

- `ScarAlpha.Domain` / `Application` / `Infrastructure` / `Api` / `Binolla`
- `bot_telegram_webapp`
- Existing Phase 3–7 tests + new Phase 8 security matrix

---

## 2. Findings summary

| ID | Severity | Status |
|---|---|---|
| A4/Z1 Market/RSI/Balance skipped AdminApproved | HIGH | **Fixed** |
| Z2 Admin role never demoted | HIGH | **Fixed** (login sync + DB re-check) |
| S1 JWT Bearer hardcoded fallback secret | HIGH | **Fixed** |
| F1 Fake seed notifications | HIGH | **Fixed** |
| C2/SE1 SSID reconnect desync | MEDIUM | **Fixed** |
| M7 Future Telegram `auth_date` accepted | MEDIUM | **Fixed** |
| S2 RealBalance exposed | MEDIUM | **Fixed** (masked to 0) |
| Z3 Admin JWT-only trust | MEDIUM | **Fixed** (DB role check) |
| F2 SessionExpired → pending UX | MEDIUM | **Fixed** |
| F3 `/admin` open to non-admins | MEDIUM | **Fixed** (redirect) |
| P2 Auth/connect unlimited | MEDIUM | **Fixed** (rate limits) |
| T2 No amount upper bound | LOW | **Fixed** (max 100000) |
| T3 Broad unique-violation match | LOW | **Fixed** |
| P1 Weak defaults in appsettings | HIGH | **Mitigated** (Production startup refuse; DEV still local defaults) |
| A1/A3 In-memory sessions + idempotency | INFO | Documented remaining risk |
| P4 No HTTPS/HSTS in app | LOW | Remaining (edge/proxy) — **NOT VERIFIED** in this environment |

---

## 3. What was fixed

### Backend
1. **`EnsureAllowed` on Market, RSI signal, Binolla balance** — pending/rejected users cannot use bot market/signal/balance; connect/status/me remain available.
2. **Admin role demotion** on login from config; **AdminAppService** re-validates `User.Role == Admin` from DB.
3. **Approval state machine helper** keeps `AdminApproved` + `ApprovalStatus` synchronized; reject idempotent.
4. **SSID rotation** reconnects when connected session SSID changes (`BinollaSessionManager`).
5. **JWT Bearer** requires configured secret (no hardcoded fallback).
6. **Production** refuses known weak JWT/encryption/Telegram secrets.
7. **Telegram** rejects future `auth_date` (>2 min skew).
8. **Balance** returns Demo-only (`RealBalance = 0`).
9. **Trade amount** max 100000; tighter unique-violation detection.
10. **Rate limits** on auth + Binolla connect (+ existing trades).

### Frontend
1. Empty notifications (no fake approval/trade seeds).
2. SessionExpired dedicated notice + copy.
3. Non-admin redirected from `/admin`.
4. Trade buttons disabled unless `botAccess === Allowed`; client pre-check before place trade (backend still authoritative).

---

## 4. What was already secure

- Telegram initData HMAC validation (no `initDataUnsafe`)
- Users cannot self-approve via user APIs
- Admin endpoints policy + service gate
- SSID AES-GCM at rest; never returned in DTOs
- Trade list/get scoped by `userId` (no IDOR)
- Sessions keyed per userId
- Real trading blocked at multiple layers
- Demo trade state machine + Unknown recovery honesty
- DB unique `(UserId, IdempotencyKey)`
- Frontend JWT-only sessionStorage; SSID not persisted

---

## 5. Access-control verification

| State | account/status | market/RSI/balance | trades |
|---|---|---|---|
| No JWT | 401 | 401 | 401 |
| JWT, no Binolla | BinollaNotConnected | blocked | blocked |
| JWT + Binolla, pending | AdminApprovalRequired | **403** | **403** |
| JWT + Binolla + approved | Allowed | OK | OK |
| Rejected | NotEligible | 403 | 403 |

Connect / status / me remain available so pending users can complete onboarding.

---

## 6. Admin security

- Policy `AdminOnly` + DB role re-check
- Approve/reject by BinollaLink id; audit events without secrets
- Duplicate approve/reject idempotent
- Concurrent approve+reject → last write wins, fields remain consistent (tested)
- Normal users cannot list/approve (tested)

---

## 7. User isolation

- Sessions keyed by userId
- Trades filtered by userId (cross-user get → 404 tested)
- Concurrent A/B market requests covered by Phase 3 tests (approved users)

---

## 8. Session security

- One in-process session per user
- Idle eviction + disconnect/dispose
- SSID change forces reconnect
- Multi-instance stickiness: **NOT SUPPORTED** (documented)

---

## 9. Secret handling

| Item | Status |
|---|---|
| SSID in API responses | NOT FOUND |
| SSID in frontend storage | NOT FOUND |
| SSID in audit logs | NOT FOUND |
| JWT/initData in app logs | NOT FOUND (app services) |
| `appsettings.json` local defaults | FOUND (dev); Production startup rejects weak values |
| Frontend `.env` secrets | NOT FOUND (only `VITE_API_BASE_URL` in example/dev) |
| Git tracking of secrets | NOT VERIFIED (workspace not a git repo here) |

---

## 10. JWT / Telegram

- Issuer, audience, signature, lifetime validated
- Role claim not client-writable; demotion reflected in DB + admin re-check
- Future/tampered initData rejected (tested)
- Expired JWT / malformed JWT → 401 (tested)

---

## 11. Trade security

Server checks: auth, AdminApproved, Demo, strategy enabled, asset/direction/amount/duration bounds.

---

## 12. Idempotency guarantees

- In-process gate serializes same key
- DB unique `(UserId, IdempotencyKey)` is durable for single instance
- Same key → same trade (tested)
- Multi-instance: DB unique still prevents double insert; in-memory gate is best-effort
- Same key + different payload: returns original trade (does not re-execute) — existing PlaceTradeAsync behavior

---

## 13. RSI verification

No algorithm change. Access gate added only. Period 14 / 30 / 70 unchanged.

---

## 14. Database integrity

- Existing unique indexes retained
- Approval fields application-enforced consistency (no new destructive migration)
- Concurrent admin last-write-wins with consistent dual fields

---

## 15. API security notes

| Endpoint group | Auth | AdminApproved | Rate limit | Notes |
|---|---|---|---|---|
| `/api/auth/telegram` | Public | — | auth | HMAC required |
| `/api/me`, `/api/account/status` | JWT | report only | — | |
| `/api/binolla/connect` | JWT | no (creates pending) | connect | |
| `/api/binolla/status` | JWT | no | — | reconnect UX |
| `/api/binolla/balance` | JWT | **yes** | — | |
| `/api/market/*` | JWT | **yes** | — | |
| `/api/strategies` | JWT | no (catalog) | — | |
| `/api/strategies/rsi/signal/*` | JWT | **yes** | — | |
| `/api/trades` | JWT | **yes** (POST) | trades | GET own only |
| `/api/admin/*` | Admin | N/A | — | |
| `/health*` | Public | — | — | minimal |

---

## 16. Frontend security

- No self-approve controls
- Admin UI redirected for non-admins
- Trade buttons disabled when not Allowed
- Backend remains source of truth

---

## 17. Production configuration

- Override via env: `JWT_SECRET`, `BINOLLA_TOKEN_ENCRYPTION_KEY` / `Security:BinollaTokenEncryptionKey`, `TELEGRAM_BOT_TOKEN`, `Admin:TelegramUserIds`, `DATABASE_CONNECTION_STRING`, `CORS_ORIGINS`
- Production host fails fast on weak defaults

---

## 18. Test results

### Backend

```text
dotnet test ScarAlpha.sln -c Release
```

```text
ScarAlpha.Binolla.Tests: Passed 10
ScarAlpha.Api.Tests:     Passed 59
Total:                   69 passed, 0 failed
```

(Phase 8 added 12 security tests in `Phase8SecurityTests.cs`.)

### Frontend

```text
npm run typecheck  → pass
npm run build      → pass
npm run test       → not configured
npm run lint       → not configured
```

---

## 19. Remaining risks

1. **Single-instance Binolla sessions** — multi-node requires sticky sessions or shared store (no Redis this phase).
2. **In-memory idempotency gate** — DB unique is the durable control.
3. **Admin JWT until next login** — demotion blocks admin API via DB check immediately; other JWT claims refresh on re-auth.
4. **HTTPS/HSTS** — expect reverse proxy; not enforced in-process.
5. **Legacy activation/subscription routes** — still present for UI compatibility; do not authorize access.
6. **Git secret scanning** — NOT VERIFIED (no git metadata in this workspace).

---

## 20. Acceptance checklist

- [x] Protected bot endpoints enforce AdminApproved server-side (market/RSI/balance/trades)
- [x] Admin endpoints admin-only + DB role check
- [x] Users cannot self-approve
- [x] User isolation for trades
- [x] Sessions isolated by userId
- [x] SSID never leaked in API tests
- [x] JWT + Telegram validation hardened
- [x] Trade validation + idempotency verified
- [x] No fake notification seeds
- [x] Existing + new tests green
- [x] Frontend typecheck/build pass

---

## STOP

Phase 8 complete. Do **not** enable Real trading, automatic RSI, EMA/MACD/AI, referral API, Redis, or deploy.
