// Deep links opened cold (SIG-LINK-001..012): every test starts a fresh page
// at a link. Runs twice: against the checkout (project "chromium") and
// against the assembled GitHub Pages site served under a sub-path (project
// "pages", e.g. http://127.0.0.1:4392/signal/web/admin/#/...), so a link
// copied from the published site opens the same view. Addresses are relative
// to the project's base URL.
import { test, expect } from "./support.js";

const ADMIN = "web/admin/";
const QUESTION = "#/assessments/SDRA/versions/0.1.0-draft/sections/D02/questions/CORE-007";

async function cold(page, fragment) {
  await page.goto(ADMIN + fragment);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
}

test("a cold deep link opens the question it names, with no sign-in", async ({ page }) => {
  await cold(page, QUESTION);
  await expect(page.locator("#question-prompt")).toHaveText(
    "The team can clarify or renegotiate planned scope when it learns something important.");
  await expect(page.locator("#question-id")).toHaveText("CORE-007");
  await expect(page.locator("#section-label")).toHaveText("D02 Change Responsiveness");
  await expect(page.locator('#section-questions a[aria-current="page"]')).toContainText("CORE-007");
  expect(new URL(page.url()).hash).toBe(QUESTION);
});

test("an unknown route, an unknown id and a bad parameter each say so", async ({ page }) => {
  await cold(page, "#/reports/2026");
  await expect(page.locator("#route-problem-title")).toHaveText("Not found");
  await expect(page.locator("#route-problem-home")).toHaveAttribute("href", "#/");

  await cold(page, "#/assessments/SDRA/versions/0.1.0-draft/sections/D09");
  await expect(page.locator("#route-problem-message")).toHaveText("Software Delivery Reality Assessment 0.1.0-draft has no section D09.");
  // The address is left as it was opened.
  expect(new URL(page.url()).hash).toBe("#/assessments/SDRA/versions/0.1.0-draft/sections/D09");

  await cold(page, "#/groups/g1/results?sort=sideways");
  await expect(page.locator("#route-problem-title")).toHaveText("This link is not valid");
  await expect(page.locator("#route-problem-message")).toContainText("'sideways' is not a valid sort");
});

test("a link in another spelling opens the same view and the address becomes canonical", async ({ page }) => {
  await cold(page, "#/assessments/SDRA/versions/0.1.0-draft/sections/D01/?utm=mail");
  await expect(page.locator("#section-label")).toHaveText("D01 Plan Commitment");
  await expect.poll(() => new URL(page.url()).hash).toBe("#/assessments/SDRA/versions/0.1.0-draft/sections/D01");
});

test("links move between views, and Back and Forward restore them from the address", async ({ page }) => {
  await cold(page, "#/assessments");
  await page.locator("#catalog-assessments a").first().click();
  await expect(page.locator("#assessment-title")).toHaveText("Software Delivery Reality Assessment 0.1.0-draft");
  await page.locator("#assessment-sections a", { hasText: "D03" }).click();
  await expect(page.locator("#section-label")).toHaveText("D03 Timebox Discipline");
  await page.locator("#section-questions a").first().click();
  await expect(page.locator("#question-id")).toHaveText("CORE-011");

  await page.goBack();
  await expect(page.locator("#question-id")).toHaveCount(0);
  await expect(page.locator("#section-label")).toHaveText("D03 Timebox Discipline");
  await page.goBack();
  await expect(page.locator("#section-label")).toHaveCount(0);
  await page.goForward();
  await expect(page.locator("#section-label")).toHaveText("D03 Timebox Discipline");

  // A reload restores the view from the address alone.
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("#section-label")).toHaveText("D03 Timebox Discipline");
});

test("Copy link writes the canonical address of the view, and it opens the same view", async ({ page, context, baseURL }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: new URL(baseURL).origin });
  await cold(page, QUESTION);
  await page.locator("#copy-link").click();
  await expect(page.locator("#notice-code")).toHaveText("SIGNAL.LINK.COPIED");

  const copied = await page.evaluate(() => navigator.clipboard.readText());
  expect(copied).toBe(new URL(ADMIN, baseURL).href + QUESTION);
  // Nothing but identifiers: no answers, no return target, no credential.
  expect(copied).not.toMatch(/[#&?](r|returnTo|code|state|token)=/);

  const fresh = await context.newPage();
  await fresh.goto(copied);
  await expect(fresh.locator("#question-id")).toHaveText("CORE-007");
});
