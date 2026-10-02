// W-10 plan task 10: end-to-end smoke of the observability pipeline against the running stack (spec
// docs/superpowers/specs/2026-09-30-observability-design.md sections 6.1, 6.7, 8 and 10). Steps:
//   1. GET /dev/throw on acme answers 500 with X-Correlation-Id.
//   2. Elasticsearch holds the trace within 60 s; its server span carries waslabid.tenant.id of acme.
//   3. ES|QL finds one Error log record for the trace: exception type, component, tenant id, no raw exception.
//   4. The F-53 query of spec section 8 as a throwaway user with the role waslabid_errors_reader counts the error; the
//      same user cannot index into logs-*.
//   5. After one run of vendor.mjs, no log record of the last hour holds an email address or the run's CR number.
//   6. Collector, then Elasticsearch, stopped: requests keep their usual time (p95), one "Telemetry is down" email naming
//      the part, one "has recovered" email after the restart.
//   7. /alive answers 200 on acme and the platform host, also with PostgreSQL stopped; /health answers 503 then.
//   8. Usage: a throwaway acme staff admin online gives waslabid.users.concurrent 1 for acme staff, the Kibana dashboard
//      shows it to the Kibana staff user, the active-users metric follows the usage job, the console usage page shows the
//      same numbers in both cultures with the Kibana link and no email or name; closing the page brings the count to 0.
//   9. Kibana's Observability views (traces, logs) open the deliberate failure's trace by its id (risk O-4).
// Run order is 1, 2, 3, 4, 5, 8, 9, 6, 7: the usage and Kibana steps run before the steps that stop containers, and
// PostgreSQL is stopped last. Pass step numbers to run a subset (`node observability.mjs 1 2 3`); steps 2, 3, 4 and 9
// add step 1, which makes their failure. Steps 6 and 7 always start what they stopped, even on failure.
// Secrets (ELASTIC_PASSWORD, KIBANA_STAFF_PASSWORD, the Keycloak admin) come from infra/compose/.env (E2E_ENV_FILE) and
// go into request headers and form fields only (N-10). Everything this script prints or stores passes through scrub(),
// which replaces every secret value of the .env, the generated passwords of the throwaway users, their Basic headers and
// the TOTP seeds in .state with [secret], so an error message that quotes a filled value (Playwright's call log does)
// cannot leak one. A short secret such as the local default Keycloak admin password `admin` is scrubbed too, so words that
// contain it read as [secret] in the output. The throwaway Elasticsearch user's password is generated, used and deleted
// with the user.
// Ctrl-C or SIGTERM runs the same restore and cleanup as the end of a run, once: containers started again, the probe
// user, the Keycloak throwaways and Kibana removed or stopped, results written; then the script exits non-zero.
// E2E_SELF_INTERRUPT_MS=<ms> raises SIGINT inside the process after that delay, to check the restore path without a
// console (on Windows a signal sent from another process cannot be caught).
import { execFileSync, spawn } from 'child_process';
import crypto from 'crypto';
import fs from 'fs';
import https from 'https';
import path from 'path';
import { launch, newPage, driveKeycloak, envValue, loadState, mailpit, mailBody, sleep, DIR } from './lib.mjs';
import { throwawayStaff, throwawayPlatformAdmin, cleanup, sql, q, tenantId } from './admin.mjs';

const ACME = 'https://acme.localhost:8443';
const PLATFORM = 'https://platform.localhost:8443';
const WEB_DIRECT = 'http://localhost:5273';
const ES = 'http://127.0.0.1:9200';
const KIBANA = 'http://127.0.0.1:5601';
const COMPOSE_DIR = path.join(DIR, '..', '..', 'infra', 'compose');
const SHOTS = path.join(DIR, 'shots-observability');
fs.mkdirSync(SHOTS, { recursive: true });
const RCPT = 'platform-admin@waslabid.test';
const THROW_MESSAGE = 'Deliberate failure for the W-10 checks.';
const run = Date.now().toString();
const ENV_FILE = process.env.E2E_ENV_FILE || path.join(DIR, '..', '..', 'infra', 'compose', '.env');

// ---- Secret scrubbing (N-10) ----
const secrets = new Set();
const addSecret = value => { if (typeof value === 'string' && value.length > 0) secrets.add(value); return value; };
for (const line of fs.readFileSync(ENV_FILE, 'utf8').split(/\r?\n/)) {
  const m = line.match(/^([A-Z0-9_]+)=(.*)$/);
  if (m && /PASSWORD|SECRET|_KEY$|TOKEN/.test(m[1])) addSecret(m[2].trim());
}
/** Every known secret value in a string replaced with [secret], longest first; TOTP seeds and the vendor password read from .state at call time. */
function scrubText(text) {
  const state = loadState();
  const all = [...secrets, ...Object.values(state.totp ?? {}), state.vendorPw].filter(v => typeof v === 'string' && v.length > 0)
    .sort((a, b) => b.length - a.length);
  let out = String(text);
  for (const v of all) out = out.split(v).join('[secret]');
  return out;
}
/** scrubText applied to every string inside a value (objects and arrays walked), so stored JSON stays valid. */
function scrub(value) {
  if (typeof value === 'string') return scrubText(value);
  if (Array.isArray(value)) return value.map(scrub);
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, scrub(v)]));
  return value;
}
const log = text => console.log(scrubText(text));

const ORDER = [1, 2, 3, 4, 5, 8, 9, 6, 7];
const asked = process.argv.slice(2).map(Number).filter(n => ORDER.includes(n));
if (asked.some(n => [2, 3, 4, 9].includes(n)) && !asked.includes(1)) asked.push(1);
const STEPS = asked.length ? ORDER.filter(n => asked.includes(n)) : ORDER;

const t0 = Date.now();
const el = () => `${Math.round((Date.now() - t0) / 1000)}s`;
const results = [];
const evidence = { date: new Date().toISOString(), steps: STEPS, fields: {}, memory: [], p95: {} };
const rec = (step, check, ok, seen) => {
  const clean = scrub(seen ?? {});
  results.push({ step, check: scrubText(check), ok, at: el(), seen: clean });
  log(`${ok ? 'PASS' : 'FAIL'} [${el()}] ${step}. ${check} :: ${JSON.stringify(clean)}`);
};
const errorText = e => scrubText(maskEmails(String(e?.stack ?? e))).slice(0, 600);

// ---------------------------------------------------------------------------------------------------------------------
// HTTP helpers. Through Caddy: connect to 127.0.0.1 with the tenant host as SNI and Host header (Node does not resolve
// *.localhost on Windows) and accept Caddy's local certificate, as the Playwright scripts do with ignoreHTTPSErrors.
function viaCaddy(url) {
  return new Promise((resolve, reject) => {
    const u = new URL(url);
    const started = process.hrtime.bigint();
    const req = https.request({
      host: '127.0.0.1', port: Number(u.port || 443), path: u.pathname + u.search, method: 'GET', agent: false,
      servername: u.hostname, headers: { Host: u.host }, rejectUnauthorized: false,
    }, res => {
      res.resume();
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, ms: Number(process.hrtime.bigint() - started) / 1e6 }));
    });
    req.on('error', reject);
    req.setTimeout(30000, () => req.destroy(new Error('timeout after 30 s')));
    req.end();
  });
}
async function direct(url) {
  try { return (await fetch(url, { signal: AbortSignal.timeout(30000) })).status; } catch (e) { return `error ${e.name}`; }
}

const basic = (user, password) => addSecret(`Basic ${Buffer.from(`${user}:${password}`).toString('base64')}`);
const elasticAuth = () => basic('elastic', envValue('ELASTIC_PASSWORD'));
const staffAuth = () => basic(envValue('KIBANA_STAFF_USER'), envValue('KIBANA_STAFF_PASSWORD'));

/** Elasticsearch call; returns the status and parsed body. Callers report only the status and the error type. */
async function es(method, p, body, auth = elasticAuth()) {
  try {
    const r = await fetch(ES + p, {
      method, headers: { Authorization: auth, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30000),
    });
    const text = await r.text();
    let json; try { json = JSON.parse(text); } catch { json = undefined; }
    return { status: r.status, json };
  } catch (e) { return { status: `error ${e.name}`, json: undefined }; }
}
const esError = r => r.json?.error?.type ?? '';
/** ES|QL as rows of objects. A parse or verification error comes back with its reason (it quotes the query, never a secret). */
async function esql(query, auth) {
  const r = await es('POST', '/_query', { query }, auth);
  if (r.status !== 200) return { status: r.status, error: esError(r), reason: String(r.json?.error?.reason ?? '').slice(0, 300), rows: [] };
  const names = r.json.columns.map(c => c.name);
  return { status: 200, columns: r.json.columns, rows: r.json.values.map(v => Object.fromEntries(names.map((n, i) => [n, v[i]]))) };
}
const hits = r => r.json?.hits?.hits?.map(h => h._source) ?? [];

async function until(fn, ms, every = 5000) {
  const end = Date.now() + ms;
  for (;;) {
    const last = await fn();
    if (last.ok || Date.now() >= end) return last;
    await sleep(every);
  }
}
const p95 = values => { const s = [...values].sort((a, b) => a - b); return s[Math.max(0, Math.ceil(0.95 * s.length) - 1)]; };
const round = n => Math.round(n * 10) / 10;

function docker(...args) { return execFileSync('docker', args, { encoding: 'utf8' }).trim(); }
function memory(label) {
  const stats = docker('stats', '--no-stream', '--format', '{{.Name}}|{{.MemUsage}}|{{.MemPerc}}').split('\n')
    .filter(l => /^erp-(elasticsearch|otel-collector|kibana)\|/.test(l)).map(l => l.split('|'))
    .map(([name, usage, percent]) => ({ name, usage, percent }));
  let es = 'not running';
  try { es = docker('inspect', '-f', 'OOMKilled={{.State.OOMKilled}} RestartCount={{.RestartCount}} Status={{.State.Status}}', 'erp-elasticsearch'); } catch { /* reported as not running */ }
  const entry = { label, at: el(), stats, elasticsearch: es };
  evidence.memory.push(entry);
  log(`MEM [${el()}] ${label} :: ${JSON.stringify(entry)}`);
  return entry;
}
async function waitHealthy(container, ms) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    try { if (docker('inspect', '-f', '{{.State.Health.Status}}', container) === 'healthy') return true; } catch { /* starting */ }
    await sleep(3000);
  }
  return false;
}

// Mailpit: telemetry alert emails to the platform recipient, newest first.
const telemetryMail = async kind => (await mailpit(`to:${RCPT} subject:Telemetry`))
  .filter(m => m.Subject === (kind === 'down' ? '[WaslaBid] Telemetry is down' : '[WaslaBid] Telemetry has recovered'));

// The masking marker the redactor writes, applied before any log text reaches this script's own output.
const maskEmails = s => String(s ?? '').replace(/[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+/g, '[email]');
// Email address pattern for ES|QL RLIKE (Lucene regular expression, whole value; '@' and '-' escaped).
const EMAIL_RLIKE = '.*[A-Za-z0-9._%+\\-]+\\@[A-Za-z0-9\\-]+(\\.[A-Za-z0-9\\-]+)+.*';

// ---------------------------------------------------------------------------------------------------------------------
let traceId;
let acmeId;

async function step1() {
  const r = await viaCaddy(`${ACME}/dev/throw`);
  traceId = r.headers['x-correlation-id'];
  evidence.traceId = traceId;
  evidence.throwAt = Date.now();
  rec(1, 'GET /dev/throw on acme answers 500 and carries X-Correlation-Id', r.status === 500 && /^[0-9a-f]{32}$/.test(traceId ?? ''),
    { status: r.status, correlationId: traceId ?? null, ms: round(r.ms) });
}

async function step2() {
  acmeId ??= tenantId('acme');
  const res = await until(async () => {
    const r = await es('POST', '/traces-*/_search', { size: 20, query: { term: { trace_id: traceId } } });
    const spans = hits(r);
    const server = spans.find(s => s.kind === 'Server');
    return { ok: !!server, status: r.status, spans, server };
  }, 60000, 3000);
  const s = res.server;
  const seconds = Math.round((Date.now() - evidence.throwAt) / 1000);
  rec(2, 'Elasticsearch holds the trace within 60 s, server span with waslabid.tenant.id of acme',
    !!s && s.attributes?.['waslabid.tenant.id'] === acmeId,
    { searchStatus: res.status, secondsAfterRequest: seconds, spans: res.spans.length,
      serverSpan: s && { name: s.name, kind: s.kind, status: s.status, tenantId: s.attributes?.['waslabid.tenant.id'], acmeId,
        service: s.resource?.attributes?.['service.name'], index: 'traces-*' } });
  if (s) {
    const mapping = await es('GET', '/traces-*/_mapping/field/trace_id,span_id,kind,name,attributes.waslabid.tenant.id,attributes.waslabid.tenant.slug,resource.attributes.service.name');
    evidence.fields.trace = {
      topLevel: Object.keys(s), serverSpanAttributes: Object.keys(s.attributes ?? {}), resourceAttributes: Object.keys(s.resource?.attributes ?? {}),
      mappedTypes: Object.fromEntries(Object.values(mapping.json ?? {}).flatMap(i => Object.entries(i.mappings ?? {}))
        .map(([k, v]) => [k, Object.values(v.mapping)[0]?.type])),
      dataStream: s.data_stream,
    };
  }
}

async function step3() {
  acmeId ??= tenantId('acme');
  // The brief's query, with the columns named so the field names are on record.
  const query = `FROM logs-* | WHERE trace_id == "${traceId}" | KEEP @timestamp, trace_id, span_id, severity_number, severity_text, `
    + 'resource.attributes.service.name, scope.name, attributes.waslabid.component, attributes.waslabid.tenant.id, attributes.exception.type, attributes.exception.message';
  const res = await until(async () => {
    const r = await esql(query);
    const errors = r.rows.filter(x => x.severity_number >= 17);
    return { ok: errors.length >= 1, ...r, errors };
  }, 60000, 3000);
  const e = res.errors[0];
  rec(3, 'ES|QL finds one Error record for the trace with exception.type, waslabid.component Web and the tenant id',
    res.status === 200 && res.errors.length === 1 && e.severity_text === 'Error'
      && e['attributes.exception.type'] === 'System.InvalidOperationException' && e['attributes.waslabid.component'] === 'Web'
      && e['attributes.waslabid.tenant.id'] === acmeId,
    { status: res.status, error: res.error, reason: res.reason, recordsForTrace: res.rows.length, errorRecords: res.errors.length,
      secondsAfterRequest: Math.round((Date.now() - evidence.throwAt) / 1000), record: e });

  // The stored document: exception data only as the three masked attributes, no raw exception object or extra field.
  const doc = hits(await es('POST', '/logs-*/_search', { size: 5, query: { bool: { filter: [{ term: { trace_id: traceId } }, { range: { severity_number: { gte: 17 } } }] } } }))[0];
  const attrKeys = Object.keys(doc?.attributes ?? {});
  const exceptionKeys = attrKeys.filter(k => /exception|error|stack/i.test(k)).sort();
  const topLevelExtra = Object.keys(doc ?? {}).filter(k => /exception|error/i.test(k));
  rec(3, 'no raw exception object: only exception.type, exception.message and exception.stacktrace',
    !!doc && JSON.stringify(exceptionKeys) === JSON.stringify(['exception.message', 'exception.stacktrace', 'exception.type']) && topLevelExtra.length === 0
      && doc.attributes['exception.message'] === THROW_MESSAGE,
    { exceptionKeys, topLevelExtra, exceptionMessage: doc?.attributes?.['exception.message'],
      note: 'the endpoint message holds nothing maskable; masking itself is proven by the unit tests of task 3' });
  if (doc) {
    evidence.fields.log = {
      topLevel: Object.keys(doc), attributes: attrKeys, resourceAttributes: Object.keys(doc.resource?.attributes ?? {}),
      message: doc.body ? `body.text` : '(none)', scope: doc.scope, severity: { number: doc.severity_number, text: doc.severity_text },
    };
  }
}

const F53_SPEC = `FROM logs-*
| WHERE @timestamp >= NOW() - 24 hours
    AND resource.attributes.service.name IN ("waslabid-web", "waslabid-worker")
    AND severity_number >= 17
| STATS errors = COUNT(*) BY resource.attributes.service.name, attributes.waslabid.component, attributes.exception.type
| SORT errors DESC`;

const PROBE_PREFIX = 'w10_f53_probe_';
let probeUser; // set while the throwaway Elasticsearch user exists, so an interrupted run deletes it

async function deleteProbeUser() {
  if (!probeUser) return undefined;
  const d = await es('DELETE', `/_security/user/${probeUser}`);
  if (d.status === 200 || d.status === 404) probeUser = undefined;
  return d.status;
}

async function step4() {
  // Leftovers of an earlier run killed before its cleanup: only users this script created (name prefix and metadata).
  const listed = await es('GET', '/_security/user');
  const leftovers = Object.entries(listed.json ?? {})
    .filter(([name, u]) => name.startsWith(PROBE_PREFIX) && u.metadata?.owner === 'waslabid-e2e').map(([name]) => name);
  const swept = [];
  for (const name of leftovers) swept.push({ name, status: (await es('DELETE', `/_security/user/${name}`)).status });
  rec(4, `leftover ${PROBE_PREFIX}* users swept before the run`, listed.status === 200 && swept.every(x => x.status === 200),
    { listStatus: listed.status, swept });

  const user = `${PROBE_PREFIX}${run}`;
  const password = addSecret(crypto.randomBytes(18).toString('base64url'));
  const created = await es('PUT', `/_security/user/${user}`, { password, roles: ['waslabid_errors_reader'], full_name: 'W-10 task 10 probe', metadata: { owner: 'waslabid-e2e' } });
  rec(4, 'throwaway user with role waslabid_errors_reader created (as elastic)', created.status === 200, { status: created.status, error: esError(created), user });
  if (created.status !== 200) return;
  probeUser = user;
  try {
    const auth = basic(user, password);
    const lastHour = F53_SPEC.replace('NOW() - 24 hours', 'NOW() - 1 hour');
    evidence.f53Query = lastHour;
    const res = await until(async () => {
      const r = await esql(lastHour, auth);
      const web = r.rows.find(x => x['resource.attributes.service.name'] === 'waslabid-web' && x['attributes.waslabid.component'] === 'Web'
        && x['attributes.exception.type'] === 'System.InvalidOperationException');
      return { ok: r.status === 200 && (web?.errors ?? 0) >= 1, ...r, web };
    }, 60000, 5000);
    rec(4, 'the F-53 query (last hour) as the reader counts at least one error for waslabid-web, component Web',
      res.ok, { status: res.status, error: res.error, reason: res.reason, matchingRow: res.web, rows: res.rows.slice(0, 8) });
    const spec = await esql(F53_SPEC, auth);
    rec(4, 'the F-53 query verbatim from spec section 8 (24 hours) runs as the reader', spec.status === 200 && spec.rows.length >= 1,
      { status: spec.status, error: spec.error, rows: spec.rows.length });

    const probeText = `W-10 task 10 write probe ${run}`;
    const w = await es('POST', '/logs-generic.otel-default/_doc', { '@timestamp': new Date().toISOString(), body: { text: probeText } }, auth);
    rec(4, 'the same user is refused (403) when indexing a document into logs-*', w.status === 403, { status: w.status, error: esError(w) });
    if (typeof w.status === 'number' && w.status < 300) {
      await es('POST', '/logs-*/_delete_by_query?refresh=true', { query: { match_phrase: { 'body.text': probeText } } });
    }
  } finally {
    const status = await deleteProbeUser();
    rec(4, 'throwaway Elasticsearch user deleted', status === 200, { status });
  }
}

async function step5() {
  const startedAt = new Date();
  const logFile = path.join(DIR, 'observability-vendor-run.log');
  const exitCode = await new Promise(resolve => {
    const out = fs.openSync(logFile, 'w');
    vendorChild = spawn(process.execPath, ['vendor.mjs'], { cwd: DIR, env: process.env, stdio: ['ignore', out, out] });
    vendorChild.on('exit', code => { fs.closeSync(out); vendorChild = undefined; resolve(code); });
  });
  fs.writeFileSync(logFile, scrubText(fs.readFileSync(logFile, 'utf8')));
  const endedAt = new Date();
  const company = sql(`select cr_number, vat_number, contact_email from vendor.companies where created_at >= ${q(startedAt.toISOString())} order by created_at desc limit 1;`).split('\t');
  const [cr, vat, email] = company.length === 3 ? company : [];
  rec(5, 'vendor.mjs ran (registration, uploads, consent) and registered a company', exitCode === 0 && !!cr,
    { exitCode, log: path.basename(logFile), companyFound: !!cr, minutes: round((endedAt - startedAt) / 60000) });
  if (!cr) return;

  // Barrier: a fresh failure on the web host and a worker record after the run, both found in Elasticsearch, so every
  // record the run caused has been exported and indexed before the searches.
  const barrier = (await viaCaddy(`${ACME}/dev/throw`)).headers['x-correlation-id'];
  const flushed = await until(async () => {
    const web = (await esql(`FROM logs-* | WHERE trace_id == "${barrier}" | STATS n = COUNT(*)`)).rows[0]?.n ?? 0;
    const worker = (await esql(`FROM logs-* | WHERE resource.attributes.service.name == "waslabid-worker" AND @timestamp > TO_DATETIME("${endedAt.toISOString()}") | STATS n = COUNT(*)`)).rows[0]?.n ?? 0;
    return { ok: web > 0 && worker > 0, web, worker };
  }, 120000, 5000);
  rec(5, 'records after the run from both hosts are indexed (export barrier)', flushed.ok, flushed);

  const control = await esql(`ROW hit = "${email}", miss = "no address in this text" | EVAL h = hit RLIKE """${EMAIL_RLIKE}""", m = miss RLIKE """${EMAIL_RLIKE}""" | KEEP h, m`);
  const onField = await esql('FROM logs-* | WHERE @timestamp >= NOW() - 1 hour AND body.text RLIKE ".*unhandled exception.*" | STATS n = COUNT(*)');
  const controlOk = control.rows[0]?.h === true && control.rows[0]?.m === false && (onField.rows[0]?.n ?? 0) > 0;
  rec(5, 'control: the email RLIKE pattern matches the run\'s address and not plain text, and RLIKE reads body.text (finds the deliberate failure)',
    controlOk, { status: control.status, error: control.error, reason: control.reason, row: control.rows[0], bodyTextRlikeHits: onField.rows[0]?.n });

  const emails = await esql(`FROM logs-* | WHERE @timestamp >= NOW() - 1 hour AND resource.attributes.service.name IN ("waslabid-web", "waslabid-worker") AND body.text RLIKE """${EMAIL_RLIKE}""" | STATS n = COUNT(*)`);
  let samples = [];
  if ((emails.rows[0]?.n ?? 0) > 0) {
    samples = (await esql(`FROM logs-* | WHERE @timestamp >= NOW() - 1 hour AND resource.attributes.service.name IN ("waslabid-web", "waslabid-worker") AND body.text RLIKE """${EMAIL_RLIKE}""" | KEEP @timestamp, resource.attributes.service.name, scope.name, severity_text, body.text | LIMIT 10`))
      .rows.map(r => ({ ...r, 'body.text': maskEmails(r['body.text']) }));
  }
  rec(5, 'no log record of the last hour from waslabid-web or waslabid-worker has an email address in body.text (ES|QL RLIKE)',
    controlOk && emails.status === 200 && emails.rows[0]?.n === 0, { status: emails.status, error: emails.error, reason: emails.reason, count: emails.rows[0]?.n, samplesMasked: samples });

  const lastHour = { range: { '@timestamp': { gte: 'now-1h' } } };
  const qs = (value, index = 'logs-*') => es('POST', `/${index}/_search`, { size: 5, track_total_hits: true, query: { bool: { filter: [lastHour, { query_string: { query: `"${value}"`, fields: ['*'], lenient: true } }] } } });
  const positive = await qs(THROW_MESSAGE);
  const positiveOk = (positive.json?.hits?.total?.value ?? 0) > 0;
  rec(5, 'control: a query_string over all fields of logs-* finds a known value (the deliberate failure message)', positiveOk, { status: positive.status, hits: positive.json?.hits?.total?.value });
  // The same search with a wildcard on both sides also finds the digits inside a longer token or value (CR7123456789,
  // cr=7123456789). Controls: part of the barrier trace id inside the keyword trace_id, and part of a word in body.text.
  const wild = (value, index = 'logs-*') => es('POST', `/${index}/_search`, { size: 5, track_total_hits: true, query: { bool: { filter: [lastHour, { query_string: { query: `*${value}*`, fields: ['*'], lenient: true, allow_leading_wildcard: true } }] } } });
  const embeddedId = barrier.slice(8, 18);
  const wildControls = {
    [`*${embeddedId}* (inside trace_id)`]: (await wild(embeddedId)).json?.hits?.total?.value,
    '*nhandle* (inside "unhandled" in body.text)': (await wild('nhandle')).json?.hits?.total?.value,
  };
  const wildOk = Object.values(wildControls).every(n => (n ?? 0) > 0);
  rec(5, 'control: a wildcard query_string over all fields of logs-* finds a value embedded in a longer token', wildOk, wildControls);
  const crHits = await qs(cr);
  const crWild = await wild(cr);
  rec(5, 'no log record of the last hour holds the run\'s CR number, alone or inside a longer token (query_string exact and *CR*, all fields of logs-*)',
    positiveOk && wildOk && crHits.status === 200 && crHits.json.hits.total.value === 0 && crWild.status === 200 && crWild.json.hits.total.value === 0,
    { exact: { status: crHits.status, error: esError(crHits), hits: crHits.json?.hits?.total?.value },
      wildcard: { status: crWild.status, error: esError(crWild), hits: crWild.json?.hits?.total?.value },
      where: [...hits(crHits), ...hits(crWild)].map(s => ({ service: s.resource?.attributes?.['service.name'], scope: s.scope?.name })) });

  // Wider look, recorded for the report: the literal address and the VAT number in logs, and all three in traces.
  const wider = {};
  for (const [label, value] of [['email', email], ['vat', vat]]) wider[`logs:${label}`] = (await qs(value)).json?.hits?.total?.value;
  for (const [label, value] of [['email', email], ['cr', cr], ['vat', vat]]) wider[`traces:${label}`] = (await qs(value, 'traces-*')).json?.hits?.total?.value;
  rec(5, 'the run\'s literal email address and VAT number in logs-*, and email, CR and VAT in traces-*: no hits',
    Object.values(wider).every(v => v === 0), wider);
}

// ---------------------------------------------------------------------------------------------------------------------
async function kibana(method, p, auth = staffAuth()) {
  try {
    const r = await fetch(KIBANA + p, { method, headers: { Authorization: auth, 'kbn-xsrf': 'e2e' }, signal: AbortSignal.timeout(30000) });
    let json; try { json = JSON.parse(await r.text()); } catch { json = undefined; }
    return { status: r.status, json };
  } catch (e) { return { status: `error ${e.name}`, json: undefined }; }
}
let kibanaStarted = false;
async function kibanaUp() {
  kibanaStarted = true;
  execFileSync('docker', ['compose', '--profile', 'kibana', 'up', '-d', 'kibana', 'kibana-setup'], { cwd: COMPOSE_DIR, stdio: 'ignore' });
  const ready = await until(async () => {
    const s = await kibana('GET', '/api/status');
    return { ok: s.status === 200 && s.json?.status?.overall?.level === 'available', status: s.status, level: s.json?.status?.overall?.level };
  }, 240000, 5000);
  return ready;
}
function kibanaStop() {
  try { execFileSync('docker', ['compose', '--profile', 'kibana', 'stop', 'kibana'], { cwd: COMPOSE_DIR, stdio: 'ignore' }); kibanaStarted = false; return 'stopped'; } catch (e) { return `stop failed: ${errorText(e).slice(0, 120)}`; }
}
async function kibanaSignIn(browser) {
  const { ctx, page } = await newPage(browser, { width: 1600, height: 1100, locale: 'en-US' });
  await page.goto(`${KIBANA}/login`);
  await page.fill('[data-test-subj="loginUsername"]', envValue('KIBANA_STAFF_USER'));
  await page.fill('[data-test-subj="loginPassword"]', envValue('KIBANA_STAFF_PASSWORD'));
  await Promise.all([page.waitForURL(u => !u.pathname.startsWith('/login'), { timeout: 60000 }), page.click('[data-test-subj="loginSubmit"]')]);
  return { ctx, page };
}

const usageWhere = (kind = 'staff') => `attributes.waslabid.tenant.slug == "acme" AND attributes.waslabid.user.kind == "${kind}"`;
async function latestConcurrent(after) {
  const r = await esql(`FROM metrics-* | WHERE @timestamp > TO_DATETIME("${after.toISOString()}") AND ${usageWhere()} AND metrics.waslabid.users.concurrent IS NOT NULL `
    + '| SORT @timestamp DESC | LIMIT 1 | KEEP @timestamp, metrics.waslabid.users.concurrent, metrics.waslabid.circuits.connected, resource.attributes.service.name, scope.name');
  return { status: r.status, error: r.error, reason: r.reason, row: r.rows[0] };
}
async function latestActive(after) {
  const r = await esql(`FROM metrics-* | WHERE @timestamp > TO_DATETIME("${after.toISOString()}") AND ${usageWhere()} AND metrics.waslabid.users.active IS NOT NULL `
    + '| SORT @timestamp DESC | LIMIT 12 | KEEP @timestamp, attributes.waslabid.window, metrics.waslabid.users.active, resource.attributes.service.name');
  const byWindow = {};
  for (const row of r.rows) byWindow[row['attributes.waslabid.window']] ??= row['metrics.waslabid.users.active'];
  return { status: r.status, error: r.error, reason: r.reason, byWindow, latestAt: r.rows[0]?.['@timestamp'] };
}

async function consoleRow(page) {
  await page.waitForSelector('[data-tenant="acme"]', { timeout: 30000 });
  return page.evaluate(() => {
    const row = document.querySelector('[data-tenant="acme"]').closest('tr');
    const cells = Object.fromEntries([...row.querySelectorAll('[data-window][data-kind]')].map(e => [`${e.dataset.window}:${e.dataset.kind}`, e.dataset.users]));
    return {
      cells, lang: document.documentElement.lang, dir: document.documentElement.dir,
      kibanaHref: document.querySelector('a[data-kibana="dashboard"]')?.getAttribute('href') ?? null,
      state: document.querySelector('[data-usage-state]')?.dataset.usageState ?? null,
      mainText: document.querySelector('main')?.innerText ?? document.body.innerText, bodyText: document.body.innerText,
    };
  });
}

async function step8(browser) {
  acmeId ??= tenantId('acme');
  const state = loadState();
  let staffCtx;
  try {
    const staff = await throwawayStaff('acme', 'tenant-admin', 'w10-usage', run);
    addSecret(staff.password);
    const s = await newPage(browser, { locale: 'en-US' });
    staffCtx = s.ctx;
    await s.page.goto(`${ACME}/admin/staff`);
    const kcSteps = [];
    await driveKeycloak(s.page, { user: staff.email, password: staff.password, state, log: x => kcSteps.push(x) });
    await s.page.waitForLoadState('networkidle');
    const signedInAt = new Date();
    rec(8, 'throwaway acme staff admin signed in and keeps /admin/staff open', s.page.url().startsWith(`${ACME}/admin/staff`), { url: s.page.url(), keycloak: kcSteps });

    const concurrent = await until(async () => { const c = await latestConcurrent(signedInAt); return { ok: c.row?.['metrics.waslabid.users.concurrent'] === 1, ...c }; }, 150000, 10000);
    rec(8, 'within two export intervals metrics.waslabid.users.concurrent for acme staff is 1', concurrent.ok,
      { secondsAfterSignIn: Math.round((Date.now() - signedInAt) / 1000), status: concurrent.status, error: concurrent.error, reason: concurrent.reason, row: concurrent.row });

    // Field names of the usage metrics as stored, and that no usage document carries a user or company id.
    const usageDocs = hits(await es('POST', '/metrics-*/_search', { size: 500, query: { bool: { filter: [{ term: { 'scope.name': 'WaslaBid.Usage' } }, { range: { '@timestamp': { gte: 'now-1h' } } }] } } }));
    const metricNames = [...new Set(usageDocs.flatMap(d => Object.keys(d.metrics ?? {})))].sort();
    const attrNames = [...new Set(usageDocs.flatMap(d => Object.keys(d.attributes ?? {})))].sort();
    evidence.fields.usage = { metricFields: metricNames.map(n => `metrics.${n}`), attributeFields: attrNames.map(n => `attributes.${n}`), scope: 'scope.name = WaslaBid.Usage',
      dataStreams: [...new Set(usageDocs.map(d => `${d.data_stream?.type}-${d.data_stream?.dataset}-${d.data_stream?.namespace}`))], units: [...new Set(usageDocs.map(d => d.unit))] };
    const unprefixed = await esql(`FROM metrics-* | WHERE waslabid.tenant.slug == "acme" AND waslabid.users.concurrent IS NOT NULL | STATS n = COUNT(*)`);
    evidence.fields.usage.passthroughWithoutPrefix = { status: unprefixed.status, error: unprefixed.error, rows: unprefixed.rows[0] };
    rec(8, 'usage metric documents carry only the tenant slug, kind and window as attributes (no user or company id)',
      usageDocs.length > 0 && attrNames.every(a => ['waslabid.tenant.slug', 'waslabid.user.kind', 'waslabid.window'].includes(a)), { documents: usageDocs.length, metricNames, attrNames });

    // Kibana: the dashboard exists for the staff user, and shows 1 concurrent staff user for acme.
    const kbUp = await kibanaUp();
    rec(8, 'Kibana up (profile kibana)', kbUp.ok, kbUp);
    memory('Kibana up, staff online');
    const saved = await kibana('GET', '/api/saved_objects/dashboard/waslabid-usage');
    rec(8, 'GET /api/saved_objects/dashboard/waslabid-usage as the Kibana staff user finds the dashboard', saved.status === 200 && saved.json?.attributes?.title === 'WaslaBid usage',
      { status: saved.status, title: saved.json?.attributes?.title });
    const k = await kibanaSignIn(browser);
    await k.page.goto(`${KIBANA}/app/dashboards#/view/waslabid-usage?_g=(time:(from:now-15m,to:now))&_a=(query:(language:kuery,query:'attributes.waslabid.tenant.slug:"acme"'))`);
    const panel = await until(async () => {
      const text = await k.page.locator('[data-test-subj="dashboardPanel"], [data-test-subj="embeddablePanel"]').filter({ hasText: 'Concurrent users now' }).first().innerText({ timeout: 5000 }).catch(() => '');
      return { ok: /staff\s*1(?!\d)/i.test(text), text: text.replace(/\s+/g, ' ').trim().slice(0, 300) };
    }, 90000, 5000);
    await k.page.screenshot({ path: path.join(SHOTS, '8-kibana-usage-acme-staff-1.png'), fullPage: true });
    const errorsOnDashboard = await k.page.locator('text=/An error occurred|Could not locate that index-pattern|field not found/i').count();
    rec(8, 'Kibana dashboard, signed in as the staff user, shows 1 concurrent staff user for acme',
      panel.ok && errorsOnDashboard === 0, { panelText: panel.text, errorsOnDashboard, screenshot: 'shots-observability/8-kibana-usage-acme-staff-1.png' });
    await k.ctx.close();

    // Active users after the usage job (every five minutes).
    const active = await until(async () => { const a = await latestActive(signedInAt); return { ok: (a.byWindow['1d'] ?? 0) >= 1, ...a }; }, 400000, 15000);
    rec(8, 'after the usage job, metrics.waslabid.users.active for acme staff and 1d is at least 1', active.ok,
      { secondsAfterSignIn: Math.round((Date.now() - signedInAt) / 1000), status: active.status, error: active.error, byWindow: active.byWindow, latestAt: active.latestAt });

    // The console usage page as a throwaway platform admin with OTP, in both cultures.
    const admin = await throwawayPlatformAdmin(run);
    addSecret(admin.password);
    const p = await newPage(browser, { locale: 'en-US' });
    await p.page.goto(`${PLATFORM}/platform/usage`);
    const pk = [];
    await driveKeycloak(p.page, { user: admin.username, password: admin.password, state, log: x => pk.push(x) });
    const views = {};
    for (const culture of ['ar-SA', 'en-US']) {
      await p.page.goto(`${PLATFORM}/culture/set?culture=${culture}&returnUrl=${encodeURIComponent('/platform/usage')}`);
      await p.page.waitForLoadState('networkidle');
      views[culture] = await consoleRow(p.page);
      await p.page.screenshot({ path: path.join(SHOTS, `8-console-usage-${culture}.png`), fullPage: true });
    }
    const v = views['en-US'];
    const esActive = (await latestActive(signedInAt)).byWindow;
    const same = v.cells['now:staff'] === '1' && ['1d', '7d', '30d'].every(w => v.cells[`${w}:staff`] === String(esActive[w]));
    rec(8, 'console usage page shows the same numbers for acme staff as Elasticsearch (now 1; 1d, 7d, 30d as the active metric)',
      same && views['ar-SA'].cells['now:staff'] === v.cells['now:staff'],
      { keycloak: pk, console: v.cells, consoleArSA: views['ar-SA'].cells, elasticsearchActive: esActive, state: v.state });
    rec(8, 'console usage page links to the Kibana dashboard',
      v.kibanaHref === `${KIBANA}/app/dashboards#/view/waslabid-usage`, { href: v.kibanaHref });
    const emailInMain = Object.fromEntries(Object.entries(views).map(([c, x]) => [c, (x.mainText.match(/[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+/g) ?? []).length]));
    const staffNamed = Object.fromEntries(Object.entries(views).map(([c, x]) => [c, x.bodyText.includes(staff.email) || /E2E w10-usage|w10-usage/.test(x.bodyText)]));
    rec(8, 'console usage page lists no email or name (main content; the counted staff user nowhere on the page)',
      Object.values(emailInMain).every(n => n === 0) && Object.values(staffNamed).every(b => !b), { emailsInMain: emailInMain, staffUserShown: staffNamed });
    rec(8, 'console usage page in Arabic is right to left and in English left to right',
      views['ar-SA'].lang === 'ar' && views['ar-SA'].dir === 'rtl' && v.lang === 'en' && v.dir === 'ltr',
      { arSA: { lang: views['ar-SA'].lang, dir: views['ar-SA'].dir }, enUS: { lang: v.lang, dir: v.dir },
        screenshots: ['shots-observability/8-console-usage-ar-SA.png', 'shots-observability/8-console-usage-en-US.png'] });

    // Close the staff page: the concurrent count drops to 0 within two export intervals.
    await staffCtx.close(); staffCtx = undefined;
    const closedAt = new Date();
    const zero = await until(async () => { const c = await latestConcurrent(closedAt); return { ok: c.row?.['metrics.waslabid.users.concurrent'] === 0, ...c }; }, 150000, 10000);
    rec(8, 'after the staff page closes, metrics.waslabid.users.concurrent for acme staff is 0 within two export intervals', zero.ok,
      { secondsAfterClose: Math.round((Date.now() - closedAt) / 1000), status: zero.status, row: zero.row });
    await p.page.goto(`${PLATFORM}/platform/usage`);
    await p.page.waitForLoadState('networkidle');
    const after = await consoleRow(p.page);
    await p.page.screenshot({ path: path.join(SHOTS, '8-console-usage-en-US-after-close.png'), fullPage: true });
    rec(8, 'console usage page shows 0 online staff for acme after the page closed', after.cells['now:staff'] === '0', { cells: after.cells });
    await p.ctx.close();
  } finally {
    if (staffCtx) await staffCtx.close().catch(() => {});
    const removed = await cleanup().catch(e => [{ error: errorText(e) }]);
    rec(8, 'cleanup: throwaway staff and platform admin deleted from Keycloak, member row deleted',
      removed.length > 0 && removed.every(r => r.keycloakDelete === 204 && (r.kind !== 'staff' || r.memberRows === '1')), removed.map(r => ({ kind: r.kind, keycloakDelete: r.keycloakDelete, memberRows: r.memberRows, error: r.error })));
  }
}

async function step9(browser) {
  const kbUp = await kibanaUp();
  if (!kbUp.ok) { rec(9, 'Kibana up (profile kibana)', false, kbUp); return; }
  // The trace must be indexed first (step 1 may have run seconds ago).
  await until(async () => ({ ok: (hits(await es('POST', '/traces-*/_search', { size: 1, query: { term: { trace_id: traceId } } }))).length > 0 }), 60000, 3000);
  const k = await kibanaSignIn(browser);
  // Each view in a page of its own: Discover is a single-page app, and a hash change alone keeps the previous results on
  // screen while the new query runs, which a text check would read as the new view's result.
  const view = async (label, url, expect, shot) => {
    const page = await k.ctx.newPage();
    await page.goto(url);
    const r = await until(async () => {
      const text = await page.locator('body').innerText().catch(() => '');
      const running = await page.locator('button:has-text("Cancel")').count().catch(() => 0);
      return { ok: running === 0 && expect.test(text), url: page.url(), text };
    }, 60000, 5000);
    await page.screenshot({ path: path.join(SHOTS, shot), fullPage: true });
    await page.close();
    const notFound = /trace not found|could not find|no results|no data/i.exec(r.text)?.[0] ?? null;
    return { label, ok: r.ok, finalUrl: r.url.slice(0, 300), notFoundText: notFound, screenshot: `shots-observability/${shot}`, excerpt: r.text.replace(/\s+/g, ' ').slice(0, 400) };
  };
  try {
    const range = 'rangeFrom=now-2h&rangeTo=now';
    // Whether the APM app sees any data at all (its own check, the internal API the APM UI calls on load).
    const hasData = await fetch(`${KIBANA}/internal/apm/has_data`, { headers: { Authorization: staffAuth(), 'kbn-xsrf': 'e2e', 'x-elastic-internal-origin': 'kibana' } })
      .then(async r => ({ status: r.status, body: await r.json().catch(() => null) })).catch(e => ({ status: `error ${e.name}` }));
    const apm = await view('APM trace by id (/app/apm/link-to/trace/<id>)', `${KIBANA}/app/apm/link-to/trace/${traceId}?${range}`, /GET \/dev\/throw/, '9-kibana-apm-trace.png');
    rec(9, 'Kibana Observability traces (APM) opens the deliberate failure\'s trace by its id', apm.ok, { ...apm, apmHasData: hasData });
    const logsView = await view('Observability logs (Discover, data view "All logs", KQL on trace_id)',
      `${KIBANA}/app/discover#/?_g=(time:(from:now-2h,to:now))&_a=(dataSource:(dataViewId:discover-observability-solution-all-logs,type:dataView),query:(language:kuery,query:'trace_id:"${traceId}"'))`,
      /unhandled exception/i, '9-kibana-observability-all-logs.png');
    rec(9, 'Kibana Observability logs (All logs) finds the deliberate failure\'s log records by its trace id', logsView.ok, logsView);
    const esqlUrl = query => `${KIBANA}/app/discover#/?_g=(time:(from:now-2h,to:now))&_a=(dataSource:(type:esql),query:(esql:'${query.replace(/!/g, '!!').replace(/'/g, "!'")}'))`;
    const logs = await view('Discover logs by trace id (ES|QL)', esqlUrl(`FROM logs-* | WHERE trace_id == "${traceId}"`), /unhandled exception|InvalidOperationException/, '9-kibana-discover-logs.png');
    rec(9, 'Kibana Discover (logs) finds the deliberate failure\'s log records by its trace id', logs.ok, logs);
    const traces = await view('Discover traces by trace id (ES|QL)', esqlUrl(`FROM traces-* | WHERE trace_id == "${traceId}"`), /CONNECT platform|http\.response\.status_code/, '9-kibana-discover-traces.png');
    rec(9, 'Kibana Discover (traces) finds the deliberate failure\'s spans by its trace id', traces.ok, traces);
  } finally {
    await k.ctx.close();
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Thirty requests a run, so the nearest-rank p95 is the 29th value, not the maximum: one blip does not decide it.
const TIMED_REQUESTS = 30;
async function timings(n = TIMED_REQUESTS) {
  const out = [];
  for (let i = 0; i < n; i++) { const r = await viaCaddy(`${ACME}/`); out.push({ status: r.status, ms: round(r.ms) }); }
  return { statuses: [...new Set(out.map(x => x.status))], ms: out.map(x => x.ms), p95: p95(out.map(x => x.ms)) };
}

async function outage(container, part, other) {
  const downBefore = new Set((await telemetryMail('down')).map(m => m.ID));
  const upBefore = new Set((await telemetryMail('up')).map(m => m.ID));
  const baseline = await timings();
  let stoppedAt;
  try {
    docker('stop', container); stoppedAt = Date.now();
    const during = await timings();
    evidence.p95[container] = { requests: TIMED_REQUESTS, baselineMs: baseline.p95, stoppedMs: during.p95 };
    rec(6, `${container} stopped: ${TIMED_REQUESTS} GET / on acme answer as usual (p95 within 100 ms of a ${TIMED_REQUESTS}-request baseline)`,
      during.statuses.length === 1 && during.statuses[0] === baseline.statuses[0] && baseline.statuses.length === 1 && during.p95 - baseline.p95 <= 100,
      { baseline, during, addedP95Ms: round(during.p95 - baseline.p95) });
    const down = await until(async () => {
      const fresh = (await telemetryMail('down')).filter(m => !downBefore.has(m.ID));
      const bodies = await Promise.all(fresh.map(async m => (await mailBody(m.ID)).Text ?? ''));
      return { ok: fresh.length >= 1, count: fresh.length, bodies };
    }, 120000 - (Date.now() - stoppedAt), 5000);
    const named = down.bodies.length > 0 && down.bodies.every(b => new RegExp(part, 'i').test(b) && !new RegExp(other, 'i').test(b));
    rec(6, `within two minutes one "[WaslaBid] Telemetry is down" email naming ${part}`, down.ok && named,
      { secondsAfterStop: Math.round((Date.now() - stoppedAt) / 1000), count: down.count, failureLine: down.bodies.map(b => (b.match(/Telemetry pipeline:[^\r\n]*/) ?? ['(line not found)'])[0]) });
  } finally {
    docker('start', container);
  }
  const startedAt = Date.now();
  const healthy = container === 'erp-elasticsearch' ? await waitHealthy(container, 240000) : true;
  const up = await until(async () => ({ ok: (await telemetryMail('up')).some(m => !upBefore.has(m.ID)) }), 360000, 5000);
  const recoverySeconds = Math.round((Date.now() - startedAt) / 1000);
  await sleep(75000); // one more health-check cycle: neither email may repeat
  const downs = (await telemetryMail('down')).filter(m => !downBefore.has(m.ID)).length;
  const ups = (await telemetryMail('up')).filter(m => !upBefore.has(m.ID)).length;
  rec(6, `${container} started: one "has recovered" email follows, and exactly one down email in total`, up.ok && downs === 1 && ups === 1,
    { healthyAfterStart: healthy, recoveryEmailSecondsAfterStart: recoverySeconds, downEmails: downs, recoveredEmails: ups });
  memory(`after ${container} restart`);
}

async function step6() {
  await outage('erp-otel-collector', 'collector', 'elasticsearch');
  await outage('erp-elasticsearch', 'elasticsearch', 'collector');
}

async function step7() {
  const alive = async () => ({ acme: (await viaCaddy(`${ACME}/alive`)).status, platform: (await viaCaddy(`${PLATFORM}/alive`)).status });
  const health = async () => ({ acmeViaCaddy: (await viaCaddy(`${ACME}/health`)).status, direct: await direct(`${WEB_DIRECT}/health`) });
  const before = { alive: await alive(), health: await health() };
  rec(7, '/alive answers 200 on acme and the platform host (PostgreSQL up)', before.alive.acme === 200 && before.alive.platform === 200, before);
  try {
    docker('stop', 'erp-postgres');
    const stoppedAt = Date.now();
    // /health answers from the key ring probe's result for up to 5 seconds (W-24, KeyRingAvailability.CacheFor) and a
    // probe gives up after 3, so the 503 is awaited for 20 seconds; /alive is then asked while the database is known down.
    const unhealthy = await until(async () => { const h = await health(); return { ok: h.direct === 503, ...h }; }, 20000, 1000);
    rec(7, 'PostgreSQL stopped: /health answers 503, as today', unhealthy.ok,
      { acmeViaCaddy: unhealthy.acmeViaCaddy, direct: unhealthy.direct, secondsAfterStop: round((Date.now() - stoppedAt) / 1000) });
    const during = await alive();
    rec(7, 'PostgreSQL stopped: /alive answers 200 on acme and the platform host', during.acme === 200 && during.platform === 200, during);
  } finally {
    docker('start', 'erp-postgres');
  }
  const healthy = await waitHealthy('erp-postgres', 120000);
  const back = await until(async () => { const h = await health(); return { ok: h.direct === 200, ...h }; }, 120000, 5000);
  rec(7, 'PostgreSQL started again: healthy, and /health answers 200 again', healthy && back.ok, { postgresHealthy: healthy, health: back });
}

// ---------------------------------------------------------------------------------------------------------------------
let browser;
let vendorChild;
let restoring;

/** Restores the stack and removes what the run created; runs once, whether the run ended, failed or was interrupted. */
function restoreAndCleanup(reason) {
  restoring ??= (async () => {
    const done = { reason };
    const attempt = async (label, fn) => { try { done[label] = await fn(); } catch (e) { done[label] = `failed: ${errorText(e)}`; } };
    if (vendorChild) await attempt('vendorRun', () => { vendorChild.kill(); return 'stopped'; });
    if (browser) await attempt('browser', async () => { await browser.close(); return 'closed'; });
    // The containers the steps stop are running again.
    for (const c of ['erp-otel-collector', 'erp-elasticsearch', 'erp-postgres']) {
      await attempt(c, () => {
        if (docker('inspect', '-f', '{{.State.Running}}', c) === 'true') return 'running';
        docker('start', c);
        return 'restarted';
      });
    }
    if (probeUser) await attempt('probeUser', async () => `delete answered ${await deleteProbeUser()}`);
    // Keycloak throwaways and their member rows: a no-op when step 8 already cleaned up; needs PostgreSQL, started above.
    await attempt('keycloakThrowaways', async () => {
      await waitHealthy('erp-postgres', 60000);
      return (await cleanup()).map(x => ({ kind: x.kind, keycloakDelete: x.keycloakDelete, memberRows: x.memberRows }));
    });
    if (kibanaStarted) await attempt('kibana', () => kibanaStop());
    await attempt('memory', () => { memory('end'); return 'sampled'; });
    evidence.restore = scrub(done);
    log(`RESTORE ${JSON.stringify(evidence.restore)}`);
    fs.writeFileSync(path.join(DIR, 'observability-results.json'), scrubText(JSON.stringify({ ...evidence, results }, null, 2)));
    const failed = results.filter(r => !r.ok);
    log(`\n${results.length - failed.length} passed, ${failed.length} failed; results in observability-results.json`);
    return failed.length;
  })();
  return restoring;
}

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, async () => {
    if (!restoring) rec(0, `interrupted by ${signal}`, false, {});
    await restoreAndCleanup(signal);
    process.exit(signal === 'SIGINT' ? 130 : 143);
  });
}
if (process.env.E2E_SELF_INTERRUPT_MS) setTimeout(() => process.emit('SIGINT', 'SIGINT'), Number(process.env.E2E_SELF_INTERRUPT_MS)).unref();

try {
  memory('start');
  for (const n of STEPS) {
    try {
      // Playwright's own signal handlers would exit before restoreAndCleanup ends; this script closes the browser itself.
      if (n === 8 || n === 9) browser ??= await launch({ handleSIGINT: false, handleSIGTERM: false, handleSIGHUP: false });
      await { 1: step1, 2: step2, 3: step3, 4: step4, 5: step5, 6: step6, 7: step7, 8: step8, 9: step9 }[n](browser);
    } catch (e) {
      rec(n, 'script error', false, { error: errorText(e) });
    }
    if (n === 5) memory('after step 5');
    if ((n === 9) || (n === 8 && !STEPS.includes(9))) { memory('Kibana up, before stop'); rec(n, 'Kibana stopped after the Kibana steps', kibanaStop() === 'stopped', {}); }
  }
} finally {
  if (await restoreAndCleanup('end')) process.exitCode = 1;
}
