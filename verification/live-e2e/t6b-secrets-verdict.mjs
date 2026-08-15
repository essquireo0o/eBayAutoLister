import { chromium, BASE, SHOTS, shot, say, result } from './lib.mjs';
import fs from 'node:fs';
import crypto from 'node:crypto';

const creds = JSON.parse(fs.readFileSync(
  'C:/Users/nsquires/source/repos/ING eBay AutoLister/ING eBay AutoLister/credentials.json', 'utf8'));
const fp = v => crypto.createHash('sha256').update(v).digest('hex').slice(0, 6);

// The things that actually grant access if they escape. These MUST NOT appear anywhere.
const SECRET = ['AnthropicApiKey', 'OpenAiApiKey', 'EbayClientSecret', 'EbayUserToken',
                'EbayRefreshToken', 'StripeSecretKey', 'StripeWebhookSecret', 'AdminKey',
                'MarketCompsApiKey'];
// Public-by-design identifiers: the client id and RuName travel in the OAuth URL in the browser's
// own address bar, and the freeware licence key is printed on the licence screen on purpose.
const IDENTIFIER = ['EbayClientId', 'EbayRuName', 'EbayDevId', 'StripePublishableKey', 'LicenseKey'];

const secrets = SECRET.map(n => ({ n, v: creds[n] })).filter(x => typeof x.v === 'string' && x.v.length >= 12);
const idents  = IDENTIFIER.map(n => ({ n, v: creds[n] })).filter(x => typeof x.v === 'string' && x.v.length >= 8);

const patterns = [
  ['Anthropic key',         /sk-ant-[A-Za-z0-9_\-]{20,}/g],
  ['OpenAI key',            /\bsk-(?!ant)[A-Za-z0-9_\-]{32,}/g],
  ['Stripe secret key',     /\bsk_(live|test)_[A-Za-z0-9]{16,}/g],
  ['Stripe webhook secret', /\bwhsec_[A-Za-z0-9]{16,}/g],
  ['eBay OAuth token',      /v\^1\.1#[A-Za-z0-9+/=_\-#^]{40,}/g],
  ['AWS key id',            /\bAKIA[0-9A-Z]{16}\b/g],
  ['JWT',                   /\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}/g],
  ['Bearer token',          /Bearer\s+[A-Za-z0-9_\-.=+/]{24,}/g],
];

const leaks = [], exposures = [], docs = [];
function scan(where, text) {
  if (!text) return;
  docs.push({ where, bytes: text.length });
  for (const s of secrets) if (text.includes(s.v)) leaks.push({ where, what: `${s.n} (sha256:${fp(s.v)})` });
  for (const [label, re] of patterns) { re.lastIndex = 0; const m = text.match(re); if (m) leaks.push({ where, what: `shape: ${label} x${m.length}` }); }
  for (const i of idents) if (text.includes(i.v)) exposures.push({ where, what: i.n });
}

const browser = await chromium.launch();
const A = JSON.parse(fs.readFileSync(`${SHOTS}/account-a.json`, 'utf8'));

// Anonymous
const anon = await browser.newContext();
const p1 = await anon.newPage();
p1.on('response', async r => { const ct = r.headers()['content-type'] || '';
  if (r.url().startsWith(BASE) && /text|json|javascript|html|css/.test(ct)) { try { scan(`anon ${r.url().replace(BASE,'')}`, await r.text()); } catch {} } });
for (const p of ['/signin.html', '/signup.html', '/']) { await p1.goto(BASE + p, { waitUntil: 'networkidle' }).catch(()=>{}); await p1.waitForTimeout(1200); }
for (const p of ['/app.js', '/index.html', '/style.css']) { const r = await fetch(BASE + p).catch(()=>null); if (r?.ok) scan(`anon-direct ${p}`, await r.text()); }

// Signed in
const authed = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const p2 = await authed.newPage();
p2.on('response', async r => { const ct = r.headers()['content-type'] || '';
  if (r.url().startsWith(BASE) && /text|json|javascript|html|css/.test(ct)) { try { scan(`auth ${r.url().replace(BASE,'')}`, await r.text()); } catch {} } });
await p2.goto(`${BASE}/signin.html`, { waitUntil: 'networkidle' });
await p2.fill('#signin-email', A.email); await p2.fill('#signin-password', A.password);
await p2.click('#signin-submit'); await p2.waitForTimeout(5000);
for (const label of ['Money Made', 'Tax Pack', 'Store Plan', 'Listings', 'AI Listing', 'Deal Pipeline']) {
  const l = p2.locator(`text="${label}"`).first();
  if (await l.count()) { await l.click().catch(()=>{}); await p2.waitForTimeout(1800); }
}
for (const p of ['/api/setup/fields', '/api/auth/me', '/api/ai-quota', '/api/stripe/config',
                 '/api/ebay/auth-url', '/api/owner/stats', '/api/earnings', '/api/tax']) {
  scan(`auth-explicit ${p}`, await p2.evaluate(async x => { try { const r = await fetch(x); return await r.text(); } catch { return ''; } }, p));
}
scan('auth-DOM', await p2.content());

// What exactly does the settings payload carry? Field names only.
const fields = await p2.evaluate(async () => { try { const r = await fetch('/api/setup/fields'); return await r.json(); } catch { return {}; } });
say('\n/api/setup/fields returns these field names:');
say('  ' + Object.keys(fields).join(', '));
say('\n  Secret-bearing fields are booleans, not values:');
for (const k of Object.keys(fields)) if (/^has/i.test(k)) say(`    ${k} = ${JSON.stringify(fields[k])}`);

say(`\nScanned ${docs.length} documents, ${docs.reduce((s,d)=>s+d.bytes,0).toLocaleString()} bytes.`);
say(`\nSECRETS (must be absent): ${secrets.map(s=>s.n).join(', ')}`);
if (leaks.length === 0) say('  --> NONE FOUND. No literal secret value, no token-shaped string, anywhere.');
else for (const l of leaks) say('  !! LEAK', JSON.stringify(l));

const uniqExp = [...new Set(exposures.map(e => e.what))];
say(`\nPUBLIC IDENTIFIERS seen (not credentials): ${uniqExp.join(', ') || 'none'}`);
for (const e of uniqExp) say(`   ${e}: ${[...new Set(exposures.filter(x=>x.what===e).map(x=>x.where))].join(' | ')}`);

result('TEST 6 secrets', leaks.length === 0,
  leaks.length === 0
    ? `0 secret values and 0 token shapes in ${docs.length} served documents (${docs.reduce((s,d)=>s+d.bytes,0).toLocaleString()} bytes)`
    : `${leaks.length} real leaks`);

fs.writeFileSync(`${SHOTS}/secret-verdict.json`, JSON.stringify({ leaks, exposures: uniqExp, docs: docs.length }, null, 2));
await browser.close();
