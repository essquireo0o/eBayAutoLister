// Opens the running desktop app and clicks every visible sidebar entry in turn, reporting for each
// one which nav item lit up, what the URL hash became, and which feature section is on screen.
// Throwaway verification for the 2026-09-12 sidebar-order pass (owner request of 2026-08-21):
// the reordered entries must still open their own screens. Not part of the app.
import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';

const browser = await chromium.launch({ headless: true });
const page = await (await browser.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
const errors = [];
page.on('pageerror', e => errors.push(e.message));

await page.goto('http://localhost:9332/', { waitUntil: 'domcontentloaded', timeout: 60000 });
await page.waitForSelector('.nav-item', { timeout: 30000 });
await page.waitForTimeout(1500);

const nav = await page.$$eval('.nav-item', els => els.map(b => ({
  page: b.dataset.page, label: b.textContent.trim().replace(/\d+$/, ''), hidden: b.hidden,
  group: b.closest('nav')?.querySelectorAll('.nav-group-label') ? null : null
})));
console.log('SIDEBAR ORDER (as served):', nav.map(n => n.page + (n.hidden ? '(hidden)' : '')).join(' > '));

const rows = [];
for (const entry of nav.filter(n => !n.hidden)) {
  await page.click(`.nav-item[data-page="${entry.page}"]`);
  await page.waitForTimeout(700);
  const state = await page.evaluate(() => {
    const visible = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden'; };
    // Feature screens are `*-section`; the AI Listing screen is the one exception (new-listing-overlay).
    const sections = [...document.querySelectorAll('[id$="-section"], #new-listing-overlay')].filter(visible).map(e => e.id);
    const tab = document.querySelector('.ws-tab.active, .workspace-tab.active, [class*="tab"][class*="active"]');
    return {
      active: document.querySelector('.nav-item.active')?.dataset.page ?? null,
      hash: location.hash.slice(1),
      sections,
      tab: tab ? tab.textContent.trim().slice(0, 40) : null
    };
  });
  rows.push({ clicked: entry.page, label: entry.label, ...state });
}

console.log('');
console.log('clicked'.padEnd(12), 'label'.padEnd(24), 'active nav'.padEnd(12), 'hash'.padEnd(12), 'visible sections');
for (const r of rows) {
  console.log(r.clicked.padEnd(12), r.label.padEnd(24), String(r.active).padEnd(12), r.hash.padEnd(12), r.sections.join(', '));
}

// Judgement: every click must land on its own page — the hash is the route, and the nav item that
// lights is the page's own, except for the two Dashboard regions (which light Dashboard) and the
// AI Listing doors (which share the one 'ai' tab and light the door that was used).
// Dashboard regions clear the hash (home has no hash) and the AI doors route to '#ai'. The
// Dashboard and its Listings region are always in the DOM, so "a screen opened" means something
// other than those two is visible.
const regions = new Set(['listings', 'activity']);
const aiDoors = new Set(['ebay', 'amazon']);
const always = new Set(['dashboard-section', 'listings-section']);
const bad = rows.filter(r => {
  if (r.active !== (regions.has(r.clicked) ? 'dashboard' : r.clicked)) return true;
  if (regions.has(r.clicked)) return !['', 'dashboard'].includes(r.hash) || !r.sections.includes('dashboard-section');
  if (r.clicked === 'dashboard') return !['', 'dashboard'].includes(r.hash) || !r.sections.includes('dashboard-section');
  if (aiDoors.has(r.clicked)) return r.hash !== 'ai' || !r.sections.includes('new-listing-overlay');
  if (r.hash !== r.clicked) return true;
  return !r.sections.some(s => !always.has(s));
});
console.log('');
console.log(bad.length === 0 ? `OK: all ${rows.length} visible sidebar entries opened their own screen`
  : `PROBLEM: ${bad.map(b => b.clicked).join(', ')}`);
if (errors.length) console.log('page errors:', errors.join(' | '));
await browser.close();
process.exit(bad.length === 0 ? 0 : 1);
