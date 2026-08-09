# PHASE 5 Report — RSI Signal Engine + Referral Provider Abstraction

**Date:** 2026-08-05  
**Status:** Implemented + Tested

---

## 1. What was implemented (Phase 5)

### 1.1 Referral (no faking in production)
- Added a clean abstraction `IReferralProvider` to decouple referral verification from the eligibility service.
- Production fallback remains unverified:
  - `Unknown` eligibility outcome always maps to `ReferralVerificationRequired` bot access.
- No Binolla affiliate/referral API was invented or integrated.

### 1.2 RSI engine (real candles + deterministic)
- Implemented deterministic Wilder RSI calculation:
  - RSI period: `14`
  - Oversold: `30`
  - Overbought: `70`
  - Candle timeframe (`period` query param): default `60` seconds
  - Closed candles only (see below)
- Implemented RSI crossing signal logic:
  - Previous RSI `<= 30` AND Current RSI `> 30`  => `Call`
  - Previous RSI `>= 70` AND Current RSI `< 70` => `Put`
  - otherwise => `None`
- Added endpoint (signal-only):
  - `GET /api/strategies/rsi/signal/{asset}?period=60`

---

## 2. Referral provider architecture

### 2.1 Abstractions
- `IReferralProvider`
  - returns `ReferralEligibilityOutcome` (`Eligible` / `NotEligible` / `Unknown`)

### 2.2 Implementations
- `UnavailableReferralProvider` (production default)
  - returns `Unknown` because the current Binolla protocol provides no affiliate/referral attribution.
- Test-only providers exist for integration tests:
  - `TestEligibleReferralProvider`
  - `TestNotEligibleReferralProvider`
  - They are only enabled in `Development` (test host) via DI selection.

### 2.3 Production mapping (required behavior)
- `ReferralEligibilityOutcome.Unknown` → `BotAccessState.ReferralVerificationRequired`
- This is enforced by existing:
  - `ReferralEligibilityService` (provider → eligibility result)
  - `BotAccessService` (eligibility/referral status → access state)

---

## 3. RSI calculation details (deterministic)

### 3.1 Candle source
- Candles come from the backend via:
  - `IBinollaClient.GetHistoryAsync(asset, periodSeconds)`
- The endpoint does **not** use any frontend candles.

### 3.2 Closed candle filtering
- Binolla candle model includes `EndTimestamp?`.
- Logic:
  - If `EndTimestamp` is present: include only candles where `EndTimestamp <= now`.
  - If `EndTimestamp` is not present: assume the candle is closed (to support existing test simulator payloads).

### 3.3 Wilder RSI formula
- Deterministic, Wilder-style RSI:
  1. For the first RSI window: compute average gain and average loss from the first `14` deltas.
  2. For subsequent deltas: apply Wilder smoothing:
     - `avgGain = (avgGain*(period-1) + gain) / period`
     - `avgLoss = (avgLoss*(period-1) + loss) / period`
  3. Convert to RSI:
     - If `avgLoss == 0` and `avgGain == 0` => RSI = `50`
     - If `avgLoss == 0` and `avgGain > 0` => RSI = `100`
     - Otherwise: `RS = avgGain/avgLoss` and `RSI = 100 - 100/(1+RS)`

---

## 4. Signal crossing logic + anti-repeat

### 4.1 Crossing logic (exactly as requested)
- Oversold crossing:
  - `Previous RSI <= 30` AND `Current RSI > 30` => `Call`
- Overbought crossing:
  - `Previous RSI >= 70` AND `Current RSI < 70` => `Put`
- Otherwise:
  - `None`

### 4.2 No repeat for the same candle
- `RsiSignalService` tracks the last non-NONE `candleTime` per:
  - `userId + asset + timeframe`
- If the endpoint is called again with the same candleTime and the crossing would trigger again, the service returns `None`.

---

## 5. No automatic trading

- Phase 5 added **signal-only** endpoint.
- No integration into the trade engine was added.
- `POST /api/trades` remains manual and controlled by existing bot access + strategy enabled checks.

---

## 6. Endpoint contract

- Implemented:
  - `GET /api/strategies/rsi/signal/{asset}?period=60`
- Response is the server-computed `StrategySignal` snapshot:
  - `strategyId: "rsi"`
  - `signal: "Call" | "Put" | "None"`
  - `rsi` (rounded to 2 decimals in the signal response)
  - `candleTime` (timestamp of the latest closed candle used)
  - `timeframe` (string form of seconds, e.g. `"60"`)

See `backend/API.md` for details.

---

## 7. Tests and results

### 7.1 RSI unit tests
- Added `Phase5RsiTests.cs`
- Covers:
  - correct calculation
  - insufficient candles (throws `VALIDATION_ERROR`)
  - all gains (RSI = 100, zero-loss handling)
  - all losses (RSI = 0)
  - mixed gains/losses (deterministic + smoothing)
  - oversold crossing => `Call`
  - overbought crossing => `Put`
  - no crossing => `None`
  - does not repeat signal for same candleTime
  - closed candle only (ignores open last candle by EndTimestamp)
  - deterministic result when signal is `None`

### 7.2 Referral provider tests
- Added `Phase5ReferralProviderTests.cs`
- Covers:
  - `UnavailableReferralProvider` returns `Unknown`
  - `Unknown` referral → `ReferralVerificationRequired`

### 7.3 Full suite
- `dotnet test ScarAlpha.sln -c Release`
  - all tests pass (42 total in current run: Phase1–4 + Phase5).

---

## 8. Limitations / remaining gaps

1. **Signal availability depends on candle history depth**
   - The signal needs at least `period + 2` closed candles (`14 + 2 = 16`) to compute previous & current RSI.
   - If insufficient closed candles exist, the endpoint returns a validation error.

2. **Anti-repeat uses in-memory state**
   - No Redis/storage; after API restart the “already emitted for candleTime” memory resets.
   - This is sufficient for Phase 5 requirements (no infra additions).

3. **Referral verification still cannot be done from current Binolla protocol**
   - Production remains `Unknown` by design until an external affiliate verification mechanism exists.

