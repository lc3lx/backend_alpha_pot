#!/usr/bin/env bash
# Compare the bot's candles and RSI against the Binolla chart.
#
# The Binolla token lives about 15 minutes and shell exports do not survive a new
# session, so capture and run happen together here rather than as separate steps.
#
#   ./tools/rsi-check.sh                      # first open EUR pair, RSI + candles
#   ./tools/rsi-check.sh AUDUSD_otc           # a specific pair
#   ./tools/rsi-check.sh AUDUSD_otc --probe   # also probe for a server-side indicator feed
#
# Credentials come from the environment; nothing is written to disk:
#   export BINOLLA_AUTH_EMAIL='you@example.com'
#   export BINOLLA_AUTH_PASSWORD='...'
set -euo pipefail

BACKEND_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
AUTH_DIR="$BACKEND_DIR/tools/binolla-auth"

if [[ -z "${BINOLLA_AUTH_EMAIL:-}" || -z "${BINOLLA_AUTH_PASSWORD:-}" ]]; then
  echo "Set BINOLLA_AUTH_EMAIL and BINOLLA_AUTH_PASSWORD first:" >&2
  echo "  export BINOLLA_AUTH_EMAIL='you@example.com'" >&2
  echo "  export BINOLLA_AUTH_PASSWORD='...'" >&2
  exit 2
fi

ASSET=""
EXTRA=()
for arg in "$@"; do
  case "$arg" in
    --*) EXTRA+=("$arg") ;;
    *)   [[ -z "$ASSET" ]] && ASSET="$arg" || EXTRA+=("$arg") ;;
  esac
done

echo "Logging in to Binolla..." >&2
OUT="$(cd "$AUTH_DIR" && node capture.mjs \
  --mode login --headless true \
  --loginUrl 'https://binolla.com/login/' --timeoutMs 90000)"

if ! echo "$OUT" | grep -q '"ok":true'; then
  echo "Login failed:" >&2
  echo "$OUT" >&2
  exit 1
fi

TOKEN="$(echo "$OUT" | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')"
COOKIES="$(echo "$OUT" | python3 -c 'import sys,json;print(json.load(sys.stdin).get("cookies",""))')"

export BINOLLA_SSID="42[\"authorization\",{\"isDemo\":true,\"token\":\"$TOKEN\"}]"
export BINOLLA_COOKIES="$COOKIES"

ARGS=(--rsi --candles)
[[ -n "$ASSET" ]] && ARGS+=(--asset "$ASSET")
ARGS+=("${EXTRA[@]:-}")

cd "$BACKEND_DIR"
dotnet run --project ScarAlpha.Binolla.Smoke -- "${ARGS[@]}" 2>/dev/null | grep -v '^DBG'
