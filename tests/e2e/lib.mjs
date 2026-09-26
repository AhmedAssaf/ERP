// Shared helpers for the admin-ui end-to-end checks. Secrets are read from infra/compose/.env or the git-ignored
// .state/ folder at run time and never printed (N-10).
import { chromium } from 'playwright';
import crypto from 'crypto';
import fs from 'fs';
import path from 'path';
import zlib from 'zlib';

export const DIR = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
export const SHOTS = path.join(DIR, 'shots-admin');
fs.mkdirSync(SHOTS, { recursive: true });
const STATE_DIR = path.join(DIR, '.state');
fs.mkdirSync(STATE_DIR, { recursive: true });
const STATE = path.join(STATE_DIR, 'state-admin.json');
// Repo root is two levels up from tests/e2e.
const ENV = path.join(DIR, '..', '..', 'infra', 'compose', '.env');
// The Chromium build Playwright installed on this machine; override with E2E_CHROMIUM_PATH if it differs (a different
// Playwright version, another drive, another OS).
const CHROMIUM_PATH_DEFAULT = path.join(process.env.LOCALAPPDATA ?? '', 'ms-playwright', 'chromium-1217', 'chrome-win64', 'chrome.exe');

export function envValue(name) {
  const m = fs.readFileSync(ENV, 'utf8').match(new RegExp(`^${name}=(.*)$`, 'm'));
  return m ? m[1].trim() : undefined;
}
export function loadState() { try { return JSON.parse(fs.readFileSync(STATE, 'utf8')); } catch { return {}; } }
export function saveState(s) { fs.writeFileSync(STATE, JSON.stringify(s)); }

function base32(s) {
  const a = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const c of s.replace(/[\s=]/g, '').toUpperCase()) bits += a.indexOf(c).toString(2).padStart(5, '0');
  const out = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) out.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(out);
}
export function totp(secret, step) {
  const buf = Buffer.alloc(8); buf.writeBigUInt64BE(BigInt(step));
  const h = crypto.createHmac('sha1', base32(secret)).update(buf).digest();
  const o = h[h.length - 1] & 0xf;
  return String(((h.readUInt32BE(o) & 0x7fffffff) % 1_000_000)).padStart(6, '0');
}
const nowStep = () => Math.floor(Date.now() / 30000);

// A code for a step not used before by this user (Keycloak refuses a code reused within its window).
async function freshCode(state, user) {
  const last = state.lastStep?.[user] ?? -1;
  let step = nowStep();
  while (step <= last || (Date.now() % 30000) > 27000) { await new Promise(r => setTimeout(r, 1000)); step = nowStep(); }
  state.lastStep = { ...(state.lastStep || {}), [user]: step }; saveState(state);
  return totp(state.totp[user], step);
}

export async function launch() {
  const exe = process.env.E2E_CHROMIUM_PATH || CHROMIUM_PATH_DEFAULT;
  return chromium.launch({ executablePath: exe });
}
export async function newPage(browser, { width = 1280, height = 900, locale } = {}) {
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width, height }, locale });
  return { ctx, page: await ctx.newPage() };
}

const visible = async (page, sel) => { const e = await page.$(sel); return e ? e.isVisible() : false; };

// Drives Keycloak pages (login, update password, configure TOTP, OTP) until the browser leaves localhost:8080.
export async function driveKeycloak(page, { user, password, newPassword, state, log = () => {} }) {
  for (let i = 0; i < 15; i++) {
    await page.waitForLoadState('domcontentloaded');
    if (!page.url().startsWith('http://localhost:8080')) return;
    const kcError = await page.$eval('.pf-v5-c-alert__title, #input-error, .kc-feedback-text, #kc-error-message', e => e.textContent.trim()).catch(() => null);
    if (kcError) log(`keycloak message: ${kcError}`);
    if (await visible(page, '#password-new')) {
      await page.fill('#password-new', newPassword); await page.fill('#password-confirm', newPassword);
      log('set password'); await Promise.all([page.waitForNavigation(), page.click('[type=submit]')]); continue;
    }
    if (await visible(page, '#totp')) {
      if (await visible(page, '#mode-manual')) await page.click('#mode-manual');
      const secret = (await page.textContent('#kc-totp-secret-key')).trim();
      state.totp = { ...(state.totp || {}), [user]: secret.replace(/\s/g, '') }; saveState(state);
      await page.fill('#totp', await freshCode(state, user));
      if (await page.$('#userLabel')) await page.fill('#userLabel', 'e2e');
      log('enrolled TOTP'); await Promise.all([page.waitForNavigation(), page.click('#saveTOTPBtn, [type=submit]')]); continue;
    }
    if (await visible(page, '#otp')) {
      await page.fill('#otp', await freshCode(state, user));
      log('entered OTP'); await Promise.all([page.waitForNavigation(), page.click('#kc-login')]); continue;
    }
    if (await visible(page, '#password')) {
      if (await visible(page, '#username')) await page.fill('#username', user);
      await page.fill('#password', password);
      log('entered password'); await Promise.all([page.waitForNavigation(), page.click('#kc-login')]); continue;
    }
    if (await visible(page, '#username')) {
      await page.fill('#username', user);
      log('entered username'); await Promise.all([page.waitForNavigation(), page.click('#kc-login')]); continue;
    }
    const proceed = await page.$('a[href*="login-actions"], #kc-info-message a, a:has-text("proceed"), a:has-text("Back to Application")');
    if (proceed) { log('proceed link'); await Promise.all([page.waitForNavigation(), proceed.click()]); continue; }
    await page.waitForTimeout(1000);
    if (!page.url().startsWith('http://localhost:8080')) return;
    if (i < 12) continue;
    fs.writeFileSync(path.join(SHOTS, 'unknown-kc-page.txt'), (await page.evaluate(() => [...document.querySelectorAll('a,button,input')].map(e => `${e.tagName}#${e.id} name=${e.name||''} type=${e.type||''} ${e.textContent.trim().slice(0,60)}`).join(String.fromCharCode(10)))) + String.fromCharCode(10) + (await page.locator('body').innerText()));
    throw new Error(`unknown Keycloak page ${page.url()} title=${await page.title()}`);
  }
  throw new Error('too many Keycloak steps');
}

// A solid PNG (width x height, RGB) without an image library.
export function png(width, height, [r, g, b]) {
  const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
  const crc = buf => { let c = 0xffffffff; for (const x of buf) c = crcTable[(c ^ x) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const chunk = (type, data) => { const len = Buffer.alloc(4); len.writeUInt32BE(data.length); const td = Buffer.concat([Buffer.from(type), data]); const c = Buffer.alloc(4); c.writeUInt32BE(crc(td)); return Buffer.concat([len, td, c]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(width, 0); ihdr.writeUInt32BE(height, 4); ihdr[8] = 8; ihdr[9] = 2;
  const row = Buffer.concat([Buffer.from([0]), Buffer.alloc(width * 3).map((_, i) => [r, g, b][i % 3])]);
  // A white stripe across the middle so the logo is visibly an image.
  const rows = []; for (let y = 0; y < height; y++) rows.push(Math.abs(y - height / 2) < height / 8 ? Buffer.concat([Buffer.from([0]), Buffer.alloc(width * 3, 255)]) : row);
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(Buffer.concat(rows))), chunk('IEND', Buffer.alloc(0))]);
}

export async function mailpit(query) {
  const r = await fetch(`http://localhost:8025/api/v1/search?query=${encodeURIComponent(query)}`);
  return (await r.json()).messages || [];
}
export async function mailBody(id) { return (await fetch(`http://localhost:8025/api/v1/message/${id}`)).json(); }
export const sleep = ms => new Promise(r => setTimeout(r, ms));
