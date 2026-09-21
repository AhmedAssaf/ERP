// Screenshots every wireframe frame in English and Arabic into docs/wireframes/img/.
const path = require("path");
const fs = require("fs");
const puppeteer = require("puppeteer"); // run: npx --yes -p puppeteer node render.js

(async () => {
  const outDir = "C:/Repo/ERP/docs/wireframes/img";
  fs.mkdirSync(outDir, { recursive: true });
  const browser = await puppeteer.launch({ headless: true });
  const page = await browser.newPage();
  await page.setViewport({ width: 1120, height: 900, deviceScaleFactor: 1.5 });
  await page.emulateMediaFeatures([{ name: "prefers-color-scheme", value: "light" }]);
  await page.goto("file:///C:/Repo/ERP/docs/wireframes/mvp-wireframes.html", { waitUntil: "networkidle0" });
  await page.evaluate(() => document.fonts.ready);
  const ids = await page.$$eval("section.screen", s => s.map(x => x.id));
  const out = [];
  for (const lang of ["en", "ar"]) {
    await page.click(lang === "en" ? "#lang-en" : "#lang-ar");
    await new Promise(r => setTimeout(r, 300));
    for (const id of ids) {
      const el = await page.$(`#${id} .frame`);
      const file = path.join(outDir, `${id}-${lang}.png`);
      await el.screenshot({ path: file });
      out.push(file);
    }
  }
  await browser.close();
  console.log(out.length + " screenshots");
})().catch(e => { console.error(e); process.exit(1); });
