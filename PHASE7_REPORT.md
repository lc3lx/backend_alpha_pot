# PHASE 7 Report — Admin Approval / Manual Access Control

**Date:** 2026-08-05  
**Status:** Complete (awaiting review)

---

## 1. Business model

```text
Telegram identity → Binolla linked → Manual Admin Approval → FREE access
```

- There is **no** automatic Binolla referral API.
- There is **no** subscription, activation key, or paid plan.
- **Admin approval is the source of truth** for bot access.

---

## 2. What changed

### Domain / DB
- `BinollaLink`: `AdminApproved`, `ApprovalStatus` (`Pending` | `Approved` | `Rejected`), `ApprovedAt`, `ApprovedBy`
- `User.Role`: `User` | `Admin`
- `AuditEvent` entity for approval actions
- EF migration: `BinollaAdminApproval`
- Legacy `ReferralStatus` column retained in DB but **not** used for authorization

### Access
- `IBotAccessService` / `BotAccessService` gates on:
  - Telegram auth
  - Binolla linked + live session
  - `AdminApproved` / approval status
- Production states: `Allowed`, `BinollaNotConnected`, `AdminApprovalRequired`, `NotEligible`, `SessionExpired`
- Removed referral providers/services from the authorization path

### Connect flow
- Connect does **not** auto-approve
- New links start pending
- Existing Approved/Rejected preserved across reconnect

### Admin API (`AdminOnly` policy)
- `GET /api/admin/binolla/accounts`
- `GET /api/admin/binolla/accounts/{id}`
- `POST /api/admin/binolla/accounts/{id}/approve`
- `POST /api/admin/binolla/accounts/{id}/reject`
- Role from JWT claim; admins promoted via `Admin:TelegramUserIds` config (not hardcoded IDs in code)
- SSID never in responses

### Account status
```json
{
  "binollaConnected": true,
  "accountType": "Demo",
  "adminApproved": false,
  "approvalStatus": "Pending",
  "botAccess": "AdminApprovalRequired"
}
```

### Trades / strategies
- Trades require `Allowed` (admin approved) + enabled strategy (RSI)
- Demo-only unchanged; Real still disabled
- RSI algorithm unchanged (period 14, 30/70)

### Frontend
- Pending / rejected notice on Account page
- Access copy updated (home, trading, dashboard, splash)
- Admin Approvals page (`/admin`) with Pending / Approved / Rejected + Approve/Reject
- Admin menu item shown when `me.isAdmin`
- Onboarding step 2 copy no longer mentions activation keys / paid subscription

---

## 3. Security

| Rule | Status |
|---|---|
| SSID never in API responses | Yes |
| SSID never in frontend storage | Yes (session JWT only) |
| User cannot self-approve | Yes (admin endpoints + role) |
| Normal user cannot call admin API | Yes (`AdminOnly` / `FORBIDDEN`) |
| No hardcoded admin Telegram ID in source | Yes (config list) |
| Secrets not logged in audit | Yes |

---

## 4. Tests / build

| Command | Result |
|---|---|
| `dotnet test ScarAlpha.sln -c Release` | Passed — Binolla 10 + API 47 = **57** |
| `npm run typecheck` | Pass (after Admin menu typing fix) |
| `npm run build` | Pass |
| `npm run test` / `npm run lint` | Not configured in `package.json` |

---

## 5. Remaining limitations (intentional)

- Real trading still disabled
- No automatic RSI trading
- EMA / MACD / AI still ComingSoon
- Admin role bootstrap via config Telegram IDs (not a full IAM UI)
- Legacy activation/subscription pages remain in the router for compatibility but do not authorize access
- Legacy `ReferralStatus` column unused for auth

---

## 6. Success criteria checklist

### Business
- [x] no subscription / activation keys / paid access as auth
- [x] no automatic referral API
- [x] manual admin approval controls access
- [x] approved users get FREE access

### Backend
- [x] AdminApproved state + migration
- [x] admin-only endpoints + role auth + audit
- [x] account status + trades require approval

### Frontend
- [x] pending / approved / rejected UI
- [x] admin dashboard with approve/reject
- [x] no fake referral verification UI

### Security
- [x] no SSID exposed; no self-approve; backend enforces approval

---

## 7. Stop

Phase 7 stops here. Do not enable Real trading, auto RSI, EMA/MACD/AI, referral API, Redis, or deploy in this phase.
