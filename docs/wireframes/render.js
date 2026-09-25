// Screenshots every wireframe screen (frames, pop-up states, and guide notes) in English and Arabic into docs/wireframes/img/.
const path = require("path");
const fs = require("fs");
// Resolves puppeteer from a local install or from the npx cache (populated by any earlier `npx @mermaid-js/mermaid-cli` or `npx -p puppeteer` run).
function loadPuppeteer() {
  try { return require("puppeteer"); } catch (_) {}
  const cache = path.join(process.env.LOCALAPPDATA || "", "npm-cache", "_npx");
  for (const d of fs.existsSync(cache) ? fs.readdirSync(cache) : []) {
    const candidate = path.join(cache, d, "node_modules", "puppeteer");
    if (fs.existsSync(candidate)) return require(candidate);
  }
  throw new Error("puppeteer not found: run `npm install --no-save puppeteer` in this folder once");
}
const puppeteer = loadPuppeteer();

(async () => {
  const outDir = "C:/Repo/ERP/docs/wireframes/img";
  fs.mkdirSync(outDir, { recursive: true });
  const browser = await puppeteer.launch({ headless: true });
  const page = await browser.newPage();
  await page.setViewport({ width: 1140, height: 900, deviceScaleFactor: 1.5 });
  await page.emulateMediaFeatures([{ name: "prefers-color-scheme", value: "light" }]);
  await page.goto("file:///C:/Repo/ERP/docs/wireframes/mvp-wireframes.html", { waitUntil: "networkidle0" });
  await page.evaluate(() => document.fonts.ready);
  // The sticky page header would cover tall frames in element screenshots.
  await page.addStyleTag({ content: ".top{position:static!important}" });
  const ids = await page.$$eval("section.screen", s => s.map(x => x.id));
  const out = [];
  for (const lang of ["en", "ar"]) {
    await page.click(lang === "en" ? "#lang-en" : "#lang-ar");
    await new Promise(r => setTimeout(r, 300));
    for (const id of ids) {
      const el = await page.$(`#${id} .shot`);
      const file = path.join(outDir, `${id}-${lang}.png`);
      await el.screenshot({ path: file });
      out.push(file);
    }
  }
  await browser.close();
  console.log(out.length + " screenshots");
})().catch(e => { console.error(e); process.exit(1); });
