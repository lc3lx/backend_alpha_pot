# PHASE 9 Audit — Production Readiness Gaps

**Date:** 2026-08-07  
**Method:** Read Phases 1–8 reports + `API.md`, then verified against **live code** (docs do not win).  
**Product model (FINAL):** Telegram → Binolla → Manual Admin Approval → FREE → RSI → Demo Trading Only  

**Out of scope (confirmed not to implement):** Real trading, auto trading, EMA/MACD/AI, Redis, multi-server, subscription, activation keys, referral verification API, payments, UI redesign.

---

## Reality vs reports (summary)

| Phase claim | Code reality |
|---|---|
| Phases 1–3 engine + market + Demo trades | **PASS** — present |
| Phase 4–5 referral eligibility as authorization | **SUPERSEDED** by Phase 7 — referral not in access path |
| Phase 7 admin approval gate | **PASS** |
| Phase 8 EnsureAllowed on market/RSI/balance | **PASS** |
| Phase 8 “SSID reconnect desync fixed” | **PARTIAL** — in-process SSID rotation only; **no DB restore after restart** |
| Phase 6 “mocks replaced” | **PARTIAL** — live APIs for Allowed path; Home Start/Pause/Stop + Auto Stop toggles still local/fake |
| API.md login/signup endpoints | **PASS** — present; Playwright path **unproven live** |

---

## Defect-first scorecard

| ID | Area | Status | Severity |
|---|---|---|---|
| D1 | Session restore from `EncryptedSsid` after API restart | **FAIL** | P0 |
| D2 | Open trade recovery without live session → `Unknown` | **RISK** | P1 |
| D3 | Playwright credential auth live verification | **NOT VERIFIED** | P1 |
| D4 | Frontend referral URL wiring | **FAIL** | P2 |
| D5 | Fake / misleading Home & Trading UI | **PARTIAL** | P2 |
| D6 | Access gates + Demo-only | **PASS** | — |
| D7 | Production secrets / config | **RISK** | P1 |
| D8 | Performance (WS dispose, workers, reconnect) | **PARTIAL** | P2 |
| D9 | End-to-end journey (Splash routing, stale onboarding) | **PARTIAL** | P2 |

---

## D1 — Session restore (P0) — FAIL

**Current behavior**

1. `BinollaAppService.ConnectAsync` encrypts SSID → stores `BinollaLink.EncryptedSsid`.
2. Live session lives only in `BinollaSessionManager` (`ConcurrentDictionary`, in-process).
3. On API restart, memory is empty.
4. `BotAccessService.CheckAsync` requires a **live** Connected/Reconnected client.
5. Approved users with valid DB link become **`BinollaNotConnected`** until they reconnect manually.

**Evidence**

- Encrypt-only in app code: `ScarAlpha.Application/Services/BinollaAppService.cs` (`EncryptedSsid = encrypted`).
- `ISecretProtector.Decrypt` used in **tests only** (`ApiIntegrationTests.cs`); no production Decrypt caller.
- Hosted services: only `TradeOutcomeWorker` (`DependencyInjection.cs`). No session restore worker.
- Access: `ScarAlpha.Infrastructure/Access/BotAccessService.cs` lines ~24–49.

**Required for Phase 9**

- On startup (async): load approved connected links → decrypt → reconnect with backoff → never crash on partial failure → never log/expose SSID.
- Restore only approved users (per Phase 9 brief).
- Skip invalid/expired SSIDs; continue others.

---

## D2 — Trade outcome recovery — RISK

`TradeOutcomeWorker.RecoverOpenTradesAsync` re-enqueues Running trades only when a live session exists; otherwise marks `Unknown` (`RECOVERY_NO_SESSION`).

Coupled to D1: fixing session restore before/with recovery improves honesty of open Demo trades after reboot.

---

## D3 — Playwright live authentication — NOT VERIFIED

**Wired**

- `POST /api/binolla/login` / `/signup`
- `NodeBinollaCredentialAuth` → `tools/binolla-auth/capture.mjs`
- Config: `Binolla:CredentialLogin` in `appsettings.json` (`SignupUrl` = `https://binolla.com/signup/?lid=15968`)

**Not proven**

- Integration tests **mock** `IBinollaCredentialAuth` and explicitly avoid real Playwright.
- Prior live attempt from agent environment failed with Binolla geo-block (NL).
- No CI live smoke.

Phase 9 Step 3 must attempt honest verification or document **NOT VERIFIED** and STOP that step without faking success.

---

## D4 — Referral flow — FAIL (frontend)

| Item | Reality |
|---|---|
| `BINOLLA_REFERRAL_SIGNUP_URL` | Defined in `bot_telegram_webapp/src/constants/binolla.ts`; **never imported** |
| UI CTAs | Navigate to in-app `ROUTES.linkBinolla` (credential form) |
| Backend signup attribution | Playwright opens `signup/?lid=15968` |
| URL consistency | FE constant `/?lid=15968` vs BE `/signup/?lid=15968` |
| Authorization | Referral is **not** an access gate (Phase 7) — **PASS** |

Onboarding still mentions paste SSID (`onboarding.mock.ts`) — stale vs credential path.

---

## D5 — Fake UI — PARTIAL

| Surface | Status |
|---|---|
| Trading candles/price/balance/place when Allowed | Live Binolla — **PASS** |
| Home Start / Pause / Stop | Local `botStatus` only — **FAIL** (looks like bot control) |
| Auto Stop Profit / Loss toggles | Enabled in mock, no backend — **FAIL** |
| Home Trade Amount / Duration | Local only — **FAIL** |
| Trading expiry countdown | Local timer (not server remaining) — **FAIL** / misleading |
| Dashboard performance chart | Static asset; value forced `—` (hidden) — **WARN** |
| Notifications | Empty local store (no fake seeds at runtime) — **PASS** / keep local |
| Seed trades | Dead code unused — **PASS** |

---

## D6 — Access gates / Demo-only — PASS

- `EnsureAllowed` on place trade, market, RSI, balance.
- Connect / credential login available without Allowed (correct).
- Real account type rejected; Demo forced; Real balance masked to 0.

---

## D7 — Production configuration — RISK

- `appsettings.json` commits weak DEV defaults (JWT, encryption key, Telegram token, Postgres password).
- `Program.ValidateProductionSecrets` **refuses** known weak markers when `ASPNETCORE_ENVIRONMENT=Production` — **PASS** as guard.
- CORS localhost defaults; override via `CORS_ORIGINS`.
- Credential login `Enabled: true` by default — requires Node + Playwright on server.
- Frontend `.env` gitignored; ensure no secrets committed.

---

## D8 — Performance — PARTIAL

| Area | Notes |
|---|---|
| WS dispose | Present on remove/disconnect |
| Idle eviction | ~30 min idle drops session; without restore → not connected |
| In-process reconnect | Exponential backoff exists; does not load from DB |
| TradeOutcomeWorker | Unbounded channel; single consumer |
| Credential auth | `SemaphoreSlim(1,1)` serial captures |
| Max sessions | Default 100 |

Fix only real bugs in Step 8; do not redesign for multi-server/Redis.

---

## D9 — User journey — PARTIAL

```text
Splash → Telegram JWT → account/status
  Allowed → Home
  else → Settings
```

- Login/Signup pages historically mixed Telegram vs Binolla credential UX (Link Binolla is primary credential path).
- Onboarding partially orphaned / stale SSID copy.
- After restart: Approved user → `BinollaNotConnected` (D1) until reconnect.
- Rejected / pending UI notices exist on Account.

---

## Phase 9 execution order (locked)

1. ~~Audit~~ → this file  
2. ~~**Session restore** (D1)~~ → COMPLETE (see PHASE9_REPORT.md)  
3. ~~Playwright live auth (D3)~~ → **NOT VERIFIED** (PHASE9_STEP3_LIVE_AUTH.md)  
4. ~~Referral cleanup (D4)~~ → COMPLETE  
5. ~~Remove fake UI (D5)~~ → COMPLETE  
6. ~~Journey verification (D9)~~ → COMPLETE (PHASE9_JOURNEY.md)  
7. ~~Production config audit (D7)~~ → COMPLETE  
8. ~~Performance fixes if real (D8)~~ → COMPLETE (CTS + admin gate)  
9. ~~Regression tests~~ → 79/79 Release  
10. ~~Final report~~ → PHASE9_REPORT.md

---

## Post-implementation note

After Phase 9 implementation, D1 session restore is **PASS** in code + tests.  
D3 remains **NOT VERIFIED** live. See `PHASE9_REPORT.md`.
