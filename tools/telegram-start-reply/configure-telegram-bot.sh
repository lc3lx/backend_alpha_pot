#!/usr/bin/env bash
# Point Telegram bot Mini App → https://www.scaralphaai.com/app/
#
#   cd /home/web/backend
#   chmod +x tools/telegram-start-reply/configure-telegram-bot.sh
#   ./tools/telegram-start-reply/configure-telegram-bot.sh

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
export MINIAPP_URL="${MINIAPP_URL:-https://www.scaralphaai.com/app/}"
export BUTTON_TEXT="${BUTTON_TEXT:-Open Scar Alpha}"

command -v node >/dev/null || { echo "node missing"; exit 1; }

node "$ROOT/tools/telegram-start-reply/configure-telegram-bot.mjs"

if command -v pm2 >/dev/null && pm2 describe scaralpha-tg-start >/dev/null 2>&1; then
  export MINIAPP_URL BUTTON_TEXT TELEGRAM_BOT_TOKEN
  pm2 restart scaralpha-tg-start --update-env
  pm2 save
  echo "==> scaralpha-tg-start restarted"
fi

echo "OK: Telegram Mini App → $MINIAPP_URL"
