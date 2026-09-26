// Foundation smoke check: Arabic/RTL rendering and the live language switch on the tenant home page, then that a
// beta.admin token is refused on the acme host (tenant isolation). Usage: node check.mjs <path-to-.env> <screenshot-dir>
import { chromium } from 'playwright';
import fs from 'fs';
import path from 'path';

const env = fs.readFileSync(process.argv[2], 'utf8');
const pw = (env.match(/^WASLABID_DEV_USER_PASSWORD=(.*)$/m) || [])[1].trim();
const out = process.argv[3];
fs.mkdirSync(out, { recursive: true });
// The Chromium build Playwright installed on this machine; override with E2E_CHROMIUM_PATH if it differs.
const chromiumDefault = path.join(process.env.LOCALAPPDATA ?? '', 'ms-playwright', 'chromium-1217', 'chrome-win64', 'chrome.exe');
const browser = await chromium.launch({ executablePath: process.env.E2E_CHROMIUM_PATH || chromiumDefault });
async function login(user, url) {
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();
  const resp = await page.goto(url);
  await page.fill('#username', user);
  if (!(await page.$('#password'))) { await page.click('#kc-login'); await page.waitForSelector('#password', { timeout: 15000 }); }
  await page.fill('#password', pw);
  const [nav] = await Promise.all([page.waitForNavigation({ waitUntil: 'networkidle' }), page.click('#kc-login')]);
  return { ctx, page, status: nav ? nav.status() : null };
}
const r = [];
{ const { ctx, page, status } = await login('acme.admin', 'https://acme.localhost:8443/');
  const html = await page.content();
  const dir = await page.getAttribute('html', 'dir'), lang = await page.getAttribute('html', 'lang');
  const bg = await page.$eval('header', e => getComputedStyle(e).backgroundColor).catch(() => 'no header');
  const h1 = await page.textContent('h1').catch(() => null);
  const who = await page.textContent('main p').catch(() => null);
  await page.screenshot({ path: out + '/acme-ar.png' });
  r.push({ step: 'acme login', status, url: page.url(), lang, dir, headerBg: bg, h1, who });
  await page.click('text=English');
  const switched = await page.waitForFunction(() => document.documentElement.lang === 'en', null, { timeout: 10000 }).then(() => true, () => false);
  r.push({ step: 'lang attribute changed without reload', switched, h1Now: await page.textContent('h1') });
  await page.reload({ waitUntil: 'networkidle' });
  r.push({ step: 'switch to English', lang: await page.getAttribute('html', 'lang'), dir: await page.getAttribute('html', 'dir'), h1: await page.textContent('h1') });
  await page.screenshot({ path: out + '/acme-en.png' });
  await ctx.close(); }
{ const { ctx, page, status } = await login('beta.admin', 'https://acme.localhost:8443/');
  r.push({ step: 'beta user on acme host', status, url: page.url() });
  await ctx.close(); }
console.log(JSON.stringify(r, null, 2));
await browser.close();
