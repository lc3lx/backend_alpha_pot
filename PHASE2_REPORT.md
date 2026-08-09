# PHASE 2 Report — ASP.NET Core API + Telegram WebApp Auth

**Status: COMPLETE**

Scope held: no frontend changes, no real-money trading, no strategies, no Redis, no production deployment. Phase 1 `ScarAlpha.Binolla` engine left intact and reused via `IBinollaSessionManager`.

---

## 1. Architecture

```text
Telegram Mini App
        ↓
Telegram initData
        ↓
ScarAlpha.Api  (minimal endpoints + JWT bearer + CORS + rate limit)
        ↓
Telegram HMAC validation (Infrastructure)
        ↓
Application services (Auth / Me / Binolla / Trade)
        ↓
JWT (sub = User.Id, telegram_user_id)
        ↓
Authenticated UserId (never from client query)
        ↓
BinollaSessionManager → per-user BinollaSession (Phase 1)
```

Layering:

| Project | Responsibility |
|---|---|
| `ScarAlpha.Api` | HTTP endpoints, middleware, Serilog, CORS, trade rate limiting |
| `ScarAlpha.Application` | Use cases, DTOs, interfaces, `ApiException` codes |
| `ScarAlpha.Domain` | `User`, `BinollaLink`, `Subscription`, `Trade`, enums |
| `ScarAlpha.Infrastructure` | EF Core/PostgreSQL, Telegram, JWT, AES-GCM protector, repos, outcome worker |
| `ScarAlpha.Binolla` | Independent Phase 1 engine (unchanged as source of truth) |

---

## 2. Projects created

```text
backend/
  ScarAlpha.Api/
  ScarAlpha.Application/
  ScarAlpha.Domain/
  ScarAlpha.Infrastructure/
  ScarAlpha.Api.Tests/
  ScarAlpha.Binolla/          (Phase 1 — unchanged)
  ScarAlpha.Binolla.Tests/
  ScarAlpha.Binolla.Smoke/
  API.md
  PHASE2_REPORT.md
```

---

## 3. Database schema

PostgreSQL via EF Core. Migration: `ScarAlpha.Infrastructure/Persistence/Migrations/20260804230732_InitialCreate.cs`

| Table | Notes |
|---|---|
| `users` | Unique index on `TelegramUserId` |
| `binolla_links` | 1:1 with user; stores **encrypted** SSID only |
| `subscriptions` | Activation key / expiry / status (schema ready; activation flow not Phase 2 auth path) |
| `trades` | Unique `(UserId, IdempotencyKey)`; PnL/status updated async |

Startup applies `MigrateAsync()` for relational providers; tests use InMemory + `EnsureCreated`.

---

## 4. Endpoints

| Method | Path | Auth | Notes |
|---|---|---|---|
| `GET` | `/health` | No | Liveness |
| `POST` | `/api/auth/telegram` | No | `{ initData }` → `{ accessToken, userId }` |
| `GET` | `/api/me` | JWT | Safe profile |
| `POST` | `/api/binolla/connect` | JWT | Encrypt SSID, Demo-only connect |
| `GET` | `/api/binolla/status` | JWT | Connection status |
| `GET` | `/api/binolla/balance` | JWT | Demo/real balances (engine) |
| `POST` | `/api/binolla/account-type` | JWT | Demo only; Real rejected |
| `POST` | `/api/binolla/disconnect` | JWT | Disconnect session + mark link |
| `POST` | `/api/trades` | JWT + `Idempotency-Key` | Demo place order; async outcome |
| `GET` | `/api/trades` | JWT | List current user's trades |
| `GET` | `/api/trades/{id}` | JWT | Owned trade only |

See `API.md` for request/response shapes.

---

## 5. Telegram auth flow

1. Client sends `Telegram.WebApp.initData` as `{ "initData": "..." }`.
2. `TelegramAuthService` validates HMAC-SHA256 per Telegram WebApp rules (`secret = HMAC("WebAppData", botToken)`).
3. Rejects missing/invalid hash, expired `auth_date`, missing `user.id`.
4. Find-or-create `User` by `TelegramUserId`.
5. Issue JWT; return `{ accessToken, userId }` (matches frontend `AuthSession`).

Client-supplied `user.id` / username without a valid signed `initData` is never trusted.

---

## 6. JWT claims

| Claim | Value |
|---|---|
| `sub` | Internal `User.Id` (GUID) |
| `telegram_user_id` | Telegram numeric id |
| `jti` | Unique token id |
| `username` | Optional |

Not included: SSID, encryption keys, bot token, passwords.

Bearer validation uses `IConfiguration` via `Configure<JwtBearerOptions, IConfiguration>` so signing key always matches the token issuer (important for tests and env overrides).

---

## 7. Binolla session ownership

- Every Binolla/trade call resolves `UserId` from JWT via `ICurrentUser`.
- Session key = `userId.ToString()` into `IBinollaSessionManager`.
- Trade/get-by-id queries filter by authenticated `UserId` → User A cannot read User B trades.
- No `userId` query/body parameter is accepted for ownership.

---

## 8. Encryption approach

- `AesGcmSecretProtector` (AES-GCM, 12-byte nonce + 16-byte tag, Base64 payload).
- Key from `BINOLLA_TOKEN_ENCRYPTION_KEY` (32-byte Base64 or any string hashed with SHA-256).
- Plaintext SSID never persisted; responses never include `ssid` / ciphertext.
- Logs use `UserId` only — never SSID, JWT, bot token, or encryption key.

---

## 9. Idempotency

- `POST /api/trades` requires header `Idempotency-Key`.
- Lookup by `(UserId, IdempotencyKey)` before placing; return existing trade if present.
- Unique DB index enforces concurrency safety (Postgres); race → unique violation → re-fetch.

---

## 10. Error model

Safe JSON: `{ "code": "...", "message": "..." }`

| Code | Typical HTTP |
|---|---|
| `UNAUTHORIZED` | 401 |
| `TELEGRAM_AUTH_INVALID` | 401 |
| `BINOLLA_NOT_CONNECTED` | 409 |
| `BINOLLA_SESSION_EXPIRED` | 401 |
| `BINOLLA_CONNECTION_FAILED` | 502 |
| `BINOLLA_MARKET_UNAVAILABLE` | 400 |
| `INSUFFICIENT_BALANCE` | 400 |
| `INVALID_TRADE` | 400 |
| `REAL_TRADING_DISABLED` | 403 |
| `DUPLICATE_REQUEST` | 409 |
| `NOT_FOUND` | 404 |
| `VALIDATION_ERROR` | 400 |

Unhandled exceptions → `INTERNAL_ERROR` without stack traces.

---

## 11. Security decisions

- Telegram initData HMAC validation (server-side).
- JWT Bearer; identity only from validated token.
- SSID encrypted at rest; never returned or logged.
- Demo-only trading and Real account connect blocked (`REAL_TRADING_DISABLED`).
- CORS origins from `CORS_ORIGINS` / `Cors:Origins`.
- Fixed-window rate limit policy `trades` (30/min per user/IP) on `/api/trades`.
- Serilog request logging without sensitive headers/bodies.
- HTTPS-ready (Kestrel / reverse proxy); secrets via env/config only.

---

## 12. Test results

Command:

```powershell
cd d:\work\flul_bot\backend
dotnet test ScarAlpha.sln -c Release
```

| Suite | Result |
|---|---|
| `ScarAlpha.Binolla.Tests` (Phase 1) | **8/8 passed** |
| `ScarAlpha.Api.Tests` (Phase 2) | **13/13 passed** |

API coverage includes: invalid/valid Telegram initData, same-user stability, JWT accept/reject, cross-user isolation, connect without SSID leakage, encrypted at rest, Demo balance via mocked engine, Real rejected, idempotency, invalid trade, disconnected session, encryption round-trip.

Automated tests mock `IBinollaSessionManager` / `IBinollaClient`. Live Demo remains `ScarAlpha.Binolla.Smoke` only.

---

## 13. Commands to run

```powershell
cd d:\work\flul_bot\backend

# All tests
dotnet test ScarAlpha.sln -c Release

# API only
dotnet test ScarAlpha.Api.Tests -c Release

# Run API (requires PostgreSQL + env)
dotnet run --project ScarAlpha.Api -c Release

# Apply migrations explicitly (also auto-applied on API start)
dotnet ef database update --project ScarAlpha.Infrastructure --startup-project ScarAlpha.Api
```

---

## 14. Environment variables

| Variable | Purpose |
|---|---|
| `TELEGRAM_BOT_TOKEN` | WebApp initData HMAC |
| `JWT_SECRET` | HMAC signing key |
| `JWT_ISSUER` | Token issuer |
| `JWT_AUDIENCE` | Token audience |
| `DATABASE_CONNECTION_STRING` | PostgreSQL (or `ConnectionStrings:Default`) |
| `BINOLLA_TOKEN_ENCRYPTION_KEY` | AES-GCM key material |
| `CORS_ORIGINS` | Comma-separated allowed origins |

Dev placeholders exist in `appsettings.json` — replace before any shared/staging use.

---

## 15. Frontend contract notes (no frontend code changed)

Inspected only:

- `features/Auth/services/authService.ts` → `AuthSession { accessToken, userId }` — **preserved** on `POST /api/auth/telegram`.
- Frontend still mocks email/password login & signup — Phase 2 primary path is Telegram `initData` only.
- `services/trades/types.ts` `TradeRecord` / `PlaceTradeInput` are UI-rich (pair, platform, strategy, candles, `up`/`down` directions). Backend Phase 2 trade DTO is leaner:

  | Backend | Frontend mock |
  |---|---|
  | `asset` | `pair` |
  | `direction`: `CALL`/`PUT` (also accepts `UP`/`DOWN`) | `up`/`down` |
  | `durationSeconds` | `durationLabel` string |
  | statuses: Pending/Running/Profit/Loss/Tie/Failed | `running`/`profit`/`loss` |
  | no strategy/indicator/candles | present in mock |

A future frontend adapter layer should map these fields; backend deliberately stays trading-engine oriented.

---

## 16. Remaining limitations

- In-process `BinollaSessionManager` only (no Redis / multi-node sticky sessions).
- Real trading explicitly disabled.
- Subscription activation / email auth not implemented as login paths.
- Trade outcome worker is a single-process channel consumer.
- PostgreSQL required for non-test API runs.
- No production hardening (secrets manager, WAF, horizontal scale).

**Phase 2 stopped here for review. Do not start Phase 3 automatically.**
