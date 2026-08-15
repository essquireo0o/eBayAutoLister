import { chromium, BASE, shot, say, result } from './lib.mjs';

// A completely fresh browser context: no cookie, no storage, nothing.
const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await ctx.newPage();
await page.goto(`${BASE}/signin.html`, { waitUntil: 'domcontentloaded' });

const targets = [
  ['GET',  '/api/auth/me',            'who am I'],
  ['GET',  '/api/earnings',           'MONEY — earnings/profit'],
  ['GET',  '/api/tax',                'MONEY — tax pack'],
  ['GET',  '/api/inventory/cost-basis','MONEY — cost basis'],
  ['GET',  '/api/store-plan',         'MONEY — store plan'],
  ['GET',  '/api/deals',              'deal pipeline'],
  ['GET',  '/api/ebay/listings',      "SETTINGS/DATA — seller's eBay listings"],
  ['GET',  '/api/ebay/auth-url',      'SETTINGS — eBay OAuth url (app identity)'],
  ['GET',  '/api/ai-quota',           'AI allowance'],
  ['GET',  '/api/owner/stats',        'owner dashboard stats'],
  ['GET',  '/api/local-drafts/list',  'saved drafts'],
  ['POST', '/api/analyze',            "AI spend on owner's key"],
  ['POST', '/api/deals',              'write a deal'],
];

const rows = [];
for (const [method, path, what] of targets) {
  const r = await page.evaluate(async ([m, p]) => {
    const res = await fetch(p, {
      method: m,
      headers: m === 'POST' ? { 'Content-Type': 'application/json' } : {},
      body: m === 'POST' ? '{}' : undefined,
      redirect: 'manual',
    });
    let body = '';
    try { body = (await res.text()).slice(0, 160); } catch {}
    return { status: res.status, type: res.type, auth: res.headers.get('x-auth-required'), body };
  }, [method, path]);
  const refused = r.status === 401 || r.status === 403 || r.status === 0;
  rows.push({ method, path, what, ...r, refused });
  say(`${refused ? 'refused ' : 'SERVED  '} ${method.padEnd(4)} ${path.padEnd(28)} -> ${r.status} ${r.auth ? '(X-Auth-Required)' : ''} ${refused ? '' : '  BODY: ' + r.body}`);
}

// Also the protected static photo mount, which static-file middleware serves before routing.
const photo = await page.evaluate(async () => {
  const res = await fetch('/photos/', { redirect: 'manual' });
  return { status: res.status, type: res.type };
});
say(`photos mount /photos/ -> ${photo.status} (type ${photo.type})`);

const allRefused = rows.every(r => r.refused);
result('TEST 5 unauthenticated access', allRefused,
  `${rows.filter(r => r.refused).length}/${rows.length} endpoints refused with 401/403`);

// Evidence on screen: render the table in the page and shoot it.
await page.evaluate((data) => {
  document.body.innerHTML = `<h2 style="font:600 20px system-ui;padding:16px">Unauthenticated API probe — https://app.inglisting.com</h2>
  <table style="font:14px ui-monospace,monospace;border-collapse:collapse;margin:12px">
  <tr style="background:#eee"><th style="padding:6px 10px;text-align:left">method</th><th style="padding:6px 10px;text-align:left">endpoint</th><th style="padding:6px 10px;text-align:left">what it returns</th><th style="padding:6px 10px">status</th><th style="padding:6px 10px">result</th></tr>
  ${data.map(r => `<tr><td style="padding:6px 10px">${r.method}</td><td style="padding:6px 10px">${r.path}</td><td style="padding:6px 10px;font-family:system-ui">${r.what}</td><td style="padding:6px 10px;text-align:center">${r.status}</td><td style="padding:6px 10px;text-align:center;color:${r.refused ? 'green' : 'red'};font-weight:700">${r.refused ? 'REFUSED' : 'SERVED'}</td></tr>`).join('')}
  </table>`;
}, rows);
await shot(page, 'unauth-api-probe');

await browser.close();
