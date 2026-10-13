// The signed-in administration page's content areas against the test-only
// fake GitHub store (fakes/github-store.js): results exploration, reports,
// snapshots and their exports, the report builder, survey drafts from
// authoring to publication and the published version's actions, and the
// administrators (UI-LAYOUT-INTERACTION@1.0.0 ULI-A01, A02, A05, A09, A10;
// signal#113). Each action asserts what the page shows and what the store
// holds or the browser receives.
import { test, expect } from "./support.js";
import { expectNoHorizontalOverflow } from "./ui-conformance-helpers.mjs";
import { createGroup, recordsOf, signInWithStore } from "./ui-conformance-fixtures.mjs";

const area = (page, name) => page.getByRole("navigation", { name: "Administration" }).getByRole("link", { name, exact: true }).click();
const notice = (page) => page.locator("#notice-message");
const groupTab = (page, name) => page.getByRole("navigation", { name: "Group" }).getByRole("link", { name, exact: true }).click();

test("ULI-A02: results exploration, scoring and report options change the view and the address", async ({ page }) => {
  await signInWithStore(page);
  const key = await createGroup(page);

  await groupTab(page, "Results");
  await expect.poll(() => new URL(page.url()).hash).toBe(`#/groups/${key}/results`);
  await page.getByRole("checkbox", { name: "Sort by value" }).check();
  await expect.poll(() => new URL(page.url()).hash).toContain("sort=value");
  await page.getByRole("checkbox", { name: "Distribution as percentages" }).check();
  await expect.poll(() => new URL(page.url()).hash).toContain("display=percent");
  await page.getByRole("button", { name: "All sections" }).click();
  await expect.poll(() => new URL(page.url()).hash).not.toContain("section=");
  // Too few responses: marked, never drawn as zero.
  await expect(page.locator("#section-chart")).toContainText("cannot be shown and are marked, not drawn as zero");

  await groupTab(page, "Scoring");
  await expect(page.getByRole("region", { name: "Scoring" })).toContainText("Calculated measures, not the canonical result.");
  await page.locator("#scoring-sections").getByRole("link", { name: "D02" }).click();
  await expect.poll(() => new URL(page.url()).hash).toContain("section=D02");

  await groupTab(page, "Report");
  const families = page.getByRole("navigation", { name: "Report family" });
  const languages = page.getByRole("navigation", { name: "Report language" });
  await families.getByRole("link", { name: "Audit" }).click();
  await expect(families.getByRole("link", { name: "Audit" })).toHaveAttribute("aria-current", "page");
  await languages.getByRole("link", { name: "Français" }).click();
  await expect(page.locator("#report")).toHaveAttribute("lang", /^fr\b/);
  await languages.getByRole("link", { name: "العربية" }).click();
  await expect(page.locator("#report")).toHaveAttribute("dir", "rtl");
  await expectNoHorizontalOverflow(page, "admin: right-to-left report @1280");

  // Leaving the report removes it (it once stayed, and printed, on other areas).
  await area(page, "Storage");
  await expect(page.locator("#report")).toHaveCount(0);
  await expect(page.locator("#group")).toHaveCount(0);
});

test("ULI-A02/A07: a snapshot is stored, and each of its three exports reaches the browser", async ({ page }) => {
  const store = await signInWithStore(page);
  await createGroup(page);
  await groupTab(page, "Report");
  await page.getByRole("button", { name: "Take a snapshot" }).click();
  await expect(notice(page)).toContainText(/^Snapshot snap-[0-9a-f]+ was taken/, { timeout: 15_000 });
  expect(recordsOf(store, "signal.report-snapshot")).toHaveLength(1);
  const snapshot = page.locator("#snapshots li");
  await expect(snapshot).toHaveCount(1);

  // All three exports render and each hands a file to the browser (two of
  // them never rendered, and none sent its format, before GH-113).
  for (const [button, suffix] of [["Report (JSON)", "-report.json"], ["Sections (CSV)", "-sections.csv"], ["Lineage", "-lineage.json"]]) {
    const download = page.waitForEvent("download");
    await snapshot.getByRole("button", { name: button }).click();
    expect((await download).suggestedFilename()).toMatch(new RegExp(`^snap-[0-9a-f]+${suffix.replace(".", "\\.")}$`));
    await expect(notice(page)).toHaveText("The export was handed to the browser to save.");
  }
});

test("ULI-A02: a report definition is started, edited, reordered and saved as a version", async ({ page }) => {
  const store = await signInWithStore(page);
  await area(page, "Reports");
  await page.getByRole("textbox", { name: /^New definition id/ }).fill("team-summary");
  await page.getByRole("textbox", { name: /^New definition id/ }).press("Tab");
  await page.getByRole("button", { name: "Start a definition" }).click();
  const builder = page.locator("#builder");
  await expect(builder.getByRole("heading", { name: "team-summary (version 1)" })).toBeVisible();
  await expect(builder).toContainText("Unsaved changes");

  // Move up: the second block becomes the first.
  const blocks = () => builder.locator("#builder-blocks li").allTextContents();
  const [first, second] = await blocks();
  await builder.locator("#builder-blocks li").nth(1).getByRole("button", { name: "Move up" }).click();
  await expect.poll(async () => (await blocks()).slice(0, 2)).toEqual([second, first]);
  await builder.getByRole("combobox", { name: "Detail" }).selectOption({ index: 2 });

  await builder.getByRole("button", { name: "Save definition" }).click();
  await expect(notice(page)).toHaveText("Report definition team-summary was saved as version 1.", { timeout: 15_000 });
  await expect(builder).not.toContainText("Unsaved changes");
  expect(recordsOf(store, "signal.report-definition")).toHaveLength(1);
  await expect(page.locator("#saved-definitions li")).toContainText("team-summary v1");
  await page.locator("#saved-definitions").getByRole("button", { name: "Open" }).click();
  await expect(builder.getByRole("heading", { name: /^team-summary/ })).toBeVisible();
});

test("ULI-A02/A05: a survey is drafted, saved, reviewed, published, hidden, and its files and test links are given", async ({ page }) => {
  const store = await signInWithStore(page);
  await area(page, "Assessments");
  const start = page.locator("#new-draft");
  await start.getByRole("textbox", { name: "Survey identifier" }).fill("PULSE");
  await start.getByRole("textbox", { name: "Survey identifier" }).press("Tab");
  await start.getByRole("textbox", { name: "Title" }).fill("Team pulse");
  await start.getByRole("textbox", { name: "Title" }).press("Tab");
  await start.getByRole("button", { name: "Start draft" }).click();
  await expect.poll(() => new URL(page.url()).hash).toBe("#/templates/PULSE/draft");

  // The whole editor renders (only its heading did, before GH-113).
  const draft = page.locator("#draft");
  await expect(draft.getByRole("heading", { name: "Team pulse (PULSE, draft)" })).toBeVisible();
  await expect(draft.getByRole("textbox", { name: "New section title" })).toBeVisible();
  await expect(page.locator("#draft-findings")).toContainText("STRUCT-NO-SECTIONS");

  await draft.getByRole("textbox", { name: "New section title" }).fill("Delivery");
  await draft.getByRole("textbox", { name: "New section title" }).press("Tab");
  await draft.getByRole("button", { name: "Add section" }).click();
  await expect(page.locator("#draft-sections li")).toHaveCount(1);
  await draft.getByRole("textbox", { name: "New question" }).fill("We ship every week.");
  await draft.getByRole("textbox", { name: "New question" }).press("Tab");
  await draft.getByRole("button", { name: "Add the question below here" }).click();
  await expect(page.locator("#draft-questions li")).toContainText("We ship every week.");
  await draft.getByRole("button", { name: "Add a fixture: every answer at the midpoint" }).click();
  await expect(page.locator("#draft-fixtures li")).toHaveCount(1);
  await expect(page.locator("#draft-findings")).not.toContainText("STRUCT-NO-SECTIONS");

  // Publishing with unacknowledged warnings is refused, and says why.
  const review = page.locator("#draft-review");
  await draft.getByRole("button", { name: "Save draft" }).click();
  await expect(notice(page)).toHaveText("The draft of PULSE was saved.", { timeout: 15_000 });
  expect(recordsOf(store, "signal.template-draft")).toHaveLength(1);
  await draft.getByRole("button", { name: "Submit for review" }).click();
  await expect(review).toHaveText("Ready for review", { timeout: 15_000 });
  await draft.getByRole("button", { name: "Approve as reviewed" }).click();
  await expect(review).toHaveText("Reviewed", { timeout: 15_000 });
  await draft.getByRole("button", { name: "Publish" }).click();
  await expect(notice(page)).toContainText("Acknowledge the warnings first", { timeout: 15_000 });
  expect(recordsOf(store, "signal.template")).toHaveLength(0);
  await draft.getByRole("checkbox", { name: "I have read the warnings and accept them" }).check();
  await draft.getByRole("button", { name: "Publish" }).click();
  await expect(notice(page)).toContainText(/^PULSE version 1 is published \(sha256:[0-9a-f]{64}\)/, { timeout: 15_000 });
  expect(recordsOf(store, "signal.template")).toHaveLength(1);

  await area(page, "Assessments");
  const versions = page.locator("#template-versions li");
  await expect(versions).toContainText("Team pulse (PULSE 1) · Listed · Can start groups.");
  const siteFile = page.waitForEvent("download");
  await versions.getByRole("button", { name: "For the respondent site" }).click();
  expect((await siteFile).suggestedFilename()).toMatch(/^[0-9a-f]{16}\.json$/);
  await versions.getByRole("button", { name: "Test links" }).click();
  const testLinks = page.getByRole("region", { name: "Test links for Team pulse (PULSE 1)" });
  await expect(testLinks.getByRole("link")).toHaveCount(3);
  await expect(testLinks.getByRole("link", { name: "Blank, identified" })).toHaveAttribute("href", /^\.\.\/survey\/#r=/);
  await page.locator("#template-actions").getByRole("button", { name: "Hide from new groups" }).click();
  await expect(notice(page)).toHaveText("PULSE version 1 is hidden from new groups; groups that use it are unaffected.", { timeout: 15_000 });
  await expect(versions).toContainText("Hidden from new groups");
  await expect(page.locator("#template-actions").getByRole("button", { name: "Hide from new groups" })).toHaveCount(0);
});

test("ULI-A02/A07: administrators are admitted by account number and removed; a login is refused", async ({ page }) => {
  const store = await signInWithStore(page);
  await area(page, "Administrators");
  const rows = page.locator("#administrators tbody tr");
  await expect(rows).toHaveCount(1);
  const account = page.getByRole("textbox", { name: "GitHub account number" });

  await account.fill("octocat");
  await account.press("Tab");
  await page.getByRole("button", { name: "Admit" }).click();
  await expect(notice(page)).toHaveText("Enter the person's GitHub account number (digits only), never a login or e-mail.");
  await expect(rows).toHaveCount(1);

  await account.fill("1234567");
  await account.press("Tab");
  await page.getByRole("combobox", { name: "Grant" }).selectOption("analyst");
  await page.getByRole("button", { name: "Admit" }).click();
  await expect(rows).toHaveCount(2, { timeout: 15_000 });
  await expect(rows.filter({ hasText: "github:1234567" })).toContainText("ViewResults, BuildReports, ExportData");
  expect(recordsOf(store, "signal.administrator")).toHaveLength(2);

  await rows.filter({ hasText: "github:1234567" }).getByRole("button", { name: "Remove" }).click();
  await expect(rows).toHaveCount(1, { timeout: 15_000 });
  await expect(rows).toContainText("github:583231");
});
