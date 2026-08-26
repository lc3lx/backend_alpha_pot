import { chromium } from 'playwright';
import fs from 'fs';

const OUT = process.argv[2];
const widths = process.argv.slice(3).map(Number);
const URL = 'http://127.0.0.1:4399/';

const browser = await chromium.launch();
for (const w of widths) {
  const ctx = await browser.newContext({ viewport: { width: w, height: 900 } });
  const page = await ctx.newPage();
  await page.goto(URL, { waitUntil: 'networkidle' });
  await page.evaluate(async () => {
    for (let y = 0; y < document.body.scrollHeight; y += window.innerHeight) {
      window.scrollTo(0, y); await new Promise(r => setTimeout(r, 150));
    }
    window.scrollTo(0, 0);
  });
  await page.waitForTimeout(1000);
  const secs = await page.$$('body section, body header, body footer');
  let i = 0;
  for (const s of secs) {
    const box = await s.boundingBox();
    if (!box || box.height < 40) { i++; continue; }
    const id = (await s.getAttribute('id')) || (await s.evaluate(e => e.className.toString().split(' ')[0])) || 'sec';
    const safe = String(id).replace(/[^a-z0-9_-]/gi, '').slice(0, 24);
    try { await s.screenshot({ path: `${OUT}/${w}-${String(i).padStart(2,'0')}-${safe}.png` }); } catch {}
    i++;
  }
  console.log(w, 'sections:', secs.length);
  await ctx.close();
}
await browser.close();
