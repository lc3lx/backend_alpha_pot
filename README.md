# ScarAlpha Backend

Multi-user Binolla engine (Phase 1) + ASP.NET Core API with Telegram WebApp auth (Phase 2).

## Projects

| Project | Purpose |
|---|---|
| `ScarAlpha.Binolla` | Multi-user Binolla WebSocket engine |
| `ScarAlpha.Binolla.Tests` | Engine isolation / lifecycle tests |
| `ScarAlpha.Binolla.Smoke` | Optional live Demo connect (`BINOLLA_SSID`) |
| `ScarAlpha.Domain` | Entities / enums |
| `ScarAlpha.Application` | Use cases / DTOs / ports |
| `ScarAlpha.Infrastructure` | EF Core, Telegram, JWT, encryption, workers |
| `ScarAlpha.Api` | HTTP API |
| `ScarAlpha.Api.Tests` | API integration tests (mocked Binolla) |

## Docs

- `PHASE1_REPORT.md` … `PHASE8_REPORT.md`
- `API.md` — endpoint reference
- `../INTEGRATION.md` — **frontend ↔ backend wiring & local run**

## Tests

```powershell
cd d:\work\flul_bot\backend
dotnet test ScarAlpha.sln -c Release
```

## Run with frontend

See root `INTEGRATION.md` or:

```powershell
cd d:\work\flul_bot
.\start-dev.ps1
```

## Run API only

```powershell
$env:TELEGRAM_BOT_TOKEN = '...'
$env:JWT_SECRET = '...'
$env:JWT_ISSUER = 'ScarAlpha'
$env:JWT_AUDIENCE = 'ScarAlpha.App'
$env:DATABASE_CONNECTION_STRING = 'Host=localhost;Port=5432;Database=scaralpha;Username=postgres;Password=postgres'
$env:BINOLLA_TOKEN_ENCRYPTION_KEY = '...'
dotnet run --project ScarAlpha.Api -c Release
```

## Run API on VPS with PM2 (Linux)

```bash
cd /home/web/backend
cp scaralpha.env.example scaralpha.env
nano scaralpha.env          # set TELEGRAM_BOT_TOKEN
chmod +x start-backend-pm2.sh
./start-backend-pm2.sh      # publish + pm2 start (scaralpha-api)
```

Useful:

```bash
./start-backend-pm2.sh status
./start-backend-pm2.sh logs
./start-backend-pm2.sh restart
./start-backend-pm2.sh stop
pm2 startup                 # enable boot persistence (once)
pm2 save
```

Files: `start-backend-pm2.sh`, `ecosystem.config.cjs`, `scaralpha.env.example`

## Demo smoke (engine only)

```powershell
$env:BINOLLA_SSID = '42["authorization",{"isDemo":true,"token":"..."}]'
dotnet run --project ScarAlpha.Binolla.Smoke -c Release
# optional Demo trade:
dotnet run --project ScarAlpha.Binolla.Smoke -c Release -- --trade
```

Never commit SSID values or secrets.
