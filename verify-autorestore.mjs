// Live check of the AI Listing auto-restore, run against app.inglisting.com from off the box.
//
// The claim under test is the seller's, not the database's: fill a listing, leave, close the tab,
// come back — the fields are there, and coming back twice shows one draft rather than two.
//
// Playwright is installed globally on this workstation, so it is imported by path rather than
// resolved from a node_modules beside this file.
import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';

const HOST = 'https://app.inglisting.com';
const stamp = Date.now();
const EMAIL = `throwaway-autorestore-${stamp}@example.com`;
const PASSWORD = 'correct horse battery staple 2026';

const TITLE = `Antminer S19 Pro 110TH — auto-restore check ${stamp}`;
const PRICE = '1899.00';
const BRAND = 'Bitmain';

const results = [];
function check(name, passed, detail = '') {
  results.push({ name, passed, detail });
  console.log(`${passed ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
}

async function openAiListing(page) {
  await page.goto(`${HOST}/`, { waitUntil: 'domcontentloaded' });
  // The dashboard's own load has to finish first: auto-restore waits on the local-mirror replay,
  // which init kicks off at the end.
  await page.waitForSelector('.nav-item[data-page="ai"]', { timeout: 30000 });
  await page.click('.nav-item[data-page="ai"]');
  await page.waitForSelector('#new-listing-overlay:not(.hidden)', { timeout: 15000 });
}

const browser = await chromium.launch();
const context = await browser.newContext();

try {
  // ── Sign up and sign in ──────────────────────────────────────────────────
  {
    const page = await context.newPage();
    await page.goto(`${HOST}/signup.html`, { waitUntil: 'domcontentloaded' });
    await page.fill('#signup-name', 'Auto Restore Check');
    await page.fill('#signup-email', EMAIL);
    await page.fill('#signup-password', PASSWORD);
    await page.fill('#signup-confirm', PASSWORD);
    await page.click('#signup-submit');
    await page.waitForURL(/signin\.html/, { timeout: 30000 });

    await page.fill('#signin-email', EMAIL);
    await page.fill('#signin-password', PASSWORD);
    await page.click('#signin-submit');
    await page.waitForURL(u => !/sign(in|up)\.html/.test(u.toString()), { timeout: 30000 });
    check('signed in as a fresh account', true, EMAIL);
    await page.close();
  }

  // ── 1. Fill an AI listing, leave the screen, close the tab ───────────────
  {
    const page = await context.newPage();
    await openAiListing(page);

    // A blank account has nothing to restore, so this open must NOT fill anything in.
    await page.waitForTimeout(4000);
    const titleOnFirstOpen = await page.inputValue('#nl-title');
    check('a first-ever open stays blank', titleOnFirstOpen === '', `title="${titleOnFirstOpen}"`);

    await page.fill('#nl-title', TITLE);
    await page.fill('#nl-price', PRICE);
    // Brand sits behind a collapsed panel, so it is set the way the app itself would see it —
    // value plus the input event autosave listens for — rather than by clicking through to it.
    await page.evaluate(b => {
      const el = document.getElementById('nl-brand');
      el.value = b;
      el.dispatchEvent(new Event('input', { bubbles: true }));
    }, BRAND);

    // Autosave is debounced at 2.5s; the line beside the tabs is what says the app has it.
    await page.waitForFunction(
      () => document.getElementById('nl-save-state')?.textContent.trim() === 'Saved',
      { timeout: 30000 });
    check('the app confirms it stored the listing', true, 'nl-save-state = "Saved"');

    // Leave the screen, then close the tab — the seller's actual sequence.
    await page.click('.nav-item[data-page="dashboard"]');
    await page.waitForTimeout(1000);
    await page.close({ runBeforeUnload: true });
    await new Promise(r => setTimeout(r, 2500));
  }

  // ── 2. Reopen in a new tab: the fields are still there ───────────────────
  let firstReopenTitle = '';
  {
    const page = await context.newPage();
    await openAiListing(page);
    await page.waitForFunction(
      expected => document.getElementById('nl-title')?.value === expected,
      TITLE, { timeout: 30000 }).catch(() => {});

    firstReopenTitle = await page.inputValue('#nl-title');
    const price = await page.inputValue('#nl-price');
    const brand = await page.inputValue('#nl-brand');

    check('reopening restores the title', firstReopenTitle === TITLE, `"${firstReopenTitle}"`);
    check('reopening restores the price', Number(price) === Number(PRICE), `"${price}"`);
    check('reopening restores the brand', brand === BRAND, `"${brand}"`);
    await page.close({ runBeforeUnload: true });
    await new Promise(r => setTimeout(r, 2000));
  }

  // ── 3. Open it again: the SAME draft, and only one of it ─────────────────
  {
    const page = await context.newPage();
    await openAiListing(page);
    await page.waitForFunction(
      expected => document.getElementById('nl-title')?.value === expected,
      TITLE, { timeout: 30000 }).catch(() => {});

    const title = await page.inputValue('#nl-title');
    check('opening a third time shows the same draft, not a blank form', title === TITLE, `"${title}"`);

    // One row, not one per open. This is what adopting the restored key buys.
    const recoverable = await page.evaluate(async () => {
      const r = await fetch('/api/work/recoverable');
      return (await r.json()).items || [];
    });
    check('one draft, not two', recoverable.length === 1,
      `${recoverable.length} recoverable row(s): ${recoverable.map(i => i.key).join(', ')}`);
    check('the restored draft kept its original row',
      recoverable.length === 1 && recoverable[0].label === TITLE,
      recoverable[0]?.label ?? '(none)');

    // ── 4. A form already typed into is never clobbered ────────────────────
    const TYPED = `Something the seller is typing right now ${stamp}`;
    await page.evaluate(t => {
      // Straight into the field, then open the screen again: the restore must leave this alone.
      document.getElementById('nl-title').value = t;
    }, TYPED);
    await page.click('.nav-item[data-page="dashboard"]');
    await page.click('.nav-item[data-page="ai"]');
    await page.waitForTimeout(5000);
    const afterReopen = await page.inputValue('#nl-title');
    check('a form with typing in it is left alone', afterReopen === TYPED, `"${afterReopen}"`);

    await page.close({ runBeforeUnload: true });
  }

  // ── 5. Failed / unknown publishes are NOT auto-restored ──────────────────
  {
    const page = await context.newPage();
    await page.goto(`${HOST}/`, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('.nav-item[data-page="ai"]', { timeout: 30000 });

    // Let the page's own load settle — including the local-mirror replay, which re-uploads whatever
    // this device is still holding and would otherwise put the ordinary draft back mid-check — then
    // drop the mirror so it cannot do it again.
    await page.waitForTimeout(6000);
    await page.evaluate(() => localStorage.removeItem('ing-autolister.wip.v1'));

    // Discard the ordinary draft, then leave only a failed publish behind. The key is per run:
    // it is the table's primary key across every account, so a literal would collide with the row
    // an earlier run left under a different throwaway user.
    const FAILED_KEY = `wip-failed-check-${stamp}`;
    const state = await page.evaluate(async (FAILED_KEY) => {
      const list = await (await fetch('/api/work/recoverable')).json();
      for (const item of list.items || []) {
        await fetch('/api/work/discard', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ key: item.key }),
        });
      }
      // A draft that then fails to publish: saved, and marked failed by the publish path.
      const wrote = await fetch('/api/work/autosave', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          key: FAILED_KEY, label: 'A publish that failed', stage: 'failed',
          payload: JSON.stringify({ title: 'A publish that failed', price: 42 }),
        }),
      });
      const wroteBody = await wrote.text();
      const after = await (await fetch('/api/work/recoverable')).json();
      const resume = await (await fetch('/api/work/resume')).json();
      return {
        recoverable: after.items || [], resume: resume.draft,
        wrote: `${wrote.status} ${wroteBody.slice(0, 200)}`,
      };
    }, FAILED_KEY);

    check('a failed publish is still offered under Recover',
      state.recoverable.some(i => i.key === FAILED_KEY),
      `${state.recoverable.length} row(s); autosave answered ${state.wrote}`);
    check('a failed publish is NOT auto-restored', state.resume === null,
      state.resume ? `resume returned ${state.resume.key}` : 'resume returned null');
    await page.close();
  }
} finally {
  await browser.close();
}

const failed = results.filter(r => !r.passed);
console.log(`\n${results.length - failed.length}/${results.length} checks passed`);
console.log(`throwaway account: ${EMAIL}`);
process.exit(failed.length ? 1 : 0);
