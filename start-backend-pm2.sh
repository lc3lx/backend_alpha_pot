#!/usr/bin/env bash
# Scar Alpha Backend — publish + run with PM2
#
# On VPS:
#   cd /home/web/backend
#   chmod +x start-backend-pm2.sh
#   nano scaralpha.env          # set TELEGRAM_BOT_TOKEN etc.
#   ./start-backend-pm2.sh     # first time: publish + pm2 start
#
# Commands:
#   ./start-backend-pm2.sh              # publish + start/restart
#   ./start-backend-pm2.sh start        # same
#   ./start-backend-pm2.sh restart      # restart without republish
#   ./start-backend-pm2.sh stop
#   ./start-backend-pm2.sh delete
#   ./start-backend-pm2.sh logs
#   ./start-backend-pm2.sh status
#   ./start-backend-pm2.sh publish      # publish only

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

APP_NAME="scaralpha-api"
PUBLISH_DIR="$ROOT/publish"
LOG_DIR="$ROOT/logs"
ENV_FILE="${SCARALPHA_ENV_FILE:-}"
BACKEND_PORT="${BACKEND_PORT:-5207}"

die() { echo "ERROR: $*" >&2; exit 1; }
info() { echo "==> $*"; }
ok() { echo "OK: $*"; }
need() { command -v "$1" >/dev/null 2>&1; }

find_env_file() {
  if [[ -n "$ENV_FILE" && -f "$ENV_FILE" ]]; then
    echo "$ENV_FILE"
    return
  fi
  for f in \
    "$ROOT/scaralpha.env" \
    "$ROOT/.env" \
    "$(dirname "$ROOT")/scaralpha.env" \
    "/home/web/scaralpha.env"
  do
    if [[ -f "$f" ]]; then
      echo "$f"
      return
    fi
  done
  echo ""
}

rand_secret() {
  if need openssl; then
    openssl rand -base64 48 | tr -d '\n'
  else
    head -c 48 /dev/urandom | base64 | tr -d '\n'
  fi
}

load_env() {
  local f
  f="$(find_env_file)"
  if [[ -n "$f" ]]; then
    info "Loading env: $f"
    set -a
    # shellcheck disable=SC1090
    source "$f"
    set +a
  else
    echo "WARN: no scaralpha.env found — using defaults. Copy scaralpha.env.example → scaralpha.env"
  fi

  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:/usr/local/bin:$PATH"

  export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
  export BACKEND_PORT="${BACKEND_PORT:-5207}"
  export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://0.0.0.0:${BACKEND_PORT}}"
  export DATABASE_PROVIDER="${DATABASE_PROVIDER:-Npgsql}"
  export DATABASE_INMEMORY_NAME="${DATABASE_INMEMORY_NAME:-ScarAlphaVps}"
  export JWT_ISSUER="${JWT_ISSUER:-ScarAlpha}"
  export JWT_AUDIENCE="${JWT_AUDIENCE:-ScarAlpha.App}"

  if [[ "${ASPNETCORE_ENVIRONMENT}" == "Production" ]]; then
    if [[ "${DATABASE_PROVIDER}" == "InMemory" ]]; then
      die "Production forbids DATABASE_PROVIDER=InMemory — set Npgsql + DATABASE_CONNECTION_STRING"
    fi
    if [[ -z "${DATABASE_CONNECTION_STRING:-}" ]]; then
      die "Production requires DATABASE_CONNECTION_STRING"
    fi
    # Unquoted ';' in scaralpha.env truncates the value when sourced by bash
    # (Password= is lost → Npgsql: "No password has been provided").
    if [[ "${DATABASE_CONNECTION_STRING}" != *"Password="* && "${DATABASE_CONNECTION_STRING}" != *"Password ="* ]]; then
      die "DATABASE_CONNECTION_STRING missing Password= — quote it in scaralpha.env like: DATABASE_CONNECTION_STRING='Host=...;Password=...'"
    fi
    if [[ -z "${JWT_SECRET:-}" ]]; then
      die "Production requires JWT_SECRET in scaralpha.env (do not leave empty)"
    fi
    # HS256 needs >= 256 bits (32 UTF-8 bytes). Short secrets cause IDX10720 at login.
    if [[ "${#JWT_SECRET}" -lt 32 ]]; then
      die "JWT_SECRET too short (${#JWT_SECRET} chars). Use: openssl rand -base64 48"
    fi
    if [[ -z "${BINOLLA_TOKEN_ENCRYPTION_KEY:-}" ]]; then
      die "Production requires BINOLLA_TOKEN_ENCRYPTION_KEY in scaralpha.env"
    fi
    if [[ "${#BINOLLA_TOKEN_ENCRYPTION_KEY}" -lt 32 ]]; then
      die "BINOLLA_TOKEN_ENCRYPTION_KEY too short (${#BINOLLA_TOKEN_ENCRYPTION_KEY} chars). Use: openssl rand -base64 48"
    fi
  else
    # Development convenience only — ephemeral secrets / InMemory allowed
    if [[ -z "${JWT_SECRET:-}" ]]; then
      JWT_SECRET="$(rand_secret)"
      export JWT_SECRET
      echo "WARN: JWT_SECRET was empty — generated ephemeral secret"
    fi
    if [[ -z "${BINOLLA_TOKEN_ENCRYPTION_KEY:-}" ]]; then
      BINOLLA_TOKEN_ENCRYPTION_KEY="$(rand_secret)"
      export BINOLLA_TOKEN_ENCRYPTION_KEY
      echo "WARN: BINOLLA_TOKEN_ENCRYPTION_KEY was empty — generated ephemeral key"
    fi
    if [[ "${DATABASE_PROVIDER}" == "InMemory" ]]; then
      echo "WARN: DATABASE_PROVIDER=InMemory — data will not survive restart"
    fi
  fi

  if [[ -z "${TELEGRAM_BOT_TOKEN:-}" ]]; then
    export TELEGRAM_BOT_TOKEN="000000000:REPLACE_ME_DEV_TOKEN"
    echo "WARN: TELEGRAM_BOT_TOKEN not set — Telegram auth will fail"
  fi

  local ip
  ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
  ip="${ip:-127.0.0.1}"
  export CORS_ORIGINS="${CORS_ORIGINS:-http://${ip}:4173,http://127.0.0.1:4173,http://localhost:4173,http://${ip}:4175,http://127.0.0.1:4175,http://localhost:4175,http://${ip}:5175,http://127.0.0.1:5175,http://localhost:5175}"
  export SCARALPHA_AGENT_DEBUG="${SCARALPHA_AGENT_DEBUG:-0}"
  export ADMIN_TELEGRAM_USER_IDS="${ADMIN_TELEGRAM_USER_IDS:-}"
  export DATABASE_CONNECTION_STRING="${DATABASE_CONNECTION_STRING:-}"
  export BINOLLA_AUTH_PROXY="${BINOLLA_AUTH_PROXY:-}"
}

ensure_pm2() {
  if need pm2; then
    return 0
  fi
  need npm || die "npm/node missing — install Node 20 first"
  info "Installing PM2 globally..."
  npm install -g pm2
  need pm2 || die "pm2 install failed"
  ok "PM2 installed: $(pm2 -v)"
}

ensure_dotnet() {
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:/usr/local/bin:$PATH"
  need dotnet || die "dotnet missing — install .NET 8 SDK first"
}

publish_api() {
  ensure_dotnet
  mkdir -p "$LOG_DIR" "$PUBLISH_DIR"
  info "Publishing ScarAlpha.Api (Release) → $PUBLISH_DIR"
  dotnet publish "$ROOT/ScarAlpha.Api/ScarAlpha.Api.csproj" -c Release -o "$PUBLISH_DIR" --nologo
  [[ -f "$PUBLISH_DIR/ScarAlpha.Api.dll" ]] || die "publish failed — ScarAlpha.Api.dll not found"
  ok "Publish done"
}

pm2_start_or_restart() {
  ensure_pm2
  mkdir -p "$LOG_DIR"

  info "Stopping previous $APP_NAME and freeing port ${BACKEND_PORT}"
  pm2 delete "$APP_NAME" >/dev/null 2>&1 || true
  # Failed restarts leave extra forks (logs showed id 105 still serving while 124 could not bind).
  if command -v fuser >/dev/null 2>&1; then
    fuser -k "${BACKEND_PORT}/tcp" >/dev/null 2>&1 || true
  fi
  if command -v lsof >/dev/null 2>&1; then
    local pids
    pids="$(lsof -ti ":${BACKEND_PORT}" 2>/dev/null || true)"
    if [[ -n "${pids}" ]]; then
      # shellcheck disable=SC2086
      kill -9 ${pids} >/dev/null 2>&1 || true
    fi
  fi
  sleep 2

  info "Starting PM2 app: $APP_NAME"
  pm2 start "$ROOT/ecosystem.config.cjs" --update-env
  pm2 save
  ok "PM2 running"
  sleep 2
  if curl -fsS "http://127.0.0.1:${BACKEND_PORT}/health" >/dev/null 2>&1; then
    ok "Health OK → http://127.0.0.1:${BACKEND_PORT}/health"
  else
    echo "WARN: /health not ready yet — check: pm2 logs $APP_NAME"
  fi
}

print_help() {
  cat <<EOF
Scar Alpha Backend (PM2)

  ./start-backend-pm2.sh           publish + start/restart
  ./start-backend-pm2.sh start     publish + start/restart
  ./start-backend-pm2.sh restart   restart only (no publish)
  ./start-backend-pm2.sh stop
  ./start-backend-pm2.sh delete
  ./start-backend-pm2.sh logs
  ./start-backend-pm2.sh status
  ./start-backend-pm2.sh publish

Env file (first found wins):
  $ROOT/scaralpha.env
  $(dirname "$ROOT")/scaralpha.env
  /home/web/scaralpha.env
EOF
}

main() {
  local cmd="${1:-start}"
  load_env

  case "$cmd" in
    start|"")
      publish_api
      pm2_start_or_restart
      echo ""
      echo "Backend: http://0.0.0.0:${BACKEND_PORT}/health"
      echo "Logs:    pm2 logs $APP_NAME"
      ;;
    restart)
      pm2_start_or_restart
      ;;
    stop)
      ensure_pm2
      pm2 stop "$APP_NAME" || true
      ;;
    delete|rm)
      ensure_pm2
      pm2 delete "$APP_NAME" || true
      if command -v fuser >/dev/null 2>&1; then
        fuser -k "${BACKEND_PORT}/tcp" >/dev/null 2>&1 || true
      fi
      pm2 save || true
      ;;
    logs)
      ensure_pm2
      pm2 logs "$APP_NAME"
      ;;
    status|list)
      ensure_pm2
      pm2 status
      curl -fsS "http://127.0.0.1:${BACKEND_PORT}/health" && echo || echo "health: down"
      ;;
    publish)
      publish_api
      ;;
    help|-h|--help)
      print_help
      ;;
    *)
      die "Unknown command: $cmd (try: start|restart|stop|logs|status)"
      ;;
  esac
}

main "${1:-start}"
