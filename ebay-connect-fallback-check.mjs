// The two branches the happy path never exercises, against the live site.
//
//   1. A pop-up blocker refuses the window. window.open is stubbed to return null — which is
//      exactly what a blocker does — and the app must SAY so and hand over a link, not fail quietly.
//   2. The grant finishing. A window is opened on /?ebay_connected=1, which is where eBay's
//      callback redirects to; it must report to its opener and close itself, and the opener must
//      go to Connected without anybody reloading anything.

import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';

const [email, password] = process.argv.slice(2);
const HOST = 'https://app.inglisting.com';

const browser = await chromium.launch();
const context = await browser.newContext();

async function signIn(page) {
  await page.goto(`${HOST}/signin.html`, { waitUntil: 'domcontentloaded' });
  await page.evaluate(async ([e, p]) => {
    await fetch('/api/auth/sign-in', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: e, password: p }),
    });
  }, [email, password]);
}

// ── 1. Blocked pop-up ────────────────────────────────────────────────────────────────────────
{
  const page = await context.newPage();
  await signIn(page);
  // Before any of the app's script runs, so the click handler sees the blocked window, not a real one.
  await page.addInitScript(() => { window.open = () => null; });
  await page.goto(`${HOST}/`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(3000);

  await page.click('#btn-connect');
  await page.waitForTimeout(4000);

  console.log('=== 1. pop-up blocked ===');
  console.log('pages open        :', context.pages().length);
  console.log('original still on :', page.url());
  console.log('message           :', (await page.locator('#result').innerText().catch(() => '(none)')).replace(/\s+/g, ' '));
  const link = page.locator('#result a');
  console.log('link offered      :', await link.count() ? (await link.getAttribute('href')).slice(0, 60) + '…' : 'NONE');
  await page.screenshot({ path: 'ebay_connect_blocked.png' });
  await page.close();
}

// ── 2. The grant finishing ───────────────────────────────────────────────────────────────────
{
  const page = await context.newPage();
  await signIn(page);
  await page.goto(`${HOST}/`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(3000);

  // Listen the way the app does, so this proves the message is posted and is same-origin.
  await page.evaluate(() => {
    window.__ebayMessages = [];
    window.addEventListener('message', (e) => {
      if (e.origin === location.origin) window.__ebayMessages.push(e.data);
    });
  });

  const opened = context.waitForEvent('page', { timeout: 15000 });
  await page.evaluate(() => window.open('/?ebay_connected=1', 'ing-ebay-signin'));
  const callbackTab = await opened;
  await page.waitForTimeout(5000);

  console.log('\n=== 2. the callback tab reports back ===');
  console.log('messages the opener got :', JSON.stringify(await page.evaluate(() => window.__ebayMessages)));
  console.log('callback tab closed     :', callbackTab.isClosed());
  if (!callbackTab.isClosed()) {
    console.log('callback tab says       :',
      ((await callbackTab.locator('body').innerText().catch(() => '')) || '').replace(/\s+/g, ' ').slice(0, 200));
  }
  console.log('original still on       :', page.url());
  await page.close();
}

await browser.close();
