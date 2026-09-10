#!/usr/bin/env node
/**
 * Port of https://github.com/A11ksa/API-Binolla/blob/main/login.py
 * Usage:
 *   node capture.mjs --mode login --email a@b.com --password secret
 *   node capture.mjs --mode signup --email a@b.com --password secret
 * Prints JSON: { "ok": true, "token": "...", "tokenSource": "ws-auth"|"api"|..., "cookies"? }
 *   or { "ok": false, "error": "..." }
 *
 * Strategy:
 * 1) Open Binolla page in Playwright (passes Cloudflare cookie jar)
 * 2) Prefer in-page fetch to /api/auth/login|register (same-origin + CF cookies)
 * 3) After API login, open trading page and capture Socket.IO authorization WS token
 * 4) Fall back to DOM form fill + network/WS/storage token capture
 *
 * IMPORTANT: API/storage session tokens are NOT the live trading Socket.IO token.
 * Prefer wsToken from 42["authorization",{"token":"..."}] over apiToken.
 */
import { chromium } from 'playwright';

// Token recognition and proxy parsing are shared with interactive-server.mjs so both
// login paths accept exactly the same sessions.
import {
  brokerPreset,
  buildCookieHeader,
  extractToken,
  extractWsAuthorizationToken,
  normalizeToken,
  parseProxy,
  scanStorage,
} from './authHelpers.mjs';

function arg(name, fallback = '') {
  const idx = process.argv.indexOf(`--${name}`);
  if (idx === -1) return fallback;
  return process.argv[idx + 1] ?? fallback;
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

/**
 * A one-line description of the page's own form fields.
 *
 * Attached to a "could not find the field" error. Without it that message says only that
 * a selector list did not match, which is the one thing already known — and the page it
 * failed on is gone by the time anyone reads the log.
 */
async function describeInputs(page) {
  try {
    const found = await page.evaluate(() =>
      Array.from(document.querySelectorAll('input')).slice(0, 25).map((el) => {
        const box = el.getBoundingClientRect();
        return [
          el.getAttribute('type') || 'text',
          el.getAttribute('name') || el.getAttribute('id') || '?',
          // Visibility is usually the reason a correct selector still misses.
          box.width > 0 && box.height > 0 ? 'shown' : 'hidden',
        ].join(':');
      }),
    );
    const where = (() => {
      try {
        return new URL(page.url()).pathname;
      } catch {
        return '?';
      }
    })();
    return `at ${where}, inputs: ${found.join(', ') || 'none'}`;
  } catch {
    return 'page could not be inspected';
  }
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
 * Call the broker's JSON auth endpoint from inside the page so Cloudflare cookies apply.
 *
 * `paths` comes from the broker preset. An empty list means this broker has no such
 * endpoint (Quotex signs in through a CSRF-bearing form), and the capture falls through
 * to the DOM path without wasting requests on guesses.
 */
async function tryInPageAuthApi(page, { isSignup, email, password, lid, paths }) {
  if (!Array.isArray(paths) || paths.length === 0) {
    return { attempts: [], okStatus: false, skipped: true };
  }

  return page.evaluate(
    async ({ isSignup, email, password, lid, paths }) => {
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
    { isSignup, email, password, lid, paths },
  );
}

/**
 * Accepts either shape and returns Playwright's { server, username, password }:
 *
 *   http://user:pass@host:port      (URL form)
 *   host:port:user:pass             (the colon-separated form proxy vendors hand out)
 *
 * Playwright ignores credentials embedded in the server URL, so they must be split out
 * here. Getting that wrong makes the proxy reject every request, which looks identical to
 * the block being worked around.
 *
 * Returns null when nothing is configured, so the caller launches without a proxy.
 */

async function main() {
  const mode = arg('mode', 'login');
  const email = arg('email') || process.env.BINOLLA_AUTH_EMAIL || '';
  const password = arg('password') || process.env.BINOLLA_AUTH_PASSWORD || '';
  const headless = arg('headless', 'true') !== 'false';
  // Which venue to capture for. The URLs come from one table in authHelpers so the two
  // brokers cannot drift into different ideas of where a login page is; an explicit
  // --loginUrl still wins for a one-off.
  const broker = arg('broker', 'binolla');
  const preset = brokerPreset(broker);
  const loginUrl = arg('loginUrl', preset.loginUrl);
  const signupUrl = arg('signupUrl', preset.signupUrl);
  const tradingUrl = arg('tradingUrl', preset.tradingUrl);
  const timeoutMs = Number(arg('timeoutMs', '45000')) || 45000;
  // Leave headroom so C# WaitForExit (timeoutMs+15s) does not kill us mid-exit.
  //
  // A broker with a JSON login answers in well under a second, so five seconds is plenty.
  // One without it — Quotex — has to submit the form, follow the redirect and let the
  // trading app boot before anything provable happens, and cutting that short reads as a
  // failed login when it was only a slow one.
  const submitBudgetCap = (preset.loginApiPaths || []).length > 0 ? 5_000 : 12_000;
  const waitBudgetMs = Math.max(3_000, Math.min(timeoutMs - 20_000, submitBudgetCap));
  // Wait for Socket.IO authorization frame after navigating to trading (API token ≠ WS token).
  const wsAuthWaitMs = Math.max(8_000, Math.min(Number(arg('wsAuthWaitMs', '20000')) || 20_000, 25_000));
  const captureStarted = Date.now();
  const agentLog = (hypothesisId, location, message, data) => {
    fetch('http://127.0.0.1:7892/ingest/aea6d51e-f3e9-4c7e-b6b4-db55c4306e97', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Debug-Session-Id': '660ec2' },
      body: JSON.stringify({
        sessionId: '660ec2',
        runId: 'login-speed',
        hypothesisId,
        location,
        message,
        data,
        timestamp: Date.now(),
      }),
    }).catch(() => {});
  };
  const proxyRaw =
    arg('proxy') || process.env.BINOLLA_AUTH_PROXY || process.env.Binolla__CredentialLogin__ProxyServer || '';
  const proxyConfig = parseProxy(proxyRaw);

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

  // Separate API/session token from live Socket.IO trading token (H1 fix).
  let apiToken = null;
  let apiTokenSource = null;
  let wsToken = null;
  let wsTokenSource = null;
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
    if (proxyConfig) {
      // Playwright needs the credentials as SEPARATE fields. Passing them inside the
      // server URL (http://user:pass@host:port) is silently ignored and the proxy then
      // rejects every request — which looks exactly like the block we are working around.
      launchOpts.proxy = proxyConfig;
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
          domain: preset.cookieHosts[0],
          path: '/',
          secure: true,
          sameSite: 'Lax',
        },
      ]);
    } catch {
      /* ignore */
    }

    const page = await context.newPage();

    const tryCaptureApi = (candidate, source) => {
      if (apiToken) return;
      const normalized = normalizeToken(candidate);
      if (!normalized) return;
      apiToken = normalized;
      apiTokenSource = source;
    };

    const tryCaptureWs = (candidate, source) => {
      if (wsToken) return;
      const normalized = normalizeToken(candidate);
      if (!normalized) return;
      wsToken = normalized;
      wsTokenSource = source;
    };

    page.on('response', async (response) => {
      try {
        const u = response.url();
        const status = response.status();
        if (/\/api\/.*auth|\/login|\/register|\/signup/i.test(u)) {
          authHits.push({ url: u.replace(/\?.*/, ''), status });
        }
        // Do not stop HTTP capture once API token exists — still harmless;
        // WS path must keep listening regardless (see websocket handler).
        if (apiToken) return;
        const ct = response.headers()['content-type'] || '';
        if (!ct.includes('application/json') && !ct.includes('text/plain')) return;
        const text = await response.text();
        tryCaptureApi(extractToken(text), `http:${status}`);
      } catch {
        /* ignore */
      }
    });

    page.on('websocket', (ws) => {
      const onFrame = (payload) => {
        // H1 fix: NEVER skip WS auth frames when apiToken is already set.
        const fromAuth = extractWsAuthorizationToken(
          typeof payload === 'string' ? payload : String(payload ?? ''),
        );
        if (fromAuth) {
          tryCaptureWs(fromAuth, 'ws-auth');
          return;
        }
        // Secondary: other WS payloads may still yield a session token before trading opens.
        if (!apiToken) {
          tryCaptureApi(extractToken(typeof payload === 'string' ? payload : ''), 'ws');
        }
      };
      ws.on('framereceived', (frame) => onFrame(frame.payload));
      ws.on('framesent', (frame) => onFrame(frame.payload));
    });

    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: timeoutMs });
    // #region agent log
    agentLog('H103', 'capture.mjs:goto', 'page_loaded', { elapsedMs: Date.now() - captureStarted, isSignup });
    // #endregion

    // --- Preferred path: in-page auth API (CF cookies already set) ---
    const apiResult = await tryInPageAuthApi(page, {
      isSignup,
      email,
      password,
      lid,
      paths: isSignup ? preset.signupApiPaths : preset.loginApiPaths,
    });
    for (const attempt of apiResult.attempts || []) {
      if (attempt.body) tryCaptureApi(extractToken(attempt.body), `api:${attempt.path}:${attempt.status}`);
    }

    // API may set cookies/storage without putting token in the JSON body.
    if (!apiToken && apiResult.okStatus) {
      const started = Date.now();
      while (!apiToken && Date.now() - started < 800) {
        await page.waitForTimeout(100);
        tryCaptureApi(await scanStorage(page), 'storage-after-api');
      }
    }
    // #region agent log
    agentLog('H103', 'capture.mjs:api', 'after_api', {
      elapsedMs: Date.now() - captureStarted,
      hasApiToken: Boolean(apiToken),
      apiTokenLen: apiToken ? apiToken.length : 0,
      hasWsToken: Boolean(wsToken),
      wsTokenLen: wsToken ? wsToken.length : 0,
      apiOk: Boolean(apiResult.okStatus),
      attempts: (apiResult.attempts || []).map((a) => `${a.path}:${a.status}`).slice(0, 6),
    });
    // #endregion

    if (!apiToken && !wsToken) {
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
      if (!emailOk) {
        throw new Error(
          `Could not find the ${preset.label} email field (${await describeInputs(page)})`,
        );
      }

      const passOk = await fillFirst(
        page,
        [
          'input[name="password"]',
          'input[type="password"]',
          'input[autocomplete="current-password"]',
          'input[autocomplete="new-password"]',
          // Quotex renders its own control rather than a plainly named input, and the
          // field can appear a moment after the email one is filled.
          'input[id*="pass" i]',
          'input[placeholder*="pass" i]',
          '[data-testid*="password" i] input',
        ],
        password,
      );
      if (!passOk) {
        throw new Error(
          `Could not find the ${preset.label} password field (${await describeInputs(page)})`,
        );
      }

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
      void clicked;

      const started = Date.now();
      while (!apiToken && !wsToken && Date.now() - started < waitBudgetMs) {
        await page.waitForTimeout(150);
        if (!apiToken) tryCaptureApi(await scanStorage(page), 'storage-poll');
      }
    }

    if (!apiToken && !wsToken) {
      tryCaptureApi(await scanStorage(page), 'storage-final');
    }

    // Whether the sign-in went through. An API 200 or a stored token proves it outright;
    // for a form login that returns neither — Quotex — the only evidence available before
    // the socket speaks is that the browser is no longer sitting on the sign-in page.
    const leftLoginPage = (() => {
      try {
        return new URL(page.url()).pathname !== new URL(url).pathname;
      } catch {
        return false;
      }
    })();

    // Open trading so the live Socket.IO client sends its authorization frame. For Quotex
    // that frame IS the session; skipping this step is why a successful login still came
    // back empty-handed.
    if ((apiResult.okStatus || apiToken || leftLoginPage) && !wsToken) {
      const tradingCandidates = [tradingUrl, ...(preset.tradingUrls || [])];
      let navigated = false;
      for (const tUrl of tradingCandidates) {
        try {
          await page.goto(tUrl, { waitUntil: 'domcontentloaded', timeout: Math.min(timeoutMs, 25_000) });
          navigated = true;
          // #region agent log
          agentLog('H1', 'capture.mjs:trading', 'navigated_trading', {
            elapsedMs: Date.now() - captureStarted,
            urlHost: (() => {
              try {
                return new URL(page.url()).pathname;
              } catch {
                return 'unknown';
              }
            })(),
          });
          // #endregion
          break;
        } catch {
          /* try next */
        }
      }

      if (navigated || apiToken || leftLoginPage) {
        const wsStarted = Date.now();
        while (!wsToken && Date.now() - wsStarted < wsAuthWaitMs) {
          await page.waitForTimeout(200);
          if (!apiToken) tryCaptureApi(await scanStorage(page), 'storage-during-ws-wait');
        }
        // #region agent log
        agentLog('H1', 'capture.mjs:trading', 'ws_auth_wait_done', {
          elapsedMs: Date.now() - captureStarted,
          waitedMs: Date.now() - wsStarted,
          hasWsToken: Boolean(wsToken),
          wsTokenLen: wsToken ? wsToken.length : 0,
          hasApiToken: Boolean(apiToken),
          apiTokenLen: apiToken ? apiToken.length : 0,
          tokensDiffer: Boolean(wsToken && apiToken && wsToken !== apiToken),
        });
        // #endregion
      }
    }

    const token = wsToken || apiToken;
    const tokenSource = wsToken
      ? wsTokenSource || 'ws-auth'
      : apiToken
        ? apiTokenSource || 'api'
        : null;

    if (!token) {
      const diag = await readPageDiagnostics(page).catch(() => null);

      const apiHint = (apiResult.attempts || [])
        .map((a) => `${a.path}:${a.status}`)
        .slice(0, 6)
        .join(', ');

      let error = isSignup
        ? `${preset.label} signup did not return a session token`
        : `${preset.label} login failed or the session token was not captured`;

      const lastBody = redactAuthBody(
        [...(apiResult.attempts || [])].reverse().find((a) => a.body)?.body || '',
      );
      if (/invalid|incorrect|wrong|credentials|not found|already/i.test(lastBody)) {
        error = lastBody.slice(0, 180) || error;
      } else if (diag?.hasCfChallenge) {
        error = `${preset.label} showed a Cloudflare challenge instead of the sign-in form`;
      } else if (
        /not available in your current location/i.test(lastBody) ||
        /United Kingdom|\(GB\)/i.test(lastBody) ||
        (diag?.alerts || []).some((a) => /current location|United Kingdom|\(GB\)/i.test(a))
      ) {
        error =
          `${preset.label} blocked this server IP by location (geo-restriction). ` +
          'Set BINOLLA_AUTH_PROXY in scaralpha.env to a proxy in an allowed country, or paste the SSID from the broker profile page.';
      } else if (diag?.alerts?.length) {
        error = diag.alerts[0].slice(0, 180);
      } else if (/registration|sign ?up/i.test(diag?.title || '')) {
        error =
          `${preset.label} stayed on the registration page (the form did not create a session)`;
      } else if ((apiResult.attempts || []).some((a) => a.status === 403)) {
        // 403 on an auth endpoint is an edge/WAF refusal, not a credential problem —
        // wrong credentials come back as 401 with a message. Saying so plainly stops the
        // hours otherwise spent re-checking a password that was never the issue.
        const snippet = lastBody ? ` Server said: ${lastBody.slice(0, 120)}` : '';
        error =
          `${preset.label} refused the login from this server (HTTP 403 — blocked before ` +
          'the password was checked). This is an IP/location or bot-protection block, not ' +
          'a wrong password. Set BINOLLA_AUTH_PROXY in scaralpha.env to a proxy in an ' +
          'allowed country, or paste the SSID from the broker profile page.' +
          snippet +
          (apiHint ? ` [${apiHint}]` : '');
      } else if (apiHint) {
        // Always carry whatever the server actually returned; a bare status code gave the
        // operator nothing to act on.
        const snippet = lastBody ? ` Server said: ${lastBody.slice(0, 120)}` : '';
        error = `${error} [${apiHint}]${snippet}`;
      }

      process.stdout.write(JSON.stringify({ ok: false, error }));
      process.exit(1);
    }

    const cookies = await buildCookieHeader(context, preset.cookieHosts);
    // #region agent log
    agentLog('H103', 'capture.mjs:done', 'token_captured', {
      elapsedMs: Date.now() - captureStarted,
      hasCookies: Boolean(cookies),
      cookieLen: cookies ? cookies.length : 0,
      tokenSource,
      tokenLen: token.length,
      apiTokenLen: apiToken ? apiToken.length : 0,
      wsTokenLen: wsToken ? wsToken.length : 0,
      usedWs: Boolean(wsToken),
    });
    // #endregion
    process.stdout.write(
      JSON.stringify({
        ok: true,
        broker,
        token,
        tokenSource,
        cookies,
      }),
    );
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
