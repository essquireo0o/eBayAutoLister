// Acceptance for "connect eBay opens a new tab and this page stays put", driven against the LIVE
// site. Not a unit test: the thing being proved is a browser's pop-up policy and eBay's own
// response to a consent URL, and neither of those exists in a test double.
//
//   node ebay-connect-live-check.mjs <email> <password>
//
// Prints the URL of every page the browser ends up with. What it must show: two pages, one still
// on app.inglisting.com and one on ebay.com.

import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';

const [email, password] = process.argv.slice(2);
const HOST = 'https://app.inglisting.com';

const browser = await chromium.launch();
const context = await browser.newContext();
const page = await context.newPage();

// Sign up, or sign in if the account is already there.
await page.goto(`${HOST}/signup.html`, { waitUntil: 'domcontentloaded' });
const signUp = await page.evaluate(async ([e, p]) => {
  const r = await fetch('/api/auth/sign-up', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: e, password: p }),
  });
  return { status: r.status, body: await r.text() };
}, [email, password]);
console.log('sign-up:', signUp.status, signUp.body.slice(0, 120));

if (signUp.status !== 200) {
  const signIn = await page.evaluate(async ([e, p]) => {
    const r = await fetch('/api/auth/sign-in', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: e, password: p }),
    });
    return { status: r.status, body: await r.text() };
  }, [email, password]);
  console.log('sign-in:', signIn.status, signIn.body.slice(0, 120));
}

await page.goto(`${HOST}/`, { waitUntil: 'domcontentloaded' });
await page.waitForTimeout(3000);

const status = await page.evaluate(() => fetch('/api/setup/status').then(r => r.json()));
console.log('setup status:', JSON.stringify(status));

const redirect = await page.evaluate(() => fetch('/api/ebay/status').then(r => r.json()).then(s => s.redirect));
console.log('redirect the app reports:', JSON.stringify(redirect));

// THE ACCEPTANCE. A real click on the real button.
const opened = context.waitForEvent('page', { timeout: 30000 });
await page.click('#btn-connect');

let second = null;
try { second = await opened; } catch { console.log('!! no second page appeared'); }

if (second) {
  await second.waitForLoadState('domcontentloaded').catch(() => {});
  await second.waitForTimeout(4000);
}

console.log('--- pages the browser now has ---');
for (const p of context.pages()) console.log('   ', p.url().slice(0, 300));

console.log('--- original page still on ---');
console.log('   ', page.url());

console.log('--- what the original page says ---');
console.log('   ', (await page.locator('#result').textContent().catch(() => '(no #result)')) || '(empty)');

if (second) {
  console.log('--- second page title ---');
  console.log('   ', await second.title().catch(() => '(none)'));
  console.log('--- second page, first 400 chars of visible text ---');
  console.log('   ', ((await second.locator('body').innerText().catch(() => '')) || '').replace(/\s+/g, ' ').slice(0, 400));
}

await page.screenshot({ path: 'ebay_connect_waiting.png', fullPage: false });
if (second) await second.screenshot({ path: 'ebay_connect_consent.png', fullPage: false }).catch(() => {});

await browser.close();
