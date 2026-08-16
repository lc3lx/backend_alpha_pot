#!/usr/bin/env bash
# Seed / update a website admin in Postgres (ASP.NET Identity password hash).
#
# On VPS:
#   cd /home/web/backend   # or wherever ScarAlpha.Api + scaralpha.env live
#   chmod +x tools/seed-admin/seed-admin.sh
#   # load DB string from env file:
#   set -a && source ./scaralpha.env && set +a
#   ./tools/seed-admin/seed-admin.sh 'scaralphaai@gmail.com' 'YourPasswordHere'
#
# Then ensure scaralpha.env contains:
#   ADMIN_EMAILS=scaralphaai@gmail.com
# and: ./start-backend-pm2.sh restart

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

EMAIL="${1:-}"
PASSWORD="${2:-}"

if [[ -z "$EMAIL" || -z "$PASSWORD" ]]; then
  echo "Usage: $0 <email> <password>" >&2
  exit 1
fi

if [[ -z "${DATABASE_CONNECTION_STRING:-}" ]]; then
  if [[ -f "$ROOT/scaralpha.env" ]]; then
    set -a
    # shellcheck disable=SC1091
    source "$ROOT/scaralpha.env"
    set +a
  fi
fi

if [[ -z "${DATABASE_CONNECTION_STRING:-}" ]]; then
  echo "ERROR: DATABASE_CONNECTION_STRING not set (export it or source scaralpha.env)" >&2
  exit 1
fi

export DATABASE_CONNECTION_STRING
dotnet run --project "$ROOT/tools/seed-admin" -c Release -- "$EMAIL" "$PASSWORD"

echo ""
echo "Next:"
echo "  1) In scaralpha.env set:  ADMIN_EMAILS=$EMAIL"
echo "  2) Restart API:           ./start-backend-pm2.sh restart"
echo "  3) Login on dashboard:    /dashboard/login"
