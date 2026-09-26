// Platform half of the admin-ui scenario: platform.admin signs in with OTP, all seven tiles healthy, ClamAV stopped then
// started, one alert and one recovery email, the incident closes.
import { execSync } from 'child_process';
import fs from 'fs';
import path from 'path';
import { launch, newPage, driveKeycloak, envValue, loadState, mailpit, sleep, SHOTS, DIR } from './lib.mjs';

const BOARD = 'https://platform.localhost:8443/platform';
const RCPT = 'platform-admin@waslabid.test';
const results = [];
const t0 = Date.now();
const el = () => `${Math.round((Date.now() - t0) / 1000)}s`;
const rec = (step, ok, seen) => { results.push({ step, ok, at: el(), seen }); console.log(`${ok ? 'PASS' : 'FAIL'} [${el()}] ${step} :: ${JSON.stringify(seen)}`); };
const state = loadState();
const pw = envValue('WASLABID_DEV_USER_PASSWORD');
const browser = await launch();
const { page } = await newPage(browser, { locale: 'en-US' });
const kcSteps = [];

async function board() {
  await page.goto(BOARD);
  if (page.url().startsWith('http://localhost:8080')) await driveKeycloak(page, { user: 'platform.admin', password: pw, state, log: s => kcSteps.push(s) });
  await page.waitForSelector('[data-component]', { timeout: 20000 });
  const tiles = await page.$$eval('[data-component]', es => Object.fromEntries(es.map(e => [e.dataset.component, e.dataset.status])));
  const incidents = await page.$$eval('[data-incident]', es => es.map(e => e.closest('tr').innerText.replace(/\s+/g, ' ').trim()));
  return { tiles, incidents };
}
async function until(label, pred, ms) {
  const end = Date.now() + ms; let last;
  while (Date.now() < end) { last = await pred(); if (last.ok) return last; await sleep(10000); }
  return { ...last, ok: false, timedOut: label };
}
const clamMails = async kind => (await mailpit(`to:${RCPT}`)).filter(m => m.Subject === (kind === 'down' ? '[WaslaBid] ClamAV is down' : '[WaslaBid] ClamAV has recovered'));

try {
  let b = await board();
  rec('platform.admin signs in with OTP at the platform host', page.url().startsWith(BOARD), { url: page.url(), keycloak: kcSteps });
  const healthy = await until('all healthy', async () => { b = await board(); return { ok: Object.values(b.tiles).length === 7 && Object.values(b.tiles).every(s => s === 'Healthy'), tiles: b.tiles }; }, 150000);
  await page.screenshot({ path: path.join(SHOTS, '10-board-all-healthy.png'), fullPage: true });
  rec('all seven tiles healthy', healthy.ok, healthy);

  const downBefore = new Set((await clamMails('down')).map(m => m.ID));
  const upBefore = new Set((await clamMails('up')).map(m => m.ID));
  const allBefore = new Set((await mailpit(`to:${RCPT}`)).map(m => m.ID));
  execSync('docker stop erp-clamav'); const stoppedAt = Date.now();
  rec('docker stop erp-clamav', true, {});
  const down = await until('clamav unhealthy', async () => { b = await board(); return { ok: b.tiles.ClamAV === 'Unhealthy', tiles: b.tiles }; }, 180000);
  const downSecs = Math.round((Date.now() - stoppedAt) / 1000);
  await page.screenshot({ path: path.join(SHOTS, '11-board-clamav-unhealthy.png'), fullPage: true });
  rec('ClamAV tile Unhealthy', down.ok, { ...down, secondsAfterStop: downSecs, openIncidents: b.incidents.filter(i => /open|مفتوح/i.test(i)) });
  const alert = await until('alert email', async () => { const n = (await clamMails('down')).filter(m => !downBefore.has(m.ID)); return { ok: n.length >= 1, subjects: n.map(m => m.Subject) }; }, 120000);
  await sleep(75000); // one more check cycle: the alert must not repeat
  const alertsAfter = (await clamMails('down')).filter(m => !downBefore.has(m.ID));
  rec('exactly one alert email', alert.ok && alertsAfter.length === 1, { subjects: alertsAfter.map(m => m.Subject), secondsAfterStop: Math.round((Date.now() - stoppedAt) / 1000) });

  execSync('docker start erp-clamav'); const startedAt = Date.now();
  rec('docker start erp-clamav', true, {});
  const up = await until('clamav healthy', async () => { b = await board(); return { ok: b.tiles.ClamAV === 'Healthy', tiles: b.tiles }; }, 360000);
  rec('ClamAV tile Healthy again', up.ok, { ...up, secondsAfterStart: Math.round((Date.now() - startedAt) / 1000) });
  const rec1 = await until('recovery email', async () => { const n = (await clamMails('up')).filter(m => !upBefore.has(m.ID)); return { ok: n.length >= 1, subjects: n.map(m => m.Subject) }; }, 120000);
  await sleep(75000);
  const recAfter = (await clamMails('up')).filter(m => !upBefore.has(m.ID));
  b = await board();
  const newMail = (await mailpit(`to:${RCPT}`)).filter(m => !allBefore.has(m.ID)).map(m => m.Subject);
  rec('exactly one recovery email', rec1.ok && recAfter.length === 1, { subjects: recAfter.map(m => m.Subject), allNewPlatformMail: newMail });
  const clamIncident = b.incidents.find(i => /ClamAV/.test(i));
  await page.screenshot({ path: path.join(SHOTS, '12-board-incident-closed.png'), fullPage: true });
  rec('incident shows as closed', !!clamIncident && !/\bOpen\b|مفتوح/.test(clamIncident), { latestClamAVIncident: clamIncident });
} catch (e) {
  rec('script error', false, { error: String(e).slice(0, 400) });
  try { execSync('docker start erp-clamav'); } catch { /* reported above */ }
} finally {
  fs.writeFileSync(path.join(DIR, 'platform-results.json'), JSON.stringify(results, null, 2));
  await browser.close();
}
