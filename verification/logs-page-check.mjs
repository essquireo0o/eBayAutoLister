// Drives the Logs screen in a real browser: the level filter, the "something went wrong" verdict,
// the row styling for warnings and errors, and "Copy for support" — including that what lands on
// the clipboard has every credential-shaped value replaced.
//
// It talks to the running desktop app on localhost:9332 but changes nothing in it: every request
// that is not a GET is refused before it leaves the browser, the two requests that would make the
// app call eBay are answered here instead, and index.html / app.js / style.css are served from
// the WORKING TREE, so the screen under test is the source in this repo and not whatever build
// happens to be installed. Log entries come from a fixture (made-up values in the
// shapes real credentials take), then one final pass reads the app's real log.
//
//   node verification/logs-page-check.mjs        exit 0 = every check passed
import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';
import { readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const wwwroot = path.join(here, '..', 'ING eBay AutoLister', 'wwwroot');
const shots = path.join(here, 'logs-page');
mkdirSync(shots, { recursive: true });
const BASE = 'http://localhost:9332';

// ── Fixture ────────────────────────────────────────────────────────────────
// None of these are real. Each is the SHAPE of something that must never reach a support e-mail.
// They are glued together at run time on purpose: written out whole, a secret scanner reading this
// repository cannot tell a made-up key from a leaked one, and would be right to stop the push.
const glue = (...parts) => parts.join('');
const SECRETS = {
  ebayToken:   glue('v^1.1', '#i^1#r^0#p^3#I^3#f^0#t^', 'H4sIAAAAAAAAAOVYa2wUVRTutkMJkU1NN'),
  bearerPlain: glue('AgAAAA7xKqJm', 'Pz2LwYh8TnVbR4cD'),
  anthropic:   glue('sk-', 'ant-', 'api03-', 'Zk3mQ9vXw2LpR7tYb5Nc', 'H8dFg1JsKa4E'),
  jwt:         glue('eyJ', 'hbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9', '.', 'eyJ', 'zdWIiOiIxMjM0NTY3ODkwIn0', '.', 'SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV'),
  appId:       glue('INGMinin-AutoList-', 'PRD-', '1a2b3c4d5-6e7f8a9b'),
  certId:      glue('PRD-', '1a2b3c4d5e6f-7a8b-4c9d-8e7f-1a2b'),
  botToken:    glue('7412589630', ':', 'AAF8kq2Zr5vLw9XnT3', 'mPyB6cJdH1sGuEo4I'),
  password:    glue('hunter2-', 'Correct-Horse'),
  oauthCode:   glue('v%5E1.1', '%23i%5E1%23f%5E0%23p%5E3'),
  bareHex:     glue('9f86d081884c7d659a2feaa0c55ad015', 'a3bf4f1b2b0b822cd15d6c15b0f00a08'),
  refresh:     glue('rt_', 'Zx9KpL2mQw8'),
};
// …and these must come through untouched, or the copy is useless to whoever reads it.
const KEEP = [
  'eBay picture upload failed', 'Production OAuth exchange failed', 'item 123456789012',
  'panasonic-toughbook-cf-31-mk5-i5-8gb-256gb-ssd-win10.json', 'https://www.ebay.com/itm/123456789012',
  'invalid_client', 'HTTP 401', '25 priced/0 profitable', 'max_tokens: 4096',
];

const now = Date.now();
const at = minutesAgo => new Date(now - minutesAgo * 60000).toISOString();
const FIXTURE = [
  { timestamp: at(1), level: 'Error', title: 'Production OAuth exchange failed',
    detail: `HTTP 401 {"error":"invalid_client","client_id":"${SECRETS.appId}","client_secret":"${SECRETS.certId}"} from https://api.ebay.com/identity/v1/oauth2/token?code=${SECRETS.oauthCode}&grant=x` },
  { timestamp: at(2), level: 'Warning', title: 'eBay picture upload failed',
    detail: `HTTP 401 for item 123456789012. Request had Authorization: Bearer ${SECRETS.ebayToken}` },
  { timestamp: at(3), level: 'Info', title: 'Draft saved locally',
    detail: 'panasonic-toughbook-cf-31-mk5-i5-8gb-256gb-ssd-win10.json https://www.ebay.com/itm/123456789012' },
  { timestamp: at(5), level: 'Error', title: 'AI analysis failed',
    detail: `x-api-key: ${SECRETS.anthropic} was rejected; max_tokens: 4096; session ${SECRETS.jwt}` },
  { timestamp: at(8), level: 'Research', title: 'Local arbitrage scan',
    detail: '"miner" within 40 mi: 666 found/25 priced/0 profitable' },
  { timestamp: at(9), level: 'Warning', title: 'Telegram alert not sent',
    detail: `POST https://api.telegram.org/bot${SECRETS.botToken}/sendMessage refused; password=${SECRETS.password}; Authorization: Basic ${SECRETS.bearerPlain}` },
  { timestamp: at(12), level: 'Negotiation', title: 'Offer drafted', detail: 'Counter at $329.99' },
  { timestamp: at(15), level: 'Error', title: 'Token refresh failed',
    detail: `refresh_token=${SECRETS.refresh} signature ${SECRETS.bareHex}` },
  { timestamp: at(60 * 26), level: 'Info', title: 'ING Listing Engine™ started', detail: 'Official product of ING Mining LLC — ready.' },
];
const CLEAN_FIXTURE = FIXTURE.filter(e => !['Error', 'Warning'].includes(e.level));

// ── Harness ────────────────────────────────────────────────────────────────
const results = [];
const check = (name, ok, detail = '') => {
  results.push({ name, ok: !!ok, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
};

const browser = await chromium.launch({ headless: true });
const pageErrors = [];
let refused = 0;

/** A page on the running app with this repo's UI files, read-only, and the given log behaviour. */
async function open({ logs, viewport = { width: 1400, height: 1000 }, clipboard = true, theme } = {}) {
  const context = await browser.newContext({
    viewport,
    permissions: clipboard ? ['clipboard-read', 'clipboard-write'] : [],
  });
  if (!clipboard) {
    // The browser that says no: neither the modern nor the old way to copy works.
    await context.addInitScript(() => {
      Object.defineProperty(navigator, 'clipboard', {
        value: { writeText: () => Promise.reject(new Error('NotAllowedError')) }, configurable: true,
      });
      document.execCommand = () => false;
    });
  }
  if (theme) await context.addInitScript(t => { try { localStorage.setItem('theme', t); } catch {} }, theme);

  const page = await context.newPage();
  page.on('pageerror', e => pageErrors.push(e.message));

  await page.route('**/*', route => {
    const req = route.request();
    const url = new URL(req.url());
    if (req.method() !== 'GET') { refused++; return route.abort(); }   // never write to the owner's app
    if (url.origin !== BASE) return route.continue();
    const file = { '/': 'index.html', '/index.html': 'index.html', '/app.js': 'app.js', '/style.css': 'style.css' }[url.pathname];
    if (file) {
      const type = file.endsWith('.html') ? 'text/html' : file.endsWith('.js') ? 'text/javascript' : 'text/css';
      return route.fulfill({ status: 200, contentType: `${type}; charset=utf-8`, body: readFileSync(path.join(wwwroot, file)) });
    }
    // Opening the app imports the seller's listings and policies from eBay, and writes a dozen
    // lines to the very log under test. Neither belongs in a check of the Logs screen.
    if (url.pathname === '/api/ebay/listings') return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    if (url.pathname === '/api/ebay/policies') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{"fulfillmentPolicies":[],"paymentPolicies":[],"returnPolicies":[]}' });
    }
    if (url.pathname === '/api/logs/recent' && logs !== 'live') {
      if (logs === 'broken') return route.fulfill({ status: 500, contentType: 'text/plain', body: 'boom' });
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(logs) });
    }
    return route.continue();
  });

  await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForSelector('.nav-item[data-page="logs"]', { timeout: 30000 });
  await page.waitForTimeout(1200);
  await page.click('.nav-item[data-page="logs"]');
  await page.waitForSelector('#logs-section:not(.hidden)', { timeout: 10000 });
  await page.waitForFunction(() => !document.querySelector('#logs-list .skeleton-row'), null, { timeout: 15000 });
  await page.waitForTimeout(300);
  return { page, context };
}

const state = page => page.evaluate(() => {
  const NEW_CONTROLS = '.logs-actions, .logs-actions *, .logs-summary, .logs-summary *, .logs-toolbar, .logs-toolbar *, .logs-copy-note, .logs-copy-fallback:not(.hidden), .logs-copy-fallback:not(.hidden) *';
  const rows = [...document.querySelectorAll('#logs-list .log-row')];
  const style = el => (el ? getComputedStyle(el) : null);
  const first = kind => document.querySelector(`#logs-list .log-row--${kind}`);
  const summary = document.getElementById('logs-summary');
  return {
    kinds: rows.map(r => r.dataset.logKind),
    counts: Object.fromEntries([...document.querySelectorAll('#logs-filter [data-log-filter]')]
      .map(b => [b.dataset.logFilter, b.querySelector('.logs-filter-count').textContent.trim()])),
    pressed: [...document.querySelectorAll('#logs-filter [aria-pressed="true"]')].map(b => b.dataset.logFilter),
    summaryClass: summary.className,
    summaryText: summary.textContent.replace(/\s+/g, ' ').trim(),
    summaryButton: summary.querySelector('button')?.textContent.trim() ?? null,
    bg: { info: style(first('info'))?.backgroundColor, warning: style(first('warning'))?.backgroundColor, error: style(first('error'))?.backgroundColor },
    bar: { info: style(first('info'))?.boxShadow, warning: style(first('warning'))?.boxShadow, error: style(first('error'))?.boxShadow },
    pill: { warning: style(first('warning')?.querySelector('.log-level'))?.backgroundColor, error: style(first('error')?.querySelector('.log-level'))?.backgroundColor },
    stateTitle: document.querySelector('#logs-list .state-title')?.textContent.trim() ?? null,
    filterHeight: Math.round(document.querySelector('.logs-filter-btn').getBoundingClientRect().height),
    copyVisible: (() => { const r = document.getElementById('btn-copy-logs').getBoundingClientRect(); return r.width > 0 && r.height > 0; })(),
    oldestTime: rows.at(-1)?.querySelector('small')?.textContent ?? '',
    fallbackHidden: document.getElementById('logs-copy-fallback-wrap').classList.contains('hidden'),
    // How far any of the new controls sticks out past the Logs panel's own right edge.
    spill: (() => {
      const section = document.getElementById('logs-section');
      const edge = section.getBoundingClientRect().right;
      return Math.max(0, ...[...section.querySelectorAll(NEW_CONTROLS)].map(e => Math.round(e.getBoundingClientRect().right - edge)));
    })(),
    spillers: (() => {
      const section = document.getElementById('logs-section');
      const edge = section.getBoundingClientRect().right;
      return [...section.querySelectorAll(NEW_CONTROLS)].filter(e => e.getBoundingClientRect().width > 0 && e.getBoundingClientRect().right - edge > 1)
        .map(e => `${e.tagName.toLowerCase()}${e.id ? `#${e.id}` : ''}.${String(e.className).split(' ')[0]}`).slice(0, 6).join(', ');
    })(),
  };
});
const shot = async (page, name) => {
  await page.evaluate(() => document.getElementById('logs-section').scrollIntoView({ block: 'start', behavior: 'instant' }));
  await page.waitForTimeout(250);
  await page.screenshot({ path: path.join(shots, name) });
};
const filter = async (page, name) => { await page.click(`#logs-filter [data-log-filter="${name}"]`); await page.waitForTimeout(150); };

// ── 1. A log with trouble in it ────────────────────────────────────────────
{
  const { page, context } = await open({ logs: FIXTURE });
  let s = await state(page);

  check('all 9 entries are listed', s.kinds.length === 9, s.kinds.join(','));
  check('filter counts: 9 / 3 errors / 2 warnings / 4 normal',
    s.counts.all === '9' && s.counts.error === '3' && s.counts.warning === '2' && s.counts.info === '4', JSON.stringify(s.counts));
  check('Research and Negotiation count as normal activity, not trouble', s.kinds.filter(k => k === 'info').length === 4);
  check('verdict line is the error kind and names both counts',
    s.summaryClass.includes('logs-summary--error') && s.summaryText.includes('3 errors and 2 warnings'), s.summaryText);
  check('verdict offers "Show only errors"', s.summaryButton === 'Show only errors', String(s.summaryButton));
  check('an error row has its own background', s.bg.error && s.bg.error !== s.bg.info, `${s.bg.error} vs ${s.bg.info}`);
  check('a warning row has its own background', s.bg.warning && s.bg.warning !== s.bg.info && s.bg.warning !== s.bg.error, s.bg.warning);
  check('error and warning rows carry a coloured bar; normal rows do not',
    /inset/.test(s.bar.error) && /inset/.test(s.bar.warning) && !/inset/.test(s.bar.info), `${s.bar.error} | ${s.bar.info}`);
  check('the level pill is a solid colour on trouble rows', s.pill.error !== s.pill.warning && s.pill.error !== 'rgba(0, 0, 0, 0)', `${s.pill.error} / ${s.pill.warning}`);
  check('an entry from yesterday shows its date, not just a time', /[A-Za-z]{3}/.test(s.oldestTime), s.oldestTime);
  check('filter buttons are at least 44px tall', s.filterHeight >= 44, `${s.filterHeight}px`);
  check('Copy for support is on screen without opening anything', s.copyVisible);
  await shot(page, 'logs_everything.png');

  await filter(page, 'error');
  s = await state(page);
  check('Errors shows only the 3 errors', s.kinds.length === 3 && s.kinds.every(k => k === 'error'), s.kinds.join(','));
  check('Errors is the one pressed button', s.pressed.join() === 'error', s.pressed.join());
  check('verdict stops offering the filter that is already on', s.summaryButton === null, String(s.summaryButton));
  await shot(page, 'logs_errors_only.png');

  // Copy while filtered: only what is on screen goes out.
  await page.click('#btn-copy-logs');
  await page.waitForTimeout(400);
  let clip = await page.evaluate(() => navigator.clipboard.readText());
  check('filtered copy holds the 3 errors and nothing else',
    (clip.match(/^\[ERROR\]/gm) || []).length === 3 && !/^\[(WARNING|INFO|RESEARCH)\]/m.test(clip));
  check('filtered copy says what it is showing', clip.includes('Showing: Errors (3 of 9 in the log)'), clip.split('\n')[2]);

  await filter(page, 'warning');
  s = await state(page);
  check('Warnings shows only the 2 warnings', s.kinds.length === 2 && s.kinds.every(k => k === 'warning'), s.kinds.join(','));
  await filter(page, 'info');
  s = await state(page);
  check('Normal activity shows the other 4', s.kinds.length === 4 && s.kinds.every(k => k === 'info'), s.kinds.join(','));
  await page.click('#logs-summary button');
  await page.waitForTimeout(150);
  s = await state(page);
  check('"Show only errors" in the verdict switches the filter', s.pressed.join() === 'error' && s.kinds.length === 3);
  await filter(page, 'all');
  s = await state(page);
  check('Everything brings all 9 back', s.kinds.length === 9);

  // The whole log, to the clipboard.
  await page.click('#btn-copy-logs');
  await page.waitForTimeout(400);
  clip = await page.evaluate(() => navigator.clipboard.readText());
  const leaked = Object.entries(SECRETS).filter(([, v]) => clip.includes(v)).map(([k]) => k);
  check('NO credential survives the copy', leaked.length === 0, leaked.length ? `LEAKED: ${leaked.join(', ')}` : `${Object.keys(SECRETS).length} shapes checked`);
  // Belt and braces: no fragment of one either (the last 12 characters of each).
  const fragments = Object.entries(SECRETS).filter(([, v]) => clip.includes(v.slice(-12))).map(([k]) => k);
  check('no tail of a credential survives either', fragments.length === 0, fragments.join(', '));
  check('the Telegram bot id goes with its token', !clip.includes('7412589630'));
  const lost = KEEP.filter(k => !clip.includes(k));
  check('everything support needs is still readable', lost.length === 0, lost.length ? `LOST: ${lost.join(' | ')}` : `${KEEP.length} phrases kept`);
  check('copy has all 9 entries', (clip.match(/^\[[A-Z]+\] /gm) || []).length === 9);
  check('copy starts with what it is and says secrets were hidden',
    clip.startsWith('ING Listing Engine - log for support') && clip.includes('were replaced with [hidden]') && clip.includes('whole log: 3 errors, 2 warnings'));
  check('copy carries the app version', /app version \d+\.\d+/.test(clip), clip.split('\n')[1]);
  const toast = await page.evaluate(() => [...document.querySelectorAll('.toast')].at(-1)?.textContent.replace(/\s+/g, ' ').trim() ?? '');
  check('the seller is told it was copied and how many values were hidden',
    /9 log entries copied/.test(toast) && /\d+ password, token or key values were hidden first/.test(toast), toast.slice(0, 160));
  check('button confirms', (await page.textContent('#btn-copy-logs')).includes('Copied'));
  console.log('\n----- what support receives -----\n' + clip + '---------------------------------\n');

  // Narrow window: nothing runs off the side.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.waitForTimeout(300);
  s = await state(page);
  // (The app shell as a whole is wider than a 390px window — true before this change too. What is
  // checked here is that the new controls wrap inside the Logs panel instead of adding to that.)
  check('at 390px wide the filter and buttons wrap inside the Logs panel', s.spill <= 1, `${s.spill}px past the panel edge${s.spillers ? `: ${s.spillers}` : ''}`);
  await page.setViewportSize({ width: 820, height: 900 });
  await page.waitForTimeout(300);
  s = await state(page);
  check('at 820px wide (small laptop, tablet) they fit too', s.spill <= 1, `${s.spill}px past the panel edge${s.spillers ? `: ${s.spillers}` : ''}`);
  await shot(page, 'logs_narrow_820.png');
  await context.close();
}

// ── 2. A clean log ─────────────────────────────────────────────────────────
{
  const { page, context } = await open({ logs: CLEAN_FIXTURE });
  let s = await state(page);
  check('clean log: verdict says nothing has gone wrong', s.summaryClass.includes('logs-summary--ok') && s.summaryText.includes('Nothing has gone wrong'), s.summaryText);
  await shot(page, 'logs_clean.png');
  await filter(page, 'error');
  s = await state(page);
  check('clean log: Errors reads "No errors"', s.stateTitle === 'No errors' && s.kinds.length === 0, String(s.stateTitle));
  await page.click('#btn-copy-logs');
  await page.waitForTimeout(300);
  const toast = await page.evaluate(() => [...document.querySelectorAll('.toast')].at(-1)?.textContent.replace(/\s+/g, ' ').trim() ?? '');
  check('copying an empty filter explains itself instead of copying nothing', /no errors to copy/i.test(toast), toast.slice(0, 120));
  await page.click('#logs-list [data-state-action="all"]');
  await page.waitForTimeout(150);
  s = await state(page);
  check('"Show everything" brings the rows back', s.kinds.length === CLEAN_FIXTURE.length && s.pressed.join() === 'all');
  await context.close();
}

// ── 3. A browser that will not copy ────────────────────────────────────────
{
  const { page, context } = await open({ logs: FIXTURE, clipboard: false });
  await page.click('#btn-copy-logs');
  await page.waitForTimeout(400);
  const f = await page.evaluate(() => {
    const box = document.getElementById('logs-copy-fallback');
    return {
      hidden: document.getElementById('logs-copy-fallback-wrap').classList.contains('hidden'),
      value: box.value, selected: box.selectionEnd - box.selectionStart, focused: document.activeElement === box,
    };
  });
  check('refused copy: the text appears on the page, selected', !f.hidden && f.focused && f.selected === f.value.length && f.value.length > 200);
  const leaked = Object.entries(SECRETS).filter(([, v]) => f.value.includes(v)).map(([k]) => k);
  check('refused copy: the fallback text is the redacted text', leaked.length === 0 && f.value.includes('[hidden'), leaked.join(', '));
  await shot(page, 'logs_copy_refused.png');
  await context.close();
}

// ── 4. The log endpoint failing ────────────────────────────────────────────
{
  const { page, context } = await open({ logs: 'broken' });
  const s = await state(page);
  check('endpoint down: says it could not read the log', s.stateTitle === "Couldn't read the log", String(s.stateTitle));
  check('endpoint down: no verdict and zero counts (no stale numbers)', s.summaryClass.includes('hidden') && s.counts.all === '0' && s.counts.error === '0');
  await context.close();
}

// ── 5. Dark theme ──────────────────────────────────────────────────────────
{
  const { page, context } = await open({ logs: FIXTURE });
  await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'dark'));
  await page.waitForTimeout(300);
  const s = await state(page);
  check('dark theme: trouble rows still stand apart', s.bg.error !== s.bg.info && s.bg.warning !== s.bg.info, `${s.bg.error} / ${s.bg.warning} / ${s.bg.info}`);
  await shot(page, 'logs_dark.png');
  await context.close();
}

// ── 6. The app's real log ──────────────────────────────────────────────────
{
  const { page, context } = await open({ logs: 'live' });
  const s = await state(page);
  const total = Number(s.counts.all);
  check('live log: rows match the Everything count', total > 0 && s.kinds.length === total, `${s.kinds.length} rows, count ${s.counts.all}`);
  check('live log: the three kinds add up',
    Number(s.counts.error) + Number(s.counts.warning) + Number(s.counts.info) === total, JSON.stringify(s.counts));
  await page.click('#btn-copy-logs');
  await page.waitForTimeout(400);
  const clip = await page.evaluate(() => navigator.clipboard.readText());
  check('live log: copy has one block per entry', (clip.match(/^\[[A-Z]+\] \d{4}-/gm) || []).length === total);
  // Only the count is printed. The real log's text stays out of this script's output.
  const hiddenNote = await page.evaluate(() => [...document.querySelectorAll('.toast-msg')].at(-1)?.textContent ?? '');
  console.log(`live log verdict: ${s.summaryText}`);
  console.log(`live log copy: ${hiddenNote.replace(/^.*support. /, '')}`);
  await context.close();
}

check('no JavaScript errors on any page', pageErrors.length === 0, pageErrors.slice(0, 3).join(' | '));
console.log(`\n(${refused} non-GET request(s) from the page were refused, so the running app was not written to.)`);

await browser.close();
const failed = results.filter(r => !r.ok);
console.log(`\n${results.length - failed.length} of ${results.length} checks passed.`);
process.exit(failed.length ? 1 : 0);
