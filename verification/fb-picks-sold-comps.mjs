// Opens the running desktop app, goes to the Opportunity Finder (which loads Today's picks from the
// seller's own Facebook Marketplace on open), waits for the cards to be priced, and reports what each
// card says about its sold evidence. Throwaway verification for the 2026-08-20 change: Facebook rows
// priced through the same stored + live sold-comps path as the eBay scanner's rows. Not part of the app.
import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';

const browser = await chromium.launch({ headless: true });
const page = await (await browser.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
page.on('console', m => { if (m.type() === 'error') console.log('console error:', m.text()); });
page.on('pageerror', e => console.log('page error:', e.message));

await page.goto('http://localhost:9332/', { waitUntil: 'domcontentloaded', timeout: 60000 });
await page.waitForTimeout(2500);
await page.click('button.nav-item[data-page="opportunity"]');
console.log('Opportunity Finder opened; waiting for the Marketplace picks (a real page load, ~30-60s)…');

await page.waitForFunction(() => {
  const s = document.getElementById('fb-picks-status')?.textContent || '';
  const skeletons = document.querySelectorAll('#fb-picks-grid .fb-pick-skeleton').length;
  return skeletons === 0 && !/Opening your Marketplace/i.test(s);
}, null, { timeout: 240000 });
console.log('picks status after load:', await page.textContent('#fb-picks-status'));

const cardCount = await page.$$eval('#fb-picks-grid .fb-pick-card', c => c.length);
console.log('cards:', cardCount);

if (cardCount) {
  console.log('waiting for pricing (stored comps, then the live pass)…');
  await page.waitForFunction(() => {
    const s = document.getElementById('fb-picks-status')?.textContent || '';
    return !/Pricing these/i.test(s);
  }, null, { timeout: 300000 });

  console.log('picks status after pricing:', await page.textContent('#fb-picks-status'));
  const cards = await page.$$eval('#fb-picks-grid .fb-pick-card', cs => cs.map(c => ({
    title: c.querySelector('.fb-pick-title')?.textContent?.trim(),
    ask: c.querySelector('.fb-pick-price')?.textContent?.trim(),
    money: (c.querySelector('.fb-pick-money')?.innerText || '').replace(/\s+/g, ' ').trim(),
  })));
  console.log(JSON.stringify(cards.slice(0, 15), null, 1));
  console.log('evidence lines:', await page.$$eval('.fb-pick-evidence', e => e.length),
              '| live buttons:', await page.$$eval('.fb-pick-live-btn', e => e.length),
              '| priced:', await page.$$eval('.fb-pick-comp', e => e.length));
}

await page.locator('#fb-picks-panel').screenshot({ path: 'verification/opportunity_finder_facebook_picks.png' });
console.log('screenshot: verification/opportunity_finder_facebook_picks.png');
await browser.close();
