// ── Photographing the running app ─────────────────────────────────────────────────────────────
//
// Drives the REAL desktop build at http://localhost:9332 — the same server the owner is looking
// at, with their real data — and writes one PNG per screen into a folder on the Desktop.
//
// Deliberately not the file:///-served-wwwroot approach amazon-ui-capture.mjs had to use. That
// script was written when 9332 could not be bound on this machine (a Windows reserved port range),
// so it served the shipped wwwroot itself and stubbed the API. The port reservation was fixed on
// 2026-08-18, the app binds 9332 again, so these are photographs of the running product rather
// than of its assets: real listings, real counts, real money.
//
// Usage:  node capture-screens.mjs [outputFolder]
// Default output:  <Desktop>\ING Listing Engine screenshots <date>
import { join } from 'node:path';
import { mkdirSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';

const playwright = await import(
  'file:///' + join(process.env.APPDATA, 'npm', 'node_modules', 'playwright', 'index.mjs').replace(/\\/g, '/'));

const BASE = process.env.ING_BASE || 'http://localhost:9332';

// The Desktop, asked of Windows rather than assembled from the home directory — OneDrive moves it,
// and a hand-built path writes somewhere the owner does not look. Falls back to ~/Desktop.
function desktop() {
  const od = process.env.OneDrive || process.env.OneDriveConsumer;
  const candidates = [od && join(od, 'Desktop'), join(homedir(), 'Desktop')].filter(Boolean);
  for (const c of candidates) { try { mkdirSync(c, { recursive: true }); return c; } catch { } }
  return homedir();
}

const stamp = new Date().toISOString().slice(0, 10);
const outDir = process.argv[2] || join(desktop(), `ING Listing Engine screenshots ${stamp}`);
mkdirSync(outDir, { recursive: true });

// Every screen worth showing, in the order the sidebar lists them. Hash routes come from the
// route table in app.js — a name that is not in that table silently shows the dashboard, so these
// are copied from it rather than guessed.
const SCREENS = [
  ['01 Dashboard',            'dashboard'],
  ['02 AI Listing eBay',      'ai'],
  ['03 Photo Box Camera',     'photobox'],
  ['04 Photo Library',        'photos'],
  ['05 Listings',             'inventory'],
  ['06 Listing Copilot',      'copilot'],
  ['07 Money Made',           'earnings'],
  ['08 Tax Pack',             'tax'],
  ['09 WhatsNot',             'whatsnot'],
  ['10 Opportunity Finder',   'opportunity'],
  ['11 Spend My Budget',      'budget'],
  ['12 Price Position',       'position'],
  ['13 Offers to Watchers',   'offers'],
  ['14 Rescue Aging Stock',   'rescue'],
  ['15 Ad Rate Advisor',      'promoted'],
  ['16 Ship Smart',           'shipping'],
  ['17 Relist',               'relist'],
  ['18 Lots',                 'lots'],
  ['19 Where To Sell',        'wheretosell'],
  ['20 Trends',               'trends'],
  ['21 Logs',                 'logs'],
  ['22 Settings',             'settings'],
];

const browser = await playwright.chromium.launch();
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 2 });

// A screen that is still fetching photographs a spinner. Give each one a moment to settle, then
// wait for the network to go quiet — but never hang on it: several of these poll forever by
// design (the camera status, the deal radar), so networkidle would never fire.
async function settle(ms = 2200) {
  try { await page.waitForLoadState('networkidle', { timeout: 4000 }); } catch { }
  await page.waitForTimeout(ms);
}

const done = [];
const failed = [];

await page.goto(BASE, { waitUntil: 'domcontentloaded' });
await settle(3500);

for (const [label, route] of SCREENS) {
  try {
    await page.goto(`${BASE}/#${route}`, { waitUntil: 'domcontentloaded' });
    // The router is hash-driven, so a same-document navigation fires no load event.
    await page.evaluate(r => { location.hash = '#' + r; window.dispatchEvent(new HashChangeEvent('hashchange')); }, route);
    await settle();
    const file = join(outDir, `${label}.png`);
    await page.screenshot({ path: file, fullPage: true });
    done.push(label);
    console.log(`  ok   ${label}`);
  } catch (err) {
    failed.push(`${label}: ${err.message}`);
    console.log(`  FAIL ${label} — ${err.message}`);
  }
}

await browser.close();

// A short note beside the images, so the folder explains itself in a week.
writeFileSync(join(outDir, '_README.txt'),
  [`ING Listing Engine — screenshots`,
   `Captured ${new Date().toString()}`,
   `From the running desktop build at ${BASE} (real data, not fixtures).`,
   `1600x1000 viewport at 2x, full page.`,
   ``,
   `Captured ${done.length} of ${SCREENS.length} screens.`,
   ...(failed.length ? [``, `Not captured:`, ...failed.map(f => `  - ${f}`)] : []),
  ].join('\r\n'), 'utf8');

console.log(`\n${done.length}/${SCREENS.length} screens -> ${outDir}`);
if (failed.length) { console.log(`${failed.length} failed`); }
