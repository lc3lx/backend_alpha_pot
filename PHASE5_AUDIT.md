# PHASE 5 Audit — RSI Engine + Referral Provider Abstraction

**Date:** 2026-08-05

## Referral
- The Binolla client/protocol does not provide affiliate/referral attribution fields required for reliable server-side verification.
- Requirement for Phase 5: keep production behavior as:
  - `ReferralEligibilityOutcome.Unknown` → `BotAccessState.ReferralVerificationRequired`
- Implemented abstraction:
  - `IReferralProvider` (pluggable later with an external affiliate verification source)

## RSI
- Requirement for Phase 5: real RSI on **real Binolla candle history**, deterministic, no frontend candle usage.
- The backend already supports candle retrieval via `IBinollaClient.GetHistoryAsync(asset, period)`.
- Binolla candle model supports `EndTimestamp?` (when provided by upstream).
- Implemented filtering:
  - Use candles where `EndTimestamp <= now` when `EndTimestamp` is available.
  - If `EndTimestamp` is not available, candles are assumed closed (to avoid breaking historical/test payloads).

## Important non-goals
- No automatic trade execution from RSI in Phase 5.
- No new strategies are enabled (only RSI remains Active/Enabled).

