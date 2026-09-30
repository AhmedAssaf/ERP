// W-06 golden set (admin and vendor subsets): /dev/gallery at 360 and 1280 px, page culture ar-SA and en-US, signed in as
// a throwaway acme tenant admin with its own TOTP seed (admin.mjs), deleted at the end. Writes the PNGs to
// ../Platform.UITests/golden and prints the overflow report; any horizontal overflow sets a non-zero exit code.
import fs from 'fs';
import path from 'path';
import { launch, newPage, driveKeycloak, loadState, DIR } from './lib.mjs';
import { throwawayStaff, cleanup } from './admin.mjs';

// tests/e2e -> ../Platform.UITests/golden.
const OUT = path.join(DIR, '..', 'Platform.UITests', 'golden');
fs.mkdirSync(OUT, { recursive: true });
const BASE = 'https://acme.localhost:8443';
const state = loadState();
const browser = await launch();
console.log('chromium', browser.version());
const report = [];
try {
  const admin = await throwawayStaff('acme', 'tenant-admin', 'gallery', Date.now().toString());
  const { ctx, page } = await newPage(browser, { width: 1280, height: 900 });
  await page.goto(`${BASE}/dev/gallery`);
  await driveKeycloak(page, { user: admin.email, password: admin.password, state });
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
        // Each textarea's own box must fit inside its panel (the Textarea component, W-33).
        textareas: [...document.querySelectorAll('textarea')].map(t => {
          const r = t.getBoundingClientRect(), p = t.closest('section[dir]')?.getBoundingClientRect();
          return { dir: getComputedStyle(t).direction, fits: !p || (r.left >= p.left - 0.5 && r.right <= p.right + 0.5) };
        }),
      }));
      const file = `gallery-${culture}-${width}.png`;
      await page.screenshot({ path: path.join(OUT, file), fullPage: true });
      report.push({ file, ...m, noOverflow: m.scrollWidth <= m.innerWidth && m.textareas.every(t => t.fits) });
    }
  }
  await ctx.close();
} catch (e) {
  report.push({ error: String(e).slice(0, 600), noOverflow: false });
} finally {
  await browser.close();
  report.push({ cleanup: await cleanup().catch(e => [{ error: String(e).slice(0, 300) }]) });
  console.log(JSON.stringify(report, null, 2));
  if (report.some(r => r.noOverflow === false)) process.exitCode = 1;
}
