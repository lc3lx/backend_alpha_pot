/**
 * Interactive Binolla login — lets the account's owner solve the CAPTCHA themselves.
 *
 * ## Why this exists
 *
 * Binolla's `/auth/login` answers HTTP 200 with `captchaRequired: true`. There is no
 * challenge image in that response to show anyone: the widget only exists inside a real
 * browser session on the page. The one-shot capture (`capture.mjs`) closes its browser as
 * soon as it hits that flag, so by the time the user sees the error there is nothing left
 * to interact with.
 *
 * This server keeps that browser OPEN. It screenshots the live page, the frontend shows it
 * to the user, and the user's clicks and keystrokes are replayed into the real page. The
 * person solving the challenge is the account holder, on their own account — this is a
 * relay, not a solver, and nothing here tries to defeat or predict a challenge.
 *
 * ## Honest limits
 *
 * Some challenge widgets score browser-interaction signals and can refuse a session driven
 * this way, however genuine the human on the other end. When that happens the state goes
 * to `failed` with the broker's own message rather than pretending otherwise.
 *
 * ## Shape
 *
 *   POST /session/start   {email, password}      -> {sessionId, state, screenshot, ...}
 *   POST /session/event   {sessionId, type, ...} -> {state, screenshot, ...}
 *   GET  /session/state   ?sessionId=            -> {state, screenshot?, token?, cookies?}
 *   POST /session/close   {sessionId}            -> {ok:true}
 *   GET  /health
 *
 * Binds to 127.0.0.1 only. It holds live credentials and browser sessions and must never
 * be reachable from outside the host.
 */

import { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';
import { chromium } from 'playwright';

import {
  buildCookieHeader,
  extractToken,
  extractWsAuthorizationToken,
  normalizeToken,
  parseProxy,
  scanStorage,
} from './authHelpers.mjs';

const HOST = process.env.BINOLLA_INTERACTIVE_HOST || '127.0.0.1';
const PORT = Number(process.env.BINOLLA_INTERACTIVE_PORT || 8110);
const TOKEN = process.env.BINOLLA_INTERACTIVE_TOKEN || '';
const LOGIN_URL = process.env.BINOLLA_LOGIN_URL || 'https://binolla.com/login/';
const TRADING_URL = process.env.BINOLLA_TRADING_URL || 'https://binolla.com/trading';

/** The page the user is shown. Wide enough for the login form, small enough to relay. */
const VIEWPORT = { width: 900, height: 760 };

/**
 * A session is abandoned if the user walks away. Each one holds a Chromium instance, so
 * leaking them would exhaust the box within a day.
 */
const SESSION_TTL_MS = 10 * 60_000;
const SWEEP_MS = 30_000;

/** sessionId -> session */
const sessions = new Map();

function log(message, data) {
  // Credentials and tokens never reach here; only ids and states.
  process.stdout.write(`${new Date().toISOString()} ${message} ${data ? JSON.stringify(data) : ''}\n`);
}

async function shot(session) {
  try {
    const buffer = await session.page.screenshot({ type: 'jpeg', quality: 70 });
    session.screenshot = `data:image/jpeg;base64,${buffer.toString('base64')}`;
  } catch {
    // A screenshot can fail mid-navigation. Keeping the previous frame is better than
    // blanking the user's screen while they are part-way through a challenge.
  }
  return session.screenshot;
}

/**
 * Public view of a session. Deliberately never includes the credentials it was started
 * with, and only includes the captured token once the login has actually succeeded.
 */
function view(session, { withFrame = true } = {}) {
  return {
    sessionId: session.id,
    state: session.state,
    message: session.message ?? null,
    viewport: VIEWPORT,
    screenshot: withFrame ? session.screenshot ?? null : null,
    token: session.state === 'success' ? session.token : null,
    cookies: session.state === 'success' ? session.cookies ?? null : null,
  };
}

function finish(session, state, message) {
  session.state = state;
  session.message = message ?? null;
  log('session_state', { id: session.id.slice(0, 8), state });
}

/**
 * Watches the live page for the session token.
 *
 * The token can surface in three places and whichever arrives first wins: the Socket.IO
 * authorization frame, an auth API response body, or storage after the redirect. The
 * one-shot capture learned that the hard way, and the same three are watched here.
 */
function watchForToken(session) {
  const { page } = session;

  page.on('websocket', (ws) => {
    const inspect = (payload) => {
      if (session.token) return;
      const token = extractWsAuthorizationToken(String(payload ?? ''));
      if (token) {
        session.token = token;
        session.tokenSource = 'ws-auth';
      }
    };
    ws.on('framesent', (frame) => inspect(frame.payload));
    ws.on('framereceived', (frame) => inspect(frame.payload));
  });

  page.on('response', async (response) => {
    if (session.token) return;
    const url = response.url();
    if (!/\/api\/auth\/|\/auth\/login|\/auth\/session/i.test(url)) return;
    try {
      const body = await response.text();
      const token = extractToken(body);
      if (token) {
        session.token = token;
        session.tokenSource = 'api';
      }
    } catch {
      /* body already consumed or binary */
    }
  });
}

/** Has the login completed? Checked after every relayed event. */
async function pollForSuccess(session) {
  if (session.state !== 'awaiting-user') return;

  if (!session.token) {
    try {
      const stored = await scanStorage(session.page);
      const token = normalizeToken(stored);
      if (token) {
        session.token = token;
        session.tokenSource = 'storage';
      }
    } catch {
      /* page navigating */
    }
  }

  if (!session.token) return;

  // A token from storage or the API is not yet proof of a live trading session: the
  // Socket.IO token is the one the bot needs. Visiting the trading page is what makes
  // the site emit it, exactly as the one-shot capture does.
  if (session.tokenSource !== 'ws-auth' && !session.visitedTrading) {
    session.visitedTrading = true;
    try {
      await session.page.goto(TRADING_URL, { waitUntil: 'domcontentloaded', timeout: 25_000 });
      await session.page.waitForTimeout(6_000);
    } catch {
      /* keep whatever token we have */
    }
  }

  try {
    session.cookies = await buildCookieHeader(session.context);
  } catch {
    /* optional */
  }

  finish(session, 'success', null);
  // Screenshot once more so the UI can show the logged-in page as confirmation.
  await shot(session);
}

async function startSession({ email, password, proxy }) {
  const proxyConfig = parseProxy(proxy || process.env.BINOLLA_AUTH_PROXY || '');

  const launchOpts = {
    // Headful is impossible on a headless VPS; the relay is what the user sees instead.
    headless: true,
    args: [
      '--no-sandbox',
      '--disable-setuid-sandbox',
      '--disable-blink-features=AutomationControlled',
      '--disable-dev-shm-usage',
      '--ignore-certificate-errors',
    ],
  };
  if (proxyConfig) launchOpts.proxy = proxyConfig;

  const browser = await chromium.launch(launchOpts);
  const context = await browser.newContext({ viewport: VIEWPORT });
  const page = await context.newPage();

  const session = {
    id: randomUUID(),
    browser,
    context,
    page,
    state: 'starting',
    message: null,
    token: null,
    tokenSource: null,
    cookies: null,
    screenshot: null,
    visitedTrading: false,
    touchedAt: Date.now(),
  };
  sessions.set(session.id, session);
  watchForToken(session);

  try {
    await page.goto(LOGIN_URL, { waitUntil: 'domcontentloaded', timeout: 45_000 });

    // Pre-fill so the user only has to deal with the challenge, not retype credentials
    // they already gave us.
    await fillIfPresent(page, ['input[type="email"]', 'input[name="email"]', '#email'], email);
    await fillIfPresent(
      page,
      ['input[type="password"]', 'input[name="password"]', '#password'],
      password,
    );

    finish(session, 'awaiting-user', null);
    await shot(session);
  } catch (err) {
    finish(session, 'failed', err instanceof Error ? err.message : String(err));
  }

  return session;
}

async function fillIfPresent(page, selectors, value) {
  if (!value) return false;
  for (const selector of selectors) {
    try {
      const el = page.locator(selector).first();
      if (await el.count()) {
        await el.fill(value, { timeout: 4_000 });
        return true;
      }
    } catch {
      /* try the next selector */
    }
  }
  return false;
}

async function applyEvent(session, event) {
  const { page } = session;
  session.touchedAt = Date.now();

  switch (event.type) {
    case 'click': {
      const x = clamp(Number(event.x), 0, VIEWPORT.width);
      const y = clamp(Number(event.y), 0, VIEWPORT.height);
      await page.mouse.click(x, y, { delay: 40 });
      break;
    }
    case 'move': {
      await page.mouse.move(
        clamp(Number(event.x), 0, VIEWPORT.width),
        clamp(Number(event.y), 0, VIEWPORT.height),
      );
      break;
    }
    case 'type': {
      const text = String(event.text ?? '').slice(0, 200);
      if (text) await page.keyboard.type(text, { delay: 25 });
      break;
    }
    case 'key': {
      const key = String(event.key ?? '').slice(0, 24);
      if (key) await page.keyboard.press(key);
      break;
    }
    case 'scroll': {
      await page.mouse.wheel(0, clamp(Number(event.deltaY) || 0, -1200, 1200));
      break;
    }
    case 'refresh':
      // No page action; the caller just wants the newest frame.
      break;
    default:
      throw new Error(`Unknown event type '${event.type}'`);
  }

  // The page reacts asynchronously — a challenge often takes a moment to accept a click.
  await page.waitForTimeout(350);
  await pollForSuccess(session);
  await shot(session);
}

function clamp(value, min, max) {
  if (!Number.isFinite(value)) return min;
  return Math.min(max, Math.max(min, value));
}

async function closeSession(session) {
  sessions.delete(session.id);
  try {
    await session.browser.close();
  } catch {
    /* already gone */
  }
}

setInterval(() => {
  const now = Date.now();
  for (const session of [...sessions.values()]) {
    if (now - session.touchedAt > SESSION_TTL_MS) {
      log('session_expired', { id: session.id.slice(0, 8) });
      void closeSession(session);
    }
  }
}, SWEEP_MS).unref();

// ---- HTTP ------------------------------------------------------------------

function send(res, status, body) {
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    'Content-Type': 'application/json',
    'Content-Length': Buffer.byteLength(payload),
  });
  res.end(payload);
}

async function readJson(req) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    // A screenshot never travels inbound; anything this large is a mistake or an attack.
    if (size > 64 * 1024) throw new Error('Request body too large');
    chunks.push(chunk);
  }
  if (!chunks.length) return {};
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

function authorized(req) {
  if (!TOKEN) return true;
  return req.headers['x-auth-token'] === TOKEN;
}

function requireSession(body, res) {
  const session = sessions.get(String(body.sessionId ?? ''));
  if (!session) {
    send(res, 404, { error: 'No such login session. It may have expired — start again.' });
    return null;
  }
  session.touchedAt = Date.now();
  return session;
}

const server = createServer(async (req, res) => {
  const url = new URL(req.url ?? '/', `http://${HOST}:${PORT}`);

  try {
    if (url.pathname === '/health') {
      return send(res, 200, { ok: true, sessions: sessions.size });
    }

    if (!authorized(req)) return send(res, 401, { error: 'Bad token.' });

    if (req.method === 'POST' && url.pathname === '/session/start') {
      const body = await readJson(req);
      if (!body.email || !body.password) {
        return send(res, 400, { error: 'email and password are required' });
      }
      const session = await startSession(body);
      return send(res, 200, view(session));
    }

    if (req.method === 'POST' && url.pathname === '/session/event') {
      const body = await readJson(req);
      const session = requireSession(body, res);
      if (!session) return undefined;
      if (session.state !== 'awaiting-user') return send(res, 200, view(session));

      try {
        await applyEvent(session, body);
      } catch (err) {
        finish(session, 'failed', err instanceof Error ? err.message : String(err));
      }
      return send(res, 200, view(session));
    }

    if (req.method === 'GET' && url.pathname === '/session/state') {
      const session = sessions.get(url.searchParams.get('sessionId') ?? '');
      if (!session) return send(res, 404, { error: 'No such login session.' });
      session.touchedAt = Date.now();
      // Poll without an event: the page can finish on its own after a challenge passes.
      if (session.state === 'awaiting-user') {
        await pollForSuccess(session);
        await shot(session);
      }
      return send(res, 200, view(session));
    }

    if (req.method === 'POST' && url.pathname === '/session/close') {
      const body = await readJson(req);
      const session = sessions.get(String(body.sessionId ?? ''));
      if (session) await closeSession(session);
      return send(res, 200, { ok: true });
    }

    return send(res, 404, { error: 'Not found' });
  } catch (err) {
    return send(res, 500, { error: err instanceof Error ? err.message : String(err) });
  }
});

server.listen(PORT, HOST, () => {
  log('listening', { host: HOST, port: PORT });
});
