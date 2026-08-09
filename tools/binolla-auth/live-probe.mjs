#!/usr/bin/env node
/**
 * Honest live probe for Phase 9 Step 3.
 * Does NOT invent success. Prints JSON with geo/auth reachability facts.
 *
 * Usage:
 *   node live-probe.mjs
 *   node live-probe.mjs --email a@b.com --password secret --mode login
 */
import { chromium } from 'playwright';
import https from 'https';

function arg(name, fallback = '') {
  const idx = process.argv.indexOf(`--${name}`);
  if (idx === -1) return fallback;
  return process.argv[idx + 1] ?? fallback;
}

function postJson(url, body) {
  return new Promise((resolve) => {
    const u = new URL(url);
    const data = JSON.stringify(body);
    const req = https.request(
      {
        hostname: u.hostname,
        path: u.pathname + u.search,
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Content-Length': Buffer.byteLength(data),
          'User-Agent': 'Mozilla/5.0 (compatible; ScarAlphaPhase9Probe/1.0)',
          Accept: 'application/json',
        },
        timeout: 20000,
      },
      (res) => {
        let d = '';
        res.on('data', (c) => (d += c));
        res.on('end', () =>
          resolve({
            status: res.statusCode,
            body: d.slice(0, 800),
          }),
        );
      },
    );
    req.on('error', (e) => resolve({ error: String(e) }));
    req.on('timeout', () => {
      req.destroy();
      resolve({ error: 'timeout' });
    });
    req.write(data);
    req.end();
  });
}

function detectGeoBlock(text) {
  if (!text) return false;
  return /netherlands|\bNL\b|not available in your country|geo|region|restricted|vpn/i.test(text);
}

async function main() {
  const email = arg('email');
  const password = arg('password');
  const mode = arg('mode', 'login');
  const result = {
    ok: false,
    verified: false,
    timestampUtc: new Date().toISOString(),
    checks: {},
  };

  // 1) Direct API probe (same class of geo failure as prior NL block).
  const apiProbe = await postJson('https://binolla.com/api/auth/login', {
    email: email || 'phase9-probe@example.com',
    password: password || 'invalid-probe-password',
  });
  result.checks.apiLogin = {
    status: apiProbe.status ?? null,
    error: apiProbe.error ?? null,
    geoBlocked: detectGeoBlock(apiProbe.body || apiProbe.error || ''),
    bodySnippet: (apiProbe.body || '').replace(/\s+/g, ' ').slice(0, 240),
  };

  // 2) Playwright navigation probe (no credential success claim without token).
  let browser;
  try {
    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage();
    const url =
      mode === 'signup'
        ? 'https://binolla.com/signup/?lid=15968'
        : 'https://binolla.com/login/';
    const nav = await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45000 });
    const html = await page.content();
    const title = await page.title();
    result.checks.playwrightNav = {
      finalUrl: page.url(),
      httpStatus: nav?.status() ?? null,
      title,
      geoBlocked: detectGeoBlock(html) || detectGeoBlock(title),
      bodySnippet: html.replace(/\s+/g, ' ').slice(0, 240),
    };

    if (email && password && !result.checks.playwrightNav.geoBlocked) {
      // Attempt real capture only when credentials supplied AND page not geo-blocked.
      const { spawn } = await import('child_process');
      const out = await new Promise((resolve) => {
        const child = spawn(
          process.execPath,
          [
            'capture.mjs',
            '--mode',
            mode,
            '--email',
            email,
            '--password',
            password,
            '--signupUrl',
            'https://binolla.com/signup/?lid=15968',
            '--loginUrl',
            'https://binolla.com/login/',
          ],
          { cwd: process.cwd(), env: process.env },
        );
        let stdout = '';
        let stderr = '';
        child.stdout.on('data', (d) => (stdout += d));
        child.stderr.on('data', (d) => (stderr += d));
        child.on('close', (code) => resolve({ code, stdout, stderr }));
      });

      let parsed = null;
      try {
        parsed = JSON.parse(out.stdout.trim().split('\n').filter(Boolean).at(-1) || '{}');
      } catch {
        parsed = null;
      }

      result.checks.capture = {
        exitCode: out.code,
        ok: parsed?.ok === true,
        hasToken: typeof parsed?.token === 'string' && parsed.token.length >= 16,
        error: parsed?.error || (out.stderr || '').slice(0, 200) || null,
        // never echo token
      };

      if (result.checks.capture.ok && result.checks.capture.hasToken) {
        result.ok = true;
        result.verified = true;
      }
    }
  } catch (e) {
    result.checks.playwrightNav = {
      error: String(e?.message || e),
      geoBlocked: detectGeoBlock(String(e?.message || e)),
    };
  } finally {
    if (browser) await browser.close().catch(() => {});
  }

  const geo =
    result.checks.apiLogin?.geoBlocked === true ||
    result.checks.playwrightNav?.geoBlocked === true;

  if (geo) {
    result.ok = false;
    result.verified = false;
    result.reason = 'GEO_BLOCKED';
    result.verdict = 'NOT VERIFIED';
  } else if (!email || !password) {
    result.reason = 'NO_LIVE_CREDENTIALS_SUPPLIED';
    result.verdict = result.checks.playwrightNav?.error
      ? 'NOT VERIFIED'
      : 'REACHABLE_BUT_CREDENTIALS_NOT_RUN';
  } else if (!result.verified) {
    result.verdict = 'NOT VERIFIED';
    result.reason = result.checks.capture?.error || 'CAPTURE_FAILED';
  } else {
    result.verdict = 'VERIFIED';
  }

  console.log(JSON.stringify(result, null, 2));
  process.exit(result.verified ? 0 : 2);
}

main().catch((e) => {
  console.log(
    JSON.stringify(
      {
        ok: false,
        verified: false,
        verdict: 'NOT VERIFIED',
        reason: String(e?.message || e),
      },
      null,
      2,
    ),
  );
  process.exit(2);
});
