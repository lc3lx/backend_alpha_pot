# PHASE 7 Audit — Admin Approval Access Control

**Date:** 2026-08-05  
**Method:** Code inspection of Phases 1–6 access path (backend + frontend).

---

## 1. Current access flow (before Phase 7)

```text
Telegram JWT
  → Binolla linked + live session
  → IReferralEligibilityService / ReferralStatus on BinollaLink
  → Eligible → Allowed
  → Unknown → ReferralVerificationRequired
  → NotEligible → NotEligible
```

Production default: `UnavailableReferralProvider` → `Unknown` → `ReferralVerificationRequired`.

There is **no automatic Binolla referral API**. Referral was an honest placeholder, never a real verification source.

---

## 2. Where referral gates access today

| Location | Role |
|---|---|
| `BotAccessService` | Access decision from `BinollaLink.ReferralStatus` |
| `BinollaAppService.ConnectAsync` | Calls referral check; persists `ReferralStatus` |
| `AccountAppService` | Returns `referralStatus` + maps `ReferralVerificationRequired` |
| `TradeAppService` | Uses `IBotAccessService.EnsureAllowed` |
| `IReferralProvider` / `ReferralEligibilityService` | Production always Unknown |
| Frontend splash/account/home/trading | Treats `ReferralVerificationRequired` as pending |
| Phase4/Phase5 tests | Assert Unknown → ReferralVerificationRequired / TestEligible |

---

## 3. What must change

1. **Source of truth** → `AdminApproved` (manual admin), not referral.
2. Replace active gate state `ReferralVerificationRequired` with `AdminApprovalRequired`.
3. Add DB fields: `AdminApproved`, `ApprovedAt`, `ApprovedBy`, plus explicit approval status (Pending/Approved/Rejected).
4. Admin-only endpoints to list/approve/reject (never expose SSID).
5. Audit log for approval actions.
6. Remove referral abstractions from the **authorization path**; delete dead referral access code.
7. Update account/status DTO + frontend pending/admin UI.
8. Update tests (TestEligible referral mode → AdminApproved setup).

---

## 4. What must be preserved

- Phases 1–3 engine, Telegram auth, encrypted SSID, Demo trades, RSI signal
- Strategy registry (RSI Active; others ComingSoon)
- No Real trading, no auto-trade, no subscription/activation
- Frontend visual design (add Admin section; update pending copy only)

---

## 5. Architecture conflict?

**None.** Extending `BinollaLink` + swapping `IBotAccessService` gate from referral to admin approval fits the existing design. No rewrite of Binolla engine or trade pipeline required.

**Continue to implementation.**
