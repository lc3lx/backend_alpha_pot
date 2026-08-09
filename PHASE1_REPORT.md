# PHASE 1 Report — ScarAlpha.Binolla Multi-User Engine Foundation

**Status: COMPLETE — 8/8 tests passed (verified 3 consecutive runs)**

Scope held: no frontend, Telegram, JWT, PostgreSQL, subscriptions, Redis, or deployment.

---

## 1. Files created

### Library `ScarAlpha.Binolla`
| Path | Role |
|---|---|
| `backend/ScarAlpha.sln` | Solution |
| `backend/ScarAlpha.Binolla/ScarAlpha.Binolla.csproj` | Class library (.NET 8) |
| `backend/ScarAlpha.Binolla/Abstractions/IBinollaClient.cs` | `IBinollaClient`, `IBinollaSessionManager`, options |
| `backend/ScarAlpha.Binolla/Models/Enums.cs` | Lifecycle, account, trade enums |
| `backend/ScarAlpha.Binolla/Models/PublicModels.cs` | BalanceInfo, OrderResponse, TradeOutcome, Quote/History |
| `backend/ScarAlpha.Binolla/Models/WireModels.cs` | Upstream JSON wire DTOs |
| `backend/ScarAlpha.Binolla/Models/Exceptions.cs` | Auth/connection/timeout/order exceptions |
| `backend/ScarAlpha.Binolla/Protocol/BinollaWire.cs` | Endpoints, event names, framing, SSID redaction |
| `backend/ScarAlpha.Binolla/Transport/IWebSocketTransport.cs` | Transport abstraction |
| `backend/ScarAlpha.Binolla/Transport/ClientWebSocketTransport.cs` | Real `ClientWebSocket` |
| `backend/ScarAlpha.Binolla/Transport/FakeWebSocketTransport.cs` | In-memory duplex for tests |
| `backend/ScarAlpha.Binolla/Session/BinollaSessionState.cs` | Per-user mutable state (replaces Globals) |
| `backend/ScarAlpha.Binolla/Session/OrderCorrelationHub.cs` | Per-session open/outcome waiters |
| `backend/ScarAlpha.Binolla/Session/SessionMessageRouter.cs` | Socket.IO + binary event parsing |
| `backend/ScarAlpha.Binolla/Session/BinollaSession.cs` | Async `IBinollaClient` implementation |
| `backend/ScarAlpha.Binolla/Session/BinollaSessionManager.cs` | In-process multi-user manager |
| `backend/ScarAlpha.Binolla/Properties/InternalsVisibleTo.cs` | Test access |
| `backend/ScarAlpha.Binolla/GlobalUsings.cs` | Usings |

### Tests + smoke
| Path | Role |
|---|---|
| `backend/ScarAlpha.Binolla.Tests/*` | Isolation, lifecycle, manager tests + simulator |
| `backend/ScarAlpha.Binolla.Smoke/Program.cs` | Live Demo connect via `BINOLLA_SSID` env only |
| `backend/IMPLEMENTATION_MAP.md` | Upstream → new mapping |
| `backend/README.md` | Commands |
| `backend/.gitignore` | bin/obj |

## 2. Files modified

None outside `backend/` (frontend untouched).

## 3. Files deleted

None in the product tree. Temporary upstream clone under `.binolla_src` used only for analysis.

## 4. Old vs new architecture

| Old (BinollaApiDotNetPro) | New (ScarAlpha.Binolla) |
|---|---|
| `Globals` process singleton | `BinollaSessionState` per user |
| Single-slot `NewOpenOrder` / `OrderData` | `OrderCorrelationHub` dictionaries + `requestId` |
| Sync busy-wait API | Async `IBinollaClient` + TCS |
| Fire-and-forget `ConnectAsync` | Awaited connect + auth waiter |
| Shared quotes/balances/orders | Fully session-scoped |
| Console `Exe` | Class library + optional smoke exe |
| Hardcoded demo SSID in Program | Env var `BINOLLA_SSID` only |

## 5. How `Globals` was removed/replaced

Upstream `Globals.Values` held SSID, balances, assets, quotes, history, and single order slots for the whole process.

Replacement: every `BinollaSession` owns a private `BinollaSessionState`. Message parsers write only into that instance. No static trading state remains.

## 6. Session isolation

- `BinollaSessionManager` keys sessions by `userId`
- Each session has its own WebSocket transport(s), state, order hub, CTS, lifecycle
- Two SSIDs ⇒ two independent connections and balances
- Proven by `Two_sessions_are_fully_isolated` and `Ten_total_concurrent_orders_across_two_sessions_have_zero_cross_talk`

## 7. Concurrent order correlation

- Client sends `requestId` on `orders/open` (compatible extension of upstream wire)
- Open waiters: `ConcurrentDictionary<int, PendingOpen>`
- Outcome waiters: `ConcurrentDictionary<string, TCS<TradeOutcome>>` keyed by order UUID
- Inbound Socket.IO binary frames processed under a per-session semaphore (prevents `451`/`payload` interleave)
- Proven by `Five_concurrent_orders_in_one_session_do_not_cross_talk` (5 orders + outcomes)

## 8. Reconnect

Lifecycle states: `Connecting`, `Connected`, `Disconnected`, `Reconnecting`, `Reconnected`, `AuthenticationFailed`, `SessionExpired`, `Faulted`.

Events: `LifecycleChanged`, `OnConnectionLost`, `OnReconnected`, `OnOrderClosed`, `OnSessionExpired`.

On drop: fail pending waiters, raise `OnConnectionLost`, optional exponential backoff reconnect (re-auth + bootstrap + re-subscribe).

## 9. Cancellation

- All public ops take `CancellationToken`
- Timeouts via linked CTS + `TaskCompletionSource`
- Cancel removes open/outcome waiters
- Proven by `Cancellation_cleans_open_and_outcome_waiters` and `Connect_cancellation_does_not_hang`

## 10. Exact test results

```
Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8
```

Verified **3 consecutive runs** all green.

| Test | Covers |
|---|---|
| `Two_sessions_are_fully_isolated` | Isolation |
| `Five_concurrent_orders_in_one_session_do_not_cross_talk` | 5 concurrent orders |
| `Ten_total_concurrent_orders_across_two_sessions_have_zero_cross_talk` | 2×5=10 orders, 0 cross-talk |
| `Connection_loss_fails_pending_ops_without_deadlock` | Drop + waiter fail |
| `Invalid_ssid_does_not_hang_and_reports_auth_failure` | Auth failure |
| `Cancellation_cleans_open_and_outcome_waiters` | Cancel cleanup |
| `Connect_cancellation_does_not_hang` | Connect cancel |
| `Manager_isolates_users_and_enforces_max` | Session manager caps |

## 11. Remaining risks

1. **Unofficial protocol** — Binolla can change Socket.IO payloads anytime.
2. **SSID expiry** — no automatic browser re-login; app must supply fresh SSID.
3. **requestId** — added on open for correlation; if live server ignores/strips it, fallback matcher uses asset/amount/cmd (weaker under identical concurrent opens).
4. **In-process manager only** — multi-node needs sticky sessions or Redis later.
5. **Chart socket** — optional; less exercised than trading socket in tests.
6. **No live Demo CI** — smoke is manual with real `BINOLLA_SSID`.

## 12. Command to run tests

```powershell
cd d:\work\flul_bot\backend
dotnet test ScarAlpha.sln -c Release
```

## 13. Command for local Demo smoke

```powershell
cd d:\work\flul_bot\backend
$env:BINOLLA_SSID = '42["authorization",{"isDemo":true,"token":"YOUR_TOKEN"}]'
dotnet run --project ScarAlpha.Binolla.Smoke -c Release
```

Never commit the SSID.

---

## Success criteria checklist

- [x] No process-wide mutable trading state
- [x] `Globals` no longer owns user/session state
- [x] Session A and B isolated
- [x] 5 concurrent orders no cross-talk
- [x] No busy-wait / Thread.Sleep / sync-over-async / fire-and-forget connect
- [x] Cancellation works
- [x] Reconnect lifecycle observable
- [x] Order waiters correlated by id
- [x] Invalid SSID does not hang
- [x] Tests prove the above

**Next phase (not started):** ASP.NET API, Telegram auth, DB, frontend wiring.
