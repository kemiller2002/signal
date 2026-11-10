// Visual-review evidence capture (WI-0110), the administrator console: the
// deployment and sign-in states, then the signed-in console against the
// browser suite's test-only GitHub and Fides fakes. The shared session, sites
// and scenario wrapper are in capture-session.mjs; the public pages are
// capture-public.spec.js. Evidence tooling, not a test: see capture-session.mjs.
import { expect, test } from "@playwright/test";
import { FakeGitHubStore } from "../browser/fakes/github-store.js";
import { deployment, links, origin, routeSignIn } from "../browser/ui-conformance-fixtures.mjs";
import { AUTH } from "./capture-plan.mjs";
import { SITES, STORE_MS, fresh, kernelRunning, scenario } from "./capture-session.mjs";

// Independent scenarios: one that stops part way does not skip the others.
test.describe.configure({ timeout: 45 * 60_000 });

// ---------------------------------------------------------------------------
// The administrator console before sign-in: loading its configuration, the
// demo's "no store", a broken configuration, and the sign-in screen.

const DEPLOYMENT = "**/web/admin/signal.deployment.json";

scenario("admin-unsigned", async ({ page, capture }) => {
  const url = `${SITES.checkout.base}web/admin/index.html`;
  let release = () => {};
  await capture.attempt(page, { id: "admin-loading-configuration", title: "Console while its deployment configuration loads (fetch held)" }, async () => {
    const held = new Promise((resolve) => { release = resolve; });
    await page.route(DEPLOYMENT, async (route) => { await held; await route.continue(); });
    await capture.step(page, `hold signal.deployment.json, open ${url}`, async () => {
      await page.goto(url);
      await kernelRunning(page);
      await expect(page.locator("#loading")).toBeVisible({ timeout: 15_000 });
    }, { operation: "deployment configuration fetch" });
  });
  await capture.attempt(page, { id: "admin-unconfigured", title: "Console with no data store configured (the demo deployment; empty state)" }, async () => {
    await capture.step(page, "release signal.deployment.json (the checkout has none: 404)", async () => {
      release();
      await expect(page.getByRole("heading", { name: "No data store is configured" })).toBeVisible();
    }, { operation: "deployment configuration fetch" });
    await page.unroute(DEPLOYMENT);
  });
  await capture.attempt(page, { id: "admin-misconfigured", title: "Console with a deployment configuration it cannot use (error)", auth: AUTH.configuredOnly }, async () => {
    await page.route(DEPLOYMENT, (route) => route.fulfill({ status: 200, contentType: "application/json",
      body: JSON.stringify({ ...deployment, datasets: [{ ...deployment.datasets[0], profile: "missing" }] }) }));
    await capture.step(page, "answer signal.deployment.json with a dataset whose profile is not configured, reload", async () => {
      await fresh(page, url);
      await kernelRunning(page);
    }, { operation: "deployment configuration fetch" });
    await expect(page.locator("#misconfigured")).toBeVisible({ timeout: 15_000 });
    await page.unroute(DEPLOYMENT);
  });
  await capture.attempt(page, { id: "admin-sign-in", title: "Console sign-in screen (a configured deployment, signed out)", auth: AUTH.configuredOnly }, async () => {
    await routeSignIn(page);
    await capture.step(page, "answer signal.deployment.json with the suite's test deployment (GitHub through Fides), reload", async () => {
      await fresh(page, url);
      await kernelRunning(page);
    }, { operation: "deployment configuration fetch" });
    await expect(page.getByRole("heading", { name: "Sign in with GitHub" })).toBeVisible();
  });
  await capture.attempt(page, { id: "admin-pages-site-demo", title: "Console on the assembled Pages site (the published demo: no store, no sign-in)", site: "pages" }, async () => {
    await capture.step(page, `open ${SITES.pages.base}web/admin/`, async () => {
      await page.goto(`${SITES.pages.base}web/admin/`);
      await kernelRunning(page);
    }, { operation: "engine start-up + deployment configuration fetch" });
    await expect(page.locator("h2").first()).toBeVisible();
  });
});

// ---------------------------------------------------------------------------
// The signed-in console against the suite's fake GitHub store: every area,
// a group from empty to imported results, a survey from draft to published,
// and a store failure.

const nav = (page) => page.getByRole("navigation", { name: "Administration" });
const area = (page, name) => nav(page).getByRole("link", { name, exact: true }).click();
const groupTab = (page, name) => page.getByRole("navigation", { name: "Group" }).getByRole("link", { name, exact: true }).click();
const notice = (page) => page.locator("#notice-message");
const fill = async (scope, name, value, nth = 0) => {
  const box = scope.getByRole("textbox", { name, exact: true }).nth(nth);
  await box.fill(value);
  await box.press("Tab");
};
const spin = async (scope, name, value) => {
  const box = scope.getByRole("spinbutton", { name });
  await box.fill(value);
  await box.press("Tab");
};

scenario("admin-signed-in", async ({ page, capture }) => {
  const signedIn = { auth: AUTH.fakes };
  const store = new FakeGitHubStore({ origin });
  await routeSignIn(page);
  await store.install(page);

  // SIG-VIS-PENDING-001: the dataset open after sign-in, held at the store's
  // first request (GET /user), to photograph the busy dataset button and the
  // activity region; then released.
  const openHold = store.holdNext("GET", /^\/user$/);
  await capture.attempt(page, { ...signedIn, id: "admin-dataset-opening", title: "Dataset opening after sign-in (store request held): busy button and activity region" }, async () => {
    await capture.step(page, "open web/admin/ with the test deployment", async () => {
      await page.goto(`${SITES.checkout.base}web/admin/index.html`);
      await kernelRunning(page);
      await expect(page.getByRole("heading", { name: "Sign in with GitHub" })).toBeVisible();
    }, { operation: "engine start-up + deployment configuration fetch" });
    await capture.step(page, "click Sign in (GitHub authorize redirect and Fides exchange, both faked); hold GET /user", async () => {
      await page.getByRole("button", { name: "Sign in", exact: true }).click();
      await page.waitForURL(/\/web\/admin\/index\.html/);
      await kernelRunning(page);
      await openHold.reached;
      await expect(page.locator("#operation-pending")).toBeVisible({ timeout: 15_000 });
    }, { operation: "sign-in (OAuth redirect and token exchange)" });
  });
  const reached = await capture.attempt(page, { ...signedIn, id: "admin-overview-empty", title: "Console home (Overview), signed in, a new empty dataset" }, async () => {
    openHold.release();
    await capture.step(page, "wait for the dataset to open writable", () =>
      expect(page.getByRole("status").filter({ hasText: "Signed in; changes can be saved." })).toBeVisible({ timeout: STORE_MS }),
    { operation: "dataset open (store reads and first set-up writes)" });
    await expect(page.getByRole("heading", { level: 2, name: "Overview" })).toBeVisible();
  });
  if (!reached) return;

  const visit = (id, title, name, shown) =>
    capture.attempt(page, { ...signedIn, id, title }, async () => {
      await capture.step(page, `open the ${name} area`, () => area(page, name), { operation: "area navigation" });
      await shown();
    });
  await visit("admin-groups-empty", "Groups, none yet (empty state) with the new-group form", "Groups", () => expect(page.locator("#new-group")).toBeVisible());
  await visit("admin-compare-empty", "Compare with no groups (empty state)", "Compare", () => expect(page.getByRole("heading", { level: 2 }).first()).toBeVisible());
  await visit("admin-assessments-empty", "Assessments: the built-in catalog, nothing published in the dataset, the start-a-survey form", "Assessments", () => expect(page.locator("#new-draft")).toBeVisible());
  await visit("admin-reports-empty", "Report definitions, none yet (empty state)", "Reports", () => expect(page.getByRole("heading", { name: "Report definitions" })).toBeVisible());
  await visit("admin-administrators", "Administrators", "Administrators", () => expect(page.getByRole("heading", { level: 2 }).first()).toBeVisible());
  await visit("admin-storage", "Storage and dataset lifecycle", "Storage", () => expect(page.locator("#dataset-lifecycle-state")).toBeVisible());

  await capture.attempt(page, { ...signedIn, id: "admin-route-problem", title: "Console address naming no view (error)" }, async () => {
    await capture.step(page, "set the address to #/no-such-view", () => page.evaluate(() => { location.hash = "#/no-such-view"; }), { operation: "route change" });
    await expect(page.locator("#route-problem-title")).toBeVisible({ timeout: 15_000 });
  });

  // A group, empty, then with an imported response.
  await capture.attempt(page, { ...signedIn, id: "admin-group-overview", title: "A new group's overview and lifecycle" }, async () => {
    await capture.step(page, "open Groups, set expected surveys 2 and minimum reportable count 1, click Create group", async () => {
      await area(page, "Groups");
      const form = page.locator("#new-group");
      await spin(form, "Expected surveys", "2");
      await spin(form, "Minimum reportable count", "1");
      await form.getByRole("button", { name: "Create group" }).click();
      await expect(page.getByRole("heading", { level: 2, name: /^Group [0-9a-f]{32}$/ })).toBeVisible({ timeout: STORE_MS });
    }, { operation: "create group (store write)" });
  });
  const tab = (id, title, name, shown) =>
    capture.attempt(page, { ...signedIn, id, title }, async () => {
      await capture.step(page, `open the group's ${name} tab`, () => groupTab(page, name), { operation: "group tab navigation" });
      await shown();
    });
  await tab("admin-group-results-empty", "Group results with no imported surveys (empty state)", "Results", () => expect(page.locator("#exploration")).toBeVisible());
  await tab("admin-group-scoring-empty", "Group scoring with no imported surveys (empty state)", "Scoring", () => expect(page.getByRole("region", { name: "Scoring" })).toBeVisible());
  await tab("admin-group-imports-empty", "Group imports: invitation and import forms, nothing imported", "Imported surveys", () => expect(page.locator("#import")).toBeVisible());

  let submission = null;
  await capture.attempt(page, { ...signedIn, id: "admin-group-invitations-issued", title: "Group imports: two invitation links issued" }, async () => {
    const invitations = page.locator("#invitations");
    await capture.step(page, "set How many links to 2, click Create links", async () => {
      await spin(invitations, "How many links", "2");
      await invitations.getByRole("button", { name: "Create links" }).click();
      await expect(page.locator("#invitation-summary")).toHaveText(/2 link\(s\) issued/, { timeout: STORE_MS });
    }, { operation: "issue invitations (store write)" });
    const issued = await page.locator("#invitation-links code").allTextContents();
    const respondent = await capture.preparePage(await page.context().newPage());
    await capture.step(respondent, "in a second tab, open the first link, answer every question \"Often\", See results, Submit answers", async () => {
      await respondent.goto(new URL(issued[0], page.url()).href);
      await kernelRunning(respondent);
      for (const group of await respondent.locator("fieldset.forma-ordinal-scale").all()) await group.getByRole("radio", { name: "Often", exact: true }).check();
      await respondent.getByRole("button", { name: "See results" }).click();
      await respondent.getByRole("button", { name: "Submit answers" }).click();
      submission = await respondent.getByRole("textbox", { name: "Submission link" }).inputValue();
    }, { operation: "respondent answers an invitation" });
    await respondent.close();
  });
  await capture.attempt(page, { ...signedIn, id: "admin-group-import-outcome", title: "Group imports: one link accepted, one rejected" }, async () => {
    if (!submission) throw new Error("no submission link: the invitation was not answered");
    const imports = page.locator("#import");
    await capture.step(page, "paste the submission link and \"not a link\", click Import", async () => {
      await imports.getByRole("textbox", { name: "Completed survey links, one per line" }).fill(`${submission}\nnot a link`);
      await imports.getByRole("button", { name: "Import" }).click();
      await expect(page.locator("#importAccepted li")).toHaveCount(1, { timeout: STORE_MS });
    }, { operation: "import submissions (store write)" });
  });
  await tab("admin-group-results", "Group results with one imported survey", "Results", () => expect(page.locator("#exploration")).toBeVisible());
  await tab("admin-group-scoring", "Group scoring with one imported survey", "Scoring", () => expect(page.getByRole("region", { name: "Scoring" })).toBeVisible());
  await tab("admin-group-report", "Group report", "Report", () => expect(page.locator("#report")).toBeVisible());
  // SIG-VIS-PENDING-002/003: the group finalized (focus moves to its phase,
  // with the focus ring), then its results retired through the confirmation,
  // the retirement's commit held, then the retired results with focus.
  await capture.attempt(page, { ...signedIn, id: "admin-group-finalized", title: "Group finalized: focus on the group's phase chip, with the Forma focus ring", extents: ["full", "viewport"] }, async () => {
    await capture.step(page, "rebuild the index, open the group, click Close incomplete, then Finalize", async () => {
      await area(page, "Storage");
      await page.getByRole("button", { name: "Rebuild the contribution index", exact: true }).click();
      await expect(notice(page)).toContainText("The contribution index was rebuilt", { timeout: STORE_MS });
      await area(page, "Groups");
      await page.locator("#group-list tbody tr a").first().click();
      await page.locator("#lifecycle").getByRole("button", { name: "Close incomplete", exact: true }).click();
      await expect(page.locator("#group-phase")).toHaveAttribute("data-phase", "ClosedIncomplete", { timeout: STORE_MS });
      await page.locator("#lifecycle").getByRole("button", { name: "Finalize", exact: true }).click();
      await expect(page.locator("#group-phase")).toHaveAttribute("data-phase", /^Finalized/, { timeout: STORE_MS });
      await expect(page.locator("#group-phase")).toBeFocused({ timeout: STORE_MS });
      await expect(page.locator("#group-phase")).toBeInViewport();
    }, { operation: "group lifecycle transitions (store writes)" });
  });
  const retireHold = { hold: null };
  await capture.attempt(page, { ...signedIn, id: "admin-group-retiring", title: "Retire accepted results confirmed, retirement in flight (its commit held): the confirmation stays, its button busy" }, async () => {
    await capture.step(page, "click Retire accepted results…, hold the retirement's branch update, confirm", async () => {
      const retention = page.locator("#group-retention");
      await retention.getByRole("button", { name: "Retire accepted results…", exact: true }).click();
      retireHold.hold = store.holdNext("PATCH", /\/git\/refs\/heads\/main$/);
      await retention.getByRole("button", { name: "Retire accepted results", exact: true }).click();
      await retireHold.hold.reached;
      await expect(retention.getByRole("button", { name: "Retiring accepted results", exact: true })).toHaveAttribute("aria-busy", "true");
    }, { operation: "retire accepted results (store write)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-group-retired", title: "Accepted results retired: the confirmation closed, focus on the retired results, with the Forma focus ring", extents: ["full", "viewport"] }, async () => {
    await capture.step(page, "release the branch update", async () => {
      retireHold.hold?.release();
      await expect(page.locator("#retire-confirm")).toHaveCount(0, { timeout: STORE_MS });
      await expect(page.locator("#sources-retired")).toBeFocused({ timeout: STORE_MS });
      await expect(page.locator("#sources-retired")).toBeInViewport();
    }, { operation: "retire accepted results (store write)" });
  });
  await visit("admin-overview", "Console home (Overview) with one group", "Overview", () => expect(page.getByRole("heading", { level: 2, name: "Overview" })).toBeVisible());
  await visit("admin-groups-list", "Groups list with one group", "Groups", () => expect(page.locator("#group-list tbody tr")).toHaveCount(1));

  // A survey from a new draft to a published version.
  const draft = page.locator("#draft");
  await capture.attempt(page, { ...signedIn, id: "admin-draft-new", title: "Survey authoring: a new draft with its validation findings" }, async () => {
    await capture.step(page, "open Assessments, enter PULSE / Team pulse, click Start draft", async () => {
      await area(page, "Assessments");
      const start = page.locator("#new-draft");
      await fill(start, "Survey identifier", "PULSE");
      await fill(start, "Title", "Team pulse");
      await start.getByRole("button", { name: "Start draft" }).click();
      await expect(draft.getByRole("heading", { name: "Team pulse (PULSE, draft)" })).toBeVisible({ timeout: STORE_MS });
    }, { operation: "start draft" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-draft-edited", title: "Survey authoring: a section, two questions and a fixture added, unsaved" }, async () => {
    await capture.step(page, "add section Delivery, two questions and the midpoint fixture", async () => {
      await fill(draft, "New section title", "Delivery");
      await draft.getByRole("button", { name: "Add section" }).click();
      for (const prompt of ["We ship every week.", "We fix bugs fast."]) {
        await fill(draft, "New question", prompt);
        await draft.getByRole("button", { name: "Add the question below here" }).click();
      }
      await draft.getByRole("button", { name: "Add a fixture: every answer at the midpoint" }).click();
      await expect(page.locator("#draft-fixtures li")).toHaveCount(1);
    }, { operation: "draft editing (engine)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-draft-preview", title: "Survey authoring: the draft previewed as a respondent" }, async () => {
    const preview = page.locator("#draft-preview-area");
    await capture.step(page, "click Preview as a respondent, answer the first question \"Often\"", async () => {
      await preview.getByRole("button", { name: "Preview as a respondent" }).click();
      await preview.locator("#preview-rows").getByRole("radio", { name: "Often", exact: true }).first().check();
    }, { operation: "draft preview (engine)" });
    await expect(preview.getByRole("note")).toBeVisible();
  });
  // SIG-VIS-PENDING-001: the save held at its branch update, to photograph
  // the busy Save draft button, the disabled others and the activity region.
  const saveHold = store.holdNext("PATCH", /\/git\/refs\/heads\/main$/);
  await capture.attempt(page, { ...signedIn, id: "admin-draft-saving", title: "Survey authoring: Save draft in flight (store write held): busy button, others unavailable, not saved yet" }, async () => {
    await capture.step(page, "close the preview, click Save draft, hold the branch update", async () => {
      await page.locator("#draft-preview-area").getByRole("button", { name: "Close the preview" }).click();
      await draft.getByRole("button", { name: "Save draft", exact: true }).click();
      await saveHold.reached;
      await expect(draft.getByRole("button", { name: "Saving draft", exact: true })).toHaveAttribute("aria-busy", "true");
    }, { operation: "save draft (store write)" });
  });
  // signal#136 review round 2: on the long draft the outcome is shown beside
  // the actions (#activity and #notice are at the top of the page); the
  // viewport shots are scrolled to the action.
  const outcome = (scope) => page.locator(`${scope} .admin-outcome`);
  await capture.attempt(page, { ...signedIn, id: "admin-draft-saved", title: "Survey authoring: the draft saved, its outcome shown beside Save", extents: ["full", "viewport"], anchor: '#draft [data-event="saveDraft"]' }, async () => {
    await capture.step(page, "release the branch update", async () => {
      saveHold.release();
      await expect(notice(page)).toHaveText("The draft of PULSE was saved.", { timeout: STORE_MS });
      await expect(outcome("#draft")).toContainText("The draft of PULSE was saved.");
    }, { operation: "save draft (store write)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-draft-reviewed", title: "Survey authoring: the draft submitted for review and approved" }, async () => {
    await capture.step(page, "click Submit for review, then Approve as reviewed", async () => {
      await draft.getByRole("button", { name: "Submit for review" }).click();
      await expect(page.locator("#draft-review")).toHaveText("Ready for review", { timeout: STORE_MS });
      await draft.getByRole("button", { name: "Approve as reviewed" }).click();
      await expect(page.locator("#draft-review")).toHaveText("Reviewed", { timeout: STORE_MS });
    }, { operation: "review transitions (store write)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-publish-refused", title: "Survey authoring: Publish refused until the warnings are acknowledged (error), the refusal shown beside the actions", extents: ["full", "viewport"], anchor: '#draft [data-event="publishDraft"]' }, async () => {
    await capture.step(page, "click Publish without acknowledging the warnings", async () => {
      await draft.getByRole("button", { name: "Publish" }).click();
      await expect(notice(page)).toContainText("Acknowledge the warnings first", { timeout: STORE_MS });
      await expect(outcome("#draft")).toContainText("Acknowledge the warnings first");
      await expect(outcome("#draft")).toBeInViewport();
    }, { operation: "publish (store write)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-draft-published", title: "Survey authoring: published as version 1" }, async () => {
    await capture.step(page, "acknowledge the warnings, click Publish", async () => {
      await draft.getByRole("checkbox", { name: "I have read the warnings and accept them" }).check();
      await draft.getByRole("button", { name: "Publish" }).click();
      await expect(notice(page)).toContainText(/^PULSE version 1 is published/, { timeout: STORE_MS });
    }, { operation: "publish (store write)" });
  });
  await visit("admin-assessments-published", "Assessments with a published version in the dataset (published-template view)", "Assessments",
    () => expect(page.locator("#template-versions li")).toContainText("Team pulse (PULSE 1)"));
  await capture.attempt(page, { ...signedIn, id: "admin-assessments-test-links", title: "Assessments: test links for the published version" }, async () => {
    await capture.step(page, "click Test links", () => page.locator("#template-versions li").getByRole("button", { name: "Test links" }).click(), { operation: "test links (engine)" });
    await expect(page.getByRole("region", { name: "Test links for Team pulse (PULSE 1)" })).toBeVisible();
  });

  // SIG-VIS-PENDING-002: a second draft deleted through its confirmation,
  // the deletion held (busy confirm button, the way out disabled), then the
  // status it hands focus to.
  const deleteHold = { hold: null };
  await capture.attempt(page, { ...signedIn, id: "admin-draft-deleting", title: "Delete draft confirmed, deletion in flight (store write held): the confirmation stays, its button busy" }, async () => {
    await capture.step(page, "start and save draft TEMP, click Delete draft…, hold the branch update, click Delete draft", async () => {
      await area(page, "Assessments");
      const start = page.locator("#new-draft");
      await fill(start, "Survey identifier", "TEMP");
      await fill(start, "Title", "Temporary");
      await start.getByRole("button", { name: "Start draft" }).click();
      await draft.getByRole("button", { name: "Save draft", exact: true }).click();
      await expect(notice(page)).toHaveText("The draft of TEMP was saved.", { timeout: STORE_MS });
      await draft.getByRole("button", { name: "Delete draft…", exact: true }).click();
      deleteHold.hold = store.holdNext("PATCH", /\/git\/refs\/heads\/main$/);
      await draft.getByRole("button", { name: "Delete draft", exact: true }).click();
      await deleteHold.hold.reached;
      await expect(draft.getByRole("button", { name: "Deleting draft", exact: true })).toHaveAttribute("aria-busy", "true");
    }, { operation: "delete draft (store write)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-draft-deleted", title: "Draft deleted: the confirmation closed, focus on the outcome shown where the draft was, with the Forma focus ring", extents: ["full", "viewport"], anchor: "#draft .admin-outcome" }, async () => {
    await capture.step(page, "release the branch update", async () => {
      deleteHold.hold?.release();
      await expect(outcome("#draft")).toBeFocused({ timeout: STORE_MS });
      await expect(outcome("#draft")).toBeInViewport();
    }, { operation: "delete draft (store write)" });
  });

  // A store that keeps failing mid-write: offline and read-only.
  await capture.attempt(page, { ...signedIn, id: "admin-store-offline", title: "Store failing mid-write: reported offline and read-only, nothing queued (error)" }, async () => {
    store.failNext("PATCH", /\/git\/refs\/heads\/main$/);
    store.failNext("PATCH", /\/git\/refs\/heads\/main$/);
    await capture.step(page, "make the store answer the next two ref updates with 503, then create a group", async () => {
      await area(page, "Groups");
      const form = page.locator("#new-group");
      await spin(form, "Expected surveys", "2");
      await form.getByRole("button", { name: "Create group" }).click();
      await expect(page.getByRole("status").filter({ hasText: "Engagement" })).toContainText("Read-only.", { timeout: STORE_MS });
    }, { operation: "create group (store write, failing)" });
  });
  await capture.attempt(page, { ...signedIn, id: "admin-store-recovered", title: "Store recovered after Refresh" }, async () => {
    await capture.step(page, "click Refresh", async () => {
      await page.getByRole("button", { name: "Refresh" }).click();
      await expect(page.getByRole("status").filter({ hasText: "Engagement" })).not.toContainText("Offline", { timeout: STORE_MS });
    }, { operation: "refresh (store reads)" });
  });

  // signal#136 review round 2: unresolved work rendered. A new group's import
  // whose branch update fails ambiguously (503) and whose check of whether it
  // landed then fails too, so its outcome stays unknown and the batch needs
  // reconciliation (GroupStore.importBatch). Dropping the update outright is
  // not enough: the store then reads the branch, sees it did not land and
  // resends. The overview names the group, links to its imported surveys and
  // says how to resume. (Only an import on this page is listed: WI-0126.)
  await capture.attempt(page, { ...signedIn, id: "admin-overview-unresolved", title: "Overview with unresolved work: an import whose outcome is unknown, its group, link and how to resume" }, async () => {
    await capture.step(page, "create a group, open Imported surveys, import two lines; hold the branch update, answer it 503 and fail the reads that check it", async () => {
      await area(page, "Groups");
      const form = page.locator("#new-group");
      await spin(form, "Expected surveys", "2");
      await form.getByRole("button", { name: "Create group" }).click();
      await expect(page.getByRole("heading", { level: 2, name: /^Group [0-9a-f]{32}$/ })).toBeVisible({ timeout: STORE_MS });
      await groupTab(page, "Imported surveys");
      await expect(page.locator("#resume-import")).toContainText("Import exactly the same completed-survey links again to resume");
      const hold = store.holdNext("PATCH", /\/git\/refs\/heads\/main$/);
      const imports = page.locator("#import");
      await imports.getByRole("textbox", { name: "Completed survey links, one per line" }).fill("not a link\nnot a link either");
      await imports.getByRole("button", { name: "Import" }).click();
      await hold.reached;
      const from = store.requests.length;
      store.failNext("PATCH", /\/git\/refs\/heads\/main$/);
      for (let i = 0; i < 8; i += 1) store.failNext("GET", /^\/repos\//);
      hold.release();
      try {
        await expect(page.locator("#importReconciliation li")).not.toHaveCount(0, { timeout: STORE_MS });
      } catch (error) {
        // Say what the store was asked, so the next attempt is not a guess.
        throw new Error(`no reconciliation; requests after the held update: ${store.requests.slice(from - 1).join(" | ")}`);
      } finally {
        // A failure the import did not use must not reach a later request.
        store.failures.splice(0);
      }
    }, { operation: "import submissions (store write, outcome unknown)" });
    await capture.step(page, "open the Overview", async () => {
      await area(page, "Overview");
      const item = page.locator("#unresolved li");
      await expect(item).toHaveCount(1);
      await expect(item.locator("code")).toHaveText(/^[0-9a-f]{32}$/);
      await expect(item.getByRole("link", { name: "Open the group's imported surveys" })).toBeVisible();
      await expect(item).toContainText("Import exactly the same completed-survey links again to resume");
    }, { operation: "area navigation" });
  });

  // signal#136 review round 2: a published version hidden; its button goes
  // away, so focus moves to the outcome beside the versions, near where the
  // button was, not to #activity at the top of the page.
  await capture.attempt(page, { ...signedIn, id: "admin-assessments-version-hidden", title: "Assessments: version hidden from new groups, focus on the outcome beside the versions, with the Forma focus ring", extents: ["full", "viewport"], anchor: "#assessments .admin-outcome" }, async () => {
    await capture.step(page, "click Refresh, open Assessments, click Hide from new groups", async () => {
      await page.getByRole("button", { name: "Refresh" }).click({ timeout: STORE_MS });
      await expect(page.getByRole("status").filter({ hasText: "Signed in; changes can be saved." })).toBeVisible({ timeout: STORE_MS });
      await area(page, "Assessments");
      await page.locator("#template-actions").getByRole("button", { name: "Hide from new groups", exact: true }).click({ timeout: STORE_MS });
      await expect(outcome("#assessments")).toBeFocused({ timeout: STORE_MS });
      await expect(outcome("#assessments")).toContainText("PULSE version 1 is hidden from new groups");
      await expect(outcome("#assessments")).toBeInViewport();
    }, { operation: "hide version (store write)" });
  });
});
