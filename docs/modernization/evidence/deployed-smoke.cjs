const { chromium } = require('playwright');
(async () => {
const base = 'https://signal.echelonfoundry.com';
const pages = ['/web/', '/web/admin/', '/web/survey/'];
const browser = await chromium.launch();
const results = [];
for (const p of pages) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await ctx.newPage();
  const errors = [];
  page.on('console', m => m.type() === 'error' && errors.push(m.text()));
  page.on('pageerror', e => errors.push(String(e)));
  const t0 = Date.now();
  const resp = await page.goto(base + p, { waitUntil: 'networkidle', timeout: 60000 });
  await page.waitForTimeout(3000);
  const text = (await page.locator('body').innerText()).slice(0, 400).replace(/\s+/g, ' ');
  const banner = await page.locator('text=/Demo/i').count();
  await page.screenshot({ path: `shot${p.replace(/\//g, '_')}.png` });
  results.push({ path: p, status: resp.status(), ms: Date.now() - t0, consoleErrors: errors, demoBanner: banner > 0, text });
  await ctx.close();
}
await browser.close();
console.log(JSON.stringify({ at: new Date().toISOString(), base, results }, null, 2));
})();
