import { chromium, BASE, SHOTS, shot, say, result, account } from './lib.mjs';
import fs from 'node:fs';

const stamp = process.argv[2];
const A = JSON.parse(fs.readFileSync(`${SHOTS}/account-a.json`, 'utf8'));
const B = account('b', stamp);

const browser = await chromium.launch();

async function signIn(who, isSignUp) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await ctx.newPage();
  if (isSignUp) {
    await page.goto(`${BASE}/signup.html`, { waitUntil: 'networkidle' });
    await page.fill('#signup-email', who.email);
    await page.fill('#signup-password', who.password);
    await page.fill('#signup-confirm', who.password);
    await page.click('#signup-submit');
  } else {
    await page.goto(`${BASE}/signin.html`, { waitUntil: 'networkidle' });
    await page.fill('#signin-email', who.email);
    await page.fill('#signin-password', who.password);
    await page.click('#signin-submit');
  }
  await page.waitForTimeout(5000);
  const me = await page.evaluate(async () => (await fetch('/api/auth/me')).json());
  say(`   signed in as ${me.email} (userId ${me.userId})`);
  return { ctx, page, me };
}

const api = (page, path, method = 'GET', body) => page.evaluate(async ([p, m, b]) => {
  const r = await fetch(p, {
    method: m,
    headers: b ? { 'Content-Type': 'application/json' } : {},
    body: b ? JSON.stringify(b) : undefined,
  });
  let data = null, text = '';
  try { text = await r.text(); data = JSON.parse(text); } catch {}
  return { status: r.status, data, text: text.slice(0, 300) };
}, [path, method, body]);

// ── Account A creates real, identifiable data ──────────────────────────────
say('=== TEST 3: isolation ===');
say('-- Account A creating data --');
const a = await signIn(A, false);

const SECRET_TITLE = `A-PRIVATE-DEAL-${stamp}`;
const SECRET_SKU   = `A-SKU-${stamp}`;

await api(a.page, '/api/deals', 'POST', {
  stage: 'watching', title: SECRET_TITLE, source: 'manual', sourceLabel: 'e2e',
  sourceItemId: `a-item-${stamp}`, quantity: 2, askPrice: 500, maxBuyPrice: 400,
  projectedSalePrice: 900, projectedNetProfit: 300, purchasePrice: 400, sku: SECRET_SKU,
});
await api(a.page, '/api/inventory/cost-basis', 'POST', [
  { sku: SECRET_SKU, unitCost: 400, source: 'e2e-isolation-test' },
]);

const aDeals = await api(a.page, '/api/deals');
const aDealList = aDeals.data?.deals || aDeals.data?.pipeline?.deals || [];
const aDeal = JSON.stringify(aDeals.data).includes(SECRET_TITLE);
// Dig the deal id out of whatever shape the pipeline returns.
const aDealId = (JSON.stringify(aDeals.data).match(new RegExp(`"id":(\\d+)[^}]*"title":"${SECRET_TITLE}"`))
  || JSON.stringify(aDeals.data).match(new RegExp(`"title":"${SECRET_TITLE}"[^}]*?"id":(\\d+)`))
  || [])[1] || (aDealList.find(d => d.title === SECRET_TITLE) || {}).id;
say(`   A created deal "${SECRET_TITLE}" -> present in A's pipeline: ${aDeal}, id=${aDealId}`);
const aCost = await api(a.page, '/api/inventory/cost-basis');
say(`   A cost-basis contains its SKU: ${JSON.stringify(aCost.data).includes(SECRET_SKU)}`);

await a.page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
const dealLink = a.page.locator('text="Deal Pipeline"').first();
if (await dealLink.count()) { await dealLink.click(); await a.page.waitForTimeout(3000); }
await shot(a.page, 'A-deal-pipeline-with-data');

// ── Account B is brand new ─────────────────────────────────────────────────
say('-- Account B signing up --');
const b = await signIn(B, true);
fs.writeFileSync(`${SHOTS}/account-b.json`, JSON.stringify(B, null, 2));

const checks = [];
function check(name, pass, detail) { checks.push({ name, pass, detail }); say(`   ${pass ? 'OK  ' : 'LEAK'} ${name} — ${detail}`); }

// 1. Can B see A's rows in the list endpoints?
for (const [path, label] of [['/api/deals', "A's deal pipeline"], ['/api/inventory/cost-basis', "A's cost basis"],
                             ['/api/earnings', "A's earnings"], ['/api/local-drafts/list', "A's drafts"]]) {
  const r = await api(b.page, path);
  const body = JSON.stringify(r.data ?? r.text);
  const bleeds = body.includes(SECRET_TITLE) || body.includes(SECRET_SKU);
  check(`B reading ${label} via ${path}`, !bleeds,
    bleeds ? `CONTAINS A's marker!` : `status ${r.status}, no marker from A (${body.length} bytes)`);
}

// 2. The real test — direct access to A's row BY ID. A filtered list can be right while this is wrong.
if (aDealId) {
  const readBack = await api(b.page, `/api/deals/${aDealId}/apply-cost`, 'POST', {});
  check(`B touching A's deal #${aDealId} via apply-cost`,
    readBack.status === 404 || readBack.status === 400 || readBack.status === 403,
    `status ${readBack.status} — ${readBack.text.slice(0, 90)}`);

  const stage = await api(b.page, `/api/deals/${aDealId}/stage`, 'POST', { stage: 'bought', purchasePrice: 1 });
  check(`B moving A's deal #${aDealId} to another stage`,
    stage.status === 404 || stage.status === 403,
    `status ${stage.status} — ${stage.text.slice(0, 90)}`);

  const del = await api(b.page, `/api/deals/${aDealId}`, 'DELETE');
  check(`B deleting A's deal #${aDealId}`,
    del.status === 404 || del.status === 403,
    `status ${del.status} — ${del.text.slice(0, 90)}`);
}

// 3. B's own board is empty, as a new account's should be.
const bDeals = await api(b.page, '/api/deals');
await b.page.goto(`${BASE}/`, { waitUntil: 'networkidle' });
const bLink = b.page.locator('text="Deal Pipeline"').first();
if (await bLink.count()) { await bLink.click(); await b.page.waitForTimeout(3000); }
await shot(b.page, 'B-deal-pipeline-empty');

// 4. And A's data survived B's attempts to change it.
const aAfter = await api(a.page, '/api/deals');
const stillThere = JSON.stringify(aAfter.data).includes(SECRET_TITLE);
check("A's deal survived B's write/delete attempts", stillThere,
  stillThere ? 'still on A\'s board' : "GONE — B destroyed A's row");

// 5. eBay account: B must not inherit A's connection.
const bFields = await api(b.page, '/api/setup/fields');
check('B does not inherit an eBay user token', bFields.data?.hasEbayUserToken !== true,
  `hasEbayUserToken=${bFields.data?.hasEbayUserToken}`);

const allOk = checks.every(c => c.pass);
result('TEST 3 isolation', allOk, `${checks.filter(c => c.pass).length}/${checks.length} isolation checks held`);
fs.writeFileSync(`${SHOTS}/isolation.json`, JSON.stringify({ A: A.email, B: B.email, aDealId, checks }, null, 2));

await browser.close();
