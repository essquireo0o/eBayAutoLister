import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';
import fs from 'node:fs';
const ctx = await chromium.launchPersistentContext('C:/Users/nsquires/AppData/Local/Temp/wn-prof', {
  headless: false, channel: 'chrome', viewport: { width: 1400, height: 950 },
  args: ['--disable-blink-features=AutomationControlled'],
});
const page = ctx.pages()[0] || await ctx.newPage();
await page.goto('https://www.whatnot.com/', { waitUntil: 'domcontentloaded', timeout: 60000 });
await page.waitForTimeout(15000);
console.log('title: ' + await page.title());
const hrefs = await page.$$eval('a', as => as.map(a => a.getAttribute('href')).filter(Boolean));
const live = [...new Set(hrefs.filter(h => h.includes('/live/')))];
console.log('total links: ' + hrefs.length + '  /live/: ' + live.length);
console.log(live.slice(0, 15).join('\n'));
fs.writeFileSync('wn-home.html', await page.content());
await page.screenshot({ path: 'wn-home.png' });
await ctx.close();
