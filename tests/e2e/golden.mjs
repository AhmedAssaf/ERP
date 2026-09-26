// W-06 golden set (admin subset): /dev/gallery at 360 and 1280 px, page culture ar-SA and en-US, signed in as acme.admin.
import fs from 'fs';
import path from 'path';
import { launch, newPage, driveKeycloak, envValue, loadState, DIR } from './lib.mjs';

// tests/e2e -> ../Platform.UITests/golden.
const OUT = path.join(DIR, '..', 'Platform.UITests', 'golden');
fs.mkdirSync(OUT, { recursive: true });
const BASE = 'https://acme.localhost:8443';
const state = loadState();
const browser = await launch();
console.log('chromium', browser.version());
const { ctx, page } = await newPage(browser, { width: 1280, height: 900 });
await page.goto(`${BASE}/dev/gallery`);
await driveKeycloak(page, { user: 'acme.admin', password: envValue('WASLABID_DEV_USER_PASSWORD'), state });
const report = [];
for (const culture of ['ar-SA', 'en-US']) {
  await page.goto(`${BASE}/culture/set?culture=${culture}&returnUrl=/dev/gallery`);
  for (const width of [360, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(`${BASE}/dev/gallery`, { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    const m = await page.evaluate(() => ({
      lang: document.documentElement.lang, dir: document.documentElement.dir,
      scrollWidth: document.documentElement.scrollWidth, innerWidth: window.innerWidth,
      panels: [...document.querySelectorAll('section[dir]')].map(s => `${s.lang}/${s.dir}`),
    }));
    const file = `gallery-${culture}-${width}.png`;
    await page.screenshot({ path: path.join(OUT, file), fullPage: true });
    report.push({ file, ...m, noOverflow: m.scrollWidth <= m.innerWidth });
  }
}
console.log(JSON.stringify(report, null, 2));
await ctx.close();
await browser.close();
