/**
 * Verifies a proxy actually reaches Binolla before it is wired into the API.
 *
 * Run this FIRST. A proxy that is unreachable, unauthenticated, or itself blocked fails
 * in exactly the same way as the block being worked around, and telling those apart from
 * inside the login flow costs a deploy cycle each time.
 *
 *   node proxy-check.mjs                       # uses BINOLLA_AUTH_PROXY
 *   node proxy-check.mjs "host:port:user:pass" # or pass it directly
 *
 * Prints the exit IP, its country, and Binolla's response to the login endpoint.
 * Credentials are never printed.
 */
import { chromium } from 'playwright';

function parseProxy(raw) {
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

const raw = process.argv[2] || process.env.BINOLLA_AUTH_PROXY || '';
const proxy = parseProxy(raw);

if (!proxy) {
  console.error('No proxy given. Pass one as an argument or set BINOLLA_AUTH_PROXY.');
  process.exit(2);
}

console.log(`proxy server : ${proxy.server}`);
console.log(`proxy auth   : ${proxy.username ? 'yes' : 'no'}`);

let browser;
try {
  browser = await chromium.launch({
    headless: true,
    proxy,
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-dev-shm-usage'],
  });
} catch (err) {
  console.error(`FAIL  browser did not launch: ${String(err?.message || err).slice(0, 200)}`);
  process.exit(1);
}

const page = await browser.newPage();
let failed = false;

// 1. Does the proxy carry traffic at all, and where does it exit?
try {
  const res = await page.goto('https://ipinfo.io/json', { timeout: 30000 });
  const body = JSON.parse(await res.text());
  console.log(`exit IP      : ${body.ip}`);
  console.log(`exit country : ${body.country}  (${body.city || '?'}, ${body.org || '?'})`);
} catch (err) {
  console.error(`FAIL  proxy did not carry traffic: ${String(err?.message || err).slice(0, 200)}`);
  console.error('      Usually wrong credentials, wrong port, or the proxy is down.');
  await browser.close();
  process.exit(1);
}

// 2. Does Binolla accept the exit IP?
for (const [label, url] of [
  ['homepage    ', 'https://binolla.com/'],
  ['login page  ', 'https://binolla.com/login/'],
]) {
  try {
    const res = await page.goto(url, { timeout: 45000, waitUntil: 'domcontentloaded' });
    const status = res?.status() ?? 0;
    const verdict = status === 403 ? 'BLOCKED' : status < 400 ? 'ok' : 'unexpected';
    if (status === 403) failed = true;
    console.log(`${label} : ${status}  ${verdict}`);
  } catch (err) {
    failed = true;
    console.log(`${label} : error  ${String(err?.message || err).slice(0, 120)}`);
  }
}

await browser.close();

console.log('');
if (failed) {
  console.log('RESULT: this proxy is ALSO blocked by Binolla. Try a different exit country.');
  process.exit(1);
}
console.log('RESULT: proxy reaches Binolla. Safe to set BINOLLA_AUTH_PROXY and restart the API.');
