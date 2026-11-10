// The outcome beside the actions on a long view (signal#136 review round 2,
// keeping #133's SIG-VIS-PENDING-002.2). The shell puts #activity (the one
// live region) and #notice at the top of <main>, thousands of pixels above
// the draft editor's Save and Publish. The editor therefore shows a copy of
// the outcome beside its actions: not a live region, so nothing is
// announced twice, and on screen where the person clicked.
import { test, expect } from "./support.js";
import { signInWithStore, createGroup, STORE_STEP_MS } from "./ui-conformance-fixtures.mjs";

test.describe.configure({ timeout: 300_000 });

async function startDraft(page) {
  await page.getByRole("navigation", { name: "Administration" }).getByRole("link", { name: "Assessments", exact: true }).click();
  const start = page.locator("#new-draft");
  await start.getByRole("textbox", { name: "Survey identifier" }).fill("PULSE");
  await start.getByRole("textbox", { name: "Survey identifier" }).press("Tab");
  await start.getByRole("textbox", { name: "Title" }).fill("Team pulse");
  await start.getByRole("textbox", { name: "Title" }).press("Tab");
  await start.getByRole("button", { name: "Start draft" }).click();
  const draft = page.locator("#draft");
  await expect(draft.getByRole("heading", { name: "Team pulse (PULSE, draft)" })).toBeVisible();
  return draft;
}

// The person has read down the long draft to an action: it is in the middle
// of the screen (not wherever Playwright's own scroll would leave it).
async function scrollTo(action) {
  await action.evaluate((element) => element.scrollIntoView({ block: "center" }));
}

// Where an element is in the document, so "beside" is a distance between
// two elements and does not depend on where the page is scrolled.
const pageTop = (locator) => locator.evaluate((element) => element.getBoundingClientRect().top + window.scrollY);

// The outcome is beside the action (within 400px of it in the document) and
// on screen. How far the page moved while the action ran is logged.
async function expectBeside(page, outcome, action, scrolledBefore, label) {
  await expect(outcome).toBeVisible();
  await expect(outcome).toBeInViewport();
  expect(Math.abs((await pageTop(outcome)) - (await pageTop(action)))).toBeLessThanOrEqual(400);
  const moved = (await page.evaluate(() => window.scrollY)) - scrolledBefore;
  test.info().annotations.push({ type: "scroll", description: `${label}: the page moved ${moved}px while it ran` });
  console.log(`${label}: the page moved ${moved}px while it ran`);
}

// At a desktop width and on a phone (390 px, signal#137 review).
for (const width of [1280, 390]) test(`at ${width} px, on the long draft page, Publish's refusal and Save's outcome are shown beside the actions, and #activity alone announces them`, async ({ page }) => {
  await signInWithStore(page, { width });
  const draft = await startDraft(page);
  // The page is long: the shell's notice is out of view from the actions.
  const viewport = page.viewportSize();
  expect(await page.evaluate(() => document.documentElement.scrollHeight)).toBeGreaterThan(viewport.height * 2);

  const outcome = draft.locator(".admin-outcome");
  // A copy, not a second live region: a group named "Outcome" (signal#136
  // re-review), no aria-live or aria-atomic, and nothing live inside it.
  await expect(outcome).toHaveCount(1);
  await expect(outcome).toHaveAttribute("role", "group");
  await expect(outcome).toHaveAttribute("aria-label", "Outcome");
  for (const attribute of ["aria-live", "aria-atomic"]) await expect(outcome).not.toHaveAttribute(attribute);
  await expect(outcome.locator("[role], [aria-live]")).toHaveCount(0);

  // Publish with blockers: refused, and the refusal is on screen beside Publish.
  const publish = draft.getByRole("button", { name: "Publish", exact: true });
  await scrollTo(publish);
  await expect(publish).toBeInViewport();
  const beforePublish = await page.evaluate(() => window.scrollY);
  await publish.click();
  const refusal = "Publication is blocked: resolve the blockers listed under validation.";
  await expect(outcome).toContainText(refusal, { timeout: STORE_STEP_MS });
  await expect(outcome).toHaveAccessibleName("Outcome");
  await expectBeside(page, outcome, publish, beforePublish, "Publish");
  // The editor's own alert is the one region that announces it. (Since the
  // draft's actions moved into its header, signal#137 review, the alert just
  // above the header may be in view as well.)
  const announcers = page.locator('[aria-live], [role="status"], [role="alert"]').filter({ hasText: refusal });
  await expect(announcers).toHaveCount(1);
  await page.screenshot({ path: test.info().outputPath("draft-publish-refused.png") });

  // Save: its outcome is shown beside the actions too.
  const save = draft.getByRole("button", { name: "Save draft", exact: true });
  await scrollTo(save);
  await expect(save).toBeInViewport();
  const beforeSave = await page.evaluate(() => window.scrollY);
  await save.click();
  const saved = "The draft of PULSE was saved.";
  await expect(outcome).toContainText(saved, { timeout: STORE_STEP_MS });
  await expectBeside(page, outcome, save, beforeSave, "Save");
  // The shell's notice says it at the top of the page (in view or not, now
  // that the actions are in the draft's header), and #activity, the one live
  // region, announces it.
  await expect(page.locator("#notice-message")).toHaveText(saved);
  await expect(page.locator("#activity")).toContainText(saved);
  await expect(page.locator('[aria-live], [role="status"], [role="alert"]').filter({ hasText: saved })).toHaveCount(1);
  await page.screenshot({ path: test.info().outputPath("draft-saved.png") });
});

// Another view's outcome never shows beside the draft's actions (signal#137
// review: a retire's line sat in every draft capture's Outcome group). The
// shell's persistence line still says it; the draft's copy shows only the
// draft's own changes. (The retire itself: OperationsFollowUpTests.)
test("a change made in another view is not shown in the draft's Outcome group; the draft's own change is", async ({ page }) => {
  await signInWithStore(page);
  await createGroup(page);
  await expect(page.locator("#activity #persistence")).toContainText("The new group is saved", { timeout: STORE_STEP_MS });
  const draft = await startDraft(page);
  await expect(page.locator("#activity #persistence")).toContainText("The new group is saved");
  const outcome = draft.locator(".admin-outcome");
  await expect(outcome).toBeHidden();
  await expect(outcome).not.toContainText("The new group is saved");
  await draft.getByRole("group", { name: "Draft actions" }).getByRole("button", { name: "Save draft", exact: true }).click();
  await expect(outcome).toContainText("The draft of PULSE was saved.", { timeout: STORE_STEP_MS });
  await expect(outcome).not.toContainText("The new group is saved");
});
