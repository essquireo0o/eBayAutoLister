// Acceptance for "eBay sign-in completes on app.inglisting.com", driven against the LIVE site.
//
//   node ebay-hosted-consent-check.mjs <email> <password>
//
// What it proves, and the reason none of it can be a unit test:
//   * the consent URL the hosted app hands out carries the hosted RuName and a state ending in 'h',
//     which is the letter the PHP relay reads to send the browser back to app.inglisting.com
//     rather than to a port on the seller's own machine;
//   * clicking the button opens eBay in a SECOND tab and leaves the app's own page where it was —
//     a browser pop-up policy, which only a browser has.
//
// IT DELIBERATELY STOPS AT THE CONSENT SCREEN. Nothing here types an eBay password or clicks
// Agree: completing the grant would spend the owner's real eBay account on a test run.

import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';

const [email, password] = process.argv.slice(2);
const HOST = 'https://app.inglisting.com';

/** The client id is an application secret's near neighbour; enough of it to identify, not to use. */
const redact = (url) =>
  url.replace(/(client_id=)([^&]{10})[^&]*([^&]{6})(?=&|$)/, '$1$2…REDACTED…$3');

const browser = await chromium.launch();
const context = await browser.newContext();
const page = await context.newPage();

await page.goto(`${HOST}/signup.html`, { waitUntil: 'domcontentloaded' });

const post = (path, body) => page.evaluate(async ([p, b]) => {
  const r = await fetch(p, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(b),
  });
  return { status: r.status, body: await r.text() };
}, [path, body]);

let auth = await post('/api/auth/sign-up', { email, password });
console.log('sign-up:', auth.status, auth.body.slice(0, 160));
if (auth.status !== 200) {
  auth = await post('/api/auth/sign-in', { email, password });
  console.log('sign-in:', auth.status, auth.body.slice(0, 160));
}
if (auth.status !== 200) { console.log('!! could not get an account'); await browser.close(); process.exit(1); }

await page.goto(`${HOST}/`, { waitUntil: 'domcontentloaded' });
await page.waitForTimeout(2500);

// ── 1. The URL itself ────────────────────────────────────────────────────────────────────────
const authUrl = await page.evaluate(() =>
  fetch('/api/ebay/auth-url').then(r => r.json()).catch(e => ({ error: String(e) })));

if (!authUrl.url) { console.log('!! no auth url:', JSON.stringify(authUrl)); await browser.close(); process.exit(1); }

console.log('\n--- the consent URL the hosted app hands out (client id redacted) ---');
console.log(redact(authUrl.url));

const q = new URL(authUrl.url).searchParams;
const state = q.get('state');
console.log('\n--- the parts that matter ---');
console.log('   host       :', new URL(authUrl.url).origin + new URL(authUrl.url).pathname);
console.log('   redirect_uri (this is the RuName, not a URL):', q.get('redirect_uri'));
console.log('   state      :', state);
console.log('   state shape:', /^[0-9a-f]{32}h$/.test(state) ? 'OK — 32 hex + h (hosted)' : '!! WRONG');
console.log('   scopes     :', (q.get('scope') || '').split(' ').length);

// ── 2. The second tab, from a real click ─────────────────────────────────────────────────────
const opened = context.waitForEvent('page', { timeout: 30000 });
await page.click('#btn-connect');

let second = null;
try { second = await opened; } catch { console.log('\n!! no second page appeared'); }

if (second) {
  await second.waitForLoadState('domcontentloaded').catch(() => {});
  await second.waitForTimeout(5000);
}

console.log('\n--- pages the browser now has ---');
for (const p of context.pages()) console.log('   ', redact(p.url()).slice(0, 240));

console.log('\n--- the app page is still the app page ---');
console.log('   ', page.url());
console.log('   says:', ((await page.locator('#result').textContent().catch(() => '')) || '(empty)').trim().slice(0, 200));

if (second) {
  console.log('\n--- the eBay tab ---');
  console.log('   title:', await second.title().catch(() => '(none)'));
  console.log('   text :', ((await second.locator('body').innerText().catch(() => '')) || '')
    .replace(/\s+/g, ' ').slice(0, 300));
  await second.screenshot({ path: 'verification/hosted_ebay_consent.png' }).catch(() => {});
}
await page.screenshot({ path: 'verification/hosted_app_waiting.png' }).catch(() => {});

// STOP. No password is typed and nothing is agreed to — see the header.
await browser.close();
