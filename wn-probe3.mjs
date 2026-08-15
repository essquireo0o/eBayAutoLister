import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';
const ctx = await chromium.launchPersistentContext('C:/Users/nsquires/AppData/Local/Temp/wn-prof', {
  headless: false, channel: 'chrome', viewport: { width: 1400, height: 950 },
  args: ['--disable-blink-features=AutomationControlled'] });
const page = ctx.pages()[0] || await ctx.newPage();
const paths = ['/explore','/browse','/category/electronics','/live-shows','/s/electronics','/category/sports-cards'];
for (const p of paths) {
  try {
    const r = await page.goto('https://www.whatnot.com' + p, { waitUntil: 'domcontentloaded', timeout: 40000 });
    await page.waitForTimeout(7000);
    const hrefs = await page.$$eval('a', as => as.map(a => a.getAttribute('href')).filter(Boolean));
    const live = [...new Set(hrefs.filter(h => h.includes('/live/')))];
    console.log(`${p} -> ${r?.status()}  links=${hrefs.length}  live=${live.length}  ${live.slice(0,3).join(' ')}`);
  } catch (e) { console.log(p + ' ERR ' + e.message.split('\n')[0]); }
}
await ctx.close();
