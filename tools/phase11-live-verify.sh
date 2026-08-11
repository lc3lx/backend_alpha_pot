#!/usr/bin/env bash
# Phase 11 — operator live verification (no secrets printed).
# Run on VPS:  cd /home/web/backend && chmod +x tools/phase11-live-verify.sh && ./tools/phase11-live-verify.sh
#
# Optional: export LIVE_JWT='<approved-user-jwt>' to probe market/trade endpoints.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="${SCARALPHA_ENV_FILE:-$ROOT/scaralpha.env}"
BASE="${API_BASE:-http://127.0.0.1:5207}"
OUT_DIR="${ROOT}/logs"
mkdir -p "$OUT_DIR"
REPORT="$OUT_DIR/phase11-live-verify.txt"

{
  echo "=== PHASE11 LIVE VERIFY $(date -u +%Y-%m-%dT%H:%M:%SZ) ==="
  echo "BASE=$BASE"

  if [[ -f "$ENV_FILE" ]]; then
    echo "ENV_FILE=$ENV_FILE"
    # Print only non-secret keys / presence flags
    # shellcheck disable=SC1090
    set -a; source "$ENV_FILE"; set +a
    echo "ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT:-<unset>}"
    echo "DATABASE_PROVIDER=${DATABASE_PROVIDER:-<unset>}"
    echo "HAS_DATABASE_CONNECTION_STRING=$([ -n "${DATABASE_CONNECTION_STRING:-}" ] && echo yes || echo no)"
    echo "HAS_JWT_SECRET=$([ -n "${JWT_SECRET:-}" ] && echo yes || echo no)"
    echo "HAS_BINOLLA_TOKEN_ENCRYPTION_KEY=$([ -n "${BINOLLA_TOKEN_ENCRYPTION_KEY:-}" ] && echo yes || echo no)"
    echo "HAS_TELEGRAM_BOT_TOKEN=$([ -n "${TELEGRAM_BOT_TOKEN:-}" ] && echo yes || echo no)"
    echo "CORS_ORIGINS=${CORS_ORIGINS:-<unset>}"
  else
    echo "ENV_FILE_MISSING=$ENV_FILE"
  fi

  echo "--- health ---"
  curl -fsS "$BASE/health" || echo "health_FAILED"
  echo
  echo "--- health/ready ---"
  curl -fsS "$BASE/health/ready" || echo "ready_FAILED"
  echo

  if command -v pm2 >/dev/null 2>&1; then
    echo "--- pm2 ---"
    pm2 describe scaralpha-api 2>/dev/null | grep -E 'status|uptime|script path|exec cwd|unstable' || pm2 status || true
  fi

  if [[ -n "${LIVE_JWT:-}" ]]; then
    echo "--- authenticated probes (LIVE_JWT set) ---"
    auth=(-H "Authorization: Bearer $LIVE_JWT")
    echo "GET /api/binolla/status"
    curl -fsS "${auth[@]}" "$BASE/api/binolla/status" || echo "binolla_status_FAILED"
    echo
    echo "GET /api/market/assets"
    curl -fsS "${auth[@]}" "$BASE/api/market/assets" || echo "assets_FAILED"
    echo
    # Pick first symbol if jq available
    ASSET="${LIVE_ASSET:-}"
    if [[ -z "$ASSET" ]] && command -v jq >/dev/null 2>&1; then
      ASSET="$(curl -fsS "${auth[@]}" "$BASE/api/market/assets" | jq -r '.assets[] | select(.available==true) | .symbol' | head -n1 || true)"
    fi
    if [[ -n "$ASSET" ]]; then
      echo "USING_ASSET=$ASSET"
      echo "GET /api/market/price/$ASSET"
      curl -fsS "${auth[@]}" "$BASE/api/market/price/$ASSET" || echo "price_FAILED"
      echo
      echo "GET /api/market/candles/$ASSET?period=60"
      curl -fsS "${auth[@]}" "$BASE/api/market/candles/$ASSET?period=60" || echo "candles_FAILED"
      echo
      echo "GET /api/strategies/rsi/signal/$ASSET"
      curl -fsS "${auth[@]}" "$BASE/api/strategies/rsi/signal/$ASSET" || echo "rsi_FAILED"
      echo
      echo "GET /api/binolla/balance"
      curl -fsS "${auth[@]}" "$BASE/api/binolla/balance" || echo "balance_FAILED"
      echo
      echo "GET /api/trades"
      curl -fsS "${auth[@]}" "$BASE/api/trades" || echo "trades_FAILED"
      echo
    else
      echo "NO_ASSET_SELECTED — set LIVE_ASSET=EURUSD_otc (only if returned by assets)"
    fi
  else
    echo "LIVE_JWT unset — skipping market/trade probes"
  fi

  echo "=== END ==="
} | tee "$REPORT"

echo "Wrote $REPORT"
