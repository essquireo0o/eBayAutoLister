// Follows the consent URL hop by hop and prints where each one goes, so an error that only shows
// up three redirects in can be attributed to the hop that caused it. Diagnostic only.

import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';

const [email, password] = process.argv.slice(2);
const HOST = 'https://app.inglisting.com';

const browser = await chromium.launch();
const context = await browser.newContext();
const page = await context.newPage();

await page.goto(`${HOST}/signin.html`, { waitUntil: 'domcontentloaded' });
await page.evaluate(async ([e, p]) => {
  await fetch('/api/auth/sign-in', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: e, password: p }),
  });
}, [email, password]);

const body = await page.evaluate(() => fetch('/api/ebay/auth-url').then(r => r.json()));
const start = process.env.EBAY_SCOPE_OVERRIDE
  ? (() => { const u = new URL(body.url); u.searchParams.set('scope', process.env.EBAY_SCOPE_OVERRIDE); return u.toString(); })()
  : body.url;

let url = start;
for (let hop = 1; hop <= 8; hop++) {
  const res = await page.request.get(url, { maxRedirects: 0, failOnStatusCode: false });
  const location = res.headers()['location'];
  const shown = url.replace(/(client_id=)[^&]+/, '$1<CLIENT_ID>').replace(/(redirect_uri=)[^&]+/, '$1<RUNAME>');
  console.log(`hop ${hop}: ${res.status()}  ${shown.slice(0, 200)}`);
  if (!location) {
    const text = await res.text().catch(() => '');
    console.log('       body:', text.replace(/\s+/g, ' ').slice(0, 300));
    break;
  }
  url = new URL(location, url).toString();
}

await browser.close();
