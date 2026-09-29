// W-24 end to end: the Data Protection key ring in platform.data_protection_keys is shared by every web instance and
// survives a restart, so a session signed in through Caddy stays signed in (docs/07 section 4, "Edge and Data Protection
// key ring"; docs/09 W-24).
//
//   signin   A throwaway acme tenant admin (created through the Keycloak Admin API, member row inserted the way the dev
//            seed does, bound on first sign-in) signs in at https://acme.localhost:8443/admin/staff through Caddy and
//            enrols TOTP. The browser's cookies are kept in .state/keyring-session.json with the key rows seen then.
//   check    A new browser context with those cookies opens /admin/staff through Caddy: it must land on the page with no
//            request to Keycloak and open an interactive circuit; the cookie replayed without a browser gets a 200 and a
//            tampered copy gets a 302 to Keycloak (so a 200 means the cookie was checked). The key rows must be the ones
//            seen at sign-in: nothing made a new key. `--direct http://localhost:5274` (repeatable) also replays the cookie
//            straight to another web instance with the headers Caddy sends (Host acme.localhost:8443, X-Forwarded-Proto),
//            and once more with X-Forwarded-Host naming beta, which must be ignored (acme's staff list comes back).
//   cleanup  Deletes the throwaway user in Keycloak, its member row, its TOTP seed and the session file.
//   all      (default) signin, check (also direct to the instance on 5273), then starts a second web instance itself on port 5274 from the built
//            Platform.Web.dll (Development, same user secrets), replays the cookie there, restarts that instance and
//            replays again, checks its log holds no key material and no "Creating key", stops it, checks through Caddy
//            once more and cleans up.
//
// To prove a restart of the host behind Caddy, run `node keyring.mjs signin`, restart `dotnet run --project
// src/Platform.Web`, then `node keyring.mjs check` and `node keyring.mjs cleanup`.
//
// Needs: the Compose stack, platform/0007 migrated with the key ring login (migrator), Platform.Web on 5273 behind Caddy,
// Platform.Web built (`dotnet build src/Platform.Web`) for mode all, KEYCLOAK_ADMIN and KEYCLOAK_ADMIN_PASSWORD in
// .env (E2E_ENV_FILE in a worktree), and the erp-postgres container. Secrets are never printed (N-10); the generated
// password stays in memory. Results in keyring-results.json; a failed step sets a non-zero exit code.
import crypto from 'crypto';
import fs from 'fs';
import http from 'http';
import path from 'path';
import { execFileSync, spawn } from 'child_process';
import { request } from 'playwright';
import { launch, newPage, driveKeycloak, envValue, loadState, saveState, sleep, DIR } from './lib.mjs';

const TENANT_HOST = 'acme.localhost:8443';
const ACME = `https://${TENANT_HOST}`;
const KC = 'http://localhost:8080';
const REALM = 'waslabid';
const REPO = path.join(DIR, '..', '..');
const WEB_DIR = path.join(REPO, 'src', 'Platform.Web');
const WEB_DLL = path.join(WEB_DIR, 'bin', 'Debug', 'net10.0', 'Platform.Web.dll');
const SECOND = 'http://localhost:5274';
const SESSION = path.join(DIR, '.state', 'keyring-session.json');
const RESULTS = path.join(DIR, 'keyring-results.json');

// signin and all start a new results file; check and cleanup add to the one signin started.
const results = ['check', 'cleanup'].includes(process.argv[2]) && fs.existsSync(RESULTS) ? JSON.parse(fs.readFileSync(RESULTS, 'utf8')) : [];
const rec = (step, ok, seen) => { results.push({ at: new Date().toISOString(), step, ok, seen }); console.log(`${ok ? 'PASS' : 'FAIL'} ${step} :: ${JSON.stringify(seen)}`); };
const state = loadState();

// ---- Keycloak Admin API as the master admin (admin-cli tokens live 60 seconds, so one per call) ----
async function kc(method, p, body) {
  const form = new URLSearchParams({ grant_type: 'password', client_id: 'admin-cli', username: envValue('KEYCLOAK_ADMIN'), password: envValue('KEYCLOAK_ADMIN_PASSWORD') });
  const t = await fetch(`${KC}/realms/master/protocol/openid-connect/token`, { method: 'POST', body: form });
  if (!t.ok) throw new Error(`Keycloak admin token refused (${t.status})`);
  const headers = { Authorization: `Bearer ${(await t.json()).access_token}` };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const r = await fetch(`${KC}/admin/realms/${REALM}${p}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!r.ok) throw new Error(`Keycloak ${method} ${p.split('?')[0]}: ${r.status} ${(await r.text()).slice(0, 200)}`);
  return r;
}
async function acmeOrgId() {
  // Keycloak's search matches the organization's name, not its alias, so list them and match the alias.
  return (await (await kc('GET', '/organizations?briefRepresentation=true&max=100')).json()).find(o => o.alias === 'acme').id;
}

// ---- PostgreSQL as the Compose superuser (test data and key ring reads only) ----
const q = s => `'${String(s).replace(/'/g, "''")}'`;
const sql = query => execFileSync('docker', ['exec', '-i', '-e', 'PGCLIENTENCODING=UTF8', 'erp-postgres', 'psql', '-U', 'erp', '-d', 'platform', '-v', 'ON_ERROR_STOP=1', '-q', '-A', '-t', '-F', '\t'],
  { input: query, encoding: 'utf8' }).trim();
// Ids and friendly names only; the key XML never leaves the database.
const keyRows = () => { const out = sql('select id, friendly_name from platform.data_protection_keys order by id;'); return out ? out.split('\n').map(l => l.replace('\t', ' ')) : []; };

// ---- Cookie replay ----
const authCookie = cookies => cookies.filter(c => c.name.startsWith('waslabid.auth')).map(c => `${c.name}=${c.value}`).join('; ');
// One character in the middle of the first cookie's value changed; the result stays a valid cookie header.
function tamper(cookie) {
  const eq = cookie.indexOf('=') + 1; const end = cookie.indexOf(';') > 0 ? cookie.indexOf(';') : cookie.length;
  const i = eq + Math.floor((end - eq) / 2);
  return cookie.slice(0, i) + (cookie[i] === 'A' ? 'B' : 'A') + cookie.slice(i + 1);
}
async function viaCaddy(cookie) {
  const api = await request.newContext({ ignoreHTTPSErrors: true });
  const r = await api.get(`${ACME}/admin/staff`, { headers: { Cookie: cookie }, maxRedirects: 0 });
  const seen = { status: r.status(), location: (r.headers().location ?? '').split('?')[0], memberList: (await r.text()).includes('data-member=') };
  await api.dispose();
  return seen;
}
// Straight to a web instance, with the headers Caddy adds (docs/07: Caddy forwards the host with its port and the scheme).
function direct(base, cookie, extra = {}) {
  const u = new URL(base);
  return new Promise((resolve, reject) => {
    const req = http.request({ host: u.hostname, port: u.port, path: '/admin/staff', method: 'GET',
      headers: { Host: TENANT_HOST, 'X-Forwarded-Proto': 'https', ...extra, ...(cookie ? { Cookie: cookie } : {}) } }, r => {
      let body = ''; r.setEncoding('utf8'); r.on('data', d => { body += d; });
      r.on('end', () => resolve({ status: r.statusCode, location: (r.headers.location ?? '').split('?')[0], memberList: body.includes('data-member='),
        acmeAdminListed: body.includes('data-member="admin@acme.waslabid.test"'), betaAdminListed: body.includes('data-member="admin@beta.waslabid.test"') }));
    });
    req.on('error', reject); req.end();
  });
}

// ---- Modes ----
async function signin(browser) {
  const run = Date.now().toString();
  const email = `keyring.${run}@acme.waslabid.test`;
  const password = crypto.randomBytes(12).toString('base64url') + 'aA1!';
  const r = await kc('POST', '/users', { username: email, email, emailVerified: true, enabled: true, firstName: 'سلمان', lastName: 'القحطاني', credentials: [{ type: 'password', value: password, temporary: false }] });
  const uid = r.headers.get('location').split('/').pop();
  fs.writeFileSync(SESSION, JSON.stringify({ email, uid }));
  await kc('POST', `/organizations/${await acmeOrgId()}/members`, uid);
  sql(`insert into identity.members (id, tenant_id, user_id, email, display_name, roles, status, invited_at)
       values (gen_random_uuid(), (select id from tenancy.tenants where slug = 'acme'), null, ${q(email)}, 'سلمان القحطاني', array['tenant-admin'], 'invited', now());`);
  const { ctx, page } = await newPage(browser);
  const steps = [];
  await page.goto(`${ACME}/admin/staff`);
  await driveKeycloak(page, { user: email, password, state, log: s => steps.push(s) });
  await page.waitForSelector('[data-member]', { timeout: 30000 }).catch(() => null);
  const onPage = page.url() === `${ACME}/admin/staff` && await page.locator('[data-member]').count() > 0;
  const storage = await ctx.storageState();
  const keys = keyRows();
  fs.writeFileSync(SESSION, JSON.stringify({ email, uid, storage, keysAtSignIn: keys }));
  const replay = await viaCaddy(authCookie(storage.cookies));
  rec('signin: a new acme tenant admin signs in through Caddy and /admin/staff shows the member list', onPage && replay.status === 200 && replay.memberList,
    { url: page.url(), keycloak: steps, cookieReplayViaCaddy: replay });
  rec('signin: the key ring table holds at least one key', keys.length > 0, { keys });
  await ctx.close();
}

async function check(browser, label, directs) {
  const s = JSON.parse(fs.readFileSync(SESSION, 'utf8'));
  const cookie = authCookie(s.storage.cookies);
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, storageState: s.storage });
  const page = await ctx.newPage();
  const toKeycloak = []; const sockets = [];
  page.on('request', r => { if (r.isNavigationRequest() && r.url().startsWith(KC)) toKeycloak.push(r.url().split('?')[0]); });
  page.on('websocket', ws => sockets.push(ws.url().split('?')[0]));
  await page.goto(`${ACME}/admin/staff`);
  await page.waitForSelector('[data-member]', { timeout: 30000 }).catch(() => null);
  for (let i = 0; i < 20 && !sockets.some(u => u.includes('/_blazor')); i++) await sleep(500);
  const seen = { url: page.url(), memberList: await page.locator('[data-member]').count() > 0, keycloakNavigations: toKeycloak, circuit: sockets.filter(u => u.includes('/_blazor')) };
  rec(`${label}: the saved browser session opens /admin/staff through Caddy with no new login and an interactive circuit`,
    seen.url === `${ACME}/admin/staff` && seen.memberList && toKeycloak.length === 0 && seen.circuit.length > 0, seen);
  await ctx.close();
  const replay = await viaCaddy(cookie); const tampered = await viaCaddy(tamper(cookie));
  rec(`${label}: the cookie replayed through Caddy is accepted and a tampered copy is sent to Keycloak`,
    replay.status === 200 && replay.memberList && tampered.status === 302 && tampered.location.startsWith(`${KC}/realms/${REALM}/`), { replay, tampered });
  for (const base of directs) await checkDirect(`${label} ${base}`, base, cookie);
  const keys = keyRows();
  rec(`${label}: the key rows are the ones seen at sign-in (nothing made a new key)`, JSON.stringify(keys) === JSON.stringify(s.keysAtSignIn), { atSignIn: s.keysAtSignIn, now: keys });
}

async function checkDirect(label, base, cookie) {
  const withCookie = await direct(base, cookie); const without = await direct(base, null); const tampered = await direct(base, tamper(cookie));
  rec(`${label}: the same cookie opens /admin/staff on that instance; none or a tampered one is sent to Keycloak`,
    withCookie.status === 200 && withCookie.memberList && [without, tampered].every(x => x.status === 302 && x.location.startsWith(`${KC}/realms/${REALM}/`)),
    { withCookie, withoutCookie: without, tampered });
  // docs/07: X-Forwarded-Host is never taken; the tenant comes from the Host header Caddy passes through.
  const forwardedHost = await direct(base, cookie, { 'X-Forwarded-Host': 'beta.localhost:8443' });
  rec(`${label}: X-Forwarded-Host naming beta is ignored (acme's staff list, not beta's)`,
    forwardedHost.status === 200 && forwardedHost.acmeAdminListed && !forwardedHost.betaAdminListed, { forwardedHost });
}

async function cleanup() {
  if (!fs.existsSync(SESSION)) { rec('cleanup: nothing to clean (no session file)', true, {}); return; }
  const s = JSON.parse(fs.readFileSync(SESSION, 'utf8'));
  const seen = { email: s.email };
  try { await kc('DELETE', `/users/${s.uid}`); seen.keycloakUser = 'deleted'; } catch (e) { seen.keycloakUser = String(e).slice(0, 200); }
  seen.memberRows = sql(`with d as (delete from identity.members where email = ${q(s.email)} returning 1) select count(*) from d;`);
  if (state.totp) delete state.totp[s.email];
  if (state.lastStep) delete state.lastStep[s.email];
  saveState(state);
  fs.rmSync(SESSION);
  rec('cleanup: throwaway user, member row, TOTP seed and session file removed', seen.keycloakUser === 'deleted' && seen.memberRows === '1', seen);
}

// A web instance this script owns: the built dll, run directly so stopping it stops the host (no dotnet run child).
async function startSecond() {
  if (!fs.existsSync(WEB_DLL)) throw new Error(`build Platform.Web first: ${WEB_DLL} is missing`);
  const child = spawn('dotnet', [WEB_DLL, '--urls', SECOND], { cwd: WEB_DIR, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' } });
  let log = ''; child.stdout.on('data', d => { log += d; }); child.stderr.on('data', d => { log += d; });
  let exited = null; child.on('exit', c => { exited = c; });
  for (let i = 0; i < 90 && exited === null; i++) {
    try { if ((await fetch(`${SECOND}/health`)).ok) return { child, log: () => log }; } catch { /* not listening yet */ }
    await sleep(1000);
  }
  child.kill();
  throw new Error(`second instance did not become healthy (exit ${exited}); last log: ${log.slice(-600)}`);
}
async function stopSecond(p) {
  const done = new Promise(r => p.child.on('exit', r)); p.child.kill(); await done;
  const log = p.log();
  return { keyMaterialInLog: /<masterKey|<value>|<encryptedSecret/.test(log), createdKey: /Creating key/.test(log), keyRingErrors: (log.match(/fail: .*DataProtection.*|Key ring.*error.*/gi) ?? []).slice(0, 3) };
}

const mode = process.argv[2] ?? 'all';
const directs = process.argv.flatMap((a, i) => a === '--direct' ? [process.argv[i + 1]] : []);
const browser = mode === 'cleanup' ? null : await launch();
try {
  if (mode === 'signin') await signin(browser);
  else if (mode === 'check') await check(browser, 'check', directs);
  else if (mode === 'cleanup') await cleanup();
  else if (mode === 'all') {
    try {
      await signin(browser);
      await check(browser, 'check', ['http://localhost:5273']);
      for (const round of ['second instance', 'second instance restarted']) {
        const p = await startSecond();
        await checkDirect(round, SECOND, authCookie(JSON.parse(fs.readFileSync(SESSION, 'utf8')).storage.cookies));
        const log = await stopSecond(p);
        rec(`${round}: its log holds no key material and it made no key of its own`, !log.keyMaterialInLog && !log.createdKey && log.keyRingErrors.length === 0, log);
      }
      await check(browser, 'check after the second instance', []);
    } finally {
      await cleanup();
    }
  } else throw new Error(`unknown mode ${mode}: signin, check [--direct <url>], cleanup or all`);
} catch (e) {
  rec('script error', false, { error: String(e).slice(0, 800) });
} finally {
  fs.writeFileSync(RESULTS, JSON.stringify(results, null, 2));
  if (browser) await browser.close();
  if (results.some(r => !r.ok)) process.exitCode = 1;
}
