#!/usr/bin/env node
/**
 * Port of https://github.com/A11ksa/API-Binolla/blob/main/login.py
 * Usage:
 *   node capture.mjs --mode login --email a@b.com --password secret
 *   node capture.mjs --mode signup --email a@b.com --password secret
 * Prints JSON: { "ok": true, "token": "..." } or { "ok": false, "error": "..." }
 *
 * Strategy:
 * 1) Open Binolla page in Playwright (passes Cloudflare cookie jar)
 * 2) Prefer in-page fetch to /api/auth/login|register (same-origin + CF cookies)
 * 3) Fall back to DOM form fill + network/WS/storage token capture
 */
import { chromium } from 'playwright';

function arg(name, fallback = '') {
  const idx = process.argv.indexOf(`--${name}`);
  if (idx === -1) return fallback;
  return process.argv[idx + 1] ?? fallback;
}

function normalizeToken(raw) {
  if (!raw || typeof raw !== 'string') return null;
  let s = raw.trim();
  if (!s) return null;

  // localStorage sometimes stores: {"value":"<uuid>","expiry":"..."}
  if (s.startsWith('{')) {
    try {
      const obj = JSON.parse(s);
      if (obj && typeof obj === 'object') {
        if (typeof obj.value === 'string' && obj.value.length >= 16) s = obj.value.trim();
        else if (typeof obj.token === 'string' && obj.token.length >= 16) s = obj.token.trim();
      }
    } catch {
      /* keep s */
    }
  }

  // Double-encoded JSON string
  if ((s.startsWith('"') && s.endsWith('"')) || (s.startsWith("'") && s.endsWith("'"))) {
    try {
      const inner = JSON.parse(s);
      if (typeof inner === 'string') return normalizeToken(inner);
    } catch {
      /* keep s */
    }
  }

  if (s.length < 16 || s.includes('{') || s.includes('}')) return null;
  return s;
}

function extractToken(text) {
  if (!text || typeof text !== 'string') return null;

  const bracket = text.indexOf('[');
  if (bracket >= 0) {
    try {
      const arr = JSON.parse(text.slice(bracket));
      if (Array.isArray(arr) && arr.length >= 2 && typeof arr[1] === 'object' && arr[1]) {
        const event = String(arr[0] ?? '');
        const data = arr[1];
        if (/auth/i.test(event)) {
          const n = normalizeToken(typeof data.token === 'string' ? data.token : null);
          if (n) return n;
        }
        for (const [k, v] of Object.entries(data)) {
          if (/token/i.test(k) && typeof v === 'string') {
            const n = normalizeToken(v);
            if (n) return n;
          }
        }
      }
    } catch {
      /* ignore */
    }
  }

  try {
    const obj = JSON.parse(text);
    if (obj && typeof obj === 'object') {
      // Auth cookie/session shape: { value, expiry }
      const fromValue = normalizeToken(typeof obj.value === 'string' ? obj.value : null);
      if (fromValue) return fromValue;

      const fromToken = normalizeToken(typeof obj.token === 'string' ? obj.token : null);
      if (fromToken) return fromToken;

      for (const key of ['message', 'data', 'payload', 'result', 'user', 'session']) {
        const nested = obj[key];
        if (!nested || typeof nested !== 'object') continue;
        const n =
          normalizeToken(typeof nested.value === 'string' ? nested.value : null) ||
          normalizeToken(typeof nested.token === 'string' ? nested.token : null);
        if (n) return n;
      }
    }
  } catch {
    /* ignore */
  }

  const m = text.match(/"value"\s*:\s*"([A-Za-z0-9._-]{16,})"/);
  if (m) return normalizeToken(m[1]);

  const m2 = text.match(/"token"\s*:\s*"([A-Za-z0-9._-]{16,})"/);
  return normalizeToken(m2?.[1] ?? null);
}

function redactAuthBody(text) {
  if (!text) return '';
  return String(text)
    .replace(/"token"\s*:\s*"[^"]+"/gi, '"token":"[redacted]"')
    .replace(/"password"\s*:\s*"[^"]+"/gi, '"password":"[redacted]"')
    .replace(/\s+/g, ' ')
    .slice(0, 280);
}

async function fillFirst(page, selectors, value) {
  for (const sel of selectors) {
    try {
      const el = page.locator(sel).first();
      await el.waitFor({ state: 'visible', timeout: 4000 });
      await el.fill(value, { timeout: 4000 });
      return true;
    } catch {
      /* next */
    }
  }
  return false;
}

async function clickSubmit(page, isSignup) {
  const selectors = isSignup
    ? [
        'button[type="submit"]',
        'button:has-text("Sign Up")',
        'button:has-text("Register")',
        'button:has-text("Create")',
        'button:has-text("Continue")',
      ]
    : [
        'button[type="submit"]',
        'button:has-text("Sign In")',
        'button:has-text("Log In")',
        'button:has-text("Login")',
      ];

  for (const sel of selectors) {
    try {
      const btn = page.locator(sel).first();
      if ((await btn.count()) > 0) {
        await btn.click({ timeout: 4000 });
        return sel;
      }
    } catch {
      /* next */
    }
  }
  await page.keyboard.press('Enter');
  return 'Enter';
}

async function scanStorage(page) {
  return page.evaluate(() => {
    const unwrap = (raw) => {
      if (!raw || typeof raw !== 'string') return null;
      let s = raw.trim();
      if (!s) return null;
      if (s.startsWith('{')) {
        try {
          const obj = JSON.parse(s);
          if (obj && typeof obj.value === 'string' && obj.value.length >= 16) s = obj.value.trim();
          else if (obj && typeof obj.token === 'string' && obj.token.length >= 16) s = obj.token.trim();
        } catch {
          /* keep */
        }
      }
      if (s.length < 16 || s.includes('{') || s.includes('}')) return null;
      return s;
    };

    try {
      for (let i = 0; i < localStorage.length; i++) {
        const k = localStorage.key(i);
        const v = localStorage.getItem(k) || '';
        if (/token|session|auth/i.test(k || '')) {
          const n = unwrap(v);
          if (n) return n;
        }
        const mValue = /"value"\s*:\s*"([A-Za-z0-9._-]{16,})"/.exec(v);
        if (mValue) {
          const n = unwrap(mValue[1]);
          if (n) return n;
        }
        const m = /"token"\s*:\s*"([A-Za-z0-9._-]{16,})"/.exec(v);
        if (m) {
          const n = unwrap(m[1]);
          if (n) return n;
        }
      }
      for (const part of (document.cookie || '').split(';')) {
        const [ck, cv] = part.split('=');
        if (/token|session|auth/i.test(ck || '')) {
          const n = unwrap(decodeURIComponent(cv || ''));
          if (n) return n;
        }
      }
    } catch {
      /* ignore */
    }
    return null;
  });
}

async function readPageDiagnostics(page) {
  return page.evaluate(() => {
    const text = (document.body?.innerText || '').replace(/\s+/g, ' ').trim();
    const inputs = Array.from(document.querySelectorAll('input, select, textarea'))
      .slice(0, 20)
      .map((el) => ({
        tag: el.tagName.toLowerCase(),
        type: el.getAttribute('type') || '',
        name: el.getAttribute('name') || '',
        autocomplete: el.getAttribute('autocomplete') || '',
        required: el.required === true,
      }));
    const alerts = Array.from(
      document.querySelectorAll('[role="alert"], .error, .errors, .toast, .notification'),
    )
      .map((el) => (el.textContent || '').replace(/\s+/g, ' ').trim())
      .filter(Boolean)
      .slice(0, 5);
    return {
      url: location.href,
      title: document.title,
      textSnippet: text.slice(0, 400),
      inputs,
      alerts,
      hasCfChallenge: /just a moment|cf-challenge|checking your browser/i.test(text),
    };
  });
}

/**
 * Call Binolla auth API from inside the page so Cloudflare cookies apply.
 */
async function tryInPageAuthApi(page, { isSignup, email, password, lid }) {
  return page.evaluate(
    async ({ isSignup, email, password, lid }) => {
      const paths = isSignup
        ? ['/api/auth/register', '/api/v1/auth/register', '/api/auth/signup']
        : ['/api/auth/login', '/api/v1/auth/login'];

      const bodies = isSignup
        ? [
            {
              email,
              password,
              passwordConfirm: password,
              agreement: true,
              isNotUsCitizen: true,
              lid,
            },
            {
              email,
              password,
              confirmPassword: password,
              agreement: true,
              isNotUsCitizen: true,
              lid: String(lid),
            },
            {
              email,
              password,
              agreement: true,
              isNotUsCitizen: true,
              lid,
            },
            { email, password, agreement: true, isNotUsCitizen: true },
          ]
        : [{ email, password }, { email, password, remember: true }];

      const attempts = [];
      for (const path of paths) {
        for (const body of bodies) {
          try {
            const res = await fetch(path, {
              method: 'POST',
              headers: {
                'Content-Type': 'application/json',
                Accept: 'application/json',
              },
              credentials: 'include',
              body: JSON.stringify(body),
            });
            const text = await res.text();
            attempts.push({
              path,
              status: res.status,
              ct: res.headers.get('content-type') || '',
              body: text.slice(0, 1200),
            });
            // Stop early on success, geo-block, or hard credential rejection.
            if (res.status >= 200 && res.status < 300) return { attempts, okStatus: true };
            if (
              /not available in your current location|geo|region|restricted/i.test(text)
            ) {
              return { attempts, okStatus: false, geoBlocked: true };
            }
            // Keep trying other bodies on validation errors (e.g. missing agreement).
            if (res.status === 401 || res.status === 403) {
              return { attempts, okStatus: false };
            }
          } catch (e) {
            attempts.push({
              path,
              status: 0,
              error: String(e?.message || e).slice(0, 160),
            });
          }
        }
      }
      return { attempts, okStatus: false };
    },
    { isSignup, email, password, lid },
  );
}

async function main() {
  const mode = arg('mode', 'login');
  const email = arg('email') || process.env.BINOLLA_AUTH_EMAIL || '';
  const password = arg('password') || process.env.BINOLLA_AUTH_PASSWORD || '';
  const headless = arg('headless', 'true') !== 'false';
  const loginUrl = arg('loginUrl', 'https://binolla.com/login/');
  const signupUrl = arg('signupUrl', 'https://binolla.com/signup/?lid=15968');
  const timeoutMs = Number(arg('timeoutMs', '45000')) || 45000;
  // Leave headroom so C# WaitForExit (timeoutMs+15s) does not kill us mid-exit.
  const waitBudgetMs = Math.max(12_000, Math.min(timeoutMs - 20_000, 45_000));
  const proxyServer =
    arg('proxy') || process.env.BINOLLA_AUTH_PROXY || process.env.Binolla__CredentialLogin__ProxyServer || '';

  if (!email || !password) {
    process.stdout.write(JSON.stringify({ ok: false, error: 'email and password are required' }));
    process.exit(2);
  }

  const isSignup = mode === 'signup';
  const url = isSignup ? signupUrl : loginUrl;
  let lid = '15968';
  try {
    lid = new URL(signupUrl).searchParams.get('lid') || '15968';
  } catch {
    /* keep default */
  }

  let token = null;
  const authHits = [];


  let browser;
  try {
    const launchOpts = {
      headless,
      args: [
        '--no-sandbox',
        '--disable-setuid-sandbox',
        '--disable-blink-features=AutomationControlled',
        '--disable-dev-shm-usage',
        '--ignore-certificate-errors',
      ],
    };
    if (proxyServer) {
      launchOpts.proxy = { server: proxyServer };
    }
    browser = await chromium.launch(launchOpts);
  } catch (launchErr) {
    const raw = launchErr instanceof Error ? launchErr.message : String(launchErr);
    const missingLibs = /shared libraries|libatk|cannot open shared object/i.test(raw);
    process.stdout.write(
      JSON.stringify({
        ok: false,
        error: missingLibs
          ? 'missing OS libraries for Chromium (libatk). Run tools/binolla-auth/install-deps.sh'
          : raw,
      }),
    );
    process.exit(1);
  }

  try {
    const context = await browser.newContext({
      locale: 'en-US',
      userAgent:
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36',
      viewport: { width: 1366, height: 768 },
      extraHTTPHeaders: { 'Accept-Language': 'en-US,en;q=0.9' },
    });
    await context.addInitScript(() => {
      Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
      try {
        localStorage.setItem('NEXT_LOCALE', 'en');
        localStorage.setItem('language', 'en');
        localStorage.setItem('locale', 'en');
        localStorage.setItem('i18nextLng', 'en');
      } catch {
        /* ignore */
      }
    });
    try {
      await context.addCookies([
        {
          name: 'NEXT_LOCALE',
          value: 'en',
          domain: 'binolla.com',
          path: '/',
          secure: true,
          sameSite: 'Lax',
        },
      ]);
    } catch {
      /* ignore */
    }

    const page = await context.newPage();

    const tryCapture = (candidate, source) => {
      if (token) return;
      const normalized = normalizeToken(candidate);
      if (!normalized) return;
      token = normalized;
    };

    page.on('response', async (response) => {
      try {
        const u = response.url();
        const status = response.status();
        if (/\/api\/.*auth|\/login|\/register|\/signup/i.test(u)) {
          authHits.push({ url: u.replace(/\?.*/, ''), status });
        }
        if (token) return;
        const ct = response.headers()['content-type'] || '';
        if (!ct.includes('application/json') && !ct.includes('text/plain')) return;
        const text = await response.text();
        tryCapture(extractToken(text), `http:${status}`);
      } catch {
        /* ignore */
      }
    });

    page.on('websocket', (ws) => {
      const onFrame = (payload) => {
        if (token) return;
        tryCapture(extractToken(payload), 'ws');
      };
      ws.on('framereceived', (frame) => onFrame(frame.payload));
      ws.on('framesent', (frame) => onFrame(frame.payload));
    });

    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: timeoutMs });
    // Give Cloudflare JS challenge a moment if present.
    try {
      await page.waitForLoadState('networkidle', { timeout: 8000 });
    } catch {
      /* best effort */
    }


    // --- Preferred path: in-page auth API (CF cookies already set) ---
    const apiResult = await tryInPageAuthApi(page, { isSignup, email, password, lid });
    for (const attempt of apiResult.attempts || []) {
      if (attempt.body) tryCapture(extractToken(attempt.body), `api:${attempt.path}:${attempt.status}`);
    }

    if (!token) {
      // --- Fallback: DOM form ---
      const emailOk = await fillFirst(
        page,
        [
          'input[name="email"]',
          'input[inputmode="email"]',
          'input[type="email"]',
          'input[autocomplete="username"]',
          'input[placeholder*="mail" i]',
        ],
        email,
      );
      if (!emailOk) throw new Error('Could not find Binolla email field');

      const passOk = await fillFirst(
        page,
        [
          'input[name="password"]',
          'input[type="password"]',
          'input[autocomplete="current-password"]',
          'input[autocomplete="new-password"]',
        ],
        password,
      );
      if (!passOk) throw new Error('Could not find Binolla password field');


      if (isSignup) {
        await fillFirst(
          page,
          [
            'input[name="passwordConfirm"]',
            'input[name="confirmPassword"]',
            'input[name="password_confirmation"]',
            'input[autocomplete="new-password"]',
          ],
          password,
        );
        // Required Binolla checkboxes (from live DOM: agreement + isNotUsCitizen).
        for (const name of ['agreement', 'isNotUsCitizen']) {
          try {
            await page.locator(`input[type="checkbox"][name="${name}"]`).check({ timeout: 2000 });
          } catch {
            /* try generic below */
          }
        }
        try {
          const boxes = page.locator('input[type="checkbox"]');
          const count = await boxes.count();
          for (let i = 0; i < Math.min(count, 6); i++) {
            try {
              await boxes.nth(i).check({ timeout: 1500 });
            } catch {
              /* optional */
            }
          }
        } catch {
          /* optional */
        }
      }

      const clicked = await clickSubmit(page, isSignup);

      try {
        await page.waitForLoadState('networkidle', { timeout: Math.min(12000, waitBudgetMs) });
      } catch {
        /* best effort */
      }

      const started = Date.now();
      while (!token && Date.now() - started < waitBudgetMs) {
        await page.waitForTimeout(400);
        if (!token) tryCapture(await scanStorage(page), 'storage-poll');
      }
    }

    if (!token) {
      tryCapture(await scanStorage(page), 'storage-final');
    }

    if (!token) {
      const diag = await readPageDiagnostics(page).catch(() => null);

      const apiHint = (apiResult.attempts || [])
        .map((a) => `${a.path}:${a.status}`)
        .slice(0, 6)
        .join(', ');

      let error = isSignup
        ? 'Binolla signup did not return a session token'
        : 'Binolla login failed or token was not captured';

      const lastBody = redactAuthBody(
        [...(apiResult.attempts || [])].reverse().find((a) => a.body)?.body || '',
      );
      if (/invalid|incorrect|wrong|credentials|not found|already/i.test(lastBody)) {
        error = lastBody.slice(0, 180) || error;
      } else if (diag?.hasCfChallenge) {
        error = 'Binolla Cloudflare challenge blocked auth on the server';
      } else if (
        /not available in your current location/i.test(lastBody) ||
        /United Kingdom|\(GB\)/i.test(lastBody) ||
        (diag?.alerts || []).some((a) => /current location|United Kingdom|\(GB\)/i.test(a))
      ) {
        error =
          'Binolla blocked this server IP by location (geo-restriction). ' +
          'Set BINOLLA_AUTH_PROXY in scaralpha.env to a proxy in an allowed country, or paste SSID from Edit Profile.';
      } else if (diag?.alerts?.length) {
        error = diag.alerts[0].slice(0, 180);
      } else if (/Registration \| Binolla/i.test(diag?.title || '')) {
        error =
          'Binolla signup stayed on registration page (form/API did not create a session)';
      } else if (apiHint) {
        error = `${error} [${apiHint}]`;
      }

      process.stdout.write(JSON.stringify({ ok: false, error }));
      process.exit(1);
    }

    process.stdout.write(JSON.stringify({ ok: true, token }));
  } catch (err) {
    process.stdout.write(
      JSON.stringify({
        ok: false,
        error: err instanceof Error ? err.message : String(err),
      }),
    );
    process.exit(1);
  } finally {
    await browser.close();
  }
}

main();
