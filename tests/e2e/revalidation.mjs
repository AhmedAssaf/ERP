// W-21 end to end through Caddy with real SignalR circuits: a session whose Keycloak organization membership is removed,
// or whose account is disabled, loses access within 5 minutes, including an open Blazor circuit, with an
// identity.session_revoked audit row naming the user and the tenant (docs/09 W-21, docs/03 diagram 5).
//
//   1. A tenant admin of acme has /admin/staff open (interactive, circuit over a WebSocket). The script removes the user
//      from the acme organization through the Keycloak Admin API and, touching nothing in the page, measures the seconds
//      until the page reloads by itself and where it lands: Keycloak's live session signs the user straight back in without
//      the organization, so it must land on the access-removed page (a 403 whose body says the access to the tenant was
//      removed, QA D2), whose Sign out must end the Keycloak session too and reach the Keycloak sign-in page. Then it replays
//      the session's original cookie on a fresh request (must be challenged, 302 to Keycloak), navigates a fresh tab, and
//      reads the audit row. The ended circuit must reload once: more than a handful of navigation requests from the tab is a
//      reload loop (QA D1).
//   2. The same for a second tenant admin whose account is disabled instead; Keycloak refuses a disabled account's session,
//      so this one lands on the Keycloak sign-in page.
//   Control for 1: a third tenant admin signed in with the others and closed its tab (no circuit); it is removed once the
//      scenarios above are done and navigates afresh every 10 seconds, which isolates the HTTP path (timing, and where a
//      removed user with a live Keycloak session lands: the access-removed page, then Sign out to the sign-in page) from
//      anything an open circuit does.
//   3. A vendor working with acme and beta has /vendor open on both hosts; it is removed from the acme organization. The
//      acme tab reloads by itself; /vendor/join on acme then refuses to put it back, in Arabic (right to left) and in
//      English, with no raw resource key on the page, the membership stays absent, and the refusal is audited.
//   4. Over the same window the vendor's beta tab keeps its circuit (no reload, WebSocket open) and a fresh navigation on
//      beta still opens the vendor page; nothing is revoked in beta's audit log.
//   Then access is restored as staff would (member added back, account enabled) and both staff users sign in again, the
//   vendor is added back to acme, and the throwaway staff users are deleted, so the realm is back to its seed state.
//
// The three removals happen at the same moment and are measured independently (the revalidation evidence is per
// organization and user). Users are created per run through the Admin API (the seeded tenant admins are left alone), the
// staff users' member rows are inserted the way the migrator's dev seed does (by email, bound on first sign-in).
// Secrets come from infra/compose/.env (E2E_ENV_FILE) and are never printed (N-10); generated passwords stay in memory.
// Run with `node revalidation.mjs` (about ten minutes). Screenshots in shots-revalidation/, results in
// revalidation-results.json; a failed step sets a non-zero exit code.
import crypto from 'crypto';
import fs from 'fs';
import path from 'path';
import { execFileSync } from 'child_process';
import { request } from 'playwright';
import { launch, newPage, driveKeycloak, envValue, loadState, saveState, sleep, DIR, trackNavigations, waitPastSignOutForm } from './lib.mjs';

const ACME = 'https://acme.localhost:8443';
const BETA = 'https://beta.localhost:8443';
const KC = 'http://localhost:8080';
const REALM = 'waslabid';
const LIMIT_S = 300; // W-21: within 5 minutes
const WATCH_S = 360; // keep watching a minute past the limit, so a late reload is measured rather than missed
const SHOTS = path.join(DIR, 'shots-revalidation');
fs.mkdirSync(SHOTS, { recursive: true });

const results = [];
const rec = (step, ok, seen) => { results.push({ step, ok, seen }); console.log(`${ok ? 'PASS' : 'FAIL'} ${step} :: ${JSON.stringify(seen)}`); };
const note = (step, seen) => { results.push({ step, ok: true, note: true, seen }); console.log(`NOTE ${step} :: ${JSON.stringify(seen)}`); };
const shot = (page, name) => page.screenshot({ path: path.join(SHOTS, `${name}.png`), fullPage: true }).catch(() => null);
const secs = ms => Math.round(ms / 100) / 10;
const state = loadState();
const run = Date.now().toString();
const suffix = run.slice(-6);
const newPassword = () => crypto.randomBytes(12).toString('base64url') + 'aA1!';

// ---- Keycloak Admin API as the master admin (admin-cli tokens live 60 seconds, so one per call) ----
async function kcToken() {
  const body = new URLSearchParams({ grant_type: 'password', client_id: 'admin-cli', username: envValue('KEYCLOAK_ADMIN'), password: envValue('KEYCLOAK_ADMIN_PASSWORD') });
  const r = await fetch(`${KC}/realms/master/protocol/openid-connect/token`, { method: 'POST', body });
  if (!r.ok) throw new Error(`Keycloak admin token refused (${r.status})`);
  return (await r.json()).access_token;
}
async function kc(method, p, body) {
  const headers = { Authorization: `Bearer ${await kcToken()}` };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  return fetch(`${KC}/admin/realms/${REALM}${p}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
}
async function kcOk(method, p, body) {
  const r = await kc(method, p, body);
  if (!r.ok) throw new Error(`Keycloak ${method} ${p.split('?')[0]}: ${r.status} ${(await r.text()).slice(0, 200)}`);
  return r;
}
const orgIds = {};
async function orgId(alias) {
  // Keycloak's search matches the organization's name, not its alias, so list them and match the alias.
  if (!orgIds[alias]) orgIds[alias] = (await (await kcOk('GET', '/organizations?briefRepresentation=true&max=100')).json()).find(o => o.alias === alias).id;
  return orgIds[alias];
}
async function createUser(email, password, firstName, lastName) {
  const r = await kcOk('POST', '/users', { username: email, email, emailVerified: true, enabled: true, firstName, lastName, credentials: [{ type: 'password', value: password, temporary: false }] });
  return r.headers.get('location').split('/').pop();
}
const addMember = async (alias, uid) => kcOk('POST', `/organizations/${await orgId(alias)}/members`, uid);
const removeMember = async (alias, uid) => kcOk('DELETE', `/organizations/${await orgId(alias)}/members/${uid}`);
const isMember = async (alias, uid) => (await kc('GET', `/organizations/${await orgId(alias)}/members/${uid}`)).status === 200;
async function setEnabled(uid, enabled) {
  const user = await (await kcOk('GET', `/users/${uid}`)).json();
  await kcOk('PUT', `/users/${uid}`, { ...user, enabled });
}
const userEnabled = async uid => (await (await kcOk('GET', `/users/${uid}`)).json()).enabled;
const userIdByEmail = async email => (await (await kcOk('GET', `/users?email=${encodeURIComponent(email)}&exact=true`)).json())[0]?.id;

// ---- PostgreSQL as the Compose superuser (test data and audit reads only) ----
// E2E_DB names another database on the same server, for a branch run against a database of its own.
const DB = process.env.E2E_DB || 'platform';
const q = s => `'${String(s).replace(/'/g, "''")}'`;
function sql(query) {
  return execFileSync('docker', ['exec', '-i', '-e', 'PGCLIENTENCODING=UTF8', 'erp-postgres', 'psql', '-U', 'erp', '-d', DB, '-v', 'ON_ERROR_STOP=1', '-q', '-A', '-t', '-F', '\t'],
    { input: query, encoding: 'utf8' }).trim();
}
const tenantId = slug => sql(`select id from tenancy.tenants where slug = ${q(slug)};`);
function auditRows(tenant, action, where) {
  const out = sql(`select actor_id, subject_type, subject_id, data::text, occurred_at from audit.events where tenant_id = ${q(tenant)} and action = ${q(action)} and ${where} order by occurred_at;`);
  return out ? out.split('\n').map(l => { const [actor, subjectType, subject, data, at] = l.split('\t'); return { actor, subjectType, subject, data: JSON.parse(data), at }; }) : [];
}
async function auditRowsSoon(tenant, action, where, atLeast = 1) {
  let rows = [];
  for (let i = 0; i < 10; i++) { rows = auditRows(tenant, action, where); if (rows.length >= atLeast) break; await sleep(1000); }
  return rows;
}

// ---- Browser observation: main-frame navigations, document statuses, the circuit's WebSocket, a marker a reload drops ----
// Blazor's forced reload of the current URL first rewrites the address with history.replaceState (a same-document
// navigation, which fires framenavigated) and then calls location.replace, so a reload is only proven by the marker
// being gone from a newly committed document, not by framenavigated alone.
// Every page the script drives is tracked, so the sign-outs of the run can be checked for id_token_hint in a URL.
const tracked = [];
const track = page => { const t = trackNavigations(page); tracked.push(t); return t; };
function instrument(page) {
  track(page);
  const p = { navs: [], docs: [], docRequests: [], docFailed: [], sockets: [] };
  const mainNav = r => r.isNavigationRequest() && r.frame() === page.mainFrame();
  page.on('framenavigated', f => { if (f === page.mainFrame()) p.navs.push({ t: Date.now(), url: f.url() }); });
  page.on('request', r => { if (mainNav(r) && !r.redirectedFrom()) p.docRequests.push({ t: Date.now(), url: r.url() }); });
  page.on('requestfailed', r => { if (mainNav(r)) p.docFailed.push({ t: Date.now(), url: r.url(), error: r.failure()?.errorText ?? '' }); });
  page.on('response', r => { if (mainNav(r.request())) p.docs.push({ t: Date.now(), status: r.status(), url: r.url() }); });
  page.on('websocket', ws => { const s = { url: ws.url(), openedAt: Date.now(), closedAt: null }; p.sockets.push(s); ws.on('close', () => { s.closedAt = Date.now(); }); });
  return p;
}
const openCircuits = p => p.sockets.filter(s => s.url.includes('/_blazor') && s.closedAt === null);
async function waitForCircuit(page, p) {
  for (let i = 0; i < 30 && openCircuits(p).length === 0; i++) await sleep(500);
  await page.evaluate(() => { window.__w21 = 'open-before-removal'; });
  return openCircuits(p).length > 0;
}
// 'open-before-removal' in the original document, 'absent' in a new one, 'unavailable' while no document can answer.
const marker = page => page.evaluate(() => window.__w21 ?? 'absent').catch(() => 'unavailable');
const visible = async (page, sel) => { try { const e = await page.$(sel); return e ? await e.isVisible() : false; } catch { return false; } };
const onKeycloakSignIn = async page => page.url().startsWith(`${KC}/realms/${REALM}/`) && (await visible(page, '#username') || await visible(page, '#password'));
const onAccessRemoved = async page => page.url().startsWith(ACME) && await visible(page, '[data-access-removed]');

// The access-removed page's Sign out: the local cookie and the Keycloak session end, so Keycloak's sign-in page shows
// (not the page again, which a still-live Keycloak session would bring back).
async function signOutFromAccessRemoved(page) {
  const t = Date.now();
  const text = await page.locator('[data-access-removed]').innerText().catch(() => null);
  const lang = await page.getAttribute('html', 'lang').catch(() => null);
  const dir = await page.getAttribute('html', 'dir').catch(() => null);
  await page.click('[data-access-removed-sign-out] button[type=submit]');
  let signIn = false;
  for (let i = 0; i < 30 && !signIn; i++) { await sleep(1000); signIn = await onKeycloakSignIn(page); }
  return { text, lang, dir, signInPage: signIn, afterS: secs(Date.now() - t), url: page.url().split('?')[0] };
}

// Watches a page nobody touches after t0 until it shows Keycloak's sign-in form, or a new document committed and the
// page has been quiet for 5 seconds elsewhere, or WATCH_S passes.
async function watchUntouched(page, p, t0) {
  let signInAt = null; let newDocumentAt = null; let accessRemovedAt = null;
  while (Date.now() - t0 < WATCH_S * 1000) {
    if (await onKeycloakSignIn(page)) { signInAt = Date.now(); newDocumentAt ??= signInAt; break; }
    if (accessRemovedAt === null && await onAccessRemoved(page)) { accessRemovedAt = Date.now(); newDocumentAt ??= accessRemovedAt; }
    if (newDocumentAt === null && await marker(page) === 'absent') newDocumentAt = Date.now();
    const lastActivity = Math.max(0, ...p.docRequests.filter(r => r.t > t0).map(r => r.t), ...p.docs.filter(d => d.t > t0).map(d => d.t));
    if (newDocumentAt !== null && Date.now() - lastActivity > 5000 && !page.url().startsWith(KC)) break;
    await sleep(1000);
  }
  const requests = p.docRequests.filter(r => r.t > t0);
  const failed = p.docFailed.filter(r => r.t > t0);
  const lastDoc = p.docs.filter(d => d.t > t0).at(-1) ?? null;
  const circuitClosed = p.sockets.find(s => s.url.includes('/_blazor') && s.closedAt && s.closedAt > t0);
  return {
    reloadedAfterS: newDocumentAt ? secs(newDocumentAt - t0) : null,
    firstReloadRequestAfterS: requests[0] ? secs(requests[0].t - t0) : null,
    signInPageAfterS: signInAt ? secs(signInAt - t0) : null,
    accessRemovedPageAfterS: accessRemovedAt ? secs(accessRemovedAt - t0) : null,
    watchedS: secs(Date.now() - t0),
    landedUrl: page.url().split('?')[0],
    landedStatus: lastDoc?.status ?? null,
    navigationRequests: requests.length,
    navigationRequestsFailed: failed.length,
    failureReasons: [...new Set(failed.map(f => f.error))],
    responses: [...new Set(p.docs.filter(d => d.t > t0).map(d => `${d.status} ${d.url.split('?')[0]}`))],
    sameDocumentNavigations: p.navs.filter(n => n.t > t0).length,
    circuitClosedAfterS: circuitClosed ? secs(circuitClosed.closedAt - t0) : null,
    marker: await marker(page),
  };
}
// A reload into the sign-in page is one navigation (its redirects do not count); a few allow for Keycloak's own steps.
const MAX_RELOAD_REQUESTS = 5;

// The app's auto-submitting end-session form (W-21), Keycloak's logout confirmation if it asks (it should not: the
// sign-out carries id_token_hint), then whatever login steps follow.
async function signInAgain(page, user, password, log) {
  await waitPastSignOutForm(page);
  for (let i = 0; i < 5; i++) {
    await page.waitForLoadState('domcontentloaded');
    const logout = await page.$('#kc-logout');
    if (logout) { log('confirmed logout'); await Promise.all([page.waitForNavigation(), logout.click()]); continue; }
    break;
  }
  await driveKeycloak(page, { user, password, state, log });
}

const cookieHeader = cookies => cookies.filter(c => c.name.startsWith('waslabid.auth')).map(c => `${c.name}=${c.value}`).join('; ');
async function replay(api, url, cookie) {
  const r = await api.get(url, { headers: { Cookie: cookie }, maxRedirects: 0 });
  return { status: r.status(), location: (r.headers().location ?? '').split('?')[0] };
}

// ---- Setup ----
const ACME_ID = tenantId('acme');
const BETA_ID = tenantId('beta');
const created = { staff: [], vendor: null };

async function staffSetup(browser, label, [firstName, lastName]) {
  const email = `${label}.${run}@acme.waslabid.test`;
  const password = newPassword();
  const uid = await createUser(email, password, firstName, lastName);
  created.staff.push({ uid, email });
  await addMember('acme', uid);
  sql(`insert into identity.members (id, tenant_id, user_id, email, display_name, roles, status, invited_at)
       values (gen_random_uuid(), ${q(ACME_ID)}, null, ${q(email)}, ${q(`${firstName} ${lastName}`)}, array['tenant-admin'], 'invited', now());`);
  const { ctx, page } = await newPage(browser);
  const p = instrument(page);
  await page.goto(`${ACME}/admin/staff`);
  const steps = [];
  await driveKeycloak(page, { user: email, password, state, log: s => steps.push(s) });
  await page.waitForSelector('[data-member]', { timeout: 30000 }).catch(() => null);
  const onPage = page.url() === `${ACME}/admin/staff` && await page.locator('[data-member]').count() > 0;
  const circuit = await waitForCircuit(page, p);
  const cookie = cookieHeader(await ctx.cookies(ACME));
  const api = await request.newContext({ ignoreHTTPSErrors: true });
  const control = await replay(api, `${ACME}/admin/staff`, cookie);
  rec(`${label}: signed in on acme, /admin/staff open with a live circuit, its cookie accepted on a fresh request`,
    onPage && circuit && control.status === 200, { url: page.url(), circuit: openCircuits(p).map(s => s.url.split('?')[0]), cookieReplay: control, keycloak: steps });
  await shot(page, `${label}-01-before`);
  return { label, email, password, uid, ctx, page, p, cookie, api };
}

async function vendorSetup(browser) {
  const email = `vendor.w21.${run}@vendor.waslabid.test`;
  const password = newPassword();
  const uid = await createUser(email, password, 'فهد', 'العتيبي');
  created.vendor = { uid, email };
  const { ctx, page } = await newPage(browser);
  track(page);
  const log = [];
  await page.goto(`${ACME}/vendor/register/company`);
  await driveKeycloak(page, { user: email, password, state, log: s => log.push(s) });
  await page.waitForSelector('[data-vendor-register]', { timeout: 30000 });
  // A Riyadh CR number (10 digits, 1010...), a VAT number of 15 digits starting and ending with 3, an Arabic trade name.
  const f = page.locator('[data-vendor-register]');
  await f.locator('input[name="Input.CrNumber"]').fill(`1010${suffix}`);
  await f.locator('input[name="Input.NameAr"]').fill(`شركة الرواد للتوريدات ${suffix}`);
  await f.locator('input[name="Input.NameEn"]').fill(`Al Rowad Supplies ${suffix}`);
  await f.locator('input[name="Input.VatNumber"]').fill(`3${run.slice(-13)}3`);
  await f.locator('input[name="Input.Address"]').fill('طريق الملك فهد، حي العليا، الرياض 12214');
  await f.locator('input[name="Input.ContactName"]').fill('فهد العتيبي');
  await f.locator('input[name="Input.ContactPhone"]').fill('+966551234567');
  await f.locator('input[name="Input.ContactEmail"]').fill(email);
  await f.locator('input[name="Input.AcceptedPrivacyNotice"]').check();
  await Promise.all([page.waitForLoadState('domcontentloaded'), f.locator('button[type=submit]').click()]);
  await page.waitForSelector('[data-vendor-registered], [data-form-error]', { timeout: 30000 }).catch(() => null);
  const registered = await page.locator('[data-vendor-registered]').count() > 0;
  const formError = await page.locator('[data-form-error]').innerText().catch(() => null);
  await Promise.all([page.waitForNavigation(), page.click('[data-vendor-sign-out] button[type=submit]')]);
  await signInAgain(page, email, password, s => log.push(s));
  await page.waitForSelector('[data-vendor-company]', { timeout: 30000 }).catch(() => null);
  const acmeHome = page.url() === `${ACME}/vendor`;
  // Join beta, then sign in again there so beta's cookie carries beta's organization.
  await page.goto(`${BETA}/vendor`);
  await driveKeycloak(page, { user: email, password, state, log: s => log.push(s) });
  await page.waitForLoadState('networkidle');
  await Promise.all([page.waitForLoadState('domcontentloaded'), page.click('[data-vendor-join-form] button[type=submit]')]);
  await page.waitForSelector('[data-vendor-joined], [data-form-error]', { timeout: 30000 }).catch(() => null);
  const joined = await page.locator('[data-vendor-joined]').count() > 0;
  await Promise.all([page.waitForNavigation(), page.click('[data-vendor-sign-out] button[type=submit]')]);
  await signInAgain(page, email, password, s => log.push(s));
  await page.waitForSelector('[data-vendor-company]', { timeout: 30000 }).catch(() => null);
  const betaHome = page.url() === `${BETA}/vendor`;
  await page.close();
  // The two tabs the scenario watches: /vendor on acme and on beta, each with its own circuit.
  const acmePage = await ctx.newPage(); const pa = instrument(acmePage);
  await acmePage.goto(`${ACME}/vendor`); await acmePage.waitForSelector('[data-vendor-company]', { timeout: 30000 }).catch(() => null);
  const acmeCircuit = acmePage.url() === `${ACME}/vendor` && await waitForCircuit(acmePage, pa);
  const betaPage = await ctx.newPage(); const pb = instrument(betaPage);
  await betaPage.goto(`${BETA}/vendor`); await betaPage.waitForSelector('[data-vendor-company]', { timeout: 30000 }).catch(() => null);
  const betaCircuit = betaPage.url() === `${BETA}/vendor` && await waitForCircuit(betaPage, pb);
  const companyId = sql(`select company_id from vendor.vendor_users where user_id = ${q(uid)};`);
  rec('vendor: registered on acme, joined beta, /vendor open on both hosts with a live circuit each',
    registered && acmeHome && joined && betaHome && acmeCircuit && betaCircuit && !!companyId,
    { registered, formError, acmeHome, joined, betaHome, acmeCircuit, betaCircuit, companyId, keycloak: log });
  await shot(acmePage, 'vendor-01-acme-before'); await shot(betaPage, 'vendor-01-beta-before');
  return { email, password, uid, ctx, acmePage, pa, betaPage, pb, companyId };
}

// ---- Checks after a staff removal ----
async function staffAfter(s, t0, reason, lands) {
  const seen = await watchUntouched(s.page, s.p, t0);
  await shot(s.page, `${s.label}-02-after`);
  rec(`${s.label}: the open /admin/staff page reloads by itself within ${LIMIT_S}s (a new document)`,
    seen.reloadedAfterS !== null && seen.reloadedAfterS <= LIMIT_S, seen);
  rec(`${s.label}: the ended circuit reloads once, not repeatedly (at most ${MAX_RELOAD_REQUESTS} navigation requests)`,
    seen.navigationRequests <= MAX_RELOAD_REQUESTS,
    { navigationRequests: seen.navigationRequests, failed: seen.navigationRequestsFailed, failureReasons: seen.failureReasons, watchedS: seen.watchedS });
  if (lands === 'access-removed') {
    rec(`${s.label}: the reloaded page is the access-removed page (403) within ${LIMIT_S}s`,
      seen.accessRemovedPageAfterS !== null && seen.accessRemovedPageAfterS <= LIMIT_S && seen.landedStatus === 403,
      { accessRemovedPageAfterS: seen.accessRemovedPageAfterS, landedUrl: seen.landedUrl, landedStatus: seen.landedStatus });
    const out = await signOutFromAccessRemoved(s.page);
    await shot(s.page, `${s.label}-02b-signed-out`);
    rec(`${s.label}: Sign out on the access-removed page ends the Keycloak session and shows the Keycloak sign-in page`,
      out.signInPage && /^ar/.test(out.lang ?? '') === (out.dir === 'rtl') && !!out.text, out);
  } else {
    rec(`${s.label}: the reloaded page shows the Keycloak sign-in page`, seen.signInPageAfterS !== null && seen.signInPageAfterS <= LIMIT_S,
      { signInPageAfterS: seen.signInPageAfterS, landedUrl: seen.landedUrl, landedStatus: seen.landedStatus });
  }
  const replayed = await replay(s.api, `${ACME}/admin/staff`, s.cookie);
  rec(`${s.label}: the session's original cookie on a fresh request is challenged (302 to Keycloak)`,
    replayed.status === 302 && replayed.location.startsWith(`${KC}/realms/${REALM}/protocol/openid-connect/auth`), replayed);
  // The looping tab is closed first, so the fresh navigation is judged on its own.
  await s.page.close().catch(() => null);
  const fresh = await s.ctx.newPage(); const pf = instrument(fresh);
  const gotoError = await fresh.goto(`${ACME}/admin/staff`).then(() => null, e => String(e).split('\n')[0].slice(0, 200));
  await fresh.waitForLoadState('networkidle').catch(() => null);
  await sleep(2000);
  const freshSeen = { url: fresh.url().split('?')[0], gotoError, statuses: pf.docs.map(d => `${d.status} ${d.url.split('?')[0]}`), staffListShown: await fresh.locator('[data-member]').count() > 0, keycloakSignIn: await onKeycloakSignIn(fresh) };
  await shot(fresh, `${s.label}-03-fresh-navigation`);
  rec(`${s.label}: a fresh navigation to /admin/staff does not open the page`, !freshSeen.staffListShown && (freshSeen.url.startsWith(KC) || pf.docs.at(-1)?.status === 403), freshSeen);
  await fresh.close();
  const rows = await auditRowsSoon(ACME_ID, 'identity.session_revoked', `subject_id = ${q(s.uid)}`);
  rec(`${s.label}: one identity.session_revoked row in acme's audit log naming the user, reason ${reason}`,
    rows.length === 1 && rows[0].actor === s.uid && rows[0].subjectType === 'user' && rows[0].data.reason === reason && rows[0].data.session === 'staff',
    { tenant: 'acme', rows });
  return seen;
}

// ---- Control: a removed staff user with no page open, so no circuit; fresh navigations every 10 seconds ----
async function httpAfter(s, t0) {
  let seen = null; let attempts = 0; const inconclusive = [];
  while (Date.now() - t0 < WATCH_S * 1000) {
    attempts++;
    const page = await s.ctx.newPage(); const pp = instrument(page);
    const gotoError = await page.goto(`${ACME}/admin/staff`).then(() => null, e => String(e).split('\n')[0].slice(0, 200));
    await page.waitForLoadState('networkidle').catch(() => null);
    // A navigation that failed proves nothing either way: note it and try again.
    if (gotoError) { inconclusive.push({ afterS: secs(Date.now() - t0), gotoError }); await page.close(); await sleep(10000); continue; }
    if (await page.locator('[data-member]').count() === 0) {
      seen = { afterS: secs(Date.now() - t0), attempts, inconclusive, url: page.url().split('?')[0], statuses: pp.docs.map(d => `${d.status} ${d.url.split('?')[0]}`), accessRemoved: await onAccessRemoved(page), keycloakSignIn: await onKeycloakSignIn(page) };
      await shot(page, `${s.label}-02-challenged`);
      if (seen.accessRemoved) { seen.signOut = await signOutFromAccessRemoved(page); await shot(page, `${s.label}-02b-signed-out`); }
      await page.close(); break;
    }
    await page.close(); await sleep(10000);
  }
  rec(`${s.label}: with no page open, a fresh navigation to /admin/staff is challenged within ${LIMIT_S}s`,
    !!seen && seen.afterS <= LIMIT_S && seen.statuses.some(x => x === `302 ${ACME}/admin/staff`), seen);
  rec(`${s.label}: the challenged navigation lands on the access-removed page (403)`,
    seen?.accessRemoved === true && seen.statuses.at(-1) === `403 ${ACME}/admin/staff`, seen);
  rec(`${s.label}: Sign out on the access-removed page ends the Keycloak session and shows the Keycloak sign-in page`,
    seen?.signOut?.signInPage === true, seen?.signOut ?? null);
  const rows = await auditRowsSoon(ACME_ID, 'identity.session_revoked', `subject_id = ${q(s.uid)}`);
  rec(`${s.label}: one identity.session_revoked row in acme's audit log naming the user, reason removed_from_organization`,
    rows.length === 1 && rows[0].actor === s.uid && rows[0].data.reason === 'removed_from_organization' && rows[0].data.session === 'staff', { tenant: 'acme', rows });
}

// ---- Vendor checks after the acme removal ----
async function joinRefusal(v, culture) {
  const page = v.acmePage; const log = [];
  await page.goto(`${ACME}/culture/set?culture=${culture}&returnUrl=/vendor/join`);
  if (page.url().startsWith(KC)) await driveKeycloak(page, { user: v.email, password: v.password, state, log: s => log.push(s) });
  await page.waitForLoadState('networkidle');
  if (!page.url().startsWith(`${ACME}/vendor/join`)) { await page.goto(`${ACME}/vendor/join`); await page.waitForLoadState('networkidle'); }
  const alreadyJoined = await page.locator('[data-vendor-already-joined]').count() > 0;
  await Promise.all([page.waitForLoadState('domcontentloaded'), page.click('[data-vendor-join-form] button[type=submit]')]);
  await page.waitForSelector('[data-form-error], [data-vendor-joined]', { timeout: 30000 }).catch(() => null);
  const error = await page.locator('[data-form-error]').innerText().catch(() => null);
  const joined = await page.locator('[data-vendor-joined]').count() > 0;
  const lang = await page.getAttribute('html', 'lang'); const dir = await page.getAttribute('html', 'dir');
  const errorDirection = await page.$eval('[data-form-error]', e => getComputedStyle(e).direction).catch(() => null);
  const body = await page.locator('body').innerText();
  // A raw resource key looks like Vendor.Join.Error.AccessRemoved: dotted PascalCase segments.
  const rawKeys = body.match(/\b[A-Z][A-Za-z]+(?:\.[A-Z][A-Za-z]+){2,}\b/g) ?? [];
  await shot(page, `vendor-03-join-refused-${culture}`);
  return { url: page.url(), alreadyJoined, joined, error, lang, dir, errorDirection, rawKeys, keycloak: log };
}

const browser = await launch();
let staffRemoved, staffDisabled, staffHttp, vendor;
try {
  staffRemoved = await staffSetup(browser, 'staff-removed', ['سارة', 'القحطاني']);
  staffDisabled = await staffSetup(browser, 'staff-disabled', ['خالد', 'الشمري']);
  vendor = await vendorSetup(browser);
  // Control: signed in like the others, then its tab is closed, so it has no circuit when it is removed.
  staffHttp = await staffSetup(browser, 'staff-http', ['نورة', 'الدوسري']);
  await staffHttp.page.close();

  // The removals at one moment; nothing in any open page is touched from here until each has been observed.
  await Promise.all([removeMember('acme', staffRemoved.uid), setEnabled(staffDisabled.uid, false), removeMember('acme', vendor.uid)]);
  const t0 = Date.now();
  note('removals done through the Keycloak Admin API', { at: new Date(t0).toISOString(), removedFromAcme: [staffRemoved.email, vendor.email], disabled: staffDisabled.email,
    stillMember: { staffRemoved: await isMember('acme', staffRemoved.uid), vendor: await isMember('acme', vendor.uid) }, disabledEnabled: await userEnabled(staffDisabled.uid) });

  const [, , vendorSeen] = await Promise.all([
    staffAfter(staffRemoved, t0, 'removed_from_organization', 'access-removed'),
    staffAfter(staffDisabled, t0, 'account_disabled', 'sign-in'),
    watchUntouched(vendor.acmePage, vendor.pa, t0),
  ]);

  // 3. The vendor's acme tab.
  await shot(vendor.acmePage, 'vendor-02-acme-after');
  rec(`vendor: the open acme /vendor page reloads by itself within ${LIMIT_S}s (a new document)`,
    vendorSeen.reloadedAfterS !== null && vendorSeen.reloadedAfterS <= LIMIT_S, vendorSeen);
  rec(`vendor: the ended acme circuit reloads once, not repeatedly (at most ${MAX_RELOAD_REQUESTS} navigation requests)`,
    vendorSeen.navigationRequests <= MAX_RELOAD_REQUESTS,
    { navigationRequests: vendorSeen.navigationRequests, failed: vendorSeen.navigationRequestsFailed, failureReasons: vendorSeen.failureReasons, watchedS: vendorSeen.watchedS });
  // A looping tab would keep aborting the steps below; a fresh tab of the same browser carries on.
  await vendor.acmePage.close().catch(() => null);
  vendor.acmePage = await vendor.ctx.newPage();
  const vendorRevoked = await auditRowsSoon(ACME_ID, 'identity.session_revoked', `subject_id = ${q(vendor.uid)}`);
  rec('vendor: one identity.session_revoked row in acme\'s audit log naming the vendor user, session vendor',
    vendorRevoked.length === 1 && vendorRevoked[0].actor === vendor.uid && vendorRevoked[0].data.reason === 'removed_from_organization' && vendorRevoked[0].data.session === 'vendor',
    { tenant: 'acme', rows: vendorRevoked });
  const ar = await joinRefusal(vendor, 'ar-SA');
  rec('vendor: /vendor/join on acme refuses in Arabic, right to left, no raw resource key',
    ar.alreadyJoined && !ar.joined && /أُلغي وصولك إلى/.test(ar.error ?? '') && /^ar/.test(ar.lang ?? '') && ar.dir === 'rtl' && ar.errorDirection === 'rtl' && ar.rawKeys.length === 0, ar);
  const en = await joinRefusal(vendor, 'en-US');
  rec('vendor: /vendor/join on acme refuses in English, left to right, no raw resource key',
    en.alreadyJoined && !en.joined && /^Your access to .+ was removed\. Only .+ can restore it; contact them\.$/.test(en.error ?? '') && /^en/.test(en.lang ?? '') && en.dir === 'ltr' && en.rawKeys.length === 0, en);
  const stillOut = !(await isMember('acme', vendor.uid));
  const refusals = await auditRowsSoon(ACME_ID, 'vendor.membership_restore_refused', `actor_id = ${q(vendor.uid)}`, 2);
  rec('vendor: the two join attempts left the acme membership absent and are audited as vendor.membership_restore_refused',
    stillOut && refusals.length === 2 && refusals.every(r => r.subject === vendor.companyId), { memberOfAcme: !stillOut, refusals });

  // 4. The beta tab over the same window: at least three circuit intervals since the removal before judging it.
  const waitMore = 190000 - (Date.now() - t0); if (waitMore > 0) await sleep(waitMore);
  const betaNavs = vendor.pb.navs.filter(n => n.t > t0).map(n => n.url);
  const betaSeen = { secondsSinceRemoval: secs(Date.now() - t0), navigationsSinceRemoval: betaNavs, circuitOpen: openCircuits(vendor.pb).length > 0, markerKept: await marker(vendor.betaPage), url: vendor.betaPage.url() };
  await shot(vendor.betaPage, 'vendor-04-beta-after');
  rec('vendor: the beta /vendor tab keeps its circuit (no reload, WebSocket open) after the acme removal',
    betaNavs.length === 0 && betaSeen.circuitOpen && betaSeen.markerKept === 'open-before-removal' && betaSeen.url === `${BETA}/vendor`, betaSeen);
  const betaFresh = await vendor.ctx.newPage(); const pbf = instrument(betaFresh);
  await betaFresh.goto(`${BETA}/vendor`); await betaFresh.waitForSelector('[data-vendor-company]', { timeout: 30000 }).catch(() => null);
  const betaFreshSeen = { url: betaFresh.url().split('?')[0], statuses: pbf.docs.map(d => `${d.status} ${d.url.split('?')[0]}`), company: await betaFresh.locator('[data-vendor-company]').count() > 0, relationship: await betaFresh.locator('[data-relationship]').getAttribute('data-relationship').catch(() => null) };
  await shot(betaFresh, 'vendor-05-beta-fresh');
  rec('vendor: a fresh navigation to beta /vendor opens the vendor page without signing in', betaFreshSeen.url === `${BETA}/vendor` && betaFreshSeen.company && pbf.docs.every(d => !d.url.startsWith(KC)), betaFreshSeen);
  await betaFresh.close();
  const betaRevoked = auditRows(BETA_ID, 'identity.session_revoked', `subject_id = ${q(vendor.uid)}`);
  rec('vendor: nothing revoked in beta\'s audit log', betaRevoked.length === 0, { tenant: 'beta', rows: betaRevoked });

  // Control, on its own once the circuits above have ended, so their reloads cannot load the host while it is measured.
  await removeMember('acme', staffHttp.uid);
  const t1 = Date.now();
  note('control removal done through the Keycloak Admin API', { at: new Date(t1).toISOString(), removedFromAcme: staffHttp.email, stillMember: await isMember('acme', staffHttp.uid) });
  await httpAfter(staffHttp, t1);

  // Restore as staff would: the member added back, the account enabled; both sign in again and open /admin/staff.
  await Promise.all([addMember('acme', staffRemoved.uid), setEnabled(staffDisabled.uid, true), addMember('acme', vendor.uid)]);
  for (const s of [staffRemoved, staffDisabled]) {
    const { ctx, page } = await newPage(browser);
    const steps = [];
    await page.goto(`${ACME}/admin/staff`);
    await driveKeycloak(page, { user: s.email, password: s.password, state, log: x => steps.push(x) });
    await page.waitForSelector('[data-member]', { timeout: 30000 }).catch(() => null);
    const ok = page.url() === `${ACME}/admin/staff` && await page.locator('[data-member]').count() > 0;
    await shot(page, `${s.label}-04-restored`);
    rec(`${s.label}: after access is restored, signing in again opens /admin/staff (the earlier removal does not refuse it)`, ok, { url: page.url(), keycloak: steps });
    await ctx.close();
  }
  // W-21: every sign-out of the run (the vendor's two, and Sign out on the access-removed page for the removed staff
  // member and the control) sent the end-session request as a form post with id_token_hint in the body, and no URL any
  // tracked page requested or navigated to carried the hint.
  const endSessions = tracked.flatMap(t => t.endSessions);
  const hintInNavigationUrls = [...new Set(tracked.flatMap(t => t.hintInNavigationUrls()))];
  const hintInAnyRequestUrl = [...new Set(tracked.flatMap(t => t.hintInAnyRequestUrl()))];
  rec('sign-out posts id_token_hint to Keycloak in a form body and never in a URL',
    endSessions.length >= 4 && endSessions.every(e => e.method === 'POST' && e.hintInBody) && hintInNavigationUrls.length === 0 && hintInAnyRequestUrl.length === 0,
    { endSessions, hintInNavigationUrls, hintInAnyRequestUrl, navigationsChecked: tracked.reduce((n, t) => n + t.navigations.length, 0) });
} catch (e) {
  rec('script error', false, { error: String(e).slice(0, 800) });
} finally {
  // Back to the seed state: throwaway staff users and their member rows go; the vendor stays (as vendor.mjs's do), back
  // in acme's organization; the seeded tenant admins are checked untouched.
  const cleanup = {};
  try {
    for (const s of created.staff) {
      cleanup[s.email] = { keycloakDelete: (await kc('DELETE', `/users/${s.uid}`)).status, memberRows: sql(`with d as (delete from identity.members where tenant_id = ${q(ACME_ID)} and email = ${q(s.email)} returning 1) select count(*) from d;`) };
      if (state.totp) delete state.totp[s.email];
      if (state.lastStep) delete state.lastStep[s.email];
    }
    saveState(state);
    if (created.vendor) {
      if (!(await isMember('acme', created.vendor.uid))) await addMember('acme', created.vendor.uid);
      cleanup.vendor = { email: created.vendor.email, memberOfAcme: await isMember('acme', created.vendor.uid), memberOfBeta: await isMember('beta', created.vendor.uid) };
    }
    const seeded = {};
    for (const [email, alias] of [['admin@acme.waslabid.test', 'acme'], ['admin@beta.waslabid.test', 'beta']]) {
      const id = await userIdByEmail(email);
      seeded[email] = { enabled: await userEnabled(id), member: await isMember(alias, id) };
    }
    cleanup.seeded = seeded;
    rec('cleanup: throwaway staff deleted, vendor back in acme, seeded admins enabled and members',
      created.staff.every(s => cleanup[s.email].keycloakDelete === 204) && (!created.vendor || cleanup.vendor.memberOfAcme) && Object.values(seeded).every(x => x.enabled && x.member), cleanup);
  } catch (e) {
    rec('cleanup error', false, { error: String(e).slice(0, 600), cleanup });
  }
  fs.writeFileSync(path.join(DIR, 'revalidation-results.json'), JSON.stringify(results, null, 2));
  await browser.close();
  if (results.some(r => !r.ok)) process.exitCode = 1;
}
