# ScarAlpha API (Phase 2–7)

Base URL: see `launchSettings.json`. Auth: `Authorization: Bearer <JWT>` unless noted.

Errors: `{ "code": "ERROR_CODE", "message": "..." }`

---

## Business model (Phase 7)

```text
Telegram identity
        ↓
Binolla linked
        ↓
Manual Admin Approval
        ↓
FREE access
```

**There is no automatic referral verification.**  
**Admin approval is the source of truth for access.**

There is **no subscription**, **no activation key**, and **no paid plan**.

Access gate:

```text
Telegram authenticated → Binolla linked → AdminApproved → Allowed
```

**Phase 8:** `AdminApproved` is required for market data, RSI signals, balance, and trade placement.  
`GET /api/account/status`, `POST /api/binolla/connect`, and `GET /api/binolla/status` remain available so pending users can connect and wait for approval.

---

## Health

### `GET /health`
```json
{ "status": "ok" }
```

### `GET /health/ready`
Database connectivity. Does not require Binolla sessions.
```json
{ "status": "ready", "database": true, "note": "..." }
```

---

## Auth

### `POST /api/auth/telegram`
```json
{ "initData": "<Telegram.WebApp.initData>" }
```
```json
{ "accessToken": "<jwt>", "userId": "<guid>" }
```

JWT includes role claim (`User` | `Admin`). Admin role is assigned server-side when the Telegram user ID is listed in config `Admin:TelegramUserIds` / `ADMIN_TELEGRAM_USER_IDS` (comma-separated), or the email is listed in `Admin:Emails` / `ADMIN_EMAILS`. The frontend cannot set the role.

### `POST /api/auth/register` · `POST /api/auth/login`
Website accounts (no Telegram required):

```json
{ "email": "trader@example.com", "password": "********", "fullName": "Optional", "country": "Optional", "username": "Optional" }
```
```json
{ "accessToken": "<jwt>", "userId": "<guid>" }
```

Password is stored hashed. Duplicate email → `EMAIL_TAKEN` (409). Bad login → `INVALID_CREDENTIALS` (401).

### `POST /api/auth/change-password` (JWT)
```json
{ "currentPassword": "***", "newPassword": "********" }
```
Telegram-only users without a password get `PASSWORD_NOT_SET`.

### `GET /api/me` · `PUT /api/me` (JWT)
Safe profile. Includes `role`, `isAdmin`, `email`, `hasPassword`. `binolla.connected` reflects **live** session when present. Never returns SSID.

```json
{
  "userId": "...",
  "telegramUserId": 123,
  "email": "trader@example.com",
  "hasPassword": true,
  "username": "trader",
  "fullName": "...",
  "country": null,
  "role": "User",
  "isAdmin": false,
  "binolla": { "connected": true, "accountType": "Demo", "status": "...", "lastConnectedAt": "...", "balance": 1000 }
}
```

`PUT /api/me` body: `{ "fullName", "country", "username" }` (any subset).

### Account extras (JWT)

- `GET /api/account/subscription` — free access card (approval-based, not paid plans)
- `GET /api/account/activation-history` — admin approve/reject audit for the current user

### Notifications (JWT)

- `GET /api/notifications`
- `GET /api/notifications/{id}`
- `POST /api/notifications/{id}/read`
- `POST /api/notifications/read-all`

Created on trade place/outcome and admin approve/reject.
---

## Account (JWT)

### `GET /api/account/status`
High-level bot access state. Source of truth for frontend.

```json
{
  "binollaConnected": true,
  "accountType": "Demo",
  "adminApproved": false,
  "approvalStatus": "Pending",
  "botAccess": "AdminApprovalRequired"
}
```

**`approvalStatus`:** `Pending` | `Approved` | `Rejected`

**`botAccess`:** `Allowed` | `BinollaNotConnected` | `AdminApprovalRequired` | `NotEligible` | `SessionExpired`

| Condition | botAccess |
|---|---|
| No Binolla link / not connected | `BinollaNotConnected` |
| Linked, awaiting admin | `AdminApprovalRequired` |
| Admin approved | `Allowed` |
| Admin rejected | `NotEligible` |
| Linked but session invalid | `SessionExpired` |

Referral fields are **not** returned and do **not** control access.

---

## Binolla (JWT, Demo-only)

### `POST /api/binolla/connect`
Links and verifies the user's Binolla account. SSID is encrypted at rest and **never** returned.

New connections start as `AdminApproved = false` / `approvalStatus = Pending` (existing Approved/Rejected state is preserved across reconnect).

```json
{ "ssid": "42[\"authorization\",{\"token\":\"...\"}]", "accountType": "Demo" }
```

### `POST /api/binolla/login`
Binolla email/password login (server-side capture via `tools/binolla-auth`, based on A11ksa/API-Binolla). Password is never stored. Returns the same shape as `/connect`.

```json
{ "email": "trader@example.com", "password": "***", "accountType": "Demo" }
```

### `POST /api/binolla/signup`
Binolla registration with partner referral URL, then connect. Same request/response as `/login`.

```json
{
  "connected": true,
  "accountType": "Demo",
  "access": "AdminApprovalRequired",
  "adminApproved": false,
  "approvalStatus": "Pending",
  "lastConnectedAt": "2026-08-05T08:00:00Z",
  "balance": 1000.0
}
```

`Real` account type → `REAL_TRADING_DISABLED` (403).

### Session restore (Phase 9)

On API startup, approved users with `Status=Connected` and a stored `EncryptedSsid` are restored asynchronously:

```text
API start → load approved links → decrypt SSID → reconnect (bounded parallelism + exponential backoff)
```

- Pending / Rejected users are **not** restored.
- Invalid / expired SSIDs are skipped; link marked `Disconnected`; access becomes `SessionExpired`.
- Partial failures never crash the host.
- SSID is never logged or returned.
- Lazy restore also runs on `GET /api/account/status` when an approved Connected link has no live session (e.g. after idle eviction).

### `GET /api/binolla/status` · `GET /api/binolla/balance`
### `POST /api/binolla/account-type` · `POST /api/binolla/disconnect`

---

## Admin (JWT + Admin role)

Policy: `AdminOnly` (`[Authorize]` equivalent via `RequireAuthorization("AdminOnly")` / role `Admin`).

Normal users receive `403` / `FORBIDDEN`.

SSID is **never** included in responses.

### `GET /api/admin/binolla/accounts?status=Pending|Approved|Rejected&q=&page=1&pageSize=50`
Lists accounts (optional status, search `q`, pagination). Response includes `items`, `total`, `page`, `pageSize`.

```json
{
  "items": [
    {
      "id": "...",
      "userId": "...",
      "telegramUserId": 123,
      "email": "user@example.com",
      "username": "trader",
      "fullName": "...",
      "binollaAccountIdentifier": "safe-id-or-null",
      "connectionStatus": "Connected",
      "approvalStatus": "Pending",
      "adminApproved": false,
      "lastConnectedAt": "...",
      "createdAt": "...",
      "approvedAt": null,
      "approvedBy": null
    }
  ],
  "total": 1,
  "page": 1,
  "pageSize": 50
}
```

### `GET /api/admin/binolla/accounts/{id}`

### `POST /api/admin/binolla/accounts/{id}/approve`
Sets `AdminApproved = true`, `ApprovalStatus = Approved`, `ApprovedAt`, `ApprovedBy` (admin identity). Writes audit event + user notification. Idempotent if already approved.

### `POST /api/admin/binolla/accounts/{id}/reject`
Sets `AdminApproved = false`, `ApprovalStatus = Rejected`. Does **not** delete the link. Writes audit event + user notification. User bot access becomes `NotEligible`.

### Marketing demo users
- `GET /api/admin/demo-users?active=true|false|all&page=&pageSize=`
- `POST /api/admin/demo-users` — create/promote (`email`/`password` and/or `telegramUserId` + optional `config`)
- `PATCH /api/admin/demo-users/{id}` — enable/disable demo, optional Telegram link, optional config
- `PUT /api/admin/demo-users/{id}/config` — update fake balance / P/L / `sampleTrades` etc.

Demo users skip Binolla; bot Mini App shows configured fake live data. Admins cannot be demos. Role bootstrap remains env `ADMIN_TELEGRAM_USER_IDS` / `ADMIN_EMAILS` (no promote API).

### Users / audit / notifications
- `GET /api/admin/users?q=&role=&isMarketingDemo=&page=&pageSize=`
- `GET /api/admin/users/{id}`
- `PATCH /api/admin/users/{id}` — `{ isMarketingDemo?, telegramUserId?, clearTelegramUserId?, config? }`
- `GET /api/admin/audit?userId=&action=&from=&to=&page=&pageSize=`
- `GET /api/admin/notifications?userId=&page=&pageSize=`
- `POST /api/admin/notifications` — `{ title, description, userIds?, allApprovedUsers?, variant?, actionPath? }` (broadcast to all approved requires explicit `allApprovedUsers: true`)

Desktop admin UI: website only at `/dashboard/admin/*` (`dashboard_web`). Not exposed in Telegram Mini App settings.

---

## Strategies (JWT)

### `GET /api/strategies`
Server-side strategy catalog. Only enabled strategies may be used in trades.

```json
{
  "strategies": [
    { "id": "rsi", "name": "RSI Smart Backtest", "status": "Active", "enabled": true },
    { "id": "ema", "name": "EMA", "status": "ComingSoon", "enabled": false },
    { "id": "macd", "name": "MACD", "status": "ComingSoon", "enabled": false },
    { "id": "ai", "name": "AI", "status": "ComingSoon", "enabled": false }
  ]
}
```

### `GET /api/strategies/rsi/signal/{asset}?period=60`
Signal-only RSI Smart Backtest. The endpoint accepts **only** one-minute candles.
Defaults: RSI length `14`, oversold `25`, overbought `75`, historical lookback
`400`, expiry `5` candles, and minimum success rate `75`.

Optional query settings: `rsiLength`, `oversold`, `overbought`,
`backtestCandles`, `expiryCandles` (`3`, `4`, or `5`), and
`minimumSuccessRate`. A CALL needs a closed RSI at/below oversold; a PUT needs
a closed RSI at/above overbought. The response is `None` unless same-direction
historical signals meet the minimum rate; its `backtest` object returns the
total, wins, losses, rate, and pass status. Open candles are excluded and the
historical sample ends before the current entry, so the calculation has no
lookahead. No automatic trading is performed by this endpoint.

---

## Market (JWT, requires connected Binolla session)

### `GET /api/market/assets`
### `GET /api/market/price/{asset}`
### `GET /api/market/candles/{asset}?period=60`

---

## Trades (JWT, Demo-only)

Requires: Telegram JWT, Binolla linked, **admin approved** (`botAccess: Allowed`), enabled strategy.

Rate limit (default): **30 / 60s** per user (`RateLimiting:Trades:*`). Exceeded → `429` + `RATE_LIMITED`.

### `POST /api/trades`
Headers: `Idempotency-Key: <unique>`

```json
{
  "asset": "EURUSD_otc",
  "direction": "CALL",
  "amount": 1,
  "durationSeconds": 60,
  "strategyId": "rsi"
}
```

Access errors:
- `ADMIN_APPROVAL_REQUIRED` — linked but not approved
- `NOT_ELIGIBLE` — rejected by admin
- `BINOLLA_NOT_CONNECTED` — no linked session
- `BINOLLA_SESSION_EXPIRED` — invalid/expired SSID
- `STRATEGY_DISABLED` — strategy not enabled
- `FORBIDDEN` — insufficient role (admin endpoints)

---

## Audit

Admin approve/reject writes `AuditEvent` records (who, which account, when, action, previous/new state).  
**Never logs:** SSID, passwords, JWT, tokens, Binolla credentials.

---

## Env

`TELEGRAM_BOT_TOKEN`, `JWT_SECRET`, `JWT_ISSUER`, `JWT_AUDIENCE`, `DATABASE_CONNECTION_STRING`, `BINOLLA_TOKEN_ENCRYPTION_KEY`, `CORS_ORIGINS`, optional `RateLimiting__Trades__PermitLimit` / `WindowSeconds`.

**Admin promotion (server-side only):**

`Admin:TelegramUserIds` or `ADMIN_TELEGRAM_USER_IDS` — comma-separated Telegram user IDs that receive role `Admin` on login. Not hardcoded in source.

---

## Obsolete contracts

- Referral eligibility as an access gate (`ReferralVerificationRequired`, `IReferralProvider`)
- Activation keys / paid subscription authorization
- Client-supplied `adminApproved` or role claims

Website email accounts are supported (`/api/auth/register` + `/login`). Telegram Mini App auth remains supported.

The **website dashboard** is a separate frontend (`dashboard_web`) served under `/dashboard/*`. The Telegram Mini App (`bot_telegram_webapp`) does not use the `/dashboard` prefix.
