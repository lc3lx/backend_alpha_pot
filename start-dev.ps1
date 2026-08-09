# Start Scar Alpha local stack (Backend API + Frontend Vite)
# Usage:  .\start-dev.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$backend = Join-Path $root 'backend'
$frontend = Join-Path $root 'bot_telegram_webapp'

Write-Host 'Starting ScarAlpha.Api on http://localhost:5207 ...' -ForegroundColor Cyan
Start-Process powershell -ArgumentList @(
  '-NoExit',
  '-Command',
  "cd '$backend'; if (-not `$env:JWT_SECRET) { `$env:JWT_SECRET='dev-only-change-me-please-use-32chars-min!!' }; if (-not `$env:BINOLLA_TOKEN_ENCRYPTION_KEY) { `$env:BINOLLA_TOKEN_ENCRYPTION_KEY='dev-binolla-encryption-key-change-me-32' }; if (-not `$env:TELEGRAM_BOT_TOKEN) { `$env:TELEGRAM_BOT_TOKEN='000000000:REPLACE_ME_DEV_TOKEN' }; if (-not `$env:DATABASE_CONNECTION_STRING) { `$env:DATABASE_CONNECTION_STRING='Host=localhost;Port=5432;Database=scaralpha;Username=postgres;Password=postgres' }; `$env:JWT_ISSUER='ScarAlpha'; `$env:JWT_AUDIENCE='ScarAlpha.App'; `$env:Cors__Origins='http://localhost:5173,http://127.0.0.1:5173'; dotnet run --project ScarAlpha.Api"
)

Start-Sleep -Seconds 2

Write-Host 'Starting Vite frontend on http://localhost:5173 (proxies /api → :5207) ...' -ForegroundColor Cyan
Start-Process powershell -ArgumentList @(
  '-NoExit',
  '-Command',
  "cd '$frontend'; if (-not (Test-Path node_modules)) { npm install }; npm run dev"
)

Write-Host ''
Write-Host 'Opened two terminals. See INTEGRATION.md for Telegram initData / admin setup.' -ForegroundColor Green
