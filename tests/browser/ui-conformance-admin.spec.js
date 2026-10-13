// The signed-in administration page against a deterministic, test-only fake
// of the GitHub store (fakes/github-store.js), installed at the network
// boundary: the real page, WASM engine, Fides client and Arca provider run
// unchanged (UI-LAYOUT-INTERACTION@1.0.0 ULI-A01, A02, A05, A07, A10;
// signal#113). Every action asserts what the page shows and what the store
// holds afterwards, so no success is taken from a click alone.
import { test, expect, expectConsoleError } from "./support.js";
import { expectNoHorizontalOverflow } from "./ui-conformance-helpers.mjs";
import { KERNEL_START_MS, answerAll, createGroup, open, recordsOf, signInWithStore, STORE_STEP_MS } from "./ui-conformance-fixtures.mjs";

// Every store request crosses Playwright's route interception, so a signed-in
// flow makes dozens of local round trips per action; on a loaded runner an
// action can take tens of seconds. The bounds are generous, never sleeps.
test.describe.configure({ timeout: 300_000 });

const areas = (page) => page.getByRole("navigation", { name: "Administration" });
const area = (page, name) => areas(page).getByRole("link", { name, exact: true }).click();
const notice = (page) => page.locator("#notice-message");

test("ULI-A02: signing in sets the dataset up in the store, opens it writable, refreshes and signs out", async ({ page }) => {
  const store = await signInWithStore(page);
  // The dataset was set up with its first administrator and an audit record.
  expect(recordsOf(store, "signal.administrator")).toEqual(["prod/signal/datasets/ds_engagement/records/signal.administrator/github_3a583231.json"]);
  expect(recordsOf(store, "signal.storage-manifest")).toHaveLength(1);
  await expect(page.getByRole("heading", { level: 2, name: "Overview" })).toBeVisible();
  await expect(page.locator("#overview")).toContainText("0 group(s) waiting for imports · 0 complete");

  // Refresh reads the store again (a request that resolves the branch).
  const before = store.requests.length;
  await page.getByRole("button", { name: "Refresh" }).click();
  await expect.poll(() => store.requests.length).toBeGreaterThan(before);
  await expect(page.getByRole("status").filter({ hasText: "changes can be saved" })).toBeVisible();
  await expectNoHorizontalOverflow(page, "admin: dataset overview @1280");

  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { name: "Sign in with GitHub" })).toBeVisible();
  await expect(page.locator("#principal")).toHaveText("");
});

test("ULI-A07: a store that fails mid-write is reported as offline, keeps nothing, and works again after Refresh", async ({ page }) => {
  const store = await signInWithStore(page);
  expectConsoleError(page, /status of 503/);
  // The publishing ref update fails once: GitHub answers 503.
  store.failNext("PATCH", /\/git\/refs\/heads\/main$/);
  await area(page, "Groups");
  const form = page.locator("#new-group");
  await form.getByRole("spinbutton", { name: "Expected surveys" }).fill("2");
  await form.getByRole("spinbutton", { name: "Expected surveys" }).press("Tab");
  await form.getByRole("button", { name: "Create group" }).click();
  // Honest: read-only and offline, nothing queued, no group anywhere.
  const status = page.getByRole("status").filter({ hasText: "Engagement" });
  await expect(status).toContainText("Changes are unavailable and nothing is queued.", { timeout: STORE_STEP_MS });
  await expect(status).toContainText("Read-only.");
  await expect(form).toHaveCount(0);
  expect(recordsOf(store, "signal.group")).toEqual([]);
  await expect(page.locator("#group-list tbody tr")).toHaveCount(0);
  await expect(page.locator("#group-key")).toHaveCount(0);

  // Recovery: Refresh reaches the store again, and the same action succeeds once.
  await page.getByRole("button", { name: "Refresh" }).click();
  // The credential line reads "changes can be saved" even while offline, so
  // recovery is the offline and read-only lines going away.
  await expect(status).not.toContainText("Offline", { timeout: STORE_STEP_MS });
  await expect(status).not.toContainText("Read-only.");
  const key = await createGroup(page);
  expect(recordsOf(store, "signal.group")).toEqual([`prod/signal/datasets/ds_engagement/records/signal.group/${key}.json`]);
});

test("ULI-A02/A05: groups are created, listed, filtered, compared and moved through their lifecycle", async ({ page }) => {
  const store = await signInWithStore(page);
  const key = await createGroup(page);
  expect(recordsOf(store, "signal.group")).toEqual([`prod/signal/datasets/ds_engagement/records/signal.group/${key}.json`]);
  await expect(page.locator("#group-phase")).toHaveText("GroupReady");

  // The list holds it, and a typed filter is held in the address.
  await area(page, "Groups");
  const row = page.locator("#group-list tbody tr");
  await expect(row).toHaveCount(1);
  await expect(row.getByRole("link", { name: key })).toHaveAttribute("href", `#/groups/${key}`);
  await page.locator("#group-filters").getByRole("checkbox", { name: "identified" }).check();
  await expect.poll(() => decodeURIComponent(new URL(page.url()).hash)).toContain("identified");
  await expect(row).toHaveCount(0);
  await page.locator("#group-filters").getByRole("checkbox", { name: "identified" }).uncheck();
  await expect(row).toHaveCount(1);

  // Compare needs two groups, and says so with one; two give rows. The
  // group view and its lifecycle buttons do not follow into another area
  // (ULI-L08; the stale view fixed under GH-113).
  await area(page, "Compare");
  await expect(page.locator("#group")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Close incomplete" })).toHaveCount(0);
  const choices = page.locator("#compare-groups").getByRole("checkbox");
  await choices.first().check();
  await expect(page.locator("#compare-reason")).toHaveText("Choose at least two groups.");
  await expect(page.locator("#compare-rows tbody tr")).toHaveCount(0);
  const second = await createGroup(page);
  await area(page, "Compare");
  await expect(choices).toHaveCount(2);
  for (const choice of await choices.all()) await choice.check();
  await expect(page.locator("#compare-rows tbody tr").first()).toBeVisible();
  await expect(page.locator("#compare-rows")).toContainText(second);

  // Lifecycle. Finalizing first waits for a current contribution index, and
  // says so; nothing changes.
  await area(page, "Groups");
  await row.getByRole("link", { name: key }).click();
  const phase = page.locator("#group-phase");
  await page.getByRole("button", { name: "Finalize" }).click();
  await expect(notice(page)).toContainText("The derived index is out of date; rebuild it.", { timeout: STORE_STEP_MS });
  await expect(phase).toHaveText("GroupReady");
  await area(page, "Storage");
  await page.getByRole("button", { name: "Rebuild the contribution index" }).click();
  await expect(notice(page)).toContainText("The contribution index was rebuilt", { timeout: STORE_STEP_MS });

  // Each lifecycle button moves the group and the stored lifecycle record.
  await area(page, "Groups");
  await row.getByRole("link", { name: key }).click();
  const lifecycle = (name) => page.locator("#lifecycle").getByRole("button", { name, exact: true });
  const stored = () => store.read(`prod/signal/datasets/ds_engagement/records/signal.group-lifecycle/${key}.json`) ?? "";
  for (const [button, after] of [["Close incomplete", "ClosedIncomplete"], ["Reopen", "GroupReady"], ["Close incomplete", "ClosedIncomplete"],
    ["Finalize", "FinalizedIncomplete"], ["Seal", "Sealed"]]) {
    const before = stored();
    await lifecycle(button).click();
    await expect(phase).toHaveText(after, { timeout: STORE_STEP_MS });
    expect(stored()).not.toBe(before);
  }
  await expect(page.locator("#lifecycle").getByRole("button")).toHaveCount(0);
});

test("ULI-A02/A10: invitations become answered assessments that import into the group, once", async ({ page, context }) => {
  const store = await signInWithStore(page);
  const key = await createGroup(page, { expected: "2" });
  await page.getByRole("link", { name: "Imported surveys" }).click();

  const invitations = page.locator("#invitations");
  await invitations.getByRole("spinbutton", { name: "How many links" }).fill("2");
  await invitations.getByRole("spinbutton", { name: "How many links" }).press("Tab");
  await invitations.getByRole("button", { name: "Create links" }).click();
  await expect(page.locator("#invitation-summary")).toHaveText(/2 link\(s\) issued/, { timeout: STORE_STEP_MS });
  const issued = await page.locator("#invitation-links code").allTextContents();
  expect(issued).toHaveLength(2);
  // Signal keeps no copy (the summary says so): no record names the links.
  expect(store.files().some((path) => issued.some((link) => (store.read(path) ?? "").includes(link)))).toBe(false);
  const download = page.waitForEvent("download");
  await invitations.getByRole("button", { name: "Save the links" }).click();
  expect((await download).suggestedFilename()).toMatch(/\S/);

  // A respondent answers the first invitation and submits.
  const respondent = await context.newPage();
  await open(respondent, issued[0]);
  await answerAll(respondent, "Often");
  await respondent.getByRole("button", { name: "See results" }).click();
  await respondent.getByRole("button", { name: "Submit answers" }).click();
  const submission = await respondent.getByRole("textbox", { name: "Submission link" }).inputValue();
  await respondent.close();

  const imports = page.locator("#import");
  await imports.getByRole("textbox", { name: "Completed survey links, one per line" }).fill(`${submission}\nnot a link`);
  await imports.getByRole("button", { name: "Import" }).click();
  await expect(page.locator("#importAccepted li")).toHaveCount(1, { timeout: STORE_STEP_MS });
  await expect(page.locator("#importRejected li")).toHaveCount(1);
  await expect(page.getByText(/1 of 2 accepted/).first()).toBeVisible();
  expect(store.files().some((path) => path.includes("signal.result") || path.includes("signal.contribution"))).toBe(true);

  // The same link again is a duplicate, not a second response.
  await imports.getByRole("textbox", { name: "Completed survey links, one per line" }).fill(submission);
  await imports.getByRole("button", { name: "Import" }).click();
  await expect(page.locator("#importDuplicate li")).toHaveCount(1, { timeout: STORE_STEP_MS });
  await expect(page.getByText(/1 of 2 accepted/).first()).toBeVisible();
  expect(key).toMatch(/^[0-9a-f]{32}$/);
});

test("ULI-A01/A02: the dataset lifecycle buttons move the dataset, read-only while archived, and back", async ({ page }) => {
  const store = await signInWithStore(page);
  await area(page, "Storage");
  const state = page.locator("#dataset-lifecycle-state");
  await expect(state).toHaveText("state-active");
  const targets = page.locator("#lifecycle-targets");
  // These buttons once sent no target (Limen sends no button value) and were refused.
  await targets.getByRole("button", { name: "Archive", exact: true }).click();
  await expect(state).toHaveText("state-archived", { timeout: STORE_STEP_MS });
  await expect(page.getByRole("status").filter({ hasText: "Engagement" })).toContainText("Read-only.");
  expect(store.files().some((path) => path.includes("signal.dataset-lifecycle/"))).toBe(true);
  await targets.getByRole("button", { name: "Make active", exact: true }).click();
  await expect(state).toHaveText("state-active", { timeout: STORE_STEP_MS });
  await expect(page.getByRole("status").filter({ hasText: "Engagement" })).toContainText("Signed in; changes can be saved.");
});
