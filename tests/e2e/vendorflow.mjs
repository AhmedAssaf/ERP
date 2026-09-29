// Vendor-side steps shared by vendor.mjs and ownership.mjs: a minimal PDF, the chunked upload on /vendor, signing in again
// after sign-out, and the company form on /vendor/register/company.
import { driveKeycloak } from './lib.mjs';

// A minimal valid one-page PDF (magic number %PDF), different content per document so the hashes differ.
export function pdf(label) {
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

/** Uploads a file of the given document type with its expiry on /vendor and returns the upload box's final state. */
export async function upload(page, type, file, expiry) {
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

/** Keycloak's logout confirmation, when it asks, then whatever login steps follow. */
export async function signInAgain(page, { user, password, state, log }) {
  for (let i = 0; i < 5; i++) {
    await page.waitForLoadState('domcontentloaded');
    const logout = await page.$('#kc-logout');
    if (logout) { log('confirmed logout'); await Promise.all([page.waitForNavigation(), logout.click()]); continue; }
    break;
  }
  await driveKeycloak(page, { user, password, state, log });
}

/** Fills and posts the company form (privacy notice accepted); returns whether it registered and any form error. */
export async function registerCompany(page, { cr, nameAr, nameEn, vat, email, contact }) {
  const f = page.locator('[data-vendor-register]');
  await f.locator('input[name="Input.CrNumber"]').fill(cr);
  await f.locator('input[name="Input.NameAr"]').fill(nameAr);
  await f.locator('input[name="Input.NameEn"]').fill(nameEn);
  await f.locator('input[name="Input.VatNumber"]').fill(vat);
  await f.locator('input[name="Input.Address"]').fill('الرياض، طريق الملك فهد، حي العليا');
  await f.locator('input[name="Input.ContactName"]').fill(contact);
  await f.locator('input[name="Input.ContactPhone"]').fill('+966501234567');
  await f.locator('input[name="Input.ContactEmail"]').fill(email);
  await f.locator('input[name="Input.AcceptedPrivacyNotice"]').check();
  await Promise.all([page.waitForLoadState('domcontentloaded'), f.locator('button[type=submit]').click()]);
  await page.waitForSelector('[data-vendor-registered], [data-form-error]', { timeout: 30000 }).catch(() => null);
  return {
    registered: await page.locator('[data-vendor-registered]').count() > 0,
    formError: await page.locator('[data-form-error]').innerText().catch(() => null),
  };
}
