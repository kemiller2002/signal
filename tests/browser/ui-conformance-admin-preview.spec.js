// The draft preview (AUT-003 §§20-22, SCS-017; WI-0081) in a browser,
// against the test-only fake GitHub store. Its markup first shipped as a
// data-if template with eleven roots; Limen mounts only a template's first
// root, so a browser showed the preview's notice and none of its questions or
// buttons (ULI-A01/L08). Each step here needs a root that was dropped then:
// the questions, an answer, the fixture it keeps, and the Close button.
import { test, expect } from "./support.js";
import { signInWithStore } from "./ui-conformance-fixtures.mjs";

// Every store request crosses Playwright's route interception, so a signed-in
// flow makes dozens of local round trips per action; the bound is generous,
// never a sleep.
test.describe.configure({ timeout: 300_000 });

test("AUT-003/ULI-A01: the draft preview shows its questions, takes an answer, keeps it as a fixture and closes", async ({ page }) => {
  await signInWithStore(page);
  await page.getByRole("navigation", { name: "Administration" }).getByRole("link", { name: "Assessments", exact: true }).click();
  const start = page.locator("#new-draft");
  for (const [name, value] of [["Survey identifier", "PULSE"], ["Title", "Team pulse"]]) {
    await start.getByRole("textbox", { name }).fill(value);
    await start.getByRole("textbox", { name }).press("Tab");
  }
  await start.getByRole("button", { name: "Start draft" }).click();
  const draft = page.locator("#draft");
  await expect(draft.getByRole("heading", { name: "Team pulse (PULSE, draft)" })).toBeVisible();
  const type = async (name, value) => {
    const box = draft.getByRole("textbox", { name, exact: true }).first();
    await box.fill(value);
    await box.press("Tab");
  };
  await type("New section title", "Delivery");
  await draft.getByRole("button", { name: "Add section" }).click();
  await type("New question", "We ship every week.");
  await draft.getByRole("button", { name: "Add the question below here" }).click();
  await expect(page.locator("#draft-questions li")).toHaveCount(1);

  const area = page.locator("#draft-preview-area");
  await area.getByRole("button", { name: "Preview as a respondent" }).click();
  await expect(area.getByRole("note")).toContainText("a test, not a real response");
  // The rows are flat, as on the survey page: the question is one row and
  // each of its answers is a row of its own after it.
  const rows = area.locator("#preview-rows");
  await expect(rows.locator(".survey-question:visible strong")).toHaveText(["We ship every week."]);
  const often = rows.getByRole("radio", { name: "Often", exact: true });
  await often.check();
  await expect(often).toBeChecked();

  await area.getByRole("button", { name: "Keep these answers as a fixture" }).click();
  await expect(page.locator("#draft-fixtures li")).toHaveText([/^Preview answers 1/]);

  await area.getByRole("button", { name: "Close the preview" }).click();
  await expect(area.getByRole("note")).toHaveCount(0);
  await expect(area.getByRole("button", { name: "Preview as a respondent" })).toBeVisible();
});
