import { chromium } from 'playwright';

const OUT = process.argv[2];
const URL = 'http://127.0.0.1:4399/';
const widths = [320, 360, 390, 430, 540, 640, 768, 900, 1024, 1200, 1440];

const browser = await chromium.launch();
const report = [];

for (const w of widths) {
  const ctx = await browser.newContext({ viewport: { width: w, height: 900 }, deviceScaleFactor: 1 });
  const page = await ctx.newPage();
  await page.goto(URL, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1500);
  // scroll through so lazy content / observers fire
  await page.evaluate(async () => {
    const step = window.innerHeight;
    for (let y = 0; y < document.body.scrollHeight; y += step) {
      window.scrollTo(0, y);
      await new Promise((r) => setTimeout(r, 120));
    }
    window.scrollTo(0, 0);
  });
  await page.waitForTimeout(800);

  const info = await page.evaluate((vw) => {
    const overflow = [];
    for (const el of document.querySelectorAll('*')) {
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      if (r.right > vw + 1 || r.left < -1) {
        const cs = getComputedStyle(el);
        if (cs.position === 'fixed') continue;
        overflow.push({
          tag: el.tagName.toLowerCase(),
          cls: (el.className && el.className.baseVal !== undefined ? el.className.baseVal : el.className || '').toString().slice(0, 70),
          left: Math.round(r.left), right: Math.round(r.right), w: Math.round(r.width),
        });
      }
    }
    // keep only outermost offenders
    return {
      scrollW: document.documentElement.scrollWidth,
      docH: document.body.scrollHeight,
      overflow: overflow.slice(0, 12),
    };
  }, w);

  report.push({ width: w, ...info });
  await page.screenshot({ path: `${OUT}/w${w}.png`, fullPage: true });
  await ctx.close();
}

await browser.close();
console.log(JSON.stringify(report, null, 1));
