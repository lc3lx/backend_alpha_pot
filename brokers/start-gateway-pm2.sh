#!/usr/bin/env bash
# Scar Alpha Broker Gateway — install + run with PM2
#
# The .NET API calls this process for every non-Binolla broker. When it is not running,
# a Quotex login fails with "Connection refused (127.0.0.1:8100)".
#
# On VPS:
#   cd /home/web/backend/brokers
#   chmod +x start-gateway-pm2.sh
#   ./start-gateway-pm2.sh          # first time: venv + deps + pm2 start
#
# Commands:
#   ./start-gateway-pm2.sh            install (if needed) + start/restart
#   ./start-gateway-pm2.sh restart    restart without reinstalling
#   ./start-gateway-pm2.sh install    dependencies only
#   ./start-gateway-pm2.sh stop | delete | logs | status

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

APP_NAME="scaralpha-brokers"
VENV="$ROOT/.venv"
VENDOR="$ROOT/vendor"
QUOTEX_REPO="https://github.com/ChipaDevTeam/QuotexAPI.git"
BROKER_GATEWAY_HOST="${BROKER_GATEWAY_HOST:-127.0.0.1}"
BROKER_GATEWAY_PORT="${BROKER_GATEWAY_PORT:-8100}"

die() { echo "ERROR: $*" >&2; exit 1; }
info() { echo "==> $*"; }
ok() { echo "OK: $*"; }
need() { command -v "$1" >/dev/null 2>&1; }

load_env() {
  local f
  for f in \
    "${SCARALPHA_ENV_FILE:-}" \
    "$ROOT/../scaralpha.env" \
    "$ROOT/../.env" \
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

  # One proxy line in scaralpha.env should serve both processes: the .NET side reads
  # BINOLLA_AUTH_PROXY, this one reads BROKER_PROXY.
  export BROKER_PROXY="${BROKER_PROXY:-${BINOLLA_AUTH_PROXY:-}}"
  export BINOLLA_AUTH_PROXY="${BINOLLA_AUTH_PROXY:-}"
  export BROKER_GATEWAY_TOKEN="${BROKER_GATEWAY_TOKEN:-}"
  export BROKER_GATEWAY_HOST BROKER_GATEWAY_PORT

  if [[ "$BROKER_GATEWAY_HOST" != "127.0.0.1" && "$BROKER_GATEWAY_HOST" != "localhost" ]]; then
    die "Refusing to bind $BROKER_GATEWAY_HOST — this process holds live trading sessions and must stay on localhost."
  fi
  if [[ -z "$BROKER_PROXY" ]]; then
    echo "WARN: no BROKER_PROXY/BINOLLA_AUTH_PROXY set — brokers that refuse this server's IP will reject every login"
  fi
}

ensure_python() {
  need python3 || die "python3 missing — install Python 3.10+ first"
  if [[ ! -d "$VENV" ]]; then
    info "Creating virtualenv: $VENV"
    python3 -m venv "$VENV" || die "venv creation failed (try: apt install python3-venv)"
  fi
}

install_deps() {
  ensure_python
  info "Installing gateway dependencies"
  "$VENV/bin/pip" install --upgrade pip >/dev/null
  "$VENV/bin/pip" install -e "$ROOT" >/dev/null
  ok "Gateway installed"

  # QuotexAPI is not on PyPI, so it is vendored. Without it the Quotex adapter imports
  # lazily and every connect fails at runtime rather than at start-up.
  if [[ ! -d "$VENDOR/QuotexAPI" ]]; then
    need git || die "git missing — needed to fetch QuotexAPI"
    info "Cloning QuotexAPI → $VENDOR/QuotexAPI"
    mkdir -p "$VENDOR"
    git clone --depth 1 "$QUOTEX_REPO" "$VENDOR/QuotexAPI" || die "QuotexAPI clone failed"
  fi
  info "Installing QuotexAPI"
  "$VENV/bin/pip" install -e "$VENDOR/QuotexAPI" >/dev/null || die "QuotexAPI install failed"

  # Its pyproject under-declares: the code imports curl_cffi, which nothing in
  # [project.dependencies] mentions, so a clean install ends in ModuleNotFoundError at
  # the first Quotex login. requirements.txt is the complete list — install it too.
  if [[ -f "$VENDOR/QuotexAPI/requirements.txt" ]]; then
    info "Installing QuotexAPI runtime requirements"
    "$VENV/bin/pip" install -r "$VENDOR/QuotexAPI/requirements.txt" >/dev/null \
      || die "QuotexAPI requirements install failed"
  fi

  # Proof the library actually imports. Without this the failure surfaces later as a 409
  # on a login, with nothing naming the real cause.
  if "$VENV/bin/python" -c "from app.brokers.quotex import _load_client_cls; _load_client_cls()" 2>/dev/null; then
    ok "QuotexAPI installed and importable"
  else
    echo "WARN: QuotexAPI installed but does not import — Quotex logins will fail. Details:"
    "$VENV/bin/python" -c "from app.brokers.quotex import _load_client_cls; _load_client_cls()" || true
  fi
}

ensure_pm2() {
  need pm2 && return 0
  need npm || die "npm/node missing — install Node 20 first"
  info "Installing PM2 globally..."
  npm install -g pm2
  need pm2 || die "pm2 install failed"
}

pm2_start_or_restart() {
  ensure_pm2
  mkdir -p "$ROOT/logs"
  [[ -x "$VENV/bin/uvicorn" ]] || die "uvicorn not installed — run: ./start-gateway-pm2.sh install"

  info "Restarting PM2 app: $APP_NAME"
  pm2 delete "$APP_NAME" >/dev/null 2>&1 || true
  pm2 start "$ROOT/ecosystem.config.cjs" --update-env
  pm2 save
  sleep 2

  if curl -fsS "http://${BROKER_GATEWAY_HOST}:${BROKER_GATEWAY_PORT}/health" >/dev/null 2>&1; then
    ok "Gateway healthy → http://${BROKER_GATEWAY_HOST}:${BROKER_GATEWAY_PORT}/health"
  else
    echo "WARN: /health not answering yet — check: pm2 logs $APP_NAME"
  fi
}

main() {
  local cmd="${1:-start}"
  load_env

  case "$cmd" in
    start|"")
      [[ -x "$VENV/bin/uvicorn" ]] || install_deps
      pm2_start_or_restart
      echo ""
      echo "Gateway: http://${BROKER_GATEWAY_HOST}:${BROKER_GATEWAY_PORT}/health"
      echo "Logs:    pm2 logs $APP_NAME"
      ;;
    install) install_deps ;;
    restart) pm2_start_or_restart ;;
    stop)    ensure_pm2; pm2 stop "$APP_NAME" || true ;;
    delete|rm) ensure_pm2; pm2 delete "$APP_NAME" || true; pm2 save || true ;;
    logs)    ensure_pm2; pm2 logs "$APP_NAME" ;;
    status)
      ensure_pm2
      pm2 status
      curl -fsS "http://${BROKER_GATEWAY_HOST}:${BROKER_GATEWAY_PORT}/health" && echo || echo "health: down"
      ;;
    *) die "Unknown command: $cmd (try: start|restart|install|stop|logs|status)" ;;
  esac
}

main "${1:-start}"
