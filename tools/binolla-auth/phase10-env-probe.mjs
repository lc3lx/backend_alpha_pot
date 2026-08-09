#!/usr/bin/env node
/**
 * Phase 10 environment probe — NO credentials, NO account creation.
 * Verifies signup URL carries lid=15968 and pages are reachable.
 * Prints JSON only (no secrets).
 */
import { chromium } from 'playwright';

const signupUrl = 'https://binolla.com/signup/?lid=15968';
const loginUrl = 'https://binolla.com/login/';

function restrictionHint(text) {
  return /netherlands|\bNL\b|not available in your country|restricted|vpn|just a moment|captcha|cloudflare/i.test(
    text || '',
  );
}

const out = {
  timestampUtc: new Date().toISOString(),
  purpose: 'Phase10 environment probe without credentials',
  checks: {},
};

let browser;
try {
  browser = await chromium.launch({
    headless: true,
    args: ['--disable-blink-features=AutomationControlled', '--disable-dev-shm-usage'],
  });
  const page = await browser.newPage({
    userAgent:
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36',
  });

  const signupNav = await page.goto(signupUrl, { waitUntil: 'domcontentloaded', timeout: 45000 });
  const signupHtml = await page.content();
  const signupTitle = await page.title();
  const signupFinal = page.url();
  out.checks.signupNav = {
    requestedUrl: signupUrl,
    finalUrl: signupFinal,
    httpStatus: signupNav?.status() ?? null,
    title: signupTitle,
    lid15968InFinalUrl: signupFinal.includes('lid=15968'),
    lid15968InHtml: signupHtml.includes('15968'),
    restrictionHint: restrictionHint(signupHtml + ' ' + signupTitle),
    emailFieldVisible: await page
      .locator('input[type="email"], input[name="email"], input[inputmode="email"]')
      .first()
      .isVisible()
      .catch(() => false),
  };

  const loginNav = await page.goto(loginUrl, { waitUntil: 'domcontentloaded', timeout: 45000 });
  const loginHtml = await page.content();
  const loginTitle = await page.title();
  out.checks.loginNav = {
    finalUrl: page.url(),
    httpStatus: loginNav?.status() ?? null,
    title: loginTitle,
    restrictionHint: restrictionHint(loginHtml + ' ' + loginTitle),
    emailFieldVisible: await page
      .locator('input[type="email"], input[name="email"], input[inputmode="email"]')
      .first()
      .isVisible()
      .catch(() => false),
    passwordFieldVisible: await page
      .locator('input[type="password"]')
      .first()
      .isVisible()
      .catch(() => false),
  };

  const credsPresent = Boolean(
    process.env.BINOLLA_LIVE_EMAIL ||
      process.env.BINOLLA_LIVE_PASSWORD ||
      process.env.BINOLLA_AUTH_EMAIL ||
      process.env.BINOLLA_AUTH_PASSWORD,
  );

  out.credentials = {
    available: credsPresent,
    reason: credsPresent
      ? 'env vars present (values not printed)'
      : 'BINOLLA_LIVE_EMAIL / BINOLLA_LIVE_PASSWORD (or BINOLLA_AUTH_*) not set',
  };

  out.verdict = credsPresent
    ? 'CREDENTIALS_PRESENT_CONTINUE_LIVE_AUTH'
    : 'NOT VERIFIED — required live credentials unavailable';
} catch (e) {
  out.error = String(e?.message || e);
  out.verdict = 'NOT VERIFIED — required live credentials unavailable';
} finally {
  if (browser) await browser.close().catch(() => {});
}

process.stdout.write(JSON.stringify(out, null, 2) + '\n');
process.exit(out.credentials?.available ? 0 : 2);
