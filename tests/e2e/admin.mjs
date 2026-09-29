// Shared test fixtures for the end-to-end scripts: the Keycloak Admin API as the master admin, read and test-data SQL as
// the Compose superuser, and throwaway staff and platform admins with their own TOTP seeds, so no script needs the seeded
// acme.admin, beta.admin or platform.admin (whose seeds live only on whichever machine enrolled them). Every throwaway
// user is removed by cleanup(). Secrets come from the .env (E2E_ENV_FILE) and never reach the console (N-10).
import crypto from 'crypto';
import { execFileSync } from 'child_process';
import { envValue, loadState, saveState } from './lib.mjs';

export const KC = 'http://localhost:8080';
export const TENANT_REALM = 'waslabid';
export const PLATFORM_REALM = 'waslabid-platform';
export const newPassword = () => crypto.randomBytes(12).toString('base64url') + 'aA1!';

// admin-cli tokens live 60 seconds, so one per call.
async function kcToken() {
  const body = new URLSearchParams({ grant_type: 'password', client_id: 'admin-cli', username: envValue('KEYCLOAK_ADMIN'), password: envValue('KEYCLOAK_ADMIN_PASSWORD') });
  const r = await fetch(`${KC}/realms/master/protocol/openid-connect/token`, { method: 'POST', body });
  if (!r.ok) throw new Error(`Keycloak admin token refused (${r.status})`);
  return (await r.json()).access_token;
}
export async function kc(method, p, body, realm = TENANT_REALM) {
  const headers = { Authorization: `Bearer ${await kcToken()}` };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  return fetch(`${KC}/admin/realms/${realm}${p}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
}
export async function kcOk(method, p, body, realm = TENANT_REALM) {
  const r = await kc(method, p, body, realm);
  if (!r.ok) throw new Error(`Keycloak ${method} ${p.split('?')[0]}: ${r.status} ${(await r.text()).slice(0, 200)}`);
  return r;
}
const orgIds = {};
export async function orgId(alias) {
  // Keycloak's search matches the organization's name, not its alias, so list them and match the alias.
  if (!orgIds[alias]) orgIds[alias] = (await (await kcOk('GET', '/organizations?briefRepresentation=true&max=100')).json()).find(o => o.alias === alias).id;
  return orgIds[alias];
}
export async function createUser({ email, username = email, password, firstName, lastName, requiredActions = [], realm = TENANT_REALM }) {
  const r = await kcOk('POST', '/users', {
    username, email, emailVerified: true, enabled: true, firstName, lastName, requiredActions,
    credentials: [{ type: 'password', value: password, temporary: false }],
  }, realm);
  return r.headers.get('location').split('/').pop();
}
export const isMember = async (alias, uid) => (await kc('GET', `/organizations/${await orgId(alias)}/members/${uid}`)).status === 200;
export const realmRoles = async (uid, realm = TENANT_REALM) => (await (await kcOk('GET', `/users/${uid}/role-mappings/realm`, undefined, realm)).json()).map(r => r.name);
export const userIdByEmail = async (email, realm = TENANT_REALM) => (await (await kcOk('GET', `/users?email=${encodeURIComponent(email)}&exact=true`, undefined, realm)).json())[0]?.id;
export const deleteUser = async (uid, realm = TENANT_REALM) => (await kc('DELETE', `/users/${uid}`, undefined, realm)).status;

// ---- PostgreSQL as the Compose superuser (test data and reads only) ----
export const q = s => `'${String(s).replace(/'/g, "''")}'`;
export function sql(query) {
  return execFileSync('docker', ['exec', '-i', '-e', 'PGCLIENTENCODING=UTF8', 'erp-postgres', 'psql', '-U', 'erp', '-d', 'platform', '-v', 'ON_ERROR_STOP=1', '-q', '-A', '-t', '-F', '\t'],
    { input: query, encoding: 'utf8' }).trim();
}
export const tenantId = slug => sql(`select id from tenancy.tenants where slug = ${q(slug)};`);

// ---- Throwaway users, each with its own TOTP seed captured by driveKeycloak into .state ----
const created = [];

/** A tenant staff member: a Keycloak user in the tenant's organization plus an invited identity.members row bound by email on first sign-in. */
export async function throwawayStaff(slug, role, label, run) {
  const email = `${label}.${run}@${slug}.waslabid.test`;
  const password = newPassword();
  const uid = await createUser({ email, password, firstName: 'E2E', lastName: label, requiredActions: ['CONFIGURE_TOTP'] });
  created.push({ kind: 'staff', uid, email, slug });
  await kcOk('POST', `/organizations/${await orgId(slug)}/members`, uid);
  sql(`insert into identity.members (id, tenant_id, user_id, email, display_name, roles, status, invited_at)
       values (gen_random_uuid(), ${q(tenantId(slug))}, null, ${q(email)}, ${q(`E2E ${label}`)}, array[${q(role)}], 'invited', now());`);
  return { email, password, uid };
}

/** A platform admin in the platform realm: the realm role platform-admin, and a one-time code enrolled on first sign-in. */
export async function throwawayPlatformAdmin(run) {
  const username = `platform.e2e.${run}`;
  const email = `${username}@waslabid.test`;
  const password = newPassword();
  const uid = await createUser({ email, username, password, firstName: 'E2E', lastName: 'Platform', requiredActions: ['CONFIGURE_TOTP'], realm: PLATFORM_REALM });
  created.push({ kind: 'platform', uid, email: username });
  const role = await (await kcOk('GET', '/roles/platform-admin', undefined, PLATFORM_REALM)).json();
  await kcOk('POST', `/users/${uid}/role-mappings/realm`, [role], PLATFORM_REALM);
  return { username, email, password, uid };
}

/** A vendor-side person (squatter or claimant) created with a verified email, deleted by cleanup like the others. */
export async function throwawayPerson(email, password, firstName, lastName) {
  const uid = await createUser({ email, password, firstName, lastName });
  created.push({ kind: 'person', uid, email });
  return uid;
}
/** A person the script created through self-registration, so cleanup removes it too. */
export const trackPerson = (uid, email) => created.push({ kind: 'person', uid, email });

/** Deletes every throwaway user this process created, their member rows and their TOTP seeds; returns what happened. */
export async function cleanup() {
  const state = loadState();
  const out = [];
  for (const u of created.splice(0)) {
    const realm = u.kind === 'platform' ? PLATFORM_REALM : TENANT_REALM;
    const entry = { kind: u.kind, email: u.email, keycloakDelete: await deleteUser(u.uid, realm) };
    if (u.kind === 'staff') {
      entry.memberRows = sql(`with d as (delete from identity.members where tenant_id = ${q(tenantId(u.slug))} and email = ${q(u.email)} returning 1) select count(*) from d;`);
    }
    if (state.totp) delete state.totp[u.email];
    if (state.lastStep) delete state.lastStep[u.email];
    out.push(entry);
  }
  saveState(state);
  return out;
}
