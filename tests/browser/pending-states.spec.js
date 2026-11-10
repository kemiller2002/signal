// Per-action pending state, loading regions and state chips
// (SIG-VIS-PENDING-001) in the real built page, against the test-only fake
// GitHub store. The fake holds a request (`holdNext`) so a test can look at
// the page while an operation is in flight: the control that started it is
// aria-busy with its present-participle label, every other operation's
// control is unavailable, a second activation queues nothing, and the
// persistence indicator says "saved" only once the store confirmed.
// The theme that styles .echelon-* (Echelon theme 1.1.0) is not installed:
// these tests assert the structure and data the theme will style, not looks.
import { readFileSync } from "node:fs";
import { test, expect, expectConsoleError } from "./support.js";
import { FakeGitHubStore } from "./fakes/github-store.js";
import { signInWithStore, routeSignIn, createGroup, open, origin, STORE_STEP_MS, KERNEL_START_MS } from "./ui-conformance-fixtures.mjs";

test.describe.configure({ timeout: 300_000 });

const links = JSON.parse(readFileSync(new URL("./fixtures/survey-links.json", import.meta.url), "utf8"));

const areas = (page) => page.getByRole("navigation", { name: "Administration" });
const area = (page, name) => areas(page).getByRole("link", { name, exact: true }).click();
const notice = (page) => page.locator("#notice-message");
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

test("a save in flight: its button is busy with its pending label, the other operations wait, and a second click queues nothing", async ({ page }) => {
  const store = await signInWithStore(page);
  const draft = await startDraft(page);

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
  const pending = page.locator("#operation-pending");
  await expectLoadingRegion(pending, "Saving draft: not saved until the store confirms.");
  await expect(pending).toHaveAttribute("data-persistence", "pending");
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

  // Settled: the button is itself again, and the indicator says saved, now.
  const save = draft.getByRole("button", { name: "Save draft", exact: true });
  await expect(save).toHaveAttribute("aria-busy", "false");
  await expect(save).toBeEnabled();
  await expect(page.locator("#operation-pending")).toHaveCount(0);
  const persistence = page.locator("#persistence");
  await expect(persistence).toHaveAttribute("role", "status");
  await expect(persistence).toHaveAttribute("data-persistence", "saved");
  await expect(persistence).toHaveText("The draft is saved: the store confirmed it.");
  await expect(draft.getByRole("button", { name: "Submit for review", exact: true })).toBeEnabled();
});

test("a group transition in flight: only its own button is busy, and the others follow the guard", async ({ page }) => {
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
  await expectLoadingRegion(page.locator("#operation-pending"), "Closing: not saved until the store confirms.");

  hold.release();
  const phase = page.locator("#group-phase");
  await expect(phase).toHaveText("ClosedIncomplete", { timeout: STORE_STEP_MS });
  await expect(phase).toHaveAttribute("data-echelon-tone", "closed");
  await expect(page.locator("#persistence")).toHaveAttribute("data-persistence", "saved");
});

test("a change the store does not take is reported as not saved, never as saved", async ({ page }) => {
  const store = await signInWithStore(page);
  expectConsoleError(page, /status of 503/);
  const draft = await startDraft(page);
  await draft.getByRole("button", { name: "Save draft", exact: true }).click();
  await expect(notice(page)).toHaveText("The draft of PULSE was saved.", { timeout: STORE_STEP_MS });
  // The branch update fails, and so does the one inspected resend (Arca 0.7,
  // ARCA-OUT-001): the request for review is not stored.
  store.failNext("PATCH", REF_UPDATE);
  store.failNext("PATCH", REF_UPDATE);
  await draft.getByRole("button", { name: "Submit for review", exact: true }).click();
  const persistence = page.locator("#persistence");
  await expect(persistence).toHaveAttribute("data-persistence", /^(failed|unknown)$/, { timeout: STORE_STEP_MS });
  await expect(persistence).not.toContainText("is saved");
  // Settled: nothing is busy any more. (The store went offline, so the dataset
  // is read-only and the review controls are withdrawn.)
  await expect(page.locator("#operation-pending")).toHaveCount(0);
  await expect(page.locator('[aria-busy="true"]')).toHaveCount(0);
});

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

  await expectLoadingRegion(page.locator("#operation-pending"), "Opening Engagement…");
  const opening = page.getByRole("button", { name: "Opening Engagement", exact: true });
  await expect(opening).toHaveAttribute("aria-busy", "true");

  hold.release();
  await expect(page.getByRole("status").filter({ hasText: "Signed in; changes can be saved." })).toBeVisible({ timeout: STORE_STEP_MS });
  await expect(page.locator("#operation-pending")).toHaveCount(0);
});

test("chips carry the authoritative state as a tone and a visible label", async ({ page }) => {
  await signInWithStore(page);
  await createGroup(page);

  // The group in view and its row in the list: the phase's tone and its name.
  const phase = page.locator("#group-phase");
  await expect(phase).toHaveClass(/(^|\s)echelon-chip(\s|$)/);
  await expect(phase).toHaveAttribute("data-echelon-tone", "ready");
  await expect(phase).toHaveText("GroupReady");
  await area(page, "Groups");
  const chip = page.locator("#group-list tbody tr .echelon-chip");
  await expect(chip).toHaveCount(1);
  await expect(chip).toHaveAttribute("data-echelon-tone", "ready");
  await expect(chip).toHaveText("GroupReady");

  // A draft through its review: draft, in review, reviewed.
  const draft = await startDraft(page);
  const lifecycle = page.locator("#draft-lifecycle");
  await expect(lifecycle).toHaveAttribute("data-echelon-tone", "draft");
  await expect(lifecycle).toHaveText("Draft");
  await draft.getByRole("button", { name: "Save draft", exact: true }).click();
  await expect(notice(page)).toHaveText("The draft of PULSE was saved.", { timeout: STORE_STEP_MS });
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
});
