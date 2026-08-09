#!/usr/bin/env bash
# Scar Alpha — fresh VPS bootstrap + start (backend API + frontend)
#
# Usage on the server:
#   cd /home/web
#   chmod +x start-vps.sh
#   sudo ./start-vps.sh
#
# Options:
#   ./start-vps.sh              # install deps if needed, then start
#   ./start-vps.sh --install    # only install runtime deps
#   ./start-vps.sh --start      # only start (skip apt/dotnet install)
#   ./start-vps.sh --stop       # stop running processes
#   ./start-vps.sh --status     # show health / pids
#
# First-run defaults (easy on empty VPS):
#   - ASPNETCORE_ENVIRONMENT=Development
#   - DATABASE_PROVIDER=InMemory  (no Postgres required)
#   - Frontend: npm run build + npm run preview (proxies /api → backend)
#
# Override via env or file: /home/web/scaralpha.env

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="${SCARALPHA_ENV_FILE:-$ROOT/scaralpha.env}"
LOG_DIR="${SCARALPHA_LOG_DIR:-$ROOT/logs}"
PID_DIR="${SCARALPHA_PID_DIR:-$ROOT/run}"
BACKEND_PORT="${BACKEND_PORT:-5207}"
FRONT_PORT="${FRONT_PORT:-4173}"

detect_backend() {
  for d in "$ROOT/backend" "$ROOT/Backend" "$ROOT/scaralpha-backend"; do
    if [[ -f "$d/ScarAlpha.Api/ScarAlpha.Api.csproj" ]] || [[ -f "$d/ScarAlpha.sln" ]]; then
      echo "$d"
      return 0
    fi
  done
  return 1
}

detect_front() {
  for d in "$ROOT/front" "$ROOT/frontend" "$ROOT/bot_telegram_webapp" "$ROOT/Front"; do
    if [[ -f "$d/package.json" ]]; then
      echo "$d"
      return 0
    fi
  done
  return 1
}

BACKEND_DIR="${BACKEND_DIR:-$(detect_backend || true)}"
FRONT_DIR="${FRONT_DIR:-$(detect_front || true)}"

die() { echo "ERROR: $*" >&2; exit 1; }
info() { echo "==> $*"; }
ok() { echo "OK: $*"; }

need_cmd() { command -v "$1" >/dev/null 2>&1; }

load_env_file() {
  if [[ -f "$ENV_FILE" ]]; then
    info "Loading $ENV_FILE"
    set -a
    # shellcheck disable=SC1090
    source "$ENV_FILE"
    set +a
  fi
}

rand_secret() {
  if need_cmd openssl; then
    openssl rand -base64 48 | tr -d '\n'
  else
    head -c 48 /dev/urandom | base64 | tr -d '\n'
  fi
}

ensure_env_defaults() {
  export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
  export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://0.0.0.0:${BACKEND_PORT}}"
  export DATABASE_PROVIDER="${DATABASE_PROVIDER:-InMemory}"
  export DATABASE_INMEMORY_NAME="${DATABASE_INMEMORY_NAME:-ScarAlphaVps}"
  export JWT_ISSUER="${JWT_ISSUER:-ScarAlpha}"
  export JWT_AUDIENCE="${JWT_AUDIENCE:-ScarAlpha.App}"

  if [[ -z "${JWT_SECRET:-}" ]]; then
    JWT_SECRET="$(rand_secret)"
    export JWT_SECRET
  fi
  if [[ -z "${BINOLLA_TOKEN_ENCRYPTION_KEY:-}" ]]; then
    BINOLLA_TOKEN_ENCRYPTION_KEY="$(rand_secret)"
    export BINOLLA_TOKEN_ENCRYPTION_KEY
  fi
  if [[ -z "${TELEGRAM_BOT_TOKEN:-}" ]]; then
    export TELEGRAM_BOT_TOKEN="${TELEGRAM_BOT_TOKEN:-000000000:REPLACE_ME_DEV_TOKEN}"
    echo "WARN: TELEGRAM_BOT_TOKEN not set — Telegram login will fail until you set a real bot token."
  fi

  # Frontend origin for CORS (preview default)
  local host_ip
  host_ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
  host_ip="${host_ip:-127.0.0.1}"
  export CORS_ORIGINS="${CORS_ORIGINS:-http://${host_ip}:${FRONT_PORT},http://127.0.0.1:${FRONT_PORT},http://localhost:${FRONT_PORT}}"

  # Preview proxies /api to backend on same machine
  export VITE_DEV_PROXY_TARGET="${VITE_DEV_PROXY_TARGET:-http://127.0.0.1:${BACKEND_PORT}}"
  # Empty = same-origin /api via Vite preview proxy
  export VITE_API_BASE_URL="${VITE_API_BASE_URL:-}"
}

install_deps() {
  if ! need_cmd apt-get; then
    die "This installer supports Debian/Ubuntu (apt). Install .NET 8 + Node 20 manually, then re-run with --start"
  fi

  info "Installing system packages (may take a few minutes)..."
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -y

  # Base packages (always)
  apt-get install -y curl ca-certificates gnupg apt-transport-https software-properties-common \
    build-essential fonts-liberation

  # Chromium/Playwright libs — Ubuntu 24.04 (Noble) uses t64 package names.
  # Install best-effort so one missing package does not abort the whole script.
  local chrome_pkgs=(
    libnss3 libdrm2 libxkbcommon0 libgbm1
    libxcomposite1 libxdamage1 libxfixes3 libxrandr2
    libpango-1.0-0 libcairo2
    libatk-bridge2.0-0t64 libatk-bridge2.0-0
    libasound2t64 libasound2
  )
  local pkg
  for pkg in "${chrome_pkgs[@]}"; do
    apt-get install -y "$pkg" >/dev/null 2>&1 || true
  done

  if ! need_cmd dotnet || ! dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; then
    info "Installing .NET 8 SDK..."
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 8.0
    export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
    export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
    if [[ ! -e /usr/local/bin/dotnet ]]; then
      ln -sf "$DOTNET_ROOT/dotnet" /usr/local/bin/dotnet || true
    fi
    # Persist for later shells
    if [[ ! -f /etc/profile.d/dotnet.sh ]]; then
      cat >/etc/profile.d/dotnet.sh <<EOF
export DOTNET_ROOT="\$HOME/.dotnet"
export PATH="\$DOTNET_ROOT:\$DOTNET_ROOT/tools:\$PATH"
EOF
    fi
  else
    ok ".NET 8 already present"
  fi

  if ! need_cmd node || [[ "$(node -v 2>/dev/null | sed 's/v//' | cut -d. -f1)" -lt 18 ]]; then
    info "Installing Node.js 20..."
    curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
    apt-get install -y nodejs
  else
    ok "Node already present: $(node -v)"
  fi

  ok "Runtime deps ready: dotnet=$(dotnet --version 2>/dev/null || echo missing) node=$(node -v)"
}

setup_binolla_tool() {
  local tool="$BACKEND_DIR/tools/binolla-auth"
  if [[ ! -d "$tool" ]]; then
    echo "WARN: Binolla auth tool missing at $tool — /api/binolla/login|signup will fail until installed."
    return 0
  fi
  info "Installing Binolla Playwright tool..."
  (cd "$tool" && npm install --silent)
  (cd "$tool" && npx playwright install chromium) || true
  # Best-effort system deps for Chromium
  (cd "$tool" && npx playwright install-deps chromium) || true
  ok "Binolla auth tool ready"
}

write_env_example() {
  if [[ -f "$ENV_FILE" ]]; then
    return 0
  fi
  cat >"$ENV_FILE" <<'EOF'
# Scar Alpha VPS env — edit and re-run: ./start-vps.sh --start
# ASPNETCORE_ENVIRONMENT=Development
# DATABASE_PROVIDER=InMemory
# TELEGRAM_BOT_TOKEN=123456:REAL_BOT_TOKEN_FROM_BOTFATHER
# ADMIN_TELEGRAM_USER_IDS=YOUR_TELEGRAM_NUMERIC_ID
# JWT_SECRET=
# BINOLLA_TOKEN_ENCRYPTION_KEY=
# CORS_ORIGINS=http://YOUR_SERVER_IP:4173
# BACKEND_PORT=5207
# FRONT_PORT=4173
EOF
  ok "Wrote template $ENV_FILE — set TELEGRAM_BOT_TOKEN before real Mini App use"
}

start_backend() {
  [[ -n "$BACKEND_DIR" ]] || die "Backend folder not found under $ROOT (expected backend/ with ScarAlpha.sln)"
  mkdir -p "$LOG_DIR" "$PID_DIR"

  if [[ -f "$PID_DIR/backend.pid" ]] && kill -0 "$(cat "$PID_DIR/backend.pid")" 2>/dev/null; then
    ok "Backend already running pid=$(cat "$PID_DIR/backend.pid")"
    return 0
  fi

  info "Starting backend on ${ASPNETCORE_URLS} ..."
  # Ensure dotnet on PATH for non-interactive nohup
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:/usr/local/bin:$PATH"

  nohup bash -lc "cd '$BACKEND_DIR' && \
    export ASPNETCORE_ENVIRONMENT='$ASPNETCORE_ENVIRONMENT' \
      ASPNETCORE_URLS='$ASPNETCORE_URLS' \
      DATABASE_PROVIDER='$DATABASE_PROVIDER' \
      DATABASE_INMEMORY_NAME='$DATABASE_INMEMORY_NAME' \
      JWT_SECRET='$JWT_SECRET' \
      JWT_ISSUER='$JWT_ISSUER' \
      JWT_AUDIENCE='$JWT_AUDIENCE' \
      BINOLLA_TOKEN_ENCRYPTION_KEY='$BINOLLA_TOKEN_ENCRYPTION_KEY' \
      TELEGRAM_BOT_TOKEN='$TELEGRAM_BOT_TOKEN' \
      CORS_ORIGINS='$CORS_ORIGINS' \
      ADMIN_TELEGRAM_USER_IDS='${ADMIN_TELEGRAM_USER_IDS:-}' \
      DATABASE_CONNECTION_STRING='${DATABASE_CONNECTION_STRING:-}' && \
    dotnet run --project ScarAlpha.Api -c Release --no-launch-profile" \
    >"$LOG_DIR/backend.log" 2>&1 &

  echo $! >"$PID_DIR/backend.pid"
  sleep 4

  if curl -fsS "http://127.0.0.1:${BACKEND_PORT}/health" >/dev/null 2>&1; then
    ok "Backend healthy: http://127.0.0.1:${BACKEND_PORT}/health"
  else
    echo "WARN: Backend started but /health not ready yet. Check: tail -f $LOG_DIR/backend.log"
  fi
}

start_front() {
  [[ -n "$FRONT_DIR" ]] || die "Frontend folder not found under $ROOT (expected front/ or bot_telegram_webapp/)"
  mkdir -p "$LOG_DIR" "$PID_DIR"

  if [[ -f "$PID_DIR/front.pid" ]] && kill -0 "$(cat "$PID_DIR/front.pid")" 2>/dev/null; then
    ok "Frontend already running pid=$(cat "$PID_DIR/front.pid")"
    return 0
  fi

  info "Installing frontend deps + building..."
  (cd "$FRONT_DIR" && npm install)
  (cd "$FRONT_DIR" && npm run build)

  info "Starting frontend preview on 0.0.0.0:${FRONT_PORT} (proxies /api → ${VITE_DEV_PROXY_TARGET}) ..."
  nohup bash -lc "cd '$FRONT_DIR' && \
    export VITE_API_BASE_URL='$VITE_API_BASE_URL' \
      VITE_DEV_PROXY_TARGET='$VITE_DEV_PROXY_TARGET' && \
    npx vite preview --host 0.0.0.0 --port ${FRONT_PORT}" \
    >"$LOG_DIR/front.log" 2>&1 &

  echo $! >"$PID_DIR/front.pid"
  sleep 2
  ok "Frontend: http://0.0.0.0:${FRONT_PORT}/"
}

stop_all() {
  for name in front backend; do
    local pf="$PID_DIR/${name}.pid"
    if [[ -f "$pf" ]]; then
      local pid
      pid="$(cat "$pf")"
      if kill -0 "$pid" 2>/dev/null; then
        info "Stopping $name pid=$pid"
        kill "$pid" 2>/dev/null || true
        sleep 1
        kill -9 "$pid" 2>/dev/null || true
      fi
      rm -f "$pf"
    fi
  done
  # Also kill stray listeners on our ports (best effort)
  if need_cmd fuser; then
    fuser -k "${BACKEND_PORT}/tcp" 2>/dev/null || true
    fuser -k "${FRONT_PORT}/tcp" 2>/dev/null || true
  fi
  ok "Stopped"
}

status_all() {
  echo "ROOT=$ROOT"
  echo "BACKEND_DIR=${BACKEND_DIR:-missing}"
  echo "FRONT_DIR=${FRONT_DIR:-missing}"
  echo "ENV_FILE=$ENV_FILE"
  echo "LOG_DIR=$LOG_DIR"
  for name in backend front; do
    local pf="$PID_DIR/${name}.pid"
    if [[ -f "$pf" ]] && kill -0 "$(cat "$pf")" 2>/dev/null; then
      echo "$name: RUNNING pid=$(cat "$pf")"
    else
      echo "$name: STOPPED"
    fi
  done
  curl -fsS "http://127.0.0.1:${BACKEND_PORT}/health" && echo || echo "backend /health: down"
}

print_summary() {
  local ip
  ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
  ip="${ip:-YOUR_SERVER_IP}"
  cat <<EOF

========================================
 Scar Alpha is starting
========================================
 Backend : http://${ip}:${BACKEND_PORT}/health
 Frontend: http://${ip}:${FRONT_PORT}/
 Logs    : $LOG_DIR/backend.log
           $LOG_DIR/front.log
 Env     : $ENV_FILE

 Next:
  1) Edit $ENV_FILE → set TELEGRAM_BOT_TOKEN (+ ADMIN_TELEGRAM_USER_IDS)
  2) ./start-vps.sh --stop && ./start-vps.sh --start
  3) Open firewall ports ${FRONT_PORT} and ${BACKEND_PORT} if needed
  4) Point Telegram Mini App URL to http://${ip}:${FRONT_PORT}/

 Note: InMemory DB resets on backend restart.
       For Postgres later: set DATABASE_PROVIDER=Npgsql and DATABASE_CONNECTION_STRING.
========================================
EOF
}

main() {
  local mode="${1:-all}"

  case "$mode" in
    --stop|stop) stop_all; exit 0 ;;
    --status|status) status_all; exit 0 ;;
  esac

  load_env_file
  write_env_example
  ensure_env_defaults

  # Refresh PATH for dotnet installed under $HOME
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:/usr/local/bin:$PATH"

  case "$mode" in
    --install|install)
      install_deps
      [[ -n "$BACKEND_DIR" ]] || die "backend folder not found"chmod +x /home/web/start-vps.sh
      setup_binolla_tool
      exit 0
      ;;
    --start|start)
      [[ -n "$BACKEND_DIR" ]] || die "backend folder not found"
      [[ -n "$FRONT_DIR" ]] || die "front folder not found"
      need_cmd dotnet || die "dotnet missing — run: sudo ./start-vps.sh --install"
      need_cmd node || die "node missing — run: sudo ./start-vps.sh --install"
      start_backend
      start_front
      print_summary
      exit 0
      ;;
    all|--all|"")
      if ! need_cmd dotnet || ! need_cmd node; then
        install_deps
      fi
      [[ -n "$BACKEND_DIR" ]] || die "backend folder not found under $ROOT"
      [[ -n "$FRONT_DIR" ]] || die "front folder not found under $ROOT (front/ or bot_telegram_webapp/)"
      setup_binolla_tool
      start_backend
      start_front
      print_summary
      exit 0
      ;;
    *)
      die "Unknown option: $mode (use --install | --start | --stop | --status)"
      ;;
  esac
}

main "${1:-}"
