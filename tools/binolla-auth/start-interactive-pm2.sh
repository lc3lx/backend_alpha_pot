#!/usr/bin/env bash
# Binolla guided-login server — run with PM2.
#
# Without this running, a login that hits a CAPTCHA can only tell the user to sign in at
# binolla.com and paste an SSID, which assumes they know what one is.
#
#   cd /home/web/backend/tools/binolla-auth
#   chmod +x start-interactive-pm2.sh
#   ./start-interactive-pm2.sh
#
# Commands: start (default) | restart | stop | delete | logs | status

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

APP_NAME="scaralpha-login"
HOST="${BINOLLA_INTERACTIVE_HOST:-127.0.0.1}"
PORT="${BINOLLA_INTERACTIVE_PORT:-8110}"

die() { echo "ERROR: $*" >&2; exit 1; }
info() { echo "==> $*"; }
ok() { echo "OK: $*"; }
need() { command -v "$1" >/dev/null 2>&1; }

load_env() {
  local f
  for f in \
    "${SCARALPHA_ENV_FILE:-}" \
    "$ROOT/../../scaralpha.env" \
    "/home/web/scaralpha.env"
  do
    if [[ -n "$f" && -f "$f" ]]; then
      info "Loading env: $f"
      set -a
      # shellcheck disable=SC1090
      source "$f"
      set +a
      break
    fi
  done

  export BINOLLA_INTERACTIVE_HOST="$HOST"
  export BINOLLA_INTERACTIVE_PORT="$PORT"
  export BINOLLA_INTERACTIVE_TOKEN="${BINOLLA_INTERACTIVE_TOKEN:-}"
  export BINOLLA_AUTH_PROXY="${BINOLLA_AUTH_PROXY:-}"

  if [[ "$HOST" != "127.0.0.1" && "$HOST" != "localhost" ]]; then
    die "Refusing to bind $HOST — this process handles live credentials and must stay on localhost."
  fi
  if [[ -z "$BINOLLA_AUTH_PROXY" ]]; then
    echo "WARN: no BINOLLA_AUTH_PROXY — the guided login will leave from this server's own IP,"
    echo "      which is the IP Binolla already refuses."
  fi
}

ensure_deps() {
  need node || die "node missing — install Node 20 first"
  [[ -d "$ROOT/node_modules/playwright" ]] || {
    info "Installing Playwright"
    (cd "$ROOT" && npm install) || die "npm install failed"
  }
}

main() {
  local cmd="${1:-start}"
  load_env

  case "$cmd" in
    start|restart|"")
      ensure_deps
      need pm2 || { need npm || die "npm missing"; npm install -g pm2; }
      mkdir -p "$ROOT/logs"
      pm2 delete "$APP_NAME" >/dev/null 2>&1 || true
      pm2 start "$ROOT/ecosystem.config.cjs" --update-env
      pm2 save
      sleep 2
      if curl -fsS "http://${HOST}:${PORT}/health" >/dev/null 2>&1; then
        ok "Guided login healthy → http://${HOST}:${PORT}/health"
      else
        echo "WARN: /health not answering yet — check: pm2 logs $APP_NAME"
      fi
      ;;
    stop)   pm2 stop "$APP_NAME" || true ;;
    delete|rm) pm2 delete "$APP_NAME" || true; pm2 save || true ;;
    logs)   pm2 logs "$APP_NAME" ;;
    status)
      pm2 status
      curl -fsS "http://${HOST}:${PORT}/health" && echo || echo "health: down"
      ;;
    *) die "Unknown command: $cmd" ;;
  esac
}

main "${1:-start}"
