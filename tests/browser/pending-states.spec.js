// Per-action pending state, loading regions and state chips
// (SIG-VIS-PENDING-001, review follow-ups SIG-VIS-PENDING-002) in the real
// built page, against the test-only fake GitHub store. The fake holds a
// request (`holdNext`) so a test can look at the page while an operation is
// in flight: the control that started it is aria-busy with its
// present-participle label, every other operation's control is unavailable,
// a second activation queues nothing, and the page's one live region
// (#activity, beside the notice) says "saved" only once the store confirmed.
// The theme that styles .echelon-* (Echelon theme 1.1.0) is not installed:
// these tests assert the structure and data the theme will style, not looks.
import { readFileSync } from "node:fs";
import { test, expect, expectConsoleError } from "./support.js";
import { FakeGitHubStore } from "./fakes/github-store.js";
import { signInWithStore, routeSignIn, createGroup, open, answerAll, origin, STORE_STEP_MS, KERNEL_START_MS } from "./ui-conformance-fixtures.mjs";

test.describe.configure({ timeout: 300_000 });

const links = JSON.parse(readFileSync(new URL("./fixtures/survey-links.json", import.meta.url), "utf8"));

const areas = (page) => page.getByRole("navigation", { name: "Administration" });
const area = (page, name) => areas(page).getByRole("link", { name, exact: true }).click();
const groupTab = (page, name) => page.getByRole("navigation", { name: "Group" }).getByRole("link", { name, exact: true }).click();
const notice = (page) => page.locator("#notice-message");
const activity = (page) => page.locator("#activity");
// The commit that lands a change: its branch update.
const REF_UPDATE = /\/git\/refs\/heads\/main$/;
const refUpdates = (store) => store.requests.filter((r) => /^PATCH .*\/git\/refs\/heads\/main$/.test(r)).length;

// A request answered only when the test says so.
const gate = () => {
  const { promise, resolve } = Promise.withResolvers();
  return { opened: promise, open: resolve };
};

// A loading region: a status region, Forma's spinner and a visible label.
async function expectLoadingRegion(region, label) {
  await expect(region).toBeVisible();
  await expect(region).toHaveAttribute("role", "status");
  await expect(region).toHaveClass(/(^|\s)echelon-loading(\s|$)/);
  await expect(region.locator(".forma-spinner .forma-spinner__indicator")).toHaveCount(1);
  await expect(region.locator(".forma-spinner__indicator")).toHaveAttribute("aria-hidden", "true");
  await expect(region.locator(".forma-spinner__label")).toBeVisible();
  await expect(region.locator(".forma-spinner__label")).toHaveText(label);
}

// The running operation inside the page's one live region: Forma's spinner
// and its label in an .echelon-loading container (not a second live region).
async function expectRunning(page, label) {
  const region = activity(page);
  await expect(region).toHaveAttribute("role", "status");
  await expect(region).toHaveAttribute("aria-live", "polite");
  const pending = region.locator("#operation-pending");
  await expect(pending).toBeVisible();
  await expect(pending).not.toHaveAttribute("role");
  await expect(pending).toHaveClass(/(^|\s)echelon-loading(\s|$)/);
  await expect(pending.locator(".forma-spinner .forma-spinner__indicator")).toHaveCount(1);
  await expect(pending.locator(".forma-spinner__label")).toHaveText(label);
}

// Focus moved by the page after an operation: on the element named, which
// is on screen (not 1,200px away), with a visible outline of the Forma
// focus-ring colour (SIG-VIS-PENDING-002, round 2).
async function expectFocusedHere(target) {
  await expect(target).toBeFocused({ timeout: STORE_STEP_MS });
  await expect(target).toBeInViewport();
  const ring = await target.evaluate((element) => {
    const style = getComputedStyle(element);
    const probe = document.createElement("span");
    probe.style.color = "var(--forma-color-focus-ring)";
    document.body.append(probe);
    const token = getComputedStyle(probe).color;
    probe.remove();
    return { style: style.outlineStyle, width: parseFloat(style.outlineWidth), color: style.outlineColor, token };
  });
  expect(ring.style).toBe("solid");
  expect(ring.width).toBeGreaterThanOrEqual(2);
  expect(ring.color).toBe(ring.token);
}

// One accepted result in a new group: a respondent answers an invitation
// and its submission link is imported.
async function groupWithOneResult(page, context) {
  const key = await createGroup(page);
  await page.getByRole("link", { name: "Imported surveys" }).click();
  const invitations = page.locator("#invitations");
  await invitations.getByRole("spinbutton", { name: "How many links" }).fill("1");
  await invitations.getByRole("spinbutton", { name: "How many links" }).press("Tab");
  await invitations.getByRole("button", { name: "Create links" }).click();
  await expect(page.locator("#invitation-summary")).toHaveText(/1 link\(s\) issued/, { timeout: STORE_STEP_MS });
  const [issued] = await page.locator("#invitation-links code").allTextContents();
  const respondent = await context.newPage();
  await open(respondent, issued);
  await answerAll(respondent, "Often");
  await respondent.getByRole("button", { name: "See results" }).click();
  await respondent.getByRole("button", { name: "Submit answers" }).click();
  const submission = await respondent.getByRole("textbox", { name: "Submission link" }).inputValue();
  await respondent.close();
  const imports = page.locator("#import");
  await imports.getByRole("textbox", { name: "Completed survey links, one per line" }).fill(submission);
  await imports.getByRole("button", { name: "Import" }).click();
  await expect(page.locator("#importAccepted li")).toHaveCount(1, { timeout: STORE_STEP_MS });
  return key;
}

async function startDraft(page) {
  await area(page, "Assessments");
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

async function saveDraft(page, draft) {
  await draft.getByRole("button", { name: "Save draft", exact: true }).click();
  await expect(notice(page)).toHaveText("The draft of PULSE was saved.", { timeout: STORE_STEP_MS });
}

test("a save in flight: its button is busy with its pending label, the other operations wait, and a second click queues nothing", async ({ page }) => {
  const store = await signInWithStore(page);
  const draft = await startDraft(page);
  // The live region is in the page before anything runs, so additions are announced.
  await expect(activity(page)).toHaveCount(1);

  const hold = store.holdNext("PATCH", REF_UPDATE);
  const before = { commits: store.commitCount, updates: refUpdates(store) };
  await draft.getByRole("button", { name: "Save draft", exact: true }).click();
  await hold.reached;

  // The triggering button: aria-busy (Forma draws its ring), its own present
  // participle, and aria-disabled rather than disabled, so it keeps focus.
  const saving = draft.getByRole("button", { name: "Saving draft", exact: true });
  await expect(saving).toHaveAttribute("aria-busy", "true");
  await expect(saving).toHaveAttribute("aria-disabled", "true");
  await expect(saving).not.toHaveAttribute("disabled");
  await expect(saving).toBeFocused();
  await expect(draft.getByRole("button", { name: "Save draft", exact: true })).toHaveCount(0);

  // The guard: every other operation is unavailable, and none is busy.
  for (const name of ["Submit for review", "Approve as reviewed", "Publish"]) {
    const other = draft.getByRole("button", { name, exact: true });
    await expect(other).toBeDisabled();
    await expect(other).toHaveAttribute("aria-busy", "false");
  }
  await expect(page.getByRole("button", { name: "Refresh", exact: true })).toBeDisabled();
  // Local editing is not an operation and stays available.
  await expect(draft.getByRole("button", { name: "Add section", exact: true })).toBeEnabled();

  // The page names what runs, and does not call it saved.
  await expectRunning(page, "Saving draft: not saved until the store confirms.");
  await expect(page.locator("#operation-pending")).toHaveAttribute("data-persistence", "pending");
  await expect(page.locator("#persistence")).toHaveCount(0);

  // A rapid second and third activation, past Playwright's actionability
  // wait, are ignored by the engine: nothing is queued.
  await saving.dispatchEvent("click");
  await saving.dispatchEvent("click");

  hold.release();
  await expect(notice(page)).toHaveText("The draft of PULSE was saved.", { timeout: STORE_STEP_MS });

  // Exactly one write landed.
  expect(refUpdates(store) - before.updates).toBe(1);
  expect(store.commitCount - before.commits).toBe(1);

  // Settled: the button is itself again, and the live region says saved, now,
  // with the notice's sentence; the notice itself is not a second live region.
  const save = draft.getByRole("button", { name: "Save draft", exact: true });
  await expect(save).toHaveAttribute("aria-busy", "false");
  await expect(save).toBeEnabled();
  await expect(page.locator("#operation-pending")).toHaveCount(0);
  const persistence = activity(page).locator("#persistence");
  await expect(persistence).toHaveAttribute("data-persistence", "saved");
  await expect(persistence).toHaveText("The draft is saved: the store confirmed it.");
  await expect(activity(page)).toContainText("The draft of PULSE was saved.");
  await expect(page.locator("#notice")).not.toHaveAttribute("role");
  await expect(page.getByRole("status").filter({ hasText: "The draft of PULSE was saved." })).toHaveCount(1);
  await expect(draft.getByRole("button", { name: "Submit for review", exact: true })).toBeEnabled();
});

test("edits made while a draft saves stay unsaved", async ({ page }) => {
  const store = await signInWithStore(page);
  const draft = await startDraft(page);
  const hold = store.holdNext("PATCH", REF_UPDATE);
  await draft.getByRole("button", { name: "Save draft", exact: true }).click();
  await hold.reached;
  const title = draft.locator('input[data-event="draftTitle"]');
  await title.fill("Team pulse, edited");
  await title.press("Tab");
  hold.release();
  await expect(notice(page)).toHaveText("The draft of PULSE was saved.", { timeout: STORE_STEP_MS });
  await expect(title).toHaveValue("Team pulse, edited");
  await expect(draft).toContainText("Unsaved changes");
});

test("a group transition in flight: only its own button is busy, the others follow the guard, and focus goes to the new phase", async ({ page }) => {
  const store = await signInWithStore(page);
  await createGroup(page);
  const lifecycle = page.locator("#lifecycle");

  const hold = store.holdNext("PATCH", REF_UPDATE);
  await lifecycle.getByRole("button", { name: "Close incomplete", exact: true }).click();
  await hold.reached;

  const closing = lifecycle.getByRole("button", { name: "Closing", exact: true });
  await expect(closing).toHaveAttribute("aria-busy", "true");
  const finalize = lifecycle.getByRole("button", { name: "Finalize", exact: true });
  await expect(finalize).toBeDisabled();
  await expect(finalize).toHaveAttribute("aria-busy", "false");
  await expectRunning(page, "Closing: not changed until the store confirms.");

  hold.release();
  const phase = page.locator("#group-phase");
  await expect(phase).toHaveText("Closed (incomplete)", { timeout: STORE_STEP_MS });
  await expect(phase).toHaveAttribute("data-phase", "ClosedIncomplete");
  await expect(phase).toHaveAttribute("data-echelon-tone", "closed");
  await expect(activity(page).locator("#persistence")).toHaveAttribute("data-persistence", "saved");
  // Its button went away; focus is on the phase it changed, beside it, not
  // on the status region at the top of the page.
  await expectFocusedHere(phase);
  await expect(activity(page)).not.toBeFocused();
});

test("delete draft keeps its confirmation, busy, until it is answered; then focus goes to the status", async ({ page }) => {
  const store = await signInWithStore(page);
  const draft = await startDraft(page);
  await saveDraft(page, draft);
  await draft.getByRole("button", { name: "Delete draft…", exact: true }).click();

  const hold = store.holdNext("PATCH", REF_UPDATE);
  await draft.getByRole("button", { name: "Delete draft", exact: true }).click();
  await hold.reached;
  const deleting = draft.getByRole("button", { name: "Deleting draft", exact: true });
  await expect(deleting).toBeVisible();
  await expect(deleting).toHaveAttribute("aria-busy", "true");
  await expect(deleting).toBeFocused();
  await expect(draft.getByRole("button", { name: "Keep it", exact: true })).toBeDisabled();
  await expectRunning(page, "Deleting draft: not deleted until the store confirms.");

  hold.release();
  await expect(draft.getByRole("button", { name: "Deleting draft", exact: true })).toHaveCount(0, { timeout: STORE_STEP_MS });
  await expect(activity(page).locator("#persistence")).toHaveText("The draft is deleted: the store confirmed it.");
  // Nothing is left beside it: focus goes to the status region, on screen.
  await expectFocusedHere(activity(page));
});

test("retire keeps its confirmation, busy, until its commit is answered; then the results are retired and focus is on them", async ({ page, context }) => {
  const store = await signInWithStore(page);
  const key = await groupWithOneResult(page, context);
  await area(page, "Storage");
  await page.getByRole("button", { name: "Rebuild the contribution index", exact: true }).click();
  await expect(notice(page)).toContainText("The contribution index was rebuilt", { timeout: STORE_STEP_MS });
  await area(page, "Groups");
  await page.locator("#group-list").getByRole("link", { name: key }).click();
  // An incomplete group (1 of 2) is closed before it is finalized.
  await page.locator("#lifecycle").getByRole("button", { name: "Close incomplete", exact: true }).click();
  await expect(page.locator("#group-phase")).toHaveAttribute("data-phase", "ClosedIncomplete", { timeout: STORE_STEP_MS });
  await page.locator("#lifecycle").getByRole("button", { name: "Finalize", exact: true }).click();
  await expect(page.locator("#group-phase")).toHaveAttribute("data-phase", /^Finalized/, { timeout: STORE_STEP_MS });
  const retention = page.locator("#group-retention");
  await retention.getByRole("button", { name: "Retire accepted results…", exact: true }).click();

  // The retirement's own commit (its branch update), held: with a result to
  // retire it is always made, so the outcome below is the only one.
  const commits = refUpdates(store);
  const hold = store.holdNext("PATCH", REF_UPDATE);
  await retention.getByRole("button", { name: "Retire accepted results", exact: true }).click();
  await hold.reached;
  const retiring = retention.getByRole("button", { name: "Retiring accepted results", exact: true });
  await expect(retiring).toBeVisible();
  await expect(retiring).toHaveAttribute("aria-busy", "true");
  await expect(retiring).toBeFocused();
  await expect(retention.getByRole("button", { name: "Keep them", exact: true })).toBeDisabled();
  await expectRunning(page, "Retiring accepted results: nothing is retired until the store confirms.");

  hold.release();
  await expect(page.locator("#retire-confirm")).toHaveCount(0, { timeout: STORE_STEP_MS });
  await expect(activity(page).locator("#persistence")).toHaveAttribute("data-persistence", "saved");
  expect(refUpdates(store)).toBeGreaterThan(commits);
  const retired = retention.locator("#sources-retired");
  await expect(retired).toContainText("Accepted results retired.");
  await expectFocusedHere(retired);
  await expect(activity(page)).not.toBeFocused();
});

test("a dataset lifecycle change moves focus to its lifecycle heading", async ({ page }) => {
  const store = await signInWithStore(page);
  await area(page, "Storage");
  const hold = store.holdNext("PATCH", REF_UPDATE);
  await page.locator("#lifecycle-targets").getByRole("button", { name: "Archive", exact: true }).click();
  await hold.reached;
  hold.release();
  await expect(page.locator("#dataset-lifecycle-state")).toHaveText("state-archived", { timeout: STORE_STEP_MS });
  await expectFocusedHere(page.locator("#dataset-lifecycle-heading"));
});

test("a change the store refuses is reported failed: not done, never saved", async ({ page }) => {
  const store = await signInWithStore(page);
  expectConsoleError(page, /status of 503/);
  const draft = await startDraft(page);
  await saveDraft(page, draft);
  // The branch update fails, and so does the one inspected resend (Arca 0.7,
  // ARCA-OUT-001): the request for review is not stored, and the store is offline.
  store.failNext("PATCH", REF_UPDATE);
  store.failNext("PATCH", REF_UPDATE);
  await draft.getByRole("button", { name: "Submit for review", exact: true }).click();
  const persistence = activity(page).locator("#persistence");
  await expect(persistence).toHaveAttribute("data-persistence", "failed", { timeout: STORE_STEP_MS });
  await expect(persistence).toHaveText("“Submit for review” did not complete; the notice says why.");
  await expect(page.locator("#operation-pending")).toHaveCount(0);
  await expect(page.locator('[aria-busy="true"]')).toHaveCount(0);
});

// An unknown outcome is asserted in the .NET wire tests (OperationsTests: a
// roster change whose provider outcome stays unknown, and an operation that
// throws), against the in-memory provider. Through this GitHub fake, Arca
// 0.7 reconciles an interrupted branch update itself and lands it (CI run
// 42747113711 reported "saved"), so a browser test cannot make it unknown
// deterministically.

test("loading regions are status regions with Forma's spinner and a visible label", async ({ page }) => {
  // The deployment's configuration, held.
  const configuration = gate();
  await routeSignIn(page);
  await page.route("**/web/admin/signal.deployment.json", async (route) => {
    await configuration.opened;
    await route.fallback();
  });
  await open(page, "/web/admin/index.html");
  await expectLoadingRegion(page.locator("#loading"), "Loading the deployment's configuration…");
  configuration.open();
  await expect(page.getByRole("heading", { name: "Sign in with GitHub" })).toBeVisible();
  await expect(page.locator("#loading")).toHaveCount(0);

  // The survey's published version, held.
  const survey = gate();
  await page.route(`**/${links.templateFile}`, async (route) => {
    await survey.opened;
    await route.fallback();
  });
  await page.goto(links.identified);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
  await expectLoadingRegion(page.locator("#loading"), "Loading the survey…");
  survey.open();
  await expect(page.locator("#survey-title")).toHaveText("Signal demo survey");
  await expect(page.locator("#loading")).toHaveCount(0);
});

test("opening the dataset after sign-in shows what it waits for", async ({ page }) => {
  const store = new FakeGitHubStore({ origin });
  expectConsoleError(page, /Failed to load resource: the server responded with a status of 404/);
  await routeSignIn(page);
  await store.install(page);
  const hold = store.holdNext("GET", /^\/user$/);
  await open(page, "/web/admin/index.html");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.waitForURL(/\/web\/admin\/index\.html/);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
  await hold.reached;

  await expectRunning(page, "Opening Engagement…");
  const opening = page.getByRole("button", { name: "Opening Engagement", exact: true });
  await expect(opening).toHaveAttribute("aria-busy", "true");

  hold.release();
  await expect(page.getByRole("status").filter({ hasText: "Signed in; changes can be saved." })).toBeVisible({ timeout: STORE_STEP_MS });
  await expect(page.locator("#operation-pending")).toHaveCount(0);
});

test("chips carry the authoritative state as a tone and a plain label, apart from the text beside them", async ({ page }) => {
  const store = await signInWithStore(page);
  await createGroup(page);

  // The group in view and its row in the list: the phase's tone, its plain
  // label, and its id.
  const phase = page.locator("#group-phase");
  await expect(phase).toHaveClass(/(^|\s)echelon-chip(\s|$)/);
  await expect(phase).toHaveAttribute("data-echelon-tone", "ready");
  await expect(phase).toHaveAttribute("data-phase", "GroupReady");
  await expect(phase).toHaveText("Ready");
  await area(page, "Groups");
  const chip = page.locator("#group-list tbody tr .echelon-chip");
  await expect(chip).toHaveCount(1);
  await expect(chip).toHaveAttribute("data-echelon-tone", "ready");
  await expect(chip).toHaveText("Ready");

  // A draft through its review: draft, in review, reviewed.
  const draft = await startDraft(page);
  const lifecycle = page.locator("#draft-lifecycle");
  await expect(lifecycle).toHaveAttribute("data-echelon-tone", "draft");
  await expect(lifecycle).toHaveText("Draft");
  await expect(lifecycle.locator("xpath=..")).toContainText("Draft · New survey");
  await draft.getByRole("textbox", { name: "New section title" }).fill("Delivery");
  await draft.getByRole("textbox", { name: "New section title" }).press("Tab");
  await draft.getByRole("button", { name: "Add section" }).click();
  await draft.getByRole("textbox", { name: "New question" }).fill("We ship every week.");
  await draft.getByRole("textbox", { name: "New question" }).press("Tab");
  await draft.getByRole("button", { name: "Add the question below here" }).click();
  await draft.getByRole("button", { name: "Add a fixture: every answer at the midpoint" }).click();
  await saveDraft(page, draft);
  await draft.getByRole("button", { name: "Submit for review", exact: true }).click();
  await expect(lifecycle).toHaveText("In review", { timeout: STORE_STEP_MS });
  await expect(lifecycle).toHaveAttribute("data-echelon-tone", "review");
  await draft.getByRole("button", { name: "Approve as reviewed", exact: true }).click();
  await expect(lifecycle).toHaveText("Reviewed", { timeout: STORE_STEP_MS });
  await expect(lifecycle).toHaveAttribute("data-echelon-tone", "review");

  // The stored draft's row under Assessments carries the same chip.
  await area(page, "Assessments");
  const draftRow = page.locator("#template-drafts li").filter({ hasText: "Team pulse (PULSE)" });
  await expect(draftRow.locator(".echelon-chip")).toHaveText("Reviewed");
  await expect(draftRow.locator(".echelon-chip")).toHaveAttribute("data-echelon-tone", "review");
  await expect(draftRow).toContainText("Reviewed · Team pulse (PULSE)");

  // Published: the version's chip; hiding it shows its own busy button, then "Hidden".
  await draftRow.getByRole("link").click();
  await draft.getByRole("checkbox", { name: "I have read the warnings and accept them" }).check();
  await draft.getByRole("button", { name: "Publish", exact: true }).click();
  await expect(notice(page)).toContainText(/^PULSE version 1 is published/, { timeout: STORE_STEP_MS });
  await area(page, "Assessments");
  const version = page.locator("#template-versions li").filter({ hasText: "Team pulse (PULSE 1)" });
  await expect(version.locator(".echelon-chip")).toHaveText("Published");
  await expect(version.locator(".echelon-chip")).toHaveAttribute("data-echelon-tone", "published");
  await expect(version).toContainText("Published · Team pulse (PULSE 1)");
  const hold = store.holdNext("PATCH", REF_UPDATE);
  const actions = page.locator("#template-actions");
  await actions.getByRole("button", { name: "Hide from new groups", exact: true }).click();
  await hold.reached;
  const hiding = actions.getByRole("button", { name: "Hiding from new groups", exact: true });
  await expect(hiding).toHaveAttribute("aria-busy", "true");
  await expect(actions.getByRole("button", { name: "Derive a draft", exact: true })).toHaveCount(0);
  hold.release();
  await expect(version.locator(".echelon-chip")).toHaveText("Hidden", { timeout: STORE_STEP_MS });
  await expect(version.locator(".echelon-chip")).toHaveAttribute("data-echelon-tone", "closed");
  await expectFocusedHere(activity(page));
});

test("an export in flight: its own button is busy, the other exports wait, and the file arrives", async ({ page }) => {
  const store = await signInWithStore(page);
  await createGroup(page);
  await groupTab(page, "Report");
  await page.getByRole("button", { name: "Take a snapshot", exact: true }).click();
  await expect(notice(page)).toContainText(/^Snapshot snap-[0-9a-f]+ was taken/, { timeout: STORE_STEP_MS });
  const snapshot = page.locator("#snapshots li");

  const hold = store.holdNext("GET", /\/contents\//);
  const download = page.waitForEvent("download");
  await snapshot.getByRole("button", { name: "Report (JSON)", exact: true }).click();
  await hold.reached;
  const exporting = snapshot.getByRole("button", { name: "Exporting", exact: true });
  await expect(exporting).toHaveAttribute("aria-busy", "true");
  for (const name of ["Sections (CSV)", "Lineage"]) await expect(snapshot.getByRole("button", { name, exact: true })).toBeDisabled();
  await expectRunning(page, "Exporting…");
  hold.release();
  expect((await download).suggestedFilename()).toMatch(/^snap-[0-9a-f]+-report\.json$/);
  await expect(snapshot.getByRole("button", { name: "Report (JSON)", exact: true })).toHaveAttribute("aria-busy", "false");
});
