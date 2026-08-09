#!/usr/bin/env bash
# Reply to Telegram /start with Mini App button (PM2)
#
#   cd /home/web/backend
#   chmod +x tools/telegram-start-reply/start-pm2.sh
#   ./tools/telegram-start-reply/start-pm2.sh

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

if [[ -f "$ROOT/scaralpha.env" ]]; then
  set -a
  # shellcheck disable=SC1091
  source "$ROOT/scaralpha.env"
  set +a
fi

: "${TELEGRAM_BOT_TOKEN:?Set TELEGRAM_BOT_TOKEN in scaralpha.env}"
export TELEGRAM_BOT_TOKEN
export MINIAPP_URL="${MINIAPP_URL:-https://www.scaralphaai.com/}"
export BUTTON_TEXT="${BUTTON_TEXT:-Open Scar Alpha}"

command -v pm2 >/dev/null || { echo "pm2 missing"; exit 1; }
command -v node >/dev/null || { echo "node missing"; exit 1; }

APP=scaralpha-tg-start
SCRIPT="$ROOT/tools/telegram-start-reply/start-reply.mjs"

pm2 delete "$APP" >/dev/null 2>&1 || true
pm2 start "$SCRIPT" --name "$APP" --interpreter node --update-env
pm2 save
echo "OK: $APP running — press Start on the bot, you should get an Open button."
pm2 logs "$APP" --lines 20
