// Throwaway harness for verifying the live hosted deployment. Not part of the product.
// Playwright is installed globally on this machine only, hence the file:/// import.
const pw = await import('file:///C:/Users/nsquires/AppData/Roaming/npm/node_modules/playwright/index.mjs');
export const { chromium } = pw;

export const BASE = 'https://app.inglisting.com';
export const SHOTS = 'C:/Users/nsquires/source/repos/ING eBay AutoLister/verification/live-e2e';

let n = 0;
export async function shot(page, name) {
  n += 1;
  const file = `${SHOTS}/${String(n).padStart(2, '0')}-${name}.png`;
  await page.screenshot({ path: file, fullPage: false });
  console.log(`   [shot] ${file.split('/').pop()}`);
  return file;
}

export function say(...a) { console.log(...a); }
export function result(id, pass, detail) {
  console.log(`\n>>> ${id}: ${pass ? 'PASS' : 'FAIL'} — ${detail}\n`);
}

// A distinct account per run, so a re-run never collides with a previous one.
export function account(tag, stamp) {
  return { email: `e2e-${tag}-${stamp}@inglisting-test.com`, password: `Pw-${tag}-${stamp}!7x` };
}
