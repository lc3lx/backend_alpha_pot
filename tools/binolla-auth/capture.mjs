#!/usr/bin/env node
/**
 * Port of https://github.com/A11ksa/API-Binolla/blob/main/login.py
 * Usage:
 *   node capture.mjs --mode login --email a@b.com --password secret
 *   node capture.mjs --mode signup --email a@b.com --password secret
 * Prints JSON: { "ok": true, "token": "..." } or { "ok": false, "error": "..." }
 */
import { chromium } from 'playwright';

function arg(name, fallback = '') {
  const idx = process.argv.indexOf(`--${name}`);
  if (idx === -1) return fallback;
  return process.argv[idx + 1] ?? fallback;
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
        if (/auth/i.test(event) && typeof data.token === 'string' && data.token.length >= 16) {
          return data.token;
        }
        for (const [k, v] of Object.entries(data)) {
          if (/token/i.test(k) && typeof v === 'string' && v.length >= 16) return v;
        }
      }
    } catch {
      /* ignore */
    }
  }

  try {
    const obj = JSON.parse(text);
    if (obj && typeof obj === 'object') {
      if (typeof obj.token === 'string' && obj.token.length >= 16) return obj.token;
      for (const key of ['message', 'data', 'payload']) {
        const nested = obj[key];
        if (nested && typeof nested.token === 'string' && nested.token.length >= 16) {
          return nested.token;
        }
      }
    }
  } catch {
    /* ignore */
  }

  const m = text.match(/"token"\s*:\s*"([A-Za-z0-9._-]{16,})"/);
  return m?.[1] ?? null;
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
      ]
    : [
        'button[type="submit"]',
        'button:has-text("Sign In")',
        'button:has-text("Log In")',
      ];

  for (const sel of selectors) {
    try {
      const btn = page.locator(sel).first();
      if ((await btn.count()) > 0) {
        await btn.click({ timeout: 4000 });
        return;
      }
    } catch {
      /* next */
    }
  }
  await page.keyboard.press('Enter');
}

async function scanStorage(page) {
  return page.evaluate(() => {
    try {
      for (let i = 0; i < localStorage.length; i++) {
        const k = localStorage.key(i);
        const v = localStorage.getItem(k) || '';
        if (/token/i.test(k || '') && v.length >= 16) return v;
        const m = /"token"\s*:\s*"([A-Za-z0-9._-]{16,})"/.exec(v);
        if (m) return m[1];
      }
      for (const part of (document.cookie || '').split(';')) {
        const [ck, cv] = part.split('=');
        if (/token/i.test(ck || '') && (cv || '').length >= 16) return cv;
      }
    } catch {
      /* ignore */
    }
    return null;
  });
}

async function main() {
  const mode = arg('mode', 'login');
  const email = arg('email') || process.env.BINOLLA_AUTH_EMAIL || '';
  const password = arg('password') || process.env.BINOLLA_AUTH_PASSWORD || '';
  const headless = arg('headless', 'true') !== 'false';
  const loginUrl = arg('loginUrl', 'https://binolla.com/login/');
  const signupUrl = arg('signupUrl', 'https://binolla.com/signup/?lid=15968');
  const timeoutMs = Number(arg('timeoutMs', '45000')) || 45000;

  if (!email || !password) {
    process.stdout.write(JSON.stringify({ ok: false, error: 'email and password are required' }));
    process.exit(2);
  }

  const isSignup = mode === 'signup';
  const url = isSignup ? signupUrl : loginUrl;
  let token = null;

  const browser = await chromium.launch({
    headless,
    args: ['--disable-blink-features=AutomationControlled', '--disable-dev-shm-usage'],
  });

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

    const tryCapture = (candidate) => {
      if (!token && candidate && candidate.length >= 16) token = candidate;
    };

    page.on('response', async (response) => {
      try {
        if (token) return;
        const ct = response.headers()['content-type'] || '';
        if (!ct.includes('application/json')) return;
        const text = await response.text();
        tryCapture(extractToken(text));
      } catch {
        /* ignore */
      }
    });

    page.on('websocket', (ws) => {
      const onFrame = (payload) => {
        if (token) return;
        tryCapture(extractToken(payload));
      };
      ws.on('framereceived', (frame) => onFrame(frame.payload));
      ws.on('framesent', (frame) => onFrame(frame.payload));
    });

    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: timeoutMs });

    const emailOk = await fillFirst(page, [
      'input[name="email"]',
      'input[inputmode="email"]',
      'input[type="email"]',
      'input[autocomplete="username"]',
      'input[placeholder*="mail" i]',
    ], email);
    if (!emailOk) throw new Error('Could not find Binolla email field');

    const passOk = await fillFirst(page, [
      'input[name="password"]',
      'input[type="password"]',
      'input[autocomplete="current-password"]',
      'input[autocomplete="new-password"]',
    ], password);
    if (!passOk) throw new Error('Could not find Binolla password field');

    if (isSignup) {
      await fillFirst(
        page,
        ['input[name="passwordConfirm"]', 'input[name="confirmPassword"]'],
        password,
      );
      try {
        const box = page.locator('input[type="checkbox"]').first();
        if ((await box.count()) > 0) await box.check({ timeout: 2000 });
      } catch {
        /* optional */
      }
    }

    await clickSubmit(page, isSignup);
    try {
      await page.waitForLoadState('networkidle', { timeout: Math.min(15000, timeoutMs) });
    } catch {
      /* best effort */
    }

    const started = Date.now();
    while (!token && Date.now() - started < timeoutMs) {
      await page.waitForTimeout(500);
    }

    if (!token) {
      tryCapture(await scanStorage(page));
    }

    if (!token) {
      process.stdout.write(
        JSON.stringify({
          ok: false,
          error: isSignup
            ? 'Binolla signup did not return a session token'
            : 'Binolla login failed or token was not captured',
        }),
      );
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
