# BinollaApiDotNetPro → ScarAlpha.Binolla Implementation Map

Read-only audit of upstream source vs multi-user engine design.
Protocol is preserved; process-wide state is replaced.

## File-by-file map

| Current class / file | Responsibility | Preserve | Replace |
|---|---|---|---|
| `WebSocketClientBinolla` | Trading WS to `ws3.binolla.com/socket.io` | endpoints, headers/UA, Socket.IO control flow, heartbeat/ping | tie to `Globals`; single `_upcomingMessageType`; fire-and-forget reconnect |
| `ControlMessageProcessor` | Engine.IO/Socket.IO: `0`→`40`, auth SSID, `s_authorization`, pong, `451-` binary headers | frame sequences, init command list | writes into singleton |
| `MessageProcessor` | Parse `s_balance/*`, `s_orders/*`, `s_assets/list`, `s_quotes/list`, `s_history/last` | all wire parsing / message type strings | `Values.*` writes |
| `ChartWebSocketClient` | Chart WS `ws2.binolla.com/ws` | endpoint, SSID send, asset change | shared SSID/globals |
| `Globals` | Process-wide SSID, balances, orders, assets, quotes | DTOs/event shapes | **removed as session store** |
| `BinollaApiClient` | Sync public API (busy-wait) | public operation names / order open format | **new async `IBinollaClient` / `BinollaSession`** |
| DTOs (`OpenedOrder`, `Deal`, `AssetData`, …) | JSON models | property shapes / wire comments | namespaces → `ScarAlpha.Binolla.Models` |
| `Program.cs` | Demo with hardcoded SSID | — | deleted; smoke uses env var only |

## New architecture

```text
ScarAlpha.Binolla
  Protocol/          Constants + framing helpers (from upstream wire knowledge)
  Models/            DTOs + enums + BalanceInfo / OrderResponse / TradeOutcome
  Transport/         IWebSocketTransport + Real/Fake implementations
  Session/           SessionState, Connection, MessageRouter, OrderCorrelation, Session, Manager
  Abstractions/      IBinollaClient, IBinollaSessionManager, lifecycle events
```
