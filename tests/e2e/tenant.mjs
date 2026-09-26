// Tenant half of the admin-ui scenario: admin signs in (TOTP), invites an evaluator, the evaluator completes setup and
// signs in with the invited role only, then the admin changes branding colour and logo.
import crypto from 'crypto';
import fs from 'fs';
import path from 'path';
import { launch, newPage, driveKeycloak, envValue, loadState, saveState, mailpit, mailBody, png, sleep, SHOTS, DIR } from './lib.mjs';

const BASE = 'https://acme.localhost:8443';
const EVAL = 'new.evaluator@acme.waslabid.test';
const results = [];
const rec = (step, ok, seen) => { results.push({ step, ok, seen }); console.log(`${ok ? 'PASS' : 'FAIL'} ${step} :: ${JSON.stringify(seen)}`); };
const state = loadState();
const devPw = envValue('WASLABID_DEV_USER_PASSWORD');
state.evalPw ||= crypto.randomBytes(12).toString('base64url') + 'aA1!'; saveState(state);
const browser = await launch();
const only = process.argv[2];

try {
  // 1. Tenant admin signs in, enrolling TOTP on first login.
  const { ctx: actx, page: admin } = await newPage(browser);
  admin.on('response', r => { if (r.status() >= 400) console.log('admin http', r.status(), r.url()); });
  await admin.goto(`${BASE}/admin/staff`);
  const adminSteps = [];
  await driveKeycloak(admin, { user: 'acme.admin', password: devPw, state, log: s => adminSteps.push(s) });
  await admin.waitForSelector('[data-invite]', { timeout: 20000 });
  await admin.waitForTimeout(1500); // circuit up
  await admin.screenshot({ path: path.join(SHOTS, '01-admin-staff.png'), fullPage: true });
  rec('acme.admin signs in with TOTP and opens /admin/staff', admin.url().endsWith('/admin/staff'), { url: admin.url(), keycloak: adminSteps });

  if (only !== 'branding') {
    // 2. Invite the evaluator.
    const before = new Set((await mailpit(`to:${EVAL}`)).map(m => m.ID));
    if (await admin.locator(`[data-member="${EVAL}"]`).count()) {
      // Rerun: the member exists from an earlier run, so resend the invitation instead.
      await admin.locator('tr', { has: admin.locator(`[data-member="${EVAL}"]`) }).locator('[data-resend]').click();
      await admin.waitForTimeout(3000);
    } else {
    await admin.click('[data-invite]');
    const dlg = admin.locator('[role=dialog]');
    await dlg.waitFor();
    await dlg.locator('input[type=email]').fill(EVAL); await dlg.locator('input[type=email]').press('Tab');
    await dlg.locator('input[type=text]').first().fill('New Evaluator'); await dlg.locator('input[type=text]').first().press('Tab');
    await dlg.locator('[data-role="technical-evaluator"]').check();
    await admin.waitForTimeout(300);
    await dlg.locator('button').last().click();
    }
    await admin.waitForSelector(`[data-member="${EVAL}"]`, { timeout: 30000 }).catch(() => null);
    const alertText = await admin.locator('[role=dialog]').locator('[role=alert], .text-danger').allTextContents().catch(() => []);
    const row = await admin.locator('tr', { has: admin.locator(`[data-member="${EVAL}"]`) }).innerText().catch(() => null);
    await admin.screenshot({ path: path.join(SHOTS, '02-admin-invited.png'), fullPage: true });
    rec('admin invites new.evaluator as technical evaluator', !!row && /invited|مدعو/i.test(row), { row, dialogErrors: alertText });

    // 3. The email arrives.
    let msg;
    for (let i = 0; i < 30 && !msg; i++) { msg = (await mailpit(`to:${EVAL}`)).find(m => !before.has(m.ID)); if (!msg) await sleep(1000); }
    const body = msg ? await mailBody(msg.ID) : null;
    const link = body ? (body.Text.match(/http:\/\/localhost:8080\/\S+action-token\S+/) || [])[0] : null;
    rec('invitation email arrives in Mailpit', !!link, { subject: msg?.Subject, to: msg?.To?.map(t => t.Address), hasActionLink: !!link });

    // 4. Evaluator follows the link, sets a password, enrols TOTP.
    const { ctx: ectx, page: ev } = await newPage(browser);
    await ev.goto(link.replace(/[)>\]]+$/, ''));
    const evSteps = [];
    await driveKeycloak(ev, { user: EVAL, password: state.evalPw, newPassword: state.evalPw, state, log: s => evSteps.push(s) });
    await ev.waitForLoadState('networkidle');
    await ev.screenshot({ path: path.join(SHOTS, '03-evaluator-after-setup.png'), fullPage: true });
    rec('evaluator sets password and enrols TOTP from the link', evSteps.includes('set password') && evSteps.includes('enrolled TOTP'), { url: ev.url(), keycloak: evSteps });
    await ectx.close();

    // 5. Evaluator signs in fresh (password + code), sees no admin menu and gets 403 on /admin/staff.
    const { ctx: e2, page: ev2 } = await newPage(browser);
    await ev2.goto(`${BASE}/`);
    const signIn = [];
    await driveKeycloak(ev2, { user: EVAL, password: state.evalPw, state, log: s => signIn.push(s) });
    await ev2.waitForLoadState('networkidle');
    const home = await ev2.locator('main').innerText().catch(() => '');
    const adminLinks = await ev2.locator('a[href*="admin/"]').count();
    await ev2.screenshot({ path: path.join(SHOTS, '04-evaluator-home.png'), fullPage: true });
    rec('evaluator signs in with password and code, no admin menu', ev2.url().startsWith(BASE) && adminLinks === 0 && signIn.includes('entered OTP'), { url: ev2.url(), keycloak: signIn, adminLinks, home });
    // Chromium turns an empty 403 into a navigation error, so ask with the page's cookies instead.
    const staff = await ev2.request.get(`${BASE}/admin/staff`, { maxRedirects: 0 });
    const branding = await ev2.request.get(`${BASE}/admin/branding`, { maxRedirects: 0 });
    await ev2.goto(`${BASE}/admin/staff`).catch(() => null);
    await ev2.screenshot({ path: path.join(SHOTS, '05-evaluator-admin-staff-403.png'), fullPage: true });
    rec('evaluator gets 403 on /admin/staff (and /admin/branding)', staff.status() === 403 && branding.status() === 403, { staff: staff.status(), branding: branding.status() });
    await e2.close();

    // 6. The member row is now active.
    await admin.reload(); await admin.waitForSelector(`[data-member="${EVAL}"]`);
    const row2 = await admin.locator('tr', { has: admin.locator(`[data-member="${EVAL}"]`) }).innerText();
    rec('member row turns active after first sign-in', /active|نشط/i.test(row2) && !/invited|مدعو/i.test(row2), { row: row2 });
  }

  // 7. Branding: a light colour is darkened; logo upload; the header shows both.
  await admin.goto(`${BASE}/admin/branding`); await admin.waitForSelector('[data-save-branding]'); await admin.waitForTimeout(1500);
  const hex = admin.locator('[data-branding-form] input[type=text][dir=ltr]');
  await hex.fill('#FFFACD'); await hex.press('Tab'); await admin.waitForTimeout(300);
  await Promise.all([admin.waitForURL(/saved=1/, { timeout: 20000 }), admin.click('[data-save-branding]')]);
  await admin.waitForSelector('[data-branding-saved]');
  const adjusted = await admin.locator('[data-color-adjusted]').innerText().catch(() => null);
  const headerBg = await admin.$eval('header', e => getComputedStyle(e).backgroundColor);
  await admin.screenshot({ path: path.join(SHOTS, '06-branding-colour-adjusted.png'), fullPage: true });
  rec('branding colour #FFFACD is stored darkened and the header uses it', !!adjusted && headerBg !== 'rgb(255, 250, 205)', { adjusted, headerBg });

  const logo = path.join(DIR, 'logo-e2e.png'); fs.writeFileSync(logo, png(240, 80, [20, 90, 160]));
  await admin.waitForTimeout(2000);
  await admin.setInputFiles('#logo-file', logo);
  console.log('file input:', JSON.stringify(await admin.$eval('#logo-file', e => ({ n: e.files.length, valid: e.checkValidity() }))),
    'button:', await admin.$eval('[data-upload-logo]', e => e.outerHTML.slice(0, 200)));
  await Promise.all([admin.waitForURL(/logo=/, { timeout: 20000 }), admin.click('[data-upload-logo]')]);
  const outcome = await admin.getAttribute('[data-logo-outcome]', 'data-logo-outcome').catch(() => null);
  const headerImg = await admin.$eval('header img', e => ({ src: e.getAttribute('src'), loaded: e.complete && e.naturalWidth > 0, w: e.naturalWidth })).catch(() => null);
  const headerName = await admin.$eval('header .truncate', e => e.textContent.trim()).catch(() => null);
  await admin.screenshot({ path: path.join(SHOTS, '07-branding-logo.png'), fullPage: true });
  rec('logo upload saved and the header shows logo and portal name', outcome === 'saved' && headerImg?.loaded && /^\/branding\/logo\/[a-f0-9]{64}\.png$/.test(headerImg.src), { outcome, headerImg, headerName });
  await actx.close();
} catch (e) {
  rec('script error', false, { error: String(e).slice(0, 400) });
} finally {
  fs.writeFileSync(path.join(DIR, 'tenant-results.json'), JSON.stringify(results, null, 2));
  await browser.close();
}
