#!/usr/bin/env bash
# Install Playwright Chromium + Linux shared libraries for Binolla credential login.
# Run once on the VPS (as root):
#   cd /home/web/backend/tools/binolla-auth
#   chmod +x install-deps.sh
#   ./install-deps.sh

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

info() { echo "==> $*"; }
ok() { echo "OK: $*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

need() { command -v "$1" >/dev/null 2>&1; }

need npm || die "npm/node missing"
need npx || die "npx missing"

info "npm install (playwright package)"
npm install

has_libatk() {
  ldconfig -p 2>/dev/null | grep -q 'libatk-1.0.so.0' && return 0
  # Fallback probe
  [[ -e /usr/lib/x86_64-linux-gnu/libatk-1.0.so.0 ]] || [[ -e /lib/x86_64-linux-gnu/libatk-1.0.so.0 ]]
}

info "Install Chromium OS dependencies (Playwright)"
if npx playwright install-deps chromium; then
  ok "playwright install-deps chromium"
else
  echo "WARN: playwright install-deps failed — trying apt packages"
  if need apt-get; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -y
    # Ubuntu 24.04 uses t64 transitional packages; older releases use unversioned names.
    apt-get install -y \
      libatk1.0-0t64 libatk-bridge2.0-0t64 libcups2t64 libasound2t64 \
      libdrm2 libxkbcommon0 libxcomposite1 libxdamage1 libxfixes3 libxrandr2 \
      libgbm1 libpango-1.0-0 libcairo2 libnss3 libnspr4 libx11-xcb1 libxcb1 \
      libxext6 libx11-6 fonts-liberation ca-certificates \
      || apt-get install -y \
      libatk1.0-0 libatk-bridge2.0-0 libcups2 libasound2 \
      libdrm2 libxkbcommon0 libxcomposite1 libxdamage1 libxfixes3 libxrandr2 \
      libgbm1 libpango-1.0-0 libcairo2 libnss3 libnspr4 libx11-xcb1 libxcb1 \
      libxext6 libx11-6 fonts-liberation ca-certificates
  else
    die "Cannot install OS libs (no apt-get)"
  fi
fi

if ! has_libatk; then
  echo "WARN: libatk-1.0.so.0 still not found after install — Chromium may fail to launch"
else
  ok "libatk-1.0.so.0 present"
fi

info "Install Playwright Chromium browser"
npx playwright install chromium
ok "Chromium ready"

info "Smoke: launch chromium briefly"
node --input-type=module <<'EOF'
import { chromium } from 'playwright';
const browser = await chromium.launch({
  headless: true,
  args: ['--disable-dev-shm-usage', '--no-sandbox'],
});
await browser.close();
console.log('SMOKE_OK');
EOF

ok "Binolla auth browser dependencies installed"
