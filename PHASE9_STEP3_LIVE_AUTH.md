# PHASE 9 Step 3 — Playwright Live Authentication

**Date (UTC):** 2026-08-07T15:31:37Z  
**Verdict:** **NOT VERIFIED**

Do **not** treat credential login/signup as production-proven.

---

## Required chain

```text
Signup → Referral URL → Login → Receive session → Extract SSID → Connect
```

---

## Commands executed

```text
cd backend/tools/binolla-auth
node live-probe.mjs
node capture.mjs --mode login --email phase9-probe-invalid@example.com --password DefinitelyNotARealPassword123! --loginUrl https://binolla.com/login/ --timeout 90000
```

---

## Results (exact)

### `node live-probe.mjs`

```json
{
  "ok": false,
  "verified": false,
  "checks": {
    "apiLogin": {
      "status": 403,
      "geoBlocked": false,
      "bodySnippet": "...Just a moment... (Cloudflare challenge HTML)..."
    },
    "playwrightNav": {
      "finalUrl": "https://binolla.com/login/",
      "httpStatus": 200,
      "title": "Binolla",
      "geoBlocked": false
    }
  },
  "reason": "NO_LIVE_CREDENTIALS_SUPPLIED",
  "verdict": "REACHABLE_BUT_CREDENTIALS_NOT_RUN"
}
```

### `node capture.mjs` (invalid credentials)

```json
{"ok":false,"error":"Binolla login failed or token was not captured"}
```

Exit code: `1`

---

## Interpretation

| Check | Outcome |
|---|---|
| Login page reachable via Playwright | Yes (HTTP 200, title Binolla) |
| Signup URL `/?lid=15968` / `/signup/?lid=15968` HTML | HTTP 200 (SPA shell) |
| Direct `POST /api/auth/login` | Cloudflare challenge (403 HTML) — not a clean REST login |
| End-to-end token capture with **valid** credentials | **NOT RUN** — no live partner credentials supplied in this Phase 9 session |
| Invalid-credential capture | Tool runs; no token (expected); does **not** prove success path |
| CI / integration tests | Still mock `IBinollaCredentialAuth` |

Prior environment history (conversation): Binolla geo-block message for Netherlands (NL) was observed. This run did **not** reproduce that exact NL message; Cloudflare challenge appeared on the raw API instead. Either way, **live success was not demonstrated**.

---

## Why Step 3 is NOT VERIFIED (honest STOP on this claim)

1. No valid Binolla email/password was provided for Phase 9 live proof.
2. Successful SSID extraction + `ConnectAsync` against real Binolla was **not** observed.
3. Claiming VERIFIED would be fabricated.

---

## What remains wired (code only — not live-proven)

- `POST /api/binolla/login` / `/signup`
- `NodeBinollaCredentialAuth` → `capture.mjs`
- Default signup URL: `https://binolla.com/signup/?lid=15968`

---

## Operator requirement before production claim

Run on a host that can complete Binolla login (allowed region / Cloudflare pass):

```text
set BINOLLA_LIVE_EMAIL=...
set BINOLLA_LIVE_PASSWORD=...
node live-probe.mjs --mode login --email %BINOLLA_LIVE_EMAIL% --password %BINOLLA_LIVE_PASSWORD%
node live-probe.mjs --mode signup --email <unique> --password ...
```

Success criteria: `verdict: VERIFIED` with `hasToken: true` (token never logged), then `POST /api/binolla/connect` with captured SSID frame succeeds.
