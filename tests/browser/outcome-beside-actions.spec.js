// The outcome beside the actions on a long view (signal#136 review round 2,
// keeping #133's SIG-VIS-PENDING-002.2). The shell puts #activity (the one
// live region) and #notice at the top of <main>, thousands of pixels above
// the draft editor's Save and Publish. The editor therefore shows a copy of
// the outcome beside its actions: not a live region, so nothing is
// announced twice, and on screen where the person clicked.
import { test, expect } from "./support.js";
import { signInWithStore, STORE_STEP_MS } from "./ui-conformance-fixtures.mjs";

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

test("on the long draft page, Publish's refusal and Save's outcome are shown beside the actions, and #activity alone announces them", async ({ page }) => {
  await signInWithStore(page);
  const draft = await startDraft(page);
  // The page is long: the shell's notice is out of view from the actions.
  const viewport = page.viewportSize();
  expect(await page.evaluate(() => document.documentElement.scrollHeight)).toBeGreaterThan(viewport.height * 2);

  const outcome = draft.locator(".admin-outcome");
  // A copy, not a second live region: no role, no aria-live, nothing inside either.
  await expect(outcome).toHaveCount(1);
  for (const attribute of ["role", "aria-live", "aria-atomic"]) await expect(outcome).not.toHaveAttribute(attribute);
  await expect(outcome.locator("[role], [aria-live]")).toHaveCount(0);

  // Publish with blockers: refused, and the refusal is on screen beside Publish.
  const publish = draft.getByRole("button", { name: "Publish", exact: true });
  await scrollTo(publish);
  await expect(publish).toBeInViewport();
  await publish.click();
  const refusal = "Publication is blocked: resolve the blockers listed under validation.";
  await expect(outcome).toContainText(refusal, { timeout: STORE_STEP_MS });
  await expect(outcome).toBeVisible();
  await expect(outcome).toBeInViewport();
  await expect(publish).toBeInViewport();
  // The editor's own alert says it at the top of the draft, out of view here,
  // and is the one region that announces it.
  const announcers = page.locator('[aria-live], [role="status"], [role="alert"]').filter({ hasText: refusal });
  await expect(announcers).toHaveCount(1);
  await expect(announcers).not.toBeInViewport();
  await page.screenshot({ path: test.info().outputPath("draft-publish-refused.png") });

  // Save: its outcome is shown beside the actions too.
  const save = draft.getByRole("button", { name: "Save draft", exact: true });
  await scrollTo(save);
  await expect(save).toBeInViewport();
  await save.click();
  const saved = "The draft of PULSE was saved.";
  await expect(outcome).toContainText(saved, { timeout: STORE_STEP_MS });
  await expect(outcome).toBeInViewport();
  await expect(save).toBeInViewport();
  // The shell's notice says it at the top of the page, out of view here, and
  // #activity, the one live region, announces it.
  await expect(page.locator("#notice-message")).toHaveText(saved);
  await expect(page.locator("#notice-message")).not.toBeInViewport();
  await expect(page.locator("#activity")).toContainText(saved);
  await expect(page.locator('[aria-live], [role="status"], [role="alert"]').filter({ hasText: saved })).toHaveCount(1);
  await page.screenshot({ path: test.info().outputPath("draft-saved.png") });
});
