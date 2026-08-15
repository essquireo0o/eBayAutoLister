import { chromium, BASE, SHOTS, shot, say, result, account } from './lib.mjs';
import fs from 'node:fs';

const stamp = process.argv[2];
if (!stamp) throw new Error('pass a stamp');
const A = account('a', stamp);

const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await ctx.newPage();

// ── 1. Sign-up ─────────────────────────────────────────────────────────────
say('=== TEST 1: sign-up ===');
await page.goto(`${BASE}/signup.html`, { waitUntil: 'networkidle' });
await shot(page, 'signup-page');

await page.fill('#signup-email', A.email);
await page.fill('#signup-password', A.password);
await page.fill('#signup-confirm', A.password);
await shot(page, 'signup-filled');

await page.click('#signup-submit');
await page.waitForTimeout(4000);
say('   after submit, url =', page.url());
await shot(page, 'signup-landed');

// Did we land signed in? Ask the app, the way the page itself does.
const me1 = await page.evaluate(async () => {
  const r = await fetch('/api/auth/me');
  return { status: r.status, body: await r.text() };
});
say('   /api/auth/me ->', me1.status, me1.body);
const signedIn = me1.status === 200 && me1.body.includes(A.email);
result('TEST 1 sign-up', signedIn && !page.url().includes('signup'),
  `landed at ${page.url()}, /api/auth/me = ${me1.status} ${me1.body}`);

// ── 2. Session: sign out, wrong password refused, right password works ─────
say('=== TEST 2: session ===');
const out = await page.evaluate(async () => {
  const r = await fetch('/api/auth/sign-out', { method: 'POST' });
  return { status: r.status, body: await r.text() };
});
say('   sign-out ->', out.status, out.body);

const meAfterOut = await page.evaluate(async () => {
  const r = await fetch('/api/auth/me');
  return { status: r.status, auth: r.headers.get('x-auth-required') };
});
say('   /api/auth/me after sign-out ->', meAfterOut.status, 'X-Auth-Required:', meAfterOut.auth);

await page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
await page.waitForTimeout(2500);
say('   / after sign-out ->', page.url());
await shot(page, 'after-signout');

// Wrong password
await page.goto(`${BASE}/signin.html`, { waitUntil: 'networkidle' });
await page.fill('#signin-email', A.email);
await page.fill('#signin-password', 'definitely-not-the-password');
await page.click('#signin-submit');
await page.waitForTimeout(2500);
const errText = (await page.textContent('#auth-error').catch(() => '')) || '';
say('   wrong-password url =', page.url());
say('   wrong-password error text =', JSON.stringify(errText.trim()));
await shot(page, 'wrong-password-refused');
const wrongRefused = page.url().includes('signin') && errText.trim().length > 0;

// Right password
await page.fill('#signin-password', A.password);
await page.click('#signin-submit');
await page.waitForTimeout(4000);
say('   right-password url =', page.url());
await shot(page, 'signed-back-in');
const me2 = await page.evaluate(async () => {
  const r = await fetch('/api/auth/me');
  return { status: r.status, body: await r.text() };
});
say('   /api/auth/me ->', me2.status, me2.body);
const backIn = me2.status === 200 && me2.body.includes(A.email);

result('TEST 2 session', meAfterOut.status === 401 && wrongRefused && backIn,
  `sign-out then /api/auth/me=${meAfterOut.status}; wrong pw refused=${wrongRefused} ("${errText.trim()}"); re-signin=${me2.status}`);

await ctx.storageState({ path: `${SHOTS}/state-a.json` });
fs.writeFileSync(`${SHOTS}/account-a.json`, JSON.stringify(A, null, 2));
await browser.close();
