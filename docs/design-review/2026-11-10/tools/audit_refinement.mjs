import { readFile, writeFile } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const {chromium}=await import(root+'/node_modules/@playwright/test/index.mjs');
const {auditPage}=await import(root+'/tests/visual/audit.mjs');
const origin=process.env.SIGNAL_REVIEW_ORIGIN??'https://signal.echelonfoundry.com';
const out=resolve(dirname(fileURLToPath(import.meta.url)), '../after');
const browser=await chromium.launch({executablePath:'/usr/bin/chromium',args:['--no-sandbox'],proxy:origin.startsWith('https:')?{server:process.env.HTTPS_PROXY}:undefined});
const context=await browser.newContext();
await context.route(origin+'/**',async route=>{
 const path=new URL(route.request().url()).pathname;
 const file=({'/web/':'web/index.html','/web/survey/':'web/survey/index.html'})[path]??path.slice(1);
 if(['web/index.html','web/survey/index.html','web/styles.css','web-kernel/page.css'].includes(file)||file.startsWith('node_modules/')) {
  try {await route.fulfill({path:resolve(root,file)});return;} catch{}
 }
 await route.continue();
});
const page=await context.newPage();
const audits={};
await page.goto(origin+'/web/');
await page.locator('#see-results').waitFor({timeout:60000});
for(const name of await page.locator('input[type=radio]').evaluateAll(a=>[...new Set(a.map(x=>x.name))])) await page.locator(`input[name="${name}"][value="2"]`).check();
await page.locator('#see-results').click();
await page.locator('#results').waitFor();
audits.results=await auditPage(page,{screenshot:()=>page.screenshot({path:out+'/results-audit-forced.png'}).then(()=> 'results-audit-forced.png')});
const links=JSON.parse(await readFile(root+'/tests/browser/fixtures/survey-links.json','utf8'));
await page.goto(origin+'/'+links.identified);
await page.locator('#submit-answers').waitFor({timeout:60000});
audits.respondent=await auditPage(page,{screenshot:()=>page.screenshot({path:out+'/respondent-audit-forced.png'}).then(()=> 'respondent-audit-forced.png')});
await writeFile(out+'/accessibility.json',JSON.stringify(audits,null,2));
console.log(JSON.stringify(audits,null,2));
await browser.close();
