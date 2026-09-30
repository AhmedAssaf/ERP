// W-33 end to end through Caddy (ADR-0013, vendors spec V-15 to V-17): the CR ownership check before a company's first
// approval, the method switch in the platform console, and the dispute path from the duplicate-CR refusal to an uphold that
// moves the company and its Keycloak organization memberships to the real owner.
//
//  1. A squatter registers company A (CR number X) on acme, uploads its CR certificate and is approved by acme's officer:
//     the dialog is checked in Arabic (right to left) and English, with no raw resource key; it refuses without the box
//     and without a note; the method is switched to Wathq in the console while Wathq is not set up, which the console and
//     the officer's dialog both say (manual fallback); the officer approves with a multi-line note in both languages,
//     stored with its line feed. The method goes back to Manual.
//  2. The squatter joins beta; beta's officer approves without the check and learns only the method.
//  3. A bystander registers company B. Five decoys dispute company A and one disputes company B, through /vendor/dispute.
//  4. The real owner registers with CR X, is refused with a link to /vendor/dispute, follows it and raises a dispute with a
//     multi-line statement (an empty statement first shows the error in Arabic): the sixth for company A, recorded, not
//     refused, and badged over the cap in the console.
//  5. The console lists the disputes grouped by company, oldest first; the platform admin accepts the owner's dispute for
//     review, upholds it with a multi-line note, and rejects company B's. The database and Keycloak show the move: the
//     owner is company A's only vendor admin, holds the realm role vendor and both organizations; the squatter has
//     neither; the decoys' disputes are closed; the replaced verification is kept on the dispute.
//  6. The owner signs in again and reaches /vendor on acme and on beta, never /vendor/join; the squatter's open session
//     and a fresh sign-in are both refused.
//  7. The platform admins got one alert email for the new disputes without personal data.
// Staff, platform admin, squatter, owner, bystander and decoys are throwaway Keycloak users with their own passwords (and
// TOTP seeds for staff), deleted at the end; company and dispute rows stay, since audit and ledger rows are append-only.
// Secrets come from the .env (E2E_ENV_FILE) and never reach the console (N-10). Run with `node ownership.mjs` (about
// eight minutes, most of it waiting for the five-minute dispute alert job). Screenshots in shots-ownership/, results in
// ownership-results.json; a failed step sets a non-zero exit code.
import fs from 'fs';
import path from 'path';
import { launch, newPage, driveKeycloak, loadState, mailpit, mailBody, sleep, DIR } from './lib.mjs';
import { pdf, upload, signInAgain, registerCompany } from './vendorflow.mjs';
import {
  throwawayStaff, throwawayPlatformAdmin, throwawayPerson, cleanup, newPassword, sql, q, tenantId, isMember, realmRoles,
} from './admin.mjs';

const ACME = 'https://acme.localhost:8443';
const BETA = 'https://beta.localhost:8443';
const PLATFORM = 'https://platform.localhost:8443';
const SHOTS = path.join(DIR, 'shots-ownership');
fs.mkdirSync(SHOTS, { recursive: true });
const started = new Date();

const results = [];
const rec = (step, ok, seen) => { results.push({ step, ok, seen }); console.log(`${ok ? 'PASS' : 'FAIL'} ${step} :: ${JSON.stringify(seen)}`); };
const shotPath = name => path.join(SHOTS, `${name}.png`);
const shot = (page, name) => page.screenshot({ path: shotPath(name), fullPage: true }).then(() => `shots-ownership/${name}.png`).catch(() => null);
const consoleErrors = [];
const watch = (page, who) => page.on('console', m => {
  if (m.type() === 'error' && !m.location()?.url?.endsWith('/favicon.ico')) consoleErrors.push(`${who}: ${m.text().slice(0, 200)} @ ${m.location()?.url ?? ''}`);
});

// ---- Resource strings, to match the exact text in each culture and to catch a raw key on screen ----
function resx(culture) {
  const file = path.join(DIR, '..', '..', 'src', 'UI', 'Platform.UI', 'Resources', `SharedResource.${culture}.resx`);
  const out = {};
  for (const m of fs.readFileSync(file, 'utf8').matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g)) {
    out[m[1]] = m[2].replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"');
  }
  return out;
}
const R = { 'ar-SA': resx('ar-SA'), 'en-US': resx('en-US') };
const KEYS = Object.keys(R['en-US']);
const PREFIXES = [...new Set(KEYS.map(k => k.split('.')[0]))].join('|');
const RAW_KEY = new RegExp(`\\b(?:${PREFIXES})\\.[A-Z][A-Za-z0-9]*(?:\\.[A-Za-z0-9]+)*`, 'g');

async function setCulture(page, base, culture, returnUrl) {
  await page.goto(`${base}/culture/set?culture=${culture}&returnUrl=${encodeURIComponent(returnUrl)}`);
  await page.waitForLoadState('networkidle');
}
/** lang and dir of the page, any raw resource key or unformatted placeholder in the visible text, and horizontal overflow. */
async function screen(page, culture, scope = 'body') {
  const m = await page.evaluate(sel => ({
    lang: document.documentElement.lang, dir: document.documentElement.dir,
    text: document.querySelector(sel)?.innerText ?? '',
    scrollWidth: document.documentElement.scrollWidth, innerWidth: window.innerWidth,
  }), scope);
  const rawKeys = [...new Set(m.text.match(RAW_KEY) ?? [])];
  const placeholders = m.text.match(/\{\d\}/g) ?? [];
  const wantLang = culture.slice(0, 2), wantDir = culture === 'ar-SA' ? 'rtl' : 'ltr';
  return {
    ok: m.lang === wantLang && m.dir === wantDir && rawKeys.length === 0 && placeholders.length === 0 && m.scrollWidth <= m.innerWidth,
    lang: m.lang, dir: m.dir, rawKeys, placeholders, overflow: m.scrollWidth > m.innerWidth, text: m.text,
  };
}
const brief = s => ({ lang: s.lang, dir: s.dir, rawKeys: s.rawKeys, placeholders: s.placeholders, overflow: s.overflow });

// ---- Test data: realistic Saudi values, unique per run ----
const state = loadState();
const run = Date.now().toString();
const suffix = run.slice(-6);
const crA = `1010${suffix}`;
const crB = `4030${suffix}`;
const vatA = `3${run.slice(-13)}3`;
const vatB = `3${run.slice(-13, -1)}03`;
const companyA = { cr: crA, nameAr: `شركة الوصل للتجارة ${suffix}`, nameEn: `Al Wasl Trading ${suffix}`, vat: vatA };
const companyB = { cr: crB, nameAr: `مؤسسة البحر الأحمر للمقاولات ${suffix}`, nameEn: `Red Sea Contracting ${suffix}`, vat: vatB };
const person = (label, firstName, lastName) => ({ email: `${label}.${run}@vendor.waslabid.test`, password: newPassword(), firstName, lastName });
const squatter = person('squatter', 'سالم', 'الدوسري');
const owner = person('owner', 'فهد', 'العتيبي');
const bystander = person('bystander', 'نورة', 'القحطاني');
const decoys = [1, 2, 3, 4, 5, 6].map(i => person(`decoy${i}`, 'مدعي', `رقم ${i}`));
const approvalNote = 'طابقتُ السجل التجاري مع اسم المسجِّل وخطاب التفويض.\nCR certificate checked against the registrant and the authorisation letter.';
const statement = 'نحن المالك الحقيقي لهذا السجل التجاري، ولدينا خطاب من المدير العام.\nWe own this CR; our general manager can confirm by phone on the registered number.';
const upholdNote = 'تم التحقق من خطاب التفويض عبر القناة الرسمية للشركة.\nAuthorisation letter confirmed through the company\'s registered phone number.';
const rejectNote = 'لم يقدّم المدعي ما يثبت الملكية.\nNo proof of ownership was provided.';
const stateDir = path.join(DIR, '.state');
fs.mkdirSync(stateDir, { recursive: true });
const crFile = path.join(stateDir, 'ownership-cr-certificate.pdf');
fs.writeFileSync(crFile, pdf(`CR ${crA}`));
const future = new Date(Date.now() + 365 * 86400000).toISOString().slice(0, 10);

const ACME_ID = tenantId('acme');
const BETA_ID = tenantId('beta');
const companyId = cr => sql(`select id from vendor.companies where cr_number = ${q(cr)};`);
const disputeOf = email => sql(`select id from vendor.cr_disputes where claimant_email = ${q(email)} order by raised_at desc limit 1;`);
const disputeRow = id => {
  const [status, note, removed, idp, superseded, reviewed, statementDb] = sql(`select status, coalesce(resolution_note,''), coalesce(array_to_string(removed_user_ids, ','),''), coalesce(idp_outcome,''), coalesce(superseded_verification::text,''), coalesce(reviewed_by,''), statement from vendor.cr_disputes where id = ${q(id)};`).split('\t');
  return { status, note, removed, idp, superseded, reviewed, statement: statementDb };
};

async function staffPage(browser, slug, culture) {
  const admin = await throwawayStaff(slug, 'contracts-officer', `${slug}-officer`, run);
  const base = slug === 'acme' ? ACME : BETA;
  const { ctx, page } = await newPage(browser);
  watch(page, `${slug} officer`);
  await page.goto(`${base}/admin/vendors`);
  await driveKeycloak(page, { user: admin.email, password: admin.password, state, log: () => {} });
  await setCulture(page, base, culture, '/admin/vendors');
  return { ctx, page, base };
}
async function openApprove(page, base, id) {
  await page.goto(`${base}/admin/vendors/${id}`);
  await page.waitForSelector('[data-approve]');
  await page.waitForTimeout(1500); // circuit up
  await page.click('[data-approve]');
  const dlg = page.locator('[role=dialog]');
  await dlg.waitFor();
  await dlg.locator('[data-ownership-check], [data-ownership-verified]').first().waitFor({ timeout: 20000 });
  return dlg;
}
async function closeDialog(page) {
  await page.keyboard.press('Escape');
  await page.locator('[role=dialog]').waitFor({ state: 'detached', timeout: 5000 }).catch(() => null);
}
async function relationshipOnStaffPage(page, id) {
  await page.waitForSelector(`[data-vendor-status="${id}:approved"]`, { timeout: 20000 }).catch(() => null);
  await page.waitForTimeout(1500); // a circuit that fails on the dialog closing reports it within this time
  return page.locator('[data-vendor-status]').getAttribute('data-vendor-status').catch(() => null);
}

/** Signs a person in on a tenant host path and returns the page there. */
async function personPage(browser, who, url, culture) {
  const { ctx, page } = await newPage(browser);
  watch(page, who.email.split('.')[0]);
  await page.goto(url);
  await driveKeycloak(page, { user: who.email, password: who.password, state, log: () => {} });
  await page.waitForLoadState('networkidle');
  if (culture) await setCulture(page, new URL(url).origin, culture, new URL(url).pathname);
  return { ctx, page };
}
async function raiseDispute(page, cr, text) {
  const form = page.locator('[data-vendor-dispute]');
  await form.locator('input[name="Input.CrNumber"]').fill(cr);
  await form.locator('textarea[name="Input.Statement"]').fill(text);
  await form.locator('input[name="Input.AcceptedPrivacyNotice"]').check();
  await Promise.all([page.waitForLoadState('domcontentloaded'), form.locator('button[type=submit]').click()]);
  await page.waitForSelector('[data-dispute-raised], [data-form-error], [role=alert], .text-danger', { timeout: 30000 }).catch(() => null);
  return page.locator('[data-dispute-raised]').count().then(n => n > 0);
}

// ---- Platform console helpers ----
async function consoleAct(page, action, disputeId, note) {
  // A toast from the previous action sits over the bottom-right of the table (the Reject column); dismiss it first.
  for (const close of await page.locator('[role=status][aria-live] button').all()) await close.click().catch(() => null);
  await page.locator('[role=status][aria-live] button').first().waitFor({ state: 'detached', timeout: 5000 }).catch(() => null);
  await page.click(`[data-${action}="${disputeId}"]`);
  const dlg = page.locator('[role=dialog]');
  await dlg.waitFor();
  if (note !== undefined) {
    await dlg.locator('[data-resolution-note]').fill(note);
    await dlg.locator('[data-resolution-note]').press('Tab');
  }
  const text = await dlg.innerText();
  await dlg.locator('button').last().click();
  await dlg.waitFor({ state: 'detached', timeout: 30000 }).catch(() => null);
  const toast = await page.locator('[role=status][aria-live]').innerText().catch(() => '');
  return { dialogText: text, toast: toast.trim(), stillOpen: await page.locator('[role=dialog]').count() > 0 };
}
const listedDisputes = page => page.$$eval('[data-dispute]', es => es.map(e => e.getAttribute('data-dispute')));

// Disputes this script raised (decoys and owners are throwaway claimants) that are still pending, oldest first.
const OWN_CLAIMANTS = String.raw`claimant_email ~ '^(decoy[0-9]+|owner)\.[0-9]+@vendor\.waslabid\.test$'`;
const leftovers = () => sql(`select id from vendor.cr_disputes where status in ('open', 'under_review') and ${OWN_CLAIMANTS} order by raised_at;`).split('\n').filter(Boolean);

// `node ownership.mjs sweep`: a run that failed before resolving its disputes leaves them pending in the console; this
// rejects them there, through the console and its audit, as a throwaway platform admin, and changes nothing else.
async function sweep(browser) {
  const pending = leftovers();
  if (pending.length === 0) { rec('sweep: no pending dispute left by an earlier run', true, {}); return; }
  const admin = await throwawayPlatformAdmin(run);
  const { ctx, page } = await newPage(browser);
  await page.goto(`${PLATFORM}/platform/vendors`);
  await driveKeycloak(page, { user: admin.username, password: admin.password, state, log: () => {} });
  await setCulture(page, PLATFORM, 'en-US', '/platform/vendors');
  await page.waitForSelector('[data-ownership-method]', { timeout: 30000 }); await page.waitForTimeout(1500);
  for (const id of pending) {
    await consoleAct(page, 'reject', id, 'End-to-end test data from an earlier failed run of ownership.mjs.\nNo claim to review.');
    rec(`sweep: dispute ${id} rejected in the console`, disputeRow(id).status === 'rejected', { status: disputeRow(id).status });
  }
  await ctx.close();
}

const browser = await launch();
if (process.argv[2] === 'sweep') {
  try { await sweep(browser); } catch (e) { rec('script error', false, { error: String(e).slice(0, 800) }); } finally {
    await browser.close();
    const removed = await cleanup().catch(e => [{ error: String(e).slice(0, 300) }]);
    rec('cleanup: the throwaway platform admin deleted', removed.every(r => r.keycloakDelete === 204), removed);
    if (results.some(r => !r.ok)) process.exitCode = 1;
  }
  process.exit();
}
try {
  // ---- Setup: throwaway people ----
  const squatterUid = await throwawayPerson(squatter.email, squatter.password, squatter.firstName, squatter.lastName);
  const ownerUid = await throwawayPerson(owner.email, owner.password, owner.firstName, owner.lastName);
  await throwawayPerson(bystander.email, bystander.password, bystander.firstName, bystander.lastName);
  for (const d of decoys) await throwawayPerson(d.email, d.password, d.firstName, d.lastName);
  const platformAdmin = await throwawayPlatformAdmin(run);

  // ==== 1. The squatter registers company A, uploads its CR certificate ====
  const { ctx: sctx, page: sq } = await personPage(browser, squatter, `${ACME}/vendor/register/company`);
  const regA = await registerCompany(sq, { ...companyA, email: squatter.email, contact: 'سالم الدوسري' });
  rec('setup: the squatter registers company A on acme', regA.registered, { cr: crA, ...regA });
  await Promise.all([sq.waitForNavigation(), sq.click('[data-vendor-sign-out] button[type=submit]')]);
  await signInAgain(sq, { user: squatter.email, password: squatter.password, state, log: () => {} });
  await sq.waitForSelector('[data-vendor-company]', { timeout: 30000 });
  await sq.waitForTimeout(1500);
  const crState = await upload(sq, 'cr_certificate', crFile, future);
  await sq.reload(); await sq.waitForSelector('[data-vendor-company]'); await sq.waitForTimeout(1000);
  const docStatuses = await sq.$$eval('[data-document-status]', es => es.map(e => e.getAttribute('data-document-status')));
  rec('setup: the squatter uploads a current, clean CR certificate', crState === 'done' && docStatuses.includes('cr_certificate:current'), { crState, docStatuses });
  const idA = companyId(crA);

  // ==== 1a. acme's officer opens the check in Arabic ====
  const { ctx: actx, page: acme } = await staffPage(browser, 'acme', 'ar-SA');
  let dlg = await openApprove(acme, ACME, idA);
  const arCheck = await screen(acme, 'ar-SA', '[role=dialog]');
  const arName = await dlg.locator('[data-registrant-name]').innerText().catch(() => '');
  const arEmail = await dlg.locator('[data-registrant-email]').getAttribute('data-registrant-email').catch(() => null);
  const arEmailText = await dlg.locator('[data-registrant-email]').innerText().catch(() => '');
  const arLookup = await dlg.locator('[data-ownership-lookup]').getAttribute('data-ownership-lookup').catch(() => null);
  const arShot = await shot(acme, '01-acme-approve-check-ar');
  rec('acme officer (ar-SA): the first approval shows the ownership check with CR, self-declared name, verified email, right to left, no raw keys',
    arCheck.ok && arCheck.text.includes(crA) && arName.includes('سالم الدوسري') && arName.includes(R['ar-SA']['Admin.Vendors.Ownership.SelfDeclared'])
      && arEmail === 'verified' && arEmailText.includes(R['ar-SA']['Admin.Vendors.Ownership.EmailVerified'])
      && await dlg.locator('[data-ownership-confirm]').count() === 1 && await dlg.locator('textarea[data-ownership-note]').count() === 1,
    { ...brief(arCheck), registrant: arName, email: arEmail, emailText: arEmailText, lookup: arLookup, screenshot: arShot });

  // Refused without the box, then without a note.
  await dlg.locator('button').last().click();
  await acme.waitForTimeout(1000);
  const noBox = await dlg.innerText();
  await dlg.locator('[data-ownership-confirm]').check();
  await dlg.locator('button').last().click();
  await acme.waitForTimeout(1500);
  const noNote = await dlg.innerText();
  const noNoteShot = await shot(acme, '02-acme-approve-refused-no-note-ar');
  const stillPending = sql(`select status from vendor.relationships where tenant_id = ${q(ACME_ID)} and company_id = ${q(idA)};`);
  rec('acme officer (ar-SA): approve is refused without the confirm box and then without a note, in Arabic',
    noBox.includes(R['ar-SA']['Admin.Vendors.Ownership.Error.NotConfirmed']) && noNote.includes(R['ar-SA']['Admin.Vendors.Ownership.Error.Note']) && stillPending === 'pending',
    { notConfirmedShown: noBox.includes(R['ar-SA']['Admin.Vendors.Ownership.Error.NotConfirmed']), noteErrorShown: noNote.includes(R['ar-SA']['Admin.Vendors.Ownership.Error.Note']), relationship: stillPending, screenshot: noNoteShot });
  await closeDialog(acme);

  // ==== 1b. The platform admin switches the method to Wathq, which is not set up ====
  const { ctx: pctx, page: plat } = await newPage(browser);
  watch(plat, 'platform admin');
  await plat.goto(`${PLATFORM}/platform/vendors`);
  const psteps = [];
  await driveKeycloak(plat, { user: platformAdmin.username, password: platformAdmin.password, state, log: s => psteps.push(s) });
  await setCulture(plat, PLATFORM, 'en-US', '/platform/vendors');
  await plat.waitForSelector('[data-ownership-method]', { timeout: 30000 });
  await plat.waitForTimeout(1500);
  const methodBefore = await plat.locator('[data-ownership-method]').getAttribute('data-ownership-method');
  const wathqConfigured = await plat.locator('[data-wathq-configured]').getAttribute('data-wathq-configured');
  rec('console: the platform admin signs in with a one-time code; method Manual, Wathq shown not set up',
    plat.url() === `${PLATFORM}/platform/vendors` && methodBefore === 'manual' && wathqConfigured === 'false', { url: plat.url(), methodBefore, wathqConfigured, keycloak: psteps });
  await plat.locator('select[data-method-select]').selectOption('wathq');
  await plat.locator('[data-wathq-fallback]').waitFor({ timeout: 10000 }).catch(() => null);
  const fallbackEn = await plat.locator('[data-wathq-fallback]').innerText().catch(() => null);
  const enConsole = await screen(plat, 'en-US');
  const fallbackEnShot = await shot(plat, '03-console-wathq-fallback-en');
  await plat.click('[data-save-method]');
  await plat.waitForSelector('[data-ownership-method="wathq"]', { timeout: 15000 }).catch(() => null);
  const methodSaved = sql('select method from vendor.ownership_settings;');
  rec('console (en-US): choosing Wathq while it is not set up shows the manual fallback message; saved as wathq',
    fallbackEn === R['en-US']['Console.Ownership.Wathq.Fallback'] && enConsole.ok && methodSaved === 'wathq',
    { fallback: fallbackEn, ...brief(enConsole), methodSaved, screenshot: fallbackEnShot });
  await setCulture(plat, PLATFORM, 'ar-SA', '/platform/vendors');
  await plat.waitForSelector('[data-wathq-fallback]', { timeout: 15000 }).catch(() => null);
  const fallbackAr = await plat.locator('[data-wathq-fallback]').innerText().catch(() => null);
  const arConsole = await screen(plat, 'ar-SA');
  const fallbackArShot = await shot(plat, '04-console-wathq-fallback-ar');
  rec('console (ar-SA): the saved Wathq method shows the fallback message right to left, no raw keys',
    fallbackAr === R['ar-SA']['Console.Ownership.Wathq.Fallback'] && arConsole.ok, { fallback: fallbackAr, ...brief(arConsole), screenshot: fallbackArShot });

  // ==== 1c. The officer approves in English under Wathq-not-set-up, with a multi-line note ====
  await setCulture(acme, ACME, 'en-US', `/admin/vendors/${idA}`);
  dlg = await openApprove(acme, ACME, idA);
  const enCheck = await screen(acme, 'en-US', '[role=dialog]');
  const enLookup = await dlg.locator('[data-ownership-lookup]').getAttribute('data-ownership-lookup').catch(() => null);
  const enLookupText = await dlg.locator('[data-ownership-lookup]').innerText().catch(() => null);
  const enName = await dlg.locator('[data-registrant-name]').innerText().catch(() => '');
  const enShot = await shot(acme, '05-acme-approve-check-wathq-not-configured-en');
  rec('acme officer (en-US): with Wathq selected but not set up, the dialog says so and falls back to the manual check; self-declared label; no raw keys',
    enCheck.ok && enLookup === 'not-configured' && enLookupText === R['en-US']['Admin.Vendors.Ownership.Lookup.NotConfigured']
      && enName.includes(R['en-US']['Admin.Vendors.Ownership.SelfDeclared']) && await dlg.locator('[data-ownership-confirm]').count() === 1,
    { ...brief(enCheck), lookup: enLookup, lookupText: enLookupText, registrant: enName, screenshot: enShot });
  await dlg.locator('[data-ownership-confirm]').check();
  await dlg.locator('[data-ownership-note]').fill(approvalNote);
  await dlg.locator('[data-ownership-note]').press('Tab');
  const noteRendered = await dlg.locator('[data-ownership-note]').inputValue();
  await dlg.locator('button').last().click();
  const approvedA = await relationshipOnStaffPage(acme, idA);
  const [vMethod, vNote, vRegistrant, vTenant] = sql(`select method, note, registrant_user_id, verified_in_tenant from vendor.ownership_verifications where company_id = ${q(idA)};`).split('\t');
  const approvedShot = await shot(acme, '06-acme-approved-en');
  rec('acme officer: approves company A with a two-line Arabic and English note, stored once with its line feed',
    approvedA === `${idA}:approved` && vNote === approvalNote && noteRendered === approvalNote && vRegistrant === squatterUid && vTenant === ACME_ID,
    { approvedA, method: vMethod, noteHasLineFeed: vNote?.includes('\n'), noteMatches: vNote === approvalNote, registrantIsSquatter: vRegistrant === squatterUid, screenshot: approvedShot });

  // The method goes back to Manual (the default) before anything else runs.
  await setCulture(plat, PLATFORM, 'en-US', '/platform/vendors');
  await plat.waitForSelector('[data-ownership-method]'); await plat.waitForTimeout(1500);
  await plat.locator('select[data-method-select]').selectOption('manual');
  await plat.click('[data-save-method]');
  await plat.waitForSelector('[data-ownership-method="manual"]', { timeout: 15000 }).catch(() => null);
  const methodBack = sql('select method from vendor.ownership_settings;');
  rec('console: the method is switched back to Manual and the fallback message goes', methodBack === 'manual' && await plat.locator('[data-wathq-fallback]').count() === 0, { methodBack });

  // ==== 2. The squatter joins beta; beta's officer approves without the check ====
  await sq.goto(`${BETA}/vendor`);
  await driveKeycloak(sq, { user: squatter.email, password: squatter.password, state, log: () => {} });
  await sq.waitForSelector('[data-vendor-join-form]', { timeout: 30000 });
  await Promise.all([sq.waitForLoadState('domcontentloaded'), sq.click('[data-vendor-join-form] button[type=submit]')]);
  await sq.waitForSelector('[data-vendor-joined]', { timeout: 30000 }).catch(() => null);
  const betaRel = sql(`select status from vendor.relationships where tenant_id = ${q(BETA_ID)} and company_id = ${q(idA)};`);
  rec('setup: the squatter joins beta (pending there)', betaRel === 'pending', { betaRel });
  const { ctx: bctx, page: beta } = await staffPage(browser, 'beta', 'ar-SA');
  dlg = await openApprove(beta, BETA, idA);
  const betaVerified = await dlg.locator('[data-ownership-verified]').getAttribute('data-ownership-verified').catch(() => null);
  const betaCheck = await screen(beta, 'ar-SA', '[role=dialog]');
  const betaDialogText = betaCheck.text;
  const betaShot = await shot(beta, '07-beta-approve-no-check-ar');
  rec('beta officer (ar-SA): the second tenant approves without the check and learns only the method',
    betaVerified === 'manual' && await dlg.locator('[data-ownership-check]').count() === 0 && betaCheck.ok
      && !betaDialogText.includes('سالم') && !betaDialogText.includes(squatter.email) && !betaDialogText.includes('acme'),
    { verified: betaVerified, ...brief(betaCheck), revealsRegistrant: betaDialogText.includes('سالم') || betaDialogText.includes(squatter.email), screenshot: betaShot });
  await dlg.locator('button').last().click();
  const approvedB = await relationshipOnStaffPage(beta, idA);
  const verifications = sql(`select count(*) from vendor.ownership_verifications where company_id = ${q(idA)};`);
  rec('beta officer: company A approved at beta; still one ownership verification', approvedB === `${idA}:approved` && verifications === '1', { approvedB, verifications });

  // ==== 3. A bystander registers company B; five decoys dispute A and one disputes B ====
  const { ctx: byctx, page: by } = await personPage(browser, bystander, `${ACME}/vendor/register/company`);
  const regB = await registerCompany(by, { ...companyB, email: bystander.email, contact: 'نورة القحطاني' });
  await byctx.close();
  const idB = companyId(crB);
  rec('setup: a bystander registers company B', regB.registered && !!idB, regB);
  const decoyOrder = [[decoys[0], crA], [decoys[5], crB], [decoys[1], crA], [decoys[2], crA], [decoys[3], crA], [decoys[4], crA]];
  const decoyResults = [];
  for (const [d, cr] of decoyOrder) {
    const { ctx, page } = await personPage(browser, d, `${ACME}/vendor/dispute`);
    decoyResults.push({ who: d.email.split('.')[0], cr, raised: await raiseDispute(page, cr, `Decoy claim ${d.lastName}.\nمطالبة تجريبية.`) });
    await ctx.close();
    await sleep(1100); // distinct raised_at, so "oldest first" has one answer
  }
  rec('setup: five decoy disputes on company A and one on company B are raised', decoyResults.every(r => r.raised), decoyResults);

  // ==== 4. The owner is refused with a link to /vendor/dispute and raises the sixth dispute on A ====
  const { ctx: octx, page: own } = await personPage(browser, owner, `${ACME}/vendor/register/company`, 'ar-SA');
  const dup = await registerCompany(own, { cr: crA, nameAr: 'شركة الوصل للتجارة', nameEn: 'Al Wasl Trading', vat: `3${run.slice(-13, -2)}003`, email: owner.email, contact: 'فهد العتيبي' });
  const dupScreen = await screen(own, 'ar-SA');
  const link = own.locator('[data-dispute-link]');
  const linkHref = await link.evaluate(a => a.href).catch(() => null);
  const linkText = await link.innerText().catch(() => null);
  const dupShot = await shot(own, '08-owner-duplicate-refusal-ar');
  rec('owner (ar-SA): a registration with the taken CR is refused without naming the company and links to /vendor/dispute',
    !dup.registered && dup.formError === R['ar-SA']['Vendor.Register.Error.Duplicate'] && linkHref === `${ACME}/vendor/dispute`
      && linkText === R['ar-SA']['Vendor.Register.DisputeLink'] && dupScreen.ok && !dupScreen.text.includes(suffix),
    { formError: dup.formError, linkHref, linkText, ...brief(dupScreen), namesCompany: dupScreen.text.includes(suffix), screenshot: dupShot });
  await Promise.all([own.waitForNavigation(), link.click()]);
  await own.waitForSelector('[data-vendor-dispute]');
  const disputeAr = await screen(own, 'ar-SA');
  const textareaDir = await own.locator('textarea[name="Input.Statement"]').evaluate(e => getComputedStyle(e).direction);
  const disputeArShot = await shot(own, '09-dispute-form-ar');
  rec('owner (ar-SA): /vendor/dispute renders right to left with a multi-line statement field, no raw keys',
    own.url() === `${ACME}/vendor/dispute` && disputeAr.ok && textareaDir === 'rtl', { url: own.url(), ...brief(disputeAr), textareaDir, screenshot: disputeArShot });
  // An empty statement shows the error in Arabic (this post counts toward the five-per-fifteen-minutes limit).
  await raiseDispute(own, crA, '');
  const emptyErr = await own.locator('[data-vendor-dispute]').innerText().catch(() => '');
  const emptyShot = await shot(own, '10-dispute-empty-statement-ar');
  rec('owner (ar-SA): an empty statement is refused with the Arabic message',
    emptyErr.includes(R['ar-SA']['Vendor.Dispute.Error.Statement']) && await own.locator('[data-dispute-raised]').count() === 0, { shown: emptyErr.includes(R['ar-SA']['Vendor.Dispute.Error.Statement']), screenshot: emptyShot });
  await setCulture(own, ACME, 'en-US', '/vendor/dispute');
  const disputeEn = await screen(own, 'en-US');
  const disputeEnShot = await shot(own, '11-dispute-form-en');
  rec('owner (en-US): /vendor/dispute renders left to right, no raw keys', disputeEn.ok, { ...brief(disputeEn), screenshot: disputeEnShot });
  await setCulture(own, ACME, 'ar-SA', '/vendor/dispute');
  const raised = await raiseDispute(own, crA, statement);
  const ownStatus = await own.locator(`[data-own-dispute="${crA}"] [data-own-dispute-status]`).getAttribute('data-own-dispute-status').catch(() => null);
  const raisedShot = await shot(own, '12-dispute-raised-ar');
  const ownerDispute = disputeOf(owner.email);
  const ownerRow = disputeRow(ownerDispute);
  rec('owner: the sixth dispute on company A is raised, not refused, listed open, statement stored with its line feed',
    raised && ownStatus === 'open' && ownerRow.status === 'open' && ownerRow.statement === statement,
    { raised, ownStatus, db: ownerRow.status, statementMatches: ownerRow.statement === statement, screenshot: raisedShot });

  // ==== 5. Console: grouped by company, over-cap badge, accept, uphold, reject ====
  const decoyIds = decoyOrder.map(([d]) => disputeOf(d.email));
  const expectedOrder = [decoyIds[0], decoyIds[2], decoyIds[3], decoyIds[4], decoyIds[5], ownerDispute, decoyIds[1]];
  await setCulture(plat, PLATFORM, 'ar-SA', '/platform/vendors');
  await plat.waitForSelector(`[data-dispute="${ownerDispute}"]`, { timeout: 20000 }).catch(() => null);
  await plat.waitForTimeout(1000);
  const listed = (await listedDisputes(plat)).filter(id => expectedOrder.includes(id));
  const overCap = await plat.$$eval('[data-over-cap]', es => es.map(e => e.getAttribute('data-over-cap')));
  const listAr = await screen(plat, 'ar-SA');
  const ownerStatementCell = await plat.locator(`[data-dispute="${ownerDispute}"]`).locator('xpath=ancestor::tr').innerText().catch(() => '');
  const listShot = await shot(plat, '13-console-disputes-ar');
  rec('console (ar-SA): disputes grouped by company (A first, its oldest first, then B); only the owner\'s is badged over the cap; statement keeps its line break',
    JSON.stringify(listed) === JSON.stringify(expectedOrder) && overCap.filter(o => expectedOrder.some(id => o.startsWith(id))).join() === `${ownerDispute}:6`
      && listAr.ok && ownerStatementCell.includes('ولدينا خطاب من المدير العام.\nWe own this CR'),
    { listed, expectedOrder, overCap, ...brief(listAr), statementLineBreak: ownerStatementCell.includes('العام.\nWe own'), screenshot: listShot });

  const accepted = await consoleAct(plat, 'accept', ownerDispute);
  await plat.waitForSelector(`[data-dispute-status="${ownerDispute}:under_review"]`, { timeout: 15000 }).catch(() => null);
  const underReview = disputeRow(ownerDispute);
  const acceptShot = await shot(plat, '14-console-under-review-ar');
  rec('console: the platform admin accepts the owner\'s dispute for review (under review, reviewer recorded)',
    underReview.status === 'under_review' && underReview.reviewed === platformAdmin.uid && await plat.locator(`[data-dispute-status="${ownerDispute}:under_review"]`).count() === 1,
    { status: underReview.status, reviewerIsAdmin: underReview.reviewed === platformAdmin.uid, toast: accepted.toast, screenshot: acceptShot });

  await setCulture(plat, PLATFORM, 'en-US', '/platform/vendors');
  await plat.waitForSelector(`[data-uphold="${ownerDispute}"]`, { timeout: 15000 }); await plat.waitForTimeout(1000);
  const upheld = await consoleAct(plat, 'uphold', ownerDispute, upholdNote);
  await plat.waitForSelector(`[data-dispute="${ownerDispute}"]`, { state: 'detached', timeout: 30000 }).catch(() => null);
  const upheldRow = disputeRow(ownerDispute);
  const upholdShot = await shot(plat, '15-console-upheld-en');
  const idpFailures = await plat.locator('[data-idp-failures]').count();
  rec('console (en-US): the admin upholds with a two-line note; the dispute leaves the list; identity provider updated, no retry listed',
    upheldRow.status === 'upheld' && upheldRow.note === upholdNote && upheldRow.idp === 'updated' && idpFailures === 0
      && upheld.toast.includes(`${companyA.nameEn} now belongs to فهد العتيبي.`) && await plat.locator(`[data-dispute="${ownerDispute}"]`).count() === 0,
    { status: upheldRow.status, noteMatches: upheldRow.note === upholdNote, idp: upheldRow.idp, removed: upheldRow.removed === squatterUid ? 'the squatter' : upheldRow.removed, toast: upheld.toast, idpFailures, screenshot: upholdShot });

  const users = sql(`select user_id || ':' || role from vendor.vendor_users where company_id = ${q(idA)};`).split('\n').filter(Boolean);
  const [newMethod, newRegistrant] = sql(`select method, registrant_user_id from vendor.ownership_verifications where company_id = ${q(idA)};`).split('\t');
  const superseded = upheldRow.superseded ? JSON.parse(upheldRow.superseded) : null;
  const decoysAfter = [0, 2, 3, 4, 5].map(i => disputeRow(decoyIds[i]).status);
  const bAfter = disputeRow(decoyIds[1]).status;
  const relsAfter = sql(`select string_agg(status, ',' order by tenant_id) from vendor.relationships where company_id = ${q(idA)};`);
  rec('database: the owner is company A\'s only vendor user (vendor-admin); ownership recorded as dispute with the replaced manual check kept; the decoys\' disputes on A closed, B\'s still open; both approvals kept',
    users.length === 1 && users[0] === `${ownerUid}:vendor-admin` && newMethod === 'dispute' && newRegistrant === ownerUid
      && superseded && JSON.stringify(superseded).includes('manual') && decoysAfter.every(s => !['open', 'under_review'].includes(s)) && bAfter === 'open' && relsAfter === 'approved,approved',
    { users: users.map(u => u.replace(ownerUid, 'owner').replace(squatterUid, 'squatter')), method: newMethod, supersededKept: !!superseded, supersededMethod: superseded?.method, decoysAfter, companyBDispute: bAfter, relationships: relsAfter });

  const ownerRoles = await realmRoles(ownerUid);
  const squatterRoles = await realmRoles(squatterUid);
  const kcState = {
    ownerVendorRole: ownerRoles.includes('vendor'), ownerInAcme: await isMember('acme', ownerUid), ownerInBeta: await isMember('beta', ownerUid),
    squatterVendorRole: squatterRoles.includes('vendor'), squatterInAcme: await isMember('acme', squatterUid), squatterInBeta: await isMember('beta', squatterUid),
  };
  rec('keycloak: the owner holds the realm role vendor and both organizations; the squatter holds neither',
    kcState.ownerVendorRole && kcState.ownerInAcme && kcState.ownerInBeta && !kcState.squatterVendorRole && !kcState.squatterInAcme && !kcState.squatterInBeta, kcState);

  const rejected = await consoleAct(plat, 'reject', decoyIds[1], rejectNote);
  await plat.waitForSelector(`[data-dispute="${decoyIds[1]}"]`, { state: 'detached', timeout: 30000 }).catch(() => null);
  const rejectedRow = disputeRow(decoyIds[1]);
  const bUsers = sql(`select count(*) from vendor.vendor_users where company_id = ${q(idB)};`);
  const rejectShot = await shot(plat, '16-console-rejected-en');
  rec('console: the admin rejects company B\'s dispute with a two-line note; nothing else changes',
    rejectedRow.status === 'rejected' && rejectedRow.note === rejectNote && bUsers === '1' && await plat.locator(`[data-dispute="${decoyIds[1]}"]`).count() === 0,
    { status: rejectedRow.status, noteMatches: rejectedRow.note === rejectNote, companyBUsers: bUsers, toast: rejected.toast, screenshot: rejectShot });

  // ==== 6. The owner signs in again; the squatter is refused ====
  await octx.close();
  const { ctx: o2ctx, page: o2 } = await newPage(browser);
  watch(o2, 'owner');
  const ownerNav = [];
  o2.on('framenavigated', f => { if (f === o2.mainFrame()) ownerNav.push(new URL(f.url()).origin + new URL(f.url()).pathname); });
  await o2.goto(`${ACME}/vendor`);
  await driveKeycloak(o2, { user: owner.email, password: owner.password, state, log: () => {} });
  await o2.waitForSelector('[data-vendor-company]', { timeout: 30000 }).catch(() => null);
  await o2.waitForTimeout(1000);
  const acmeHome = { url: o2.url(), company: await o2.locator('[data-vendor-company]').innerText().catch(() => null), relationship: await o2.locator('[data-relationship]').getAttribute('data-relationship').catch(() => null) };
  const acmeHomeShot = await shot(o2, '17-owner-acme-vendor-home');
  await o2.goto(`${BETA}/vendor`);
  await driveKeycloak(o2, { user: owner.email, password: owner.password, state, log: () => {} });
  await o2.waitForSelector('[data-vendor-company]', { timeout: 30000 }).catch(() => null);
  await o2.waitForTimeout(1000);
  const betaHome = { url: o2.url(), company: await o2.locator('[data-vendor-company]').innerText().catch(() => null), relationship: await o2.locator('[data-relationship]').getAttribute('data-relationship').catch(() => null) };
  const betaHomeShot = await shot(o2, '18-owner-beta-vendor-home');
  const sawJoin = ownerNav.some(u => u.endsWith('/vendor/join'));
  rec('owner signs in again and reaches /vendor on acme and on beta as company A, approved at both, never /vendor/join',
    acmeHome.url === `${ACME}/vendor` && betaHome.url === `${BETA}/vendor` && acmeHome.relationship === 'approved' && betaHome.relationship === 'approved'
      && (acmeHome.company ?? '').match(new RegExp(`${companyA.nameAr}|${companyA.nameEn}`)) && !sawJoin,
    { acme: acmeHome, beta: betaHome, sawJoin, navigations: ownerNav.filter(u => !u.startsWith('http://localhost:8080')), screenshots: [acmeHomeShot, betaHomeShot] });
  await o2.goto(`${ACME}/vendor/dispute`); await o2.waitForLoadState('networkidle');
  const ownListed = await o2.locator(`[data-own-dispute="${crA}"] [data-own-dispute-status]`).getAttribute('data-own-dispute-status').catch(() => null);
  rec('owner: /vendor/dispute lists their dispute as upheld', ownListed === 'upheld', { ownListed });

  // Checked before the squatter's refusals, whose 403s the browser logs as console errors on purpose.
  rec('no browser console errors (no failed circuit) on any page', consoleErrors.length === 0, { consoleErrors });

  // A 403 with an empty body makes Chromium fail the navigation (net::ERR_HTTP_RESPONSE_CODE_FAILURE), so the status is
  // read from the navigation responses rather than from goto's return value.
  const navLog = page => {
    const seen = [];
    page.on('response', r => { if (r.request().isNavigationRequest() && r.url().includes('.localhost:8443')) seen.push(`${r.status()} ${new URL(r.url()).origin}${new URL(r.url()).pathname}`); });
    return seen;
  };
  const tryGoto = (page, url, seen) => page.goto(url).catch(e => seen.push(`goto: ${String(e).split('\n')[0].slice(0, 120)}`));
  const oldNav = navLog(sq);
  await tryGoto(sq, `${ACME}/vendor`, oldNav);
  const oldSessionCompany = await sq.locator('[data-vendor-company]').count();
  const oldShot = await shot(sq, '19-squatter-old-session-refused');
  const oldLast = oldNav.filter(f => /^\d{3} /.test(f) && f.endsWith('acme.localhost:8443/vendor')).pop();
  rec('squatter: the session opened before the uphold is refused on acme /vendor (no sign-in asked, 403)',
    oldLast?.startsWith('403') && oldSessionCompany === 0, { navigations: oldNav, companyShown: oldSessionCompany > 0, screenshot: oldShot });
  await sctx.close();
  const { ctx: s2ctx, page: s2 } = await newPage(browser);
  const fresh = navLog(s2);
  await tryGoto(s2, `${ACME}/vendor`, fresh);
  await driveKeycloak(s2, { user: squatter.email, password: squatter.password, state, log: () => {} }).catch(e => fresh.push(`keycloak: ${String(e).split('\n')[0].slice(0, 120)}`));
  await s2.waitForLoadState('networkidle').catch(() => null);
  const freshCompany = await s2.locator('[data-vendor-company]').count();
  const freshAcmeShot = await shot(s2, '20-squatter-fresh-sign-in-acme-refused');
  await tryGoto(s2, `${BETA}/vendor/join`, fresh);
  await driveKeycloak(s2, { user: squatter.email, password: squatter.password, state, log: () => {} }).catch(e => fresh.push(`keycloak: ${String(e).split('\n')[0].slice(0, 120)}`));
  await s2.waitForLoadState('networkidle').catch(() => null);
  const joinForm = await s2.locator('[data-vendor-join-form]').count();
  const freshBetaShot = await shot(s2, '21-squatter-fresh-sign-in-beta-join-refused');
  const lastAcme = fresh.filter(f => /^\d{3} /.test(f) && f.endsWith('acme.localhost:8443/vendor')).pop();
  const lastBeta = fresh.filter(f => /^\d{3} /.test(f) && f.endsWith('beta.localhost:8443/vendor/join')).pop();
  rec('squatter: a fresh sign-in is refused on acme /vendor and on beta /vendor/join (403, no company, no join form)',
    freshCompany === 0 && joinForm === 0 && lastAcme?.startsWith('403') && lastBeta?.startsWith('403'),
    { navigations: fresh, screenshots: [freshAcmeShot, freshBetaShot] });
  await s2ctx.close();

  // ==== 7. One alert email for the new disputes, without personal data ====
  const ours = [...decoyIds, ownerDispute];
  let alerted = '0';
  for (let i = 0; i < 70 && alerted !== String(ours.length); i++) {
    alerted = sql(`select count(*) from vendor.cr_disputes where id in (${ours.map(q).join(',')}) and alerted_at is not null;`);
    if (alerted !== String(ours.length)) await sleep(5000);
  }
  const mails = (await mailpit('subject:"CR ownership dispute"')).filter(m => new Date(m.Created) >= started);
  const bodies = await Promise.all(mails.map(m => mailBody(m.ID)));
  const personal = [crA, crB, owner.email, squatter.email, 'فهد', 'سالم', companyA.nameEn, companyA.nameAr];
  const leaks = bodies.flatMap(b => personal.filter(p => `${b.Subject}\n${b.Text}\n${b.HTML}`.includes(p)));
  rec('alert: the platform admins get the new disputes by email once, without CR numbers, names or email addresses',
    alerted === String(ours.length) && mails.length >= 1 && leaks.length === 0,
    { alerted: `${alerted} of ${ours.length}`, subjects: mails.map(m => m.Subject), to: mails.map(m => m.To?.map(t => t.Address).join(',')), leaks });

  await Promise.all([actx.close(), bctx.close(), pctx.close(), o2ctx.close()]);
} catch (e) {
  rec('script error', false, { error: String(e).slice(0, 800) });
} finally {
  await browser.close();
  // The method back to Manual whatever happened above (the default; an earlier failure may have left it on Wathq).
  const left = leftovers();
  if (left.length) rec('pending disputes left by this run (run `node ownership.mjs sweep` to reject them in the console)', false, { left });
  const method = sql('select method from vendor.ownership_settings;');
  if (method !== 'manual') sql(`update vendor.ownership_settings set method = 'manual';`);
  const removed = await cleanup().catch(e => [{ error: String(e).slice(0, 300) }]);
  rec('cleanup: every throwaway user deleted from Keycloak, staff member rows deleted, method Manual',
    removed.every(r => r.keycloakDelete === 204 && (r.kind !== 'staff' || r.memberRows === '1')) && sql('select method from vendor.ownership_settings;') === 'manual',
    { methodWas: method, removed: removed.map(r => ({ kind: r.kind, keycloakDelete: r.keycloakDelete, memberRows: r.memberRows })) });
  fs.writeFileSync(path.join(DIR, 'ownership-results.json'), JSON.stringify(results, null, 2));
  if (results.some(r => !r.ok)) process.exitCode = 1;
}
