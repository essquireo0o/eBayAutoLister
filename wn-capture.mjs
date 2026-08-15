// Grabs real frames off a live Whatnot show, so the live video read can be tested against the
// thing it is actually pointed at rather than against a stock photo. Throwaway: not part of the app.
import { chromium } from 'file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs';
import fs from 'node:fs';

const OUT = 'C:/Users/nsquires/source/repos/ING eBay AutoLister/wn-frames';
fs.mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
const page = await ctx.newPage();

let url = process.argv[2];

if (!url) {
  console.log('Finding a live show on whatnot.com …');
  await page.goto('https://www.whatnot.com/live', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.waitForTimeout(6000);
  const links = await page.$$eval('a[href*="/live/"]', as => as.map(a => a.href).slice(0, 40));
  console.log(`live links found: ${links.length}`);
  console.log(links.slice(0, 10).join('\n'));
  url = links[0];
}

if (!url) { console.log('NO LIVE SHOW FOUND'); await browser.close(); process.exit(1); }

console.log('Opening show: ' + url);
await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 });
await page.waitForTimeout(10000);

console.log('title: ' + (await page.title()));
const hasVideo = await page.$$eval('video', vs => vs.map(v => ({ w: v.videoWidth, h: v.videoHeight, paused: v.paused })));
console.log('video elements: ' + JSON.stringify(hasVideo));

// Whole-page shots at intervals — the same thing the seller's shared tab would give the canvas.
for (let i = 0; i < 6; i++) {
  const file = `${OUT}/frame-${i}.jpg`;
  await page.screenshot({ path: file, type: 'jpeg', quality: 70 });
  console.log(`${file} ${fs.statSync(file).size} bytes`);
  if (i < 5) await page.waitForTimeout(15000);
}

fs.writeFileSync(`${OUT}/show-url.txt`, url);
await browser.close();
console.log('DONE');
