# PHASE 10 — Live Binolla Verification / Beta Readiness

**Date/time (UTC):** 2026-08-07T21:25:12Z  
**Environment:** Windows agent host (`win32`), Playwright Chromium headless, workspace `d:\work\flul_bot`  
**Phase type:** Verification only (no product-feature development)  

---

## FINAL VERDICT

```text
NOT READY — EXTERNAL VERIFICATION BLOCKER
```

**Primary blocker class:** `UNAVAILABLE CREDENTIALS`  

Secondary observation: signup/login DOM fields were **not visible** in the headless probe (`restrictionHint: true`), which may also indicate `ENVIRONMENT ISSUE` / Cloudflare / SPA challenge — but live credential auth was **not attempted** because required secrets were absent.

---

## 1. Environment used

| Item | Value |
|---|---|
| Host OS | Windows 10 (build from user_info) |
| Tooling | Node + Playwright Chromium (`backend/tools/binolla-auth`) |
| Backend tests | .NET 8 Release |
| Live credential env vars | **Not set** (`BINOLLA_LIVE_EMAIL`, `BINOLLA_LIVE_PASSWORD`, `BINOLLA_AUTH_EMAIL`, `BINOLLA_AUTH_PASSWORD`, `BINOLLA_SSID` all absent) |
| Credential files on disk | `.env.development(.local)` exist but contain **no** Binolla email/password keys |

Secrets were never printed.

---

## 2. Exact production auth flow (verified against code)

```text
FE Signup/Login (BinollaPlatformAuth / useBinollaPlatformAuth)
  → POST /api/binolla/signup | /api/binolla/login
  → BinollaAppService.SignUpWithCredentialsAsync | LoginWithCredentialsAsync
  → NodeBinollaCredentialAuth.CaptureAsync
  → node capture.mjs (--mode signup|login, --signupUrl, --loginUrl)
  → Playwright opens Binolla URL, fills email/password, captures token
  → NodeBinollaCredentialAuth builds SSID frame:
       42["authorization",{"isDemo":true,"token":"<token>"}]
  → BinollaAppService.ConnectAsync
  → ISecretProtector.Encrypt → BinollaLink.EncryptedSsid
  → IBinollaSessionManager.GetOrCreateAsync → Demo account
  → AdminApprovalStatus.Pending (unless already Approved/Rejected)
```

### Exact files / functions

| Stage | Location |
|---|---|
| FE submit | `bot_telegram_webapp/src/pages/Bot/LinkBinolla/hooks/useBinollaPlatformAuth.ts` → `binollaApi.login` / `signup` |
| API endpoints | `ScarAlpha.Api/Endpoints/ApiEndpoints.cs` → `POST /api/binolla/login`, `/signup`, `/connect` |
| App service | `ScarAlpha.Application/Services/BinollaAppService.cs` → `LoginWithCredentialsAsync`, `SignUpWithCredentialsAsync`, `ConnectAsync` |
| Playwright runner | `ScarAlpha.Infrastructure/Binolla/NodeBinollaCredentialAuth.cs` → `CaptureAsync`, `RunNodeCaptureAsync` |
| Browser tool | `backend/tools/binolla-auth/capture.mjs` → `main()`, `extractToken()`, `fillFirst()`, `clickSubmit()` |
| Encrypt at rest | `ConnectAsync` + `AesGcmSecretProtector` |
| Session restore | `BinollaSessionRestoreService` (Phase 9) |
| Access gate | `BotAccessService` + admin `AdminAppService` |

---

## 3. Referral URL result

**Configured / wired signup URL (all aligned):**

```text
https://binolla.com/signup/?lid=15968
```

| Source | Status |
|---|---|
| `appsettings.json` → `Binolla:CredentialLogin:SignupUrl` | PASS — `lid=15968` |
| `capture.mjs` default `--signupUrl` | PASS |
| `NodeBinollaCredentialAuth` default | PASS |
| FE `BINOLLA_REFERRAL_SIGNUP_URL` | PASS — same URL |
| Onboarding `openExternalLink(BINOLLA_REFERRAL_SIGNUP_URL)` | PASS — wired |
| In-app signup path | PASS — `POST /api/binolla/signup` uses server SignupUrl |

**Live browser probe (no credentials):**

```json
{
  "requestedUrl": "https://binolla.com/signup/?lid=15968",
  "finalUrl": "https://binolla.com/signup/?lid=15968",
  "httpStatus": 200,
  "lid15968InFinalUrl": true,
  "emailFieldVisible": false,
  "restrictionHint": true
}
```

**Referral URL propagation = VERIFIED** (URL retained with `lid=15968`).  
**Referral attribution = NOT VERIFIED** (no Binolla affiliate API / partner-dashboard proof; admin approval remains the access gate).

No dead referral constant found requiring a product UI redesign. No code fix applied in Phase 10.

---

## 4. Signup result

```text
Live Binolla signup = NOT VERIFIED
```

Reason: `UNAVAILABLE CREDENTIALS` — Step 4 hard stop. No account creation attempted (avoids unnecessary real accounts / CAPTCHA bypass).

---

## 5. Login result

```text
Live Binolla login = NOT VERIFIED
```

Reason: `UNAVAILABLE CREDENTIALS`.

Invalid-credential live attempts were **not** re-run aggressively in Phase 10 (Phase 9 already showed capture failure without a token). Login page probe: HTTP 200, title Binolla, **email/password fields not visible** in headless snapshot (`restrictionHint: true`).

---

## 6. Session / SSID extraction

```text
SESSION EXTRACTED = NO
```

(No successful authenticated Binolla state in this phase.)

---

## 7. Scar Alpha connection

```text
NOT RUN (blocked by missing live session)
```

Code path exists and is covered by mocked API tests (Phases 7–9). Not re-claimed as live-proven here.

---

## 8. Admin approval

```text
NOT RUN live
```

Covered by existing API tests (`Phase7` / `Phase8` / `Phase9`). Not live E2E in this phase.

---

## 9. Demo trading / market / balance / history

```text
NOT RUN live
```

Out of reach without authenticated Demo session. Real trading intentionally not attempted.

---

## 10. API restart restore

```text
NOT RUN live
```

Implemented and unit/integration-tested in Phase 9 (`Phase9SessionRestoreTests` — 7 passed). Not re-proven against a real Binolla SSID in Phase 10.

---

## 11. Referral attribution

```text
Referral URL propagation = VERIFIED
Referral attribution = NOT VERIFIED
```

Acceptable per Phase 10 Step 12. Admin approval remains authorization.

---

## 12. Evidence / screenshots

No screenshots captured (would risk embedding session/PII). Probe JSON above is the evidence artifact. Tool added: `backend/tools/binolla-auth/phase10-env-probe.mjs`.

---

## 13. Commands executed

```text
# Credential presence (lengths/flags only — no secret values)
# BINOLLA_LIVE_* / BINOLLA_AUTH_* / BINOLLA_SSID → unset

cd backend/tools/binolla-auth
node phase10-env-probe.mjs
# exit 2 — NOT VERIFIED — required live credentials unavailable

dotnet test ScarAlpha.sln -c Release
# Passed: Binolla 10 + API 69 = 79 total, 0 failed

cd bot_telegram_webapp
npm run typecheck   # exit 0
npm run build       # exit 0
```

`npm run test` / `npm run lint`: **not configured** in `package.json`.

---

## 14. Exact test results (regression)

| Command | Result |
|---|---|
| `dotnet test ScarAlpha.sln -c Release` | **Passed! Failed: 0, Passed: 10** (Binolla) + **Passed: 69** (API) = **79/79** |
| `npm run typecheck` | **pass** (exit 0) |
| `npm run build` | **pass** (exit 0) |

No product code changes in Phase 10; only verification probe script + this report.

---

## 15. Failures / external blockers

| Class | Detail |
|---|---|
| `UNAVAILABLE CREDENTIALS` | No live Binolla email/password/SSID in env or project env files |
| `ENVIRONMENT ISSUE` (possible) | Headless signup/login: form fields not visible; `restrictionHint: true` |
| `NOT VERIFIED` | Signup, login, session extract, connect, demo trade, restart with real SSID |

Did **not** bypass CAPTCHA / 2FA / email verification. Did **not** mock success.

---

## 16. Remaining risks

1. Playwright credential path still **unproven** against real Binolla.
2. Headless / Cloudflare / geo may block even after credentials are supplied.
3. Referral attribution cannot be proven without Binolla partner tooling.
4. Live Demo trade + restart restore with a real SSID still required before beta confidence rises.

---

## 17. End-to-end matrix

| Step | Result |
| --- | --- |
| Telegram authentication | PASS (code + prior API tests; not re-run as live Mini App in Phase 10) |
| Referral signup URL | PASS |
| lid=15968 propagation | PASS (browser finalUrl retained `lid=15968`) |
| Live Binolla signup | NOT VERIFIED |
| Live Binolla login | NOT VERIFIED |
| Session extraction | NOT VERIFIED (`SESSION EXTRACTED = NO`) |
| Scar Alpha connection | NOT VERIFIED (not run live) |
| Encrypted persistence | PASS (code + Phase 9 tests; not live) |
| Admin approval | PASS (code + API tests; not live) |
| Live market | NOT VERIFIED |
| Demo balance | NOT VERIFIED |
| Demo trade | NOT VERIFIED |
| Trade result | NOT VERIFIED |
| Trade history | PASS (code + API tests; not live) |
| API restart restore | PASS (Phase 9 tests; not live with real SSID) |
| Referral attribution | NOT VERIFIED |

---

## 18. Operator resume checklist (when credentials exist)

Do **not** commit secrets. Set env then re-run:

```text
set BINOLLA_LIVE_EMAIL=...
set BINOLLA_LIVE_PASSWORD=...
cd backend/tools/binolla-auth
node live-probe.mjs --mode login --email %BINOLLA_LIVE_EMAIL% --password %BINOLLA_LIVE_PASSWORD%
```

Success criteria for upgrading this verdict: `SESSION EXTRACTED = YES` (token never logged), then real `POST /api/binolla/login` → Pending → Admin approve → Allowed → Demo market/trade → API restart restore.

---

## ABSOLUTE STOP

Phase 10 ends here. No Phase 11. No Real Trading. No Auto RSI. No deploy.
