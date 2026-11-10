// Bounded before/after group results review. Opt-in (not matched by the
// ordinary capture config); uses the real runtime and test-only store.
import { mkdir, writeFile, readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { test, expect } from "../browser/support.js";
import { signInWithStore, createGroup, answerAll, open, STORE_STEP_MS } from "../browser/ui-conformance-fixtures.mjs";
import { auditPage } from "./audit.mjs";

test("group results, scoring and Folio report preserve values and reflow with the approved theme", async ({ page, context }) => {
  test.setTimeout(300_000);
  const root=process.cwd();
  const evidence=resolve(root,"docs/design-review/2026-11-10");
  const out=resolve(process.env.SIGNAL_RESULTS_REVIEW_OUT ?? test.info().outputPath("group-review"));
  await mkdir(out,{recursive:true});
  let baseline=true;
  await page.route("**/web/admin/index.html",async route=> baseline
    ? route.fulfill({contentType:"text/html",body:await readFile(resolve(evidence,"group-before/admin.html"),"utf8")})
    : route.continue());
  await page.route("**/web/admin/admin.css",async route=> baseline
    ? route.fulfill({contentType:"text/css",body:await readFile(resolve(evidence,"group-before/admin.css"),"utf8")})
    : route.continue());
  const states={};
  const tab=name=>page.getByRole("navigation",{name:"Group",exact:true}).getByRole("link",{name,exact:true}).click();
  const capture=async (variant,name)=>{
    for(const colorScheme of ["light","dark"]){
      await page.emulateMedia({colorScheme});
      for(const [viewport,width,height] of [["desktop",1440,900],["tablet",834,1112],["mobile",390,844]]){
        await page.setViewportSize({width,height});await page.mouse.move(0,0);
        await page.evaluate(()=>document.fonts.ready.then(()=>Promise.all(document.getAnimations().filter(a=>a.effect?.getTiming().iterations!==Infinity).map(a=>a.finished.catch(()=>{})))));
        await page.screenshot({path:resolve(out,`${variant}-${name}-${viewport}-${colorScheme}.png`),fullPage:true});
        if(variant==="after") expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
      }
    }
    if(variant==="after"){
      const audit=await auditPage(page,{screenshot:()=>page.screenshot({path:resolve(out,`${variant}-${name}-forced.png`)}).then(()=>`${variant}-${name}-forced.png`)});
      states[`${variant}-${name}-audit`]=audit;
      expect(audit.contrastLight.failures).toEqual([]);expect(audit.contrastDark.failures).toEqual([]);
      expect(audit.text200at320.overflow).toBeLessThanOrEqual(1);
      expect(audit.keyboard.withoutIndicator).toEqual([]);
    }
  };
  for(const variant of ["before","after"]){
    baseline=variant==="before";
    await page.setViewportSize({width:1440,height:900});
    await signInWithStore(page);
    await createGroup(page,{expected:"1",minimum:"1"});
    await tab("Imported surveys");
    const invites=page.locator("#invitations");
    await invites.getByRole("spinbutton",{name:"How many links"}).fill("1");
    await invites.getByRole("spinbutton",{name:"How many links"}).press("Tab");
    await invites.getByRole("button",{name:"Create links"}).click();
    await expect(page.locator("#invitation-links code")).toHaveCount(1,{timeout:STORE_STEP_MS});
    const issued=await page.locator("#invitation-links code").innerText();
    const respondent=await context.newPage();await open(respondent,issued);await answerAll(respondent,"Often");
    await respondent.getByRole("button",{name:"See results",exact:true}).click();
    await respondent.getByRole("button",{name:"Submit answers",exact:true}).click();
    const submission=await respondent.getByRole("textbox",{name:"Submission link"}).inputValue();await respondent.close();
    const imports=page.locator("#import");
    await imports.getByRole("textbox",{name:"Completed survey links, one per line"}).fill(submission);
    await imports.getByRole("button",{name:"Import",exact:true}).click();
    await expect(page.locator("#importAccepted li")).toHaveCount(1,{timeout:STORE_STEP_MS});
    await tab("Results");await expect(page.locator("#exploration")).toBeVisible();
    states[`${variant}-results`]=await page.locator("#section-chart tbody").innerText();
    if(!baseline){await expect(page.locator("#exploration")).toHaveClass(/echelon-panel/);await expect(page.getByRole("region",{name:"Section means",exact:true})).toHaveAttribute("tabindex","0");}
    await capture(variant,"results");
    await page.setViewportSize({width:1440,height:900});await tab("Scoring");
    states[`${variant}-scoring`]=await page.locator("#scoring-measures tbody").innerText();
    if(!baseline){await expect(page.getByRole("heading",{name:"How this was calculated",exact:true})).toBeVisible();await expect(page.locator("#lineage")).not.toHaveText("");}
    await capture(variant,"scoring");
    await page.setViewportSize({width:1440,height:900});await tab("Report");
    await expect(page.locator("#report-sections")).toBeVisible();
    states[`${variant}-report`]=await page.locator("#report-sections tbody").innerText();
    expect(await page.locator("#report-sections").evaluate(e=>e.parentElement.tagName)).toBe("EF-PRINT-TABLE");
    await capture(variant,"report");
    if(!baseline){await page.emulateMedia({media:"print"});await expect(page.getByRole("navigation",{name:"Administration",exact:true})).toBeHidden();await page.pdf({path:resolve(out,"after-report.pdf"),printBackground:true});await page.emulateMedia({media:"screen"});}
  }
  for(const name of ["results","scoring","report"]) expect(states[`after-${name}`]).toBe(states[`before-${name}`]);
  await writeFile(resolve(out,"verification.json"),JSON.stringify({kind:"test-fixture functional and visual measurements, not live authentication",...states},null,2));
});
