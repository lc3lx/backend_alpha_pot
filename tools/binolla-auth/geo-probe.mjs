#!/usr/bin/env node
/**
 * Quick geo probe from the VPS (no secrets).
 * Usage: node geo-probe.mjs
 */
import { chromium } from 'playwright';

const out = { ok: false, checks: {} };

const browser = await chromium.launch({
  headless: true,
  args: ['--no-sandbox', '--disable-dev-shm-usage'],
});

try {
  const page = await browser.newPage();
  await page.goto('https://binolla.com/login/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  try {
    await page.waitForLoadState('networkidle', { timeout: 10000 });
  } catch {
    /* ignore */
  }

  const api = await page.evaluate(async () => {
    const res = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      credentials: 'include',
      body: JSON.stringify({ email: 'geo-probe@example.com', password: 'invalid-probe' }),
    });
    const text = await res.text();
    return { status: res.status, body: text.slice(0, 400) };
  });

  out.checks.page = { url: page.url(), title: await page.title() };
  out.checks.api = api;
  out.geoBlocked = /not available in your current location|United Kingdom|\(GB\)|geo|region|restricted/i.test(
    api.body || '',
  );
  out.ok = !out.geoBlocked && api.status !== 0;
  out.verdict = out.geoBlocked ? 'GEO_BLOCKED' : api.status === 401 || api.status === 400 ? 'REACHABLE' : `HTTP_${api.status}`;
} catch (e) {
  out.error = String(e?.message || e);
  out.verdict = 'PROBE_FAILED';
} finally {
  await browser.close();
}

console.log(JSON.stringify(out, null, 2));
process.exit(out.geoBlocked ? 2 : out.ok ? 0 : 1);
