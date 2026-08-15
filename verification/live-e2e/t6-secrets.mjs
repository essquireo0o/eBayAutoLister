import { chromium, BASE, SHOTS, shot, say, result } from './lib.mjs';
import fs from 'node:fs';

// The real secret values, loaded locally so we can search for them literally.
// Nothing here ever prints a value — only whether it was found, and a 6-char fingerprint.
const creds = JSON.parse(fs.readFileSync(
  'C:/Users/nsquires/source/repos/ING eBay AutoLister/ING eBay AutoLister/credentials.json', 'utf8'));

const crypto = await import('node:crypto');
const fp = v => crypto.createHash('sha256').update(v).digest('hex').slice(0, 6);

const literals = [];
for (const [name, value] of Object.entries(creds)) {
  if (typeof value !== 'string' || value.length < 12) continue;
  if (!/key|secret|token|runame|clientid|devid/i.test(name)) continue;
  literals.push({ name, value, fpr: fp(value) });
}
say('Searching for these literal secrets (values never printed):');
for (const l of literals) say(`   ${l.name.padEnd(28)} len=${String(l.value.length).padEnd(4)} sha256:${l.fpr}`);

// Shapes, so a secret we do not hold locally (a per-user eBay token, a rotated key) is still caught.
const patterns = [
  ['Anthropic key',        /sk-ant-[A-Za-z0-9_\-]{20,}/g],
  ['OpenAI key',           /\bsk-(?!ant)[A-Za-z0-9_\-]{32,}/g],
  ['Stripe secret key',    /\bsk_(live|test)_[A-Za-z0-9]{16,}/g],
  ['Stripe webhook secret',/\bwhsec_[A-Za-z0-9]{16,}/g],
  ['eBay OAuth token',     /v\^1\.1#[A-Za-z0-9+/=_\-#^]{40,}/g],
  ['eBay refresh token',   /v\^1\.1#i\^1#/g],
  ['AWS key id',           /\bAKIA[0-9A-Z]{16}\b/g],
  ['JWT',                  /\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}/g],
  ['Bearer token',         /Bearer\s+[A-Za-z0-9_\-.=+/]{24,}/g],
];

const findings = [];
const scanned = [];

function scan(where, text) {
  if (!text) return;
  scanned.push({ where, bytes: text.length });
  for (const l of literals) {
    if (text.includes(l.value)) findings.push({ where, kind: `LITERAL ${l.name} (sha256:${l.fpr})` });
  }
  for (const [label, re] of patterns) {
    re.lastIndex = 0;
    const m = text.match(re);
    if (m) findings.push({ where, kind: `SHAPE ${label}`, count: m.length, sample: m[0].slice(0, 12) + '…' });
  }
}

const browser = await chromium.launch();

// ── Pass 1: anonymous. Everything a stranger can pull without signing in. ──
const anon = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const p1 = await anon.newPage();
const seen = new Set();
p1.on('response', async res => {
  const url = res.url();
  if (seen.has(url) || !url.startsWith(BASE)) return;
  seen.add(url);
  const ct = res.headers()['content-type'] || '';
  if (!/text|json|javascript|html|css|xml/.test(ct)) return;
  try { scan(`anon-response ${url.replace(BASE, '')}`, await res.text()); } catch {}
});

for (const path of ['/signin.html', '/signup.html', '/']) {
  await p1.goto(BASE + path, { waitUntil: 'networkidle' }).catch(() => {});
  await p1.waitForTimeout(1500);
}
say(`\nAnonymous pass: ${seen.size} responses captured.`);

// Direct fetches of the bundles, in case the page did not request them on this route.
for (const path of ['/app.js', '/index.html', '/style.css', '/signin.html', '/signup.html', '/favicon.svg']) {
  const r = await fetch(BASE + path).catch(() => null);
  if (r && r.ok) scan(`anon-direct ${path}`, await r.text());
}

// ── Pass 2: signed in. Every response the app makes while a real user uses it. ──
const A = JSON.parse(fs.readFileSync(`${SHOTS}/account-a.json`, 'utf8'));
const authed = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const p2 = await authed.newPage();
const seen2 = new Set();
p2.on('response', async res => {
  const url = res.url();
  if (seen2.has(url) || !url.startsWith(BASE)) return;
  seen2.add(url);
  const ct = res.headers()['content-type'] || '';
  if (!/text|json|javascript|html|css|xml/.test(ct)) return;
  try { scan(`auth-response ${url.replace(BASE, '')}`, await res.text()); } catch {}
});

await p2.goto(`${BASE}/signin.html`, { waitUntil: 'networkidle' });
await p2.fill('#signin-email', A.email);
await p2.fill('#signin-password', A.password);
await p2.click('#signin-submit');
await p2.waitForTimeout(5000);

// Click through the main screens so their API calls fire.
for (const label of ['Money Made', 'Tax Pack', 'Store Plan', 'Listings', 'AI Listing', 'Deal Pipeline', 'Photo Library']) {
  const link = p2.locator(`text="${label}"`).first();
  if (await link.count()) { await link.click().catch(() => {}); await p2.waitForTimeout(2200); }
}
await p2.waitForTimeout(2000);
say(`Signed-in pass: ${seen2.size} responses captured.`);

// Endpoints most likely to echo configuration back, asked for explicitly.
const explicit = ['/api/auth/me', '/api/ai-quota', '/api/stripe/config', '/api/ebay/auth-url',
                  '/api/owner/stats', '/api/earnings', '/api/store-plan', '/api/tax',
                  '/api/inventory/cost-basis', '/api/deals'];
for (const path of explicit) {
  const body = await p2.evaluate(async p => {
    try { const r = await fetch(p); return await r.text(); } catch { return ''; }
  }, path);
  scan(`auth-explicit ${path}`, body);
}

// The settings screen, which is where a key would be shown if anywhere.
await p2.goto(`${BASE}/`, { waitUntil: 'networkidle' });
await p2.waitForTimeout(1500);
const gear = p2.locator('#settings-btn, .settings-btn, [title*="Settings" i]').first();
if (await gear.count()) { await gear.click().catch(() => {}); await p2.waitForTimeout(2500); }
await shot(p2, 'settings-screen-authed');
scan('auth-settings-DOM', await p2.content());

// ── Verdict ────────────────────────────────────────────────────────────────
say(`\nScanned ${scanned.length} documents, ${scanned.reduce((s, d) => s + d.bytes, 0).toLocaleString()} bytes total.`);
if (findings.length === 0) {
  result('TEST 6 secrets', true, `no secret literal or token shape found in ${scanned.length} served documents`);
} else {
  say('\n!!! LEAK CANDIDATES !!!');
  for (const f of findings) say('   ', JSON.stringify(f));
  result('TEST 6 secrets', false, `${findings.length} candidate leaks`);
}
fs.writeFileSync(`${SHOTS}/secret-scan.json`, JSON.stringify({ findings, scanned }, null, 2));

await browser.close();
process.exit(findings.length === 0 ? 0 : 1);
