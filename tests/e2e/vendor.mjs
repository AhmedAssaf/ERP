// Vendor slice end to end (vendor plan task 7): a vendor self-registers on acme, verifies the email in Mailpit, registers
// the company with the privacy notice, signs in again, uploads the CR certificate (future expiry) and the VAT certificate
// (past expiry, shown expired), grants and revokes a consent to the seeded test recipient; acme's admin approves it; the
// same vendor opens beta's /vendor, lands on /vendor/join, joins, and beta's admin sees it pending only from then on.
// Secrets come from infra/compose/.env and .state/ at run time and are never printed (N-10).
import crypto from 'crypto';
import fs from 'fs';
import path from 'path';
import { launch, newPage, driveKeycloak, envValue, loadState, saveState, mailpit, mailBody, sleep, DIR } from './lib.mjs';

const ACME = 'https://acme.localhost:8443';
const BETA = 'https://beta.localhost:8443';
const SHOTS = path.join(DIR, 'shots-vendor');
fs.mkdirSync(SHOTS, { recursive: true });
// Browser console errors on every page (a failed interactive circuit logs here, and the page otherwise looks fine).
const consoleErrors = [];
// The browser's own favicon request is ignored: the app has no favicon (a 404 for staff, and a 403 for a vendor since the
// path falls to the staff fallback policy), which is noise, not a page failure.
const watch = (page, who) => page.on('console', m => {
  if (m.type() === 'error' && !m.location()?.url?.endsWith('/favicon.ico')) consoleErrors.push(`${who}: ${m.text().slice(0, 200)} @ ${m.location()?.url ?? ''}`);
});
const shot = (page, name) => page.screenshot({ path: path.join(SHOTS, `${name}.png`), fullPage: true }).catch(() => null);

const results = [];
const rec = (step, ok, seen) => { results.push({ step, ok, seen }); console.log(`${ok ? 'PASS' : 'FAIL'} ${step} :: ${JSON.stringify(seen)}`); };
const state = loadState();
const devPw = envValue('WASLABID_DEV_USER_PASSWORD');

// A fresh vendor per run: unique email and CR number (10 digits), VAT 15 digits starting and ending with 3.
const run = Date.now().toString();
const vendorEmail = `vendor.${run}@vendor.waslabid.test`;
const cr = `7${run.slice(-9)}`;
const vat = `3${run.slice(-13)}3`;
const nameEn = `E2E Supplies ${run.slice(-6)}`;
const nameAr = `مؤسسة التوريد ${run.slice(-6)}`;
state.vendorPw ||= crypto.randomBytes(12).toString('base64url') + 'aA1!'; saveState(state);

const isoDate = d => d.toISOString().slice(0, 10);
const today = new Date();
const future = isoDate(new Date(today.getTime() + 365 * 86400000));
const past = isoDate(new Date(today.getTime() - 30 * 86400000));

// A minimal valid one-page PDF (magic number %PDF), different content per document so the hashes differ.
function pdf(label) {
  const objs = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 144] /Contents 4 0 R >>',
  ];
  const stream = `BT /F1 12 Tf 20 70 Td (${label}) Tj ET`;
  objs.push(`<< /Length ${stream.length} >>\nstream\n${stream}\nendstream`);
  let out = '%PDF-1.4\n'; const offsets = [];
  objs.forEach((o, i) => { offsets.push(out.length); out += `${i + 1} 0 obj\n${o}\nendobj\n`; });
  const xref = out.length;
  out += `xref\n0 ${objs.length + 1}\n0000000000 65535 f \n` + offsets.map(o => `${String(o).padStart(10, '0')} 00000 n \n`).join('');
  out += `trailer\n<< /Size ${objs.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(out, 'latin1');
}
const stateDir = path.join(DIR, '.state');
const crFile = path.join(stateDir, 'cr-certificate.pdf'); fs.writeFileSync(crFile, pdf(`CR ${cr}`));
const vatFile = path.join(stateDir, 'vat-certificate.pdf'); fs.writeFileSync(vatFile, pdf(`VAT ${vat}`));

// Keycloak's logout confirmation, when it asks, then whatever login steps follow.
async function signInAgain(page, user, password, log) {
  for (let i = 0; i < 5; i++) {
    await page.waitForLoadState('domcontentloaded');
    const logout = await page.$('#kc-logout');
    if (logout) { log('confirmed logout'); await Promise.all([page.waitForNavigation(), logout.click()]); continue; }
    break;
  }
  await driveKeycloak(page, { user, password, state, log });
}

async function staffSignIn(browser, base, user) {
  const { ctx, page } = await newPage(browser);
  watch(page, user);
  await page.goto(`${base}/admin/vendors`);
  const steps = [];
  await driveKeycloak(page, { user, password: devPw, state, log: s => steps.push(s) });
  await page.waitForLoadState('networkidle');
  return { ctx, page, steps };
}

async function vendorRowOnStaffList(page, base) {
  await page.goto(`${base}/admin/vendors`); await page.waitForLoadState('networkidle');
  const link = page.locator('[data-vendor]', { hasText: new RegExp(`${nameEn}|${nameAr}`) });
  const count = await link.count();
  const id = count ? await link.first().getAttribute('data-vendor') : null;
  const status = id ? await page.locator(`[data-vendor-status^="${id}:"]`).getAttribute('data-vendor-status') : null;
  return { count, id, status };
}

async function upload(page, type, file, expiry) {
  const box = page.locator(`[data-upload-type="${type}"]`);
  await box.locator('input[type=file]').setInputFiles(file);
  await page.waitForTimeout(800);
  await box.locator('input[type=date]').fill(expiry);
  await box.locator('input[type=date]').dispatchEvent('change');
  await page.waitForTimeout(500);
  await box.locator('[data-file-upload-start]').click();
  await page.waitForFunction(t => {
    const s = document.querySelector(`[data-upload-type="${t}"] [data-file-upload-state]`)?.getAttribute('data-file-upload-state');
    return s === 'done' || s === 'rejected' || s === 'failed' || s === 'pending';
  }, type, { timeout: 90000 }).catch(() => null);
  return box.locator('[data-file-upload-state]').getAttribute('data-file-upload-state');
}

const browser = await launch();
try {
  // 1. Register at /vendor/register: Keycloak's self-registration form of the tenant realm.
  const { ctx: vctx, page: vendor } = await newPage(browser);
  watch(vendor, 'vendor');
  vendor.on('response', r => { if (r.status() >= 500) console.log('vendor http', r.status(), r.url()); });
  await vendor.goto(`${ACME}/vendor/register`);
  await vendor.waitForLoadState('domcontentloaded');
  const onRegister = vendor.url().startsWith('http://localhost:8080') && await vendor.locator('#email').count() > 0 && await vendor.locator('#password-confirm').count() > 0;
  const regFields = await vendor.$$eval('form input', es => es.filter(e => e.type !== 'hidden').map(e => e.name));
  await shot(vendor, '01-keycloak-register');
  rec('/vendor/register opens Keycloak self-registration', onRegister, { url: vendor.url().split('?')[0], fields: regFields });
  const before = new Set((await mailpit(`to:${vendorEmail}`)).map(m => m.ID));
  await vendor.fill('#email', vendorEmail);
  if (await vendor.locator('#firstName').count()) await vendor.fill('#firstName', 'Vendor');
  if (await vendor.locator('#lastName').count()) await vendor.fill('#lastName', `E2E ${run.slice(-4)}`);
  await vendor.fill('#password', state.vendorPw); await vendor.fill('#password-confirm', state.vendorPw);
  await Promise.all([vendor.waitForNavigation(), vendor.click('[type=submit]')]);
  const verifyText = await vendor.locator('body').innerText();
  await shot(vendor, '02-verify-email-page');
  rec('registration asks to verify the email before signing in', /verify|تحقق|التحقق/i.test(verifyText) && vendor.url().startsWith('http://localhost:8080'), { url: vendor.url().split('?')[0], text: verifyText.slice(0, 200) });

  // 2. The verification email in Mailpit; following it signs the vendor in and returns to /vendor/register/company.
  let msg;
  for (let i = 0; i < 30 && !msg; i++) { msg = (await mailpit(`to:${vendorEmail}`)).find(m => !before.has(m.ID)); if (!msg) await sleep(1000); }
  const body = msg ? await mailBody(msg.ID) : null;
  const link = body ? (body.Text.match(/http:\/\/localhost:8080\/\S+action-token\S+/) || [])[0] : null;
  rec('verification email arrives in Mailpit', !!link, { subject: msg?.Subject, hasActionLink: !!link });
  await vendor.goto(link.replace(/[)>\]]+$/, ''));
  const vsteps = [];
  await driveKeycloak(vendor, { user: vendorEmail, password: state.vendorPw, newPassword: state.vendorPw, state, log: s => vsteps.push(s) });
  await vendor.waitForLoadState('networkidle');
  await shot(vendor, '03-after-verify');
  if (!vendor.url().includes('/vendor/register/company')) { await vendor.goto(`${ACME}/vendor/register/company`); await driveKeycloak(vendor, { user: vendorEmail, password: state.vendorPw, state, log: s => vsteps.push(s) }); }
  const formShown = await vendor.locator('[data-vendor-register]').count() > 0;
  rec('verified vendor reaches the company form', formShown && vendor.url().includes('/vendor/register/company'), { url: vendor.url(), keycloak: vsteps });

  // 3. Company form with the privacy notice.
  const privacyVersion = await vendor.locator('[data-privacy-notice]').getAttribute('data-privacy-notice');
  const f = vendor.locator('[data-vendor-register]');
  await f.locator('input[name="Input.CrNumber"]').fill(cr);
  await f.locator('input[name="Input.NameAr"]').fill(nameAr);
  await f.locator('input[name="Input.NameEn"]').fill(nameEn);
  await f.locator('input[name="Input.VatNumber"]').fill(vat);
  await f.locator('input[name="Input.Address"]').fill('Riyadh, Olaya Street');
  await f.locator('input[name="Input.ContactName"]').fill('Vendor Contact');
  await f.locator('input[name="Input.ContactPhone"]').fill('+966500000000');
  await f.locator('input[name="Input.ContactEmail"]').fill(vendorEmail);
  // First without the notice: the form must refuse.
  await Promise.all([vendor.waitForLoadState('domcontentloaded'), f.locator('button[type=submit]').click()]);
  await vendor.waitForTimeout(1000);
  const privacyError = await vendor.locator('#privacy-error').count() > 0;
  rec('the company form refuses without the privacy notice', privacyError, { privacyError });
  const f2 = vendor.locator('[data-vendor-register]');
  await f2.locator('input[name="Input.AcceptedPrivacyNotice"]').check();
  await Promise.all([vendor.waitForLoadState('domcontentloaded'), f2.locator('button[type=submit]').click()]);
  await vendor.waitForSelector('[data-vendor-registered], [data-form-error]', { timeout: 30000 }).catch(() => null);
  const registered = await vendor.locator('[data-vendor-registered]').count() > 0;
  const formError = await vendor.locator('[data-form-error]').innerText().catch(() => null);
  await shot(vendor, '04-company-registered');
  rec('company registered with the privacy notice', registered, { privacyVersion, formError });

  // 4. Sign in again so the token carries the vendor role and the organization; land on /vendor.
  const rsteps = [];
  await Promise.all([vendor.waitForNavigation(), vendor.click('[data-vendor-sign-out] button[type=submit]')]);
  await signInAgain(vendor, vendorEmail, state.vendorPw, s => rsteps.push(s));
  await vendor.waitForSelector('[data-vendor-company]', { timeout: 30000 }).catch(() => null);
  await vendor.waitForTimeout(1500); // circuit up
  const relationship = await vendor.locator('[data-relationship]').getAttribute('data-relationship').catch(() => null);
  await shot(vendor, '05-vendor-home');
  rec('re-sign-in lands on /vendor with the company, relationship pending', vendor.url() === `${ACME}/vendor` && relationship === 'pending', { url: vendor.url(), relationship, keycloak: rsteps });

  // 5. Upload the CR certificate (future expiry) and the VAT certificate (past expiry).
  const crState = await upload(vendor, 'cr_certificate', crFile, future);
  await vendor.waitForTimeout(1500);
  const vatState = await upload(vendor, 'vat_certificate', vatFile, past);
  await vendor.waitForTimeout(1500);
  await vendor.reload(); await vendor.waitForSelector('[data-vendor-company]'); await vendor.waitForTimeout(1500);
  const statuses = await vendor.$$eval('[data-document-status]', es => es.map(e => e.getAttribute('data-document-status')));
  const blocking = await vendor.$$eval('[data-blocking]', es => es.map(e => e.getAttribute('data-blocking')));
  const expiredText = await vendor.locator('[data-document-status="vat_certificate:expired"]').innerText().catch(() => null);
  await shot(vendor, '06-documents');
  rec('CR certificate uploads, scans clean and is current', crState === 'done' && statuses.includes('cr_certificate:current'), { crState, statuses });
  rec('VAT certificate with a past expiry is shown expired', vatState === 'done' && statuses.includes('vat_certificate:expired') && blocking.includes('vat_certificate:expired'), { vatState, statuses, blocking, expiredText });

  // 6. Grant and revoke a consent to the test recipient.
  await vendor.goto(`${ACME}/vendor/consent`); await vendor.waitForSelector('[data-grant]'); await vendor.waitForTimeout(1500);
  await vendor.click('[data-grant]');
  const dlg = vendor.locator('[role=dialog]'); await dlg.waitFor();
  const recipientOptions = await dlg.locator('[data-consent-recipient] option').allTextContents();
  const recipientValue = await dlg.locator('[data-consent-recipient] option').nth(1).getAttribute('value');
  await dlg.locator('[data-consent-recipient]').selectOption(recipientValue);
  const scopeValue = await dlg.locator('[data-consent-scope] option').nth(1).getAttribute('value');
  await dlg.locator('[data-consent-scope]').selectOption(scopeValue);
  await vendor.waitForTimeout(300);
  await dlg.locator('button').last().click();
  await vendor.waitForSelector('[data-consent-status$=":active"]', { timeout: 20000 }).catch(() => null);
  const afterGrant = await vendor.$$eval('[data-consent-status]', es => es.map(e => e.getAttribute('data-consent-status')));
  await shot(vendor, '07-consent-granted');
  rec('consent granted to the test recipient and listed active', afterGrant.some(s => s.endsWith(':active')), { recipientOptions, scopeValue, statuses: afterGrant });
  const grantId = afterGrant.find(s => s.endsWith(':active'))?.split(':')[0];
  await vendor.click(`[data-revoke="${grantId}"]`);
  const rdlg = vendor.locator('[role=dialog]'); await rdlg.waitFor();
  await rdlg.locator('button').last().click();
  await vendor.waitForSelector(`[data-consent-status="${grantId}:revoked"]`, { timeout: 20000 }).catch(() => null);
  const afterRevoke = await vendor.$$eval('[data-consent-status]', es => es.map(e => e.getAttribute('data-consent-status')));
  await shot(vendor, '08-consent-revoked');
  rec('consent revoked and listed revoked', afterRevoke.includes(`${grantId}:revoked`), { statuses: afterRevoke });

  // 7. acme's admin sees it pending and approves.
  const { ctx: actx, page: acme, steps: asteps } = await staffSignIn(browser, ACME, 'acme.admin');
  const acmeBefore = await vendorRowOnStaffList(acme, ACME);
  rec('acme.admin sees the vendor pending on /admin/vendors', acmeBefore.status?.endsWith(':pending'), { ...acmeBefore, keycloak: asteps });
  await acme.goto(`${ACME}/admin/vendors/${acmeBefore.id}`); await acme.waitForSelector('[data-approve]'); await acme.waitForTimeout(1500);
  await acme.click('[data-approve]');
  const adlg = acme.locator('[role=dialog]'); await adlg.waitFor();
  await adlg.locator('button').last().click();
  await acme.waitForSelector(`[data-vendor-status="${acmeBefore.id}:approved"]`, { timeout: 20000 }).catch(() => null);
  await acme.waitForTimeout(2000); // a circuit that fails on the dialog closing reports it within this time
  const approvedStatus = await acme.locator('[data-vendor-status]').getAttribute('data-vendor-status').catch(() => null);
  await shot(acme, '09-acme-approved');
  rec('acme.admin approves the vendor', approvedStatus === `${acmeBefore.id}:approved`, { approvedStatus });
  await vendor.goto(`${ACME}/vendor`); await vendor.waitForSelector('[data-vendor-company]'); await vendor.waitForTimeout(1000);
  const relAfter = await vendor.locator('[data-relationship]').getAttribute('data-relationship').catch(() => null);
  rec('the vendor sees itself approved at acme', relAfter === 'approved', { relationship: relAfter });

  // 8. beta's admin does not see the vendor before it joins.
  const { ctx: bctx, page: beta, steps: bsteps } = await staffSignIn(browser, BETA, 'beta.admin');
  const betaBefore = await vendorRowOnStaffList(beta, BETA);
  rec('beta.admin does not see the vendor before it joins', betaBefore.count === 0, { ...betaBefore, keycloak: bsteps });

  // 9. The same vendor opens beta's /vendor and is sent to /vendor/join; joins; signs in again.
  const jsteps = [];
  await vendor.goto(`${BETA}/vendor`);
  await driveKeycloak(vendor, { user: vendorEmail, password: state.vendorPw, state, log: s => jsteps.push(s) });
  await vendor.waitForLoadState('networkidle');
  await shot(vendor, '10-beta-vendor');
  rec('beta.localhost/vendor redirects the vendor to /vendor/join', vendor.url() === `${BETA}/vendor/join` && await vendor.locator('[data-vendor-join-form]').count() > 0, { url: vendor.url(), keycloak: jsteps });
  await Promise.all([vendor.waitForLoadState('domcontentloaded'), vendor.click('[data-vendor-join-form] button[type=submit]')]);
  await vendor.waitForSelector('[data-vendor-joined], [data-form-error]', { timeout: 30000 }).catch(() => null);
  const joined = await vendor.locator('[data-vendor-joined]').count() > 0;
  const joinError = await vendor.locator('[data-form-error]').innerText().catch(() => null);
  await shot(vendor, '11-beta-joined');
  rec('the vendor joins beta', joined, { joinError });
  const j2 = [];
  await Promise.all([vendor.waitForNavigation(), vendor.click('[data-vendor-sign-out] button[type=submit]')]);
  await signInAgain(vendor, vendorEmail, state.vendorPw, s => j2.push(s));
  await vendor.waitForSelector('[data-vendor-company]', { timeout: 30000 }).catch(() => null);
  await vendor.waitForTimeout(1000);
  const betaRel = await vendor.locator('[data-relationship]').getAttribute('data-relationship').catch(() => null);
  await shot(vendor, '12-beta-vendor-home');
  rec('after re-sign-in the vendor home at beta shows pending', vendor.url() === `${BETA}/vendor` && betaRel === 'pending', { url: vendor.url(), relationship: betaRel, keycloak: j2 });

  // 10. beta's admin sees it pending only now; acme still approved.
  const betaAfter = await vendorRowOnStaffList(beta, BETA);
  await shot(beta, '13-beta-staff-list');
  rec('beta.admin sees the vendor pending after it joins', betaAfter.count === 1 && betaAfter.status?.endsWith(':pending'), betaAfter);
  const acmeAfter = await vendorRowOnStaffList(acme, ACME);
  rec('acme still lists the vendor approved', acmeAfter.status?.endsWith(':approved'), acmeAfter);
  rec('no browser console errors (no failed circuit) on any page', consoleErrors.length === 0, { consoleErrors });
  await Promise.all([vctx.close(), actx.close(), bctx.close()]);
} catch (e) {
  rec('script error', false, { error: String(e).slice(0, 600) });
} finally {
  fs.writeFileSync(path.join(DIR, 'vendor-results.json'), JSON.stringify(results, null, 2));
  await browser.close();
}
