// Prints the consent URL the LIVE app builds, with the client id and RuName partially redacted,
// and then asks eBay what it thinks of a few scope subsets. Diagnostic only.

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
if (!body.url) { console.log('no url:', JSON.stringify(body)); await browser.close(); process.exit(1); }

const u = new URL(body.url);
const redact = (s) => s.length <= 12 ? s : `${s.slice(0, 8)}…REDACTED…${s.slice(-4)}`;

console.log('origin+path :', u.origin + u.pathname);
console.log('client_id   :', redact(u.searchParams.get('client_id')));
console.log('redirect_uri:', redact(u.searchParams.get('redirect_uri')));
console.log('response_type:', u.searchParams.get('response_type'));
console.log('state       :', u.searchParams.get('state'));
console.log('scope       :');
for (const s of (u.searchParams.get('scope') || '').split(' ')) console.log('              ', s);

// Ask eBay about progressively smaller scope sets, straight from this machine.
const scopes = (u.searchParams.get('scope') || '').split(' ');
const trials = [
  ['all', scopes],
  ['no negotiation', scopes.filter(s => !s.endsWith('sell.negotiation'))],
  ['base only', [scopes[0]]],
];

console.log('\n--- what eBay says to each scope set ---');
for (const [label, set] of trials) {
  const probe = new URL(u.toString());
  probe.searchParams.set('scope', set.join(' '));
  const res = await page.request.get(probe.toString(), { maxRedirects: 0, failOnStatusCode: false });
  const location = res.headers()['location'] || '';
  console.log(`${label.padEnd(16)} -> ${res.status()} ${location.slice(0, 140)}`);
}

await browser.close();
