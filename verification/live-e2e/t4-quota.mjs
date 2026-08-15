import { chromium, BASE, SHOTS, shot, say, result } from './lib.mjs';
import fs from 'node:fs';

const A = JSON.parse(fs.readFileSync(`${SHOTS}/account-a.json`, 'utf8'));
const B = JSON.parse(fs.readFileSync(`${SHOTS}/account-b.json`, 'utf8'));

const browser = await chromium.launch();

async function open(who) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await ctx.newPage();
  await page.goto(`${BASE}/signin.html`, { waitUntil: 'networkidle' });
  await page.fill('#signin-email', who.email);
  await page.fill('#signin-password', who.password);
  await page.click('#signin-submit');
  await page.waitForTimeout(5000);
  const me = await page.evaluate(async () => (await fetch('/api/auth/me')).json());
  say(`   signed in as ${me.email} (userId ${me.userId})`);
  return page;
}

// A real photograph-shaped JPEG, drawn in the browser so the bytes are a genuine image.
async function makePhoto(page) {
  const dataUrl = await page.evaluate(() => {
    const c = document.createElement('canvas');
    c.width = 640; c.height = 480;
    const x = c.getContext('2d');
    x.fillStyle = '#d8d8d4'; x.fillRect(0, 0, 640, 480);
    x.fillStyle = '#2b2b30'; x.fillRect(160, 120, 320, 240);
    x.fillStyle = '#4a4a52'; x.fillRect(180, 140, 280, 60);
    x.fillStyle = '#8a8a94'; for (let i = 0; i < 8; i++) x.fillRect(190 + i * 34, 220, 24, 110);
    x.fillStyle = '#e0b23a'; x.fillRect(180, 340, 90, 14);
    x.fillStyle = '#fff'; x.font = 'bold 22px sans-serif';
    x.fillText('ANTMINER S19', 200, 180);
    return c.toDataURL('image/jpeg', 0.85);
  });
  const b64 = dataUrl.split(',')[1];
  fs.writeFileSync(`${SHOTS}/test-photo.jpg`, Buffer.from(b64, 'base64'));
  return { path: `${SHOTS}/test-photo.jpg`, b64 };
}

const quota = page => page.evaluate(async () => (await fetch('/api/ai-quota')).json());

// One generation through the API, from inside the signed-in page.
const generate = page => page.evaluate(async (b64) => {
  const r = await fetch('/api/analyze', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ imageBase64: b64, mimeType: 'image/jpeg' }),
  });
  let t = ''; try { t = await r.text(); } catch {}
  let j = null; try { j = JSON.parse(t); } catch {}
  return { status: r.status, ok: r.ok, headline: j?.failure?.headline, whatHappened: j?.failure?.whatHappened,
           whatToDo: j?.failure?.whatToDo, kind: j?.failure?.kind, title: j?.title, body: t.slice(0, 200) };
}, page._b64);

say('=== TEST 4: AI quota ===');
const pa = await open(A);
const photo = await makePhoto(pa);
pa._b64 = photo.b64;

const q0 = await quota(pa);
say(`   A quota at start: enforced=${q0.enforced} limit=${q0.limit} used=${q0.used} remaining=${q0.remaining}`);
const LIMIT = q0.limit;

// ── Generation 1 through the real UI, so we see a human doing it ───────────
say('-- generation 1 via the UI --');
await pa.goto(`${BASE}/`, { waitUntil: 'networkidle' });
const aiLink = pa.locator('text="AI Listing"').first();
if (await aiLink.count()) { await aiLink.click(); await pa.waitForTimeout(2500); }
await pa.setInputFiles('#nl-file-input', photo.path);
await pa.waitForTimeout(3000);
// If the drop did not auto-analyze, there is usually an explicit button.
const status = await pa.locator('#nl-ai-status').isVisible().catch(() => false);
say(`   AI status panel visible after drop: ${status}`);
await pa.waitForTimeout(45000);
await shot(pa, 'quota-gen1-ui-result');
const q1 = await quota(pa);
say(`   A quota after UI generation: used=${q1.used}/${q1.limit}`);

// ── Fill the rest of the allowance ─────────────────────────────────────────
let used = q1.used;
while (used < LIMIT) {
  const r = await generate(pa);
  const q = await quota(pa);
  say(`   generation -> HTTP ${r.status}${r.title ? ` title="${String(r.title).slice(0, 50)}"` : ''} | used ${q.used}/${q.limit}`);
  if (q.used === used) { say('   (count did not move — stopping to avoid a loop)'); break; }
  used = q.used;
}
const qFull = await quota(pa);
say(`   A allowance now: used=${qFull.used}/${qFull.limit} remaining=${qFull.remaining} exhausted=${qFull.exhausted}`);

// ── The one past the limit, through the UI, so we read what a person reads ──
say(`-- generation ${LIMIT + 1} (the one that must be refused) via the UI --`);
await pa.goto(`${BASE}/`, { waitUntil: 'networkidle' });
const aiLink2 = pa.locator('text="AI Listing"').first();
if (await aiLink2.count()) { await aiLink2.click(); await pa.waitForTimeout(2500); }
await pa.setInputFiles('#nl-file-input', photo.path);
await pa.waitForTimeout(15000);
await shot(pa, 'quota-exceeded-on-screen');

const onScreen = await pa.evaluate(() => {
  const el = document.querySelector('#nl-failure');
  return el ? el.innerText.replace(/\s+\n/g, '\n').trim() : '(no failure panel found)';
});
say('\n   --- what the person sees on screen ---');
say(onScreen.split('\n').map(l => '   | ' + l).join('\n'));

const refused = await generate(pa);
say(`\n   API refusal: HTTP ${refused.status} kind=${refused.kind}`);
say(`   headline:     ${refused.headline}`);
say(`   whatHappened: ${refused.whatHappened}`);
say(`   whatToDo:     ${refused.whatToDo}`);

const humanReadable = !!refused.headline && !!refused.whatToDo
  && /generation|allowance|limit/i.test(refused.headline + refused.whatHappened)
  && !/exception|stack|null reference/i.test(refused.headline + refused.whatHappened);

// ── The other account still has its own allowance ──────────────────────────
say('\n-- account B, which must be untouched --');
const pb = await open(B);
pb._b64 = photo.b64;
const qb0 = await quota(pb);
say(`   B quota: used=${qb0.used}/${qb0.limit} remaining=${qb0.remaining} exhausted=${qb0.exhausted}`);
const bGen = await generate(pb);
const qb1 = await quota(pb);
say(`   B generation -> HTTP ${bGen.status}${bGen.title ? ` title="${String(bGen.title).slice(0, 50)}"` : ''}`);
say(`   B quota after: used=${qb1.used}/${qb1.limit}`);
await pb.goto(`${BASE}/`, { waitUntil: 'networkidle' });
await shot(pb, 'B-still-has-allowance');

const pass = qFull.exhausted && refused.status !== 200 && humanReadable
          && qb0.used === 0 && bGen.status === 200 && qb1.used === 1;
result('TEST 4 AI quota', pass,
  `A used ${qFull.used}/${qFull.limit} then refused with "${refused.headline}"; B independent at ${qb1.used}/${qb1.limit}`);

fs.writeFileSync(`${SHOTS}/quota.json`, JSON.stringify({ q0, qFull, refused, onScreen, qb0, qb1 }, null, 2));
await browser.close();
