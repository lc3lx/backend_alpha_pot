/**
 * Helpers shared by the Binolla auth tools.
 *
 * Both the one-shot capture (`capture.mjs`) and the interactive CAPTCHA session
 * (`interactive-server.mjs`) have to recognise the same session token in the same places:
 * the Socket.IO authorization frame, the auth API body, localStorage, and cookies. Two
 * copies of that logic would drift, and the drift shows up as "login works one way but
 * not the other" — which is exactly the class of bug that is expensive to find here.
 *
 * These are pure functions plus two thin Playwright readers. Nothing about a particular
 * login flow lives in this file.
 */


export function normalizeToken(raw) {
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

/** Build Cookie header for Binolla domains — never printed to logs. */
export async function buildCookieHeader(context) {
  try {
    const cookies = await context.cookies();
    const relevant = cookies.filter((c) => {
      const d = (c.domain || '').replace(/^\./, '');
      return d === 'binolla.com' || d.endsWith('.binolla.com');
    });
    if (!relevant.length) return undefined;
    return relevant.map((c) => `${c.name}=${c.value}`).join('; ');
  } catch {
    return undefined;
  }
}

export function extractWsAuthorizationToken(payload) {
  if (!payload || typeof payload !== 'string') return null;
  // Outbound/inbound Socket.IO: 42["authorization",{"isDemo":true,"token":"..."}]
  const idx = payload.indexOf('[');
  if (idx < 0) return null;
  try {
    const arr = JSON.parse(payload.slice(idx));
    if (!Array.isArray(arr) || arr.length < 2) return null;
    const event = String(arr[0] ?? '');
    const data = arr[1];
    if (!/authorization|authorize|^auth$/i.test(event)) return null;
    if (!data || typeof data !== 'object') return null;
    return normalizeToken(typeof data.token === 'string' ? data.token : null);
  } catch {
    return null;
  }
}

export function extractToken(text) {
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

export async function scanStorage(page) {
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

export function parseProxy(raw) {
  const value = String(raw || '').trim();
  if (!value) return null;

  if (/^[a-z0-9+.-]+:\/\//i.test(value)) {
    try {
      const u = new URL(value);
      const server = `${u.protocol}//${u.host}`;
      const username = decodeURIComponent(u.username || '');
      const password = decodeURIComponent(u.password || '');
      return username ? { server, username, password } : { server };
    } catch {
      return { server: value };
    }
  }

  // host:port:user:pass — take host/port from the left, then keep the rest as the
  // password so one containing ':' still survives.
  const parts = value.split(':');
  if (parts.length >= 4) {
    const [host, port, username, ...passwordParts] = parts;
    return {
      server: `http://${host}:${port}`,
      username,
      password: passwordParts.join(':'),
    };
  }

  if (parts.length === 2) return { server: `http://${value}` };
  return { server: value };
}
