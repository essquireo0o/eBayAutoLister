import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';
import fs from 'node:fs';
const browser = await chromium.launch({ headless: false });
const ctx = await browser.newContext({ viewport: { width: 1400, height: 950 },
  userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36' });
const page = await ctx.newPage();
for (const u of ['https://www.whatnot.com/live', 'https://www.whatnot.com/']) {
  try {
    const r = await page.goto(u, { waitUntil: 'domcontentloaded', timeout: 45000 });
    await page.waitForTimeout(9000);
    const html = await page.content();
    console.log(`${u} -> ${r?.status()} title="${await page.title()}" bytes=${html.length}`);
    const hrefs = await page.$$eval('a', as => as.map(a => a.getAttribute('href')).filter(Boolean));
    const live = [...new Set(hrefs.filter(h => h.includes('/live/')))];
    console.log('  /live/ links: ' + live.length + ' ' + live.slice(0,5).join(' '));
    fs.writeFileSync('wn-probe-' + (u.endsWith('live') ? 'live' : 'home') + '.html', html);
  } catch (e) { console.log(u + ' FAILED ' + e.message); }
}
await browser.close();
