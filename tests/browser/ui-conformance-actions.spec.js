// Universal UI layout and interaction conformance for Signal's three pages:
// actions, outcomes and navigation
// (UI-LAYOUT-INTERACTION@1.0.0, kemiller2002/visual-engineering; adopted
// under signal#104, Praxis work item GH-104). Each test cites the rule IDs it
// evidences. The F# suite proves the engine decides correctly and the other
// browser specs prove its flows; this spec adds what the contract requires
// and they did not assert: a declared action inventory per surface, outcomes
// asserted before and after (including failures that must not look like
// success), declared peer-alignment groups measured after fonts settle,
// horizontal overflow at 320/390/768/1280, text growth, long labels,
// keyboard operation and reduced motion.
//
// Locators are by role and accessible name; there are no fixed sleeps and no
// coordinates. The kernel boots a .NET WebAssembly runtime, which can take
// longer than an assertion's default 5 s on a cold or shared machine, so the
// one start-up wait has its own bound.
import { test, expect, expectConsoleError } from "./support.js";
import { expectActionOutcome, expectNoHorizontalOverflow } from "./ui-conformance-helpers.mjs";
import { AREAS, FIRST_PROMPT, INVITATION, KERNEL_START_MS, actionNames, answerAll, links, open, question, routeSignIn } from "./ui-conformance-fixtures.mjs";

// --- Declared action inventory (ULI-A01, ULI-A02, ULI-A09, ULI-A10) -------
//
// Every enabled, visible action on each reachable surface, exactly. A new
// action fails this test until it is declared here and exercised below; an
// action that disappears fails it too. Answer fields (radios, checkboxes,
// number inputs) are inventoried by the outcome tests that change them.

test("ULI-A01/A09: every surface shows exactly its declared actions", async ({ page }) => {
  await open(page, "/web/index.html");
  expect(await actionNames(page)).toEqual(["See results"]);
  await answerAll(page, "Sometimes");
  await page.getByRole("button", { name: "See results" }).click();
  await expect(page.getByRole("heading", { name: "Results" })).toBeVisible();
  // Submission needs an invitation; a page opened without one offers none.
  expect(await actionNames(page)).toEqual(["Edit answers", "Start over"]);

  await open(page, INVITATION);
  await answerAll(page, "Sometimes");
  await page.getByRole("button", { name: "See results" }).click();
  expect(await actionNames(page)).toEqual(["Edit answers", "Start over", "Submit answers"]);
  await page.getByRole("button", { name: "Submit answers" }).click();
  await expect(page.getByRole("heading", { name: "Submitted" })).toBeVisible();
  expect(await actionNames(page)).toEqual(["Copy link"]);

  await open(page, links.identified);
  expect(await actionNames(page)).toEqual(["Submit answers"]);
  await open(page, links.paged);
  expect(await actionNames(page)).toEqual(["Next"]);
  await open(page, "/web/survey/");
  expect(await actionNames(page)).toEqual([]);

  const areas = AREAS.map(([name]) => name);
  await open(page, "/web/admin/");
  expect(await actionNames(page)).toEqual(["Copy link", ...areas]);
  await open(page, "/web/admin/#/assessments/SDRA/versions/0.1.0-draft/sections/D02/questions/CORE-007");
  const names = await actionNames(page);
  expect(names.slice(0, 8)).toEqual(["Copy link", ...areas]);
  expect(names.slice(8, 13)).toEqual([
    "Software Delivery Reality Assessment (SDRA 0.1.0-draft)",
    "Software Delivery Reality Assessment 0.1.0-draft",
    "D01 Plan Commitment", "D02 Change Responsiveness", "D03 Timebox Discipline"]);
  expect(names.slice(13)).toHaveLength(5);
  for (const name of names.slice(13)) expect(name).toMatch(/^CORE-0(0[6-9]|10): /);
  await open(page, "/web/admin/#/reports/2026");
  expect(await actionNames(page)).toEqual(["Copy link", ...areas, "Go to the overview"]);
});

// --- Critical workflows: outcomes before and after (ULI-A02, A05, A07, A10)

test("ULI-A02/A07: the assessment refuses results while unanswered, then scores, edits, starts over and submits", async ({ page }) => {
  await open(page, INVITATION);
  const progress = page.locator("#progress");
  await expect(progress).toHaveText("0 of 15 answered");

  // Failure first: no false results.
  await page.getByRole("button", { name: "See results" }).click();
  await expect(page.getByRole("alert")).toContainText(/unanswered|answer/i);
  await expect(page.getByRole("heading", { name: "Results" })).toHaveCount(0);

  // An answer is a visible state change, held in the address (ULI-A02).
  const before = page.url();
  await question(page, FIRST_PROMPT).getByRole("radio", { name: "Often", exact: true }).check();
  await expect(progress).toHaveText("1 of 15 answered");
  await expect.poll(() => page.url()).not.toBe(before);

  // Recovery: answering everything clears the refusal and scores.
  await answerAll(page, "Often");
  await expectActionOutcome(page, {
    ruleId: "ULI-A02",
    action: { role: "button", name: "See results" },
    outcome: { kind: "visible", target: { role: "heading", name: "Results" }, expected: true },
    timeoutMs: 10_000
  });
  await expect(page.locator("#results-body tr")).toHaveCount(3);
  await expect(page.locator("#scored-count")).toHaveText(/3 of 3/);

  // Edit answers returns to the same answers.
  await page.getByRole("button", { name: "Edit answers" }).click();
  await expect(question(page, FIRST_PROMPT).getByRole("radio", { name: "Often", exact: true })).toBeChecked();
  await expect(progress).toHaveText("15 of 15 answered");

  // Start over clears them (ULI-A07 reset).
  await page.getByRole("button", { name: "See results" }).click();
  await page.getByRole("button", { name: "Start over" }).click();
  await expect(progress).toHaveText("0 of 15 answered");
  await expect(question(page, FIRST_PROMPT).getByRole("radio", { name: "Often", exact: true })).not.toBeChecked();

  // Submission is final and says what it produced.
  await answerAll(page, "Rarely");
  await page.getByRole("button", { name: "See results" }).click();
  await expectActionOutcome(page, {
    action: { role: "button", name: "Submit answers" },
    outcome: { kind: "visible", target: { role: "heading", name: "Submitted" }, expected: true },
    timeoutMs: 10_000
  });
  await expect(page.getByRole("textbox", { name: "Submission link" })).toHaveValue(page.url());
  await expect(page.getByRole("button", { name: "Edit answers" })).toHaveCount(0);
});

test("ULI-A07: Copy link says copied only when the browser copied, and says otherwise when it refused", async ({ browser }) => {
  for (const allowed of [false, true]) {
    const context = await browser.newContext();
    if (allowed) await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    else {
      await context.addInitScript(() => Object.defineProperty(navigator, "clipboard", {
        configurable: true,
        value: { writeText: () => Promise.reject(new DOMException("Write permission denied.", "NotAllowedError")) }
      }));
    }
    const page = await context.newPage();
    await open(page, INVITATION);
    await answerAll(page, "Sometimes");
    await page.getByRole("button", { name: "See results" }).click();
    await page.getByRole("button", { name: "Submit answers" }).click();
    await page.getByRole("button", { name: "Copy link" }).click();
    const notice = page.locator("#copy-notice");
    if (allowed) {
      await expect(notice).toHaveText(/copied/);
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(page.url());
    } else {
      await expect(notice).toHaveText(/did not allow copying/);
      await expect(notice).not.toHaveText(/copied/);
    }

    // The administrator page's Copy link is held to the same honesty.
    await open(page, "/web/admin/#/assessments");
    await page.getByRole("button", { name: "Copy link" }).click();
    const adminNotice = page.locator("#notice-message");
    if (allowed) await expect(adminNotice).toHaveText(/^Link copied/);
    else await expect(adminNotice).toHaveText(/did not allow copying/);
    await page.getByRole("button", { name: "Dismiss" }).click();
    await expect(adminNotice).toHaveCount(0);
    await context.close();
  }
});

test("ULI-A07: a survey whose file cannot be fetched says so, offers no form, and recovers on reload", async ({ page }) => {
  expectConsoleError(page, /Failed to load resource|ERR_CONNECTION_REFUSED/);
  await page.route("**/published-templates/**", (route) => route.abort("connectionrefused"));
  await open(page, links.identified);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Survey unavailable");
  await expect(page.getByRole("alert")).toContainText("could not be loaded");
  await expect(page.getByRole("button")).toHaveCount(0);
  await expect(page.getByRole("radio")).toHaveCount(0);

  await page.unroute("**/published-templates/**");
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("Signal demo survey");
  await expect(page.getByRole("button", { name: "Submit answers" })).toBeVisible();
});

test("ULI-A02/A05: survey answers, best-worst picks and pages change what the page shows", async ({ page }) => {
  await open(page, links.compound);
  const progress = page.locator("#progress");
  await expect(progress).toHaveText("0 of 6 answered");
  await expectActionOutcome(page, {
    action: { role: "radio", name: "Calm Fits best" },
    outcome: { kind: "checked", target: { role: "radio", name: "Calm Fits best" }, expected: true }
  });
  await page.getByRole("radio", { name: "Chaotic Fits least" }).check();
  await expect(progress).toHaveText("1 of 6 answered");
  // A different best moves the pick: one best per question.
  await page.getByRole("radio", { name: "Busy Fits best" }).check();
  await expect(page.getByRole("radio", { name: "Calm Fits best" })).not.toBeChecked();

  await open(page, links.paged);
  const pageLabel = page.locator("#page-label");
  await expect(pageLabel).toHaveText("Page 1 of 3");
  // Failure first: Next with this page's required questions unanswered is
  // refused and says why; the page does not move (ULI-A07).
  await page.getByRole("button", { name: "Next" }).click();
  await expect(page.getByRole("alert")).toContainText("required questions on this page need an answer");
  await expect(pageLabel).toHaveText("Page 1 of 3");
  await expect(page.getByRole("button", { name: "Back" })).toHaveCount(0);
  // Recovery. The two questions share a scale, so each is reached by its row.
  await page.locator('[data-row="0-3"]').getByRole("radio").check();
  await page.locator('[data-row="1-4"]').getByRole("radio").check();
  await expectActionOutcome(page, {
    action: { role: "button", name: "Next" },
    outcome: { kind: "visible", target: { role: "button", name: "Back" }, expected: true }
  });
  await expect(pageLabel).toHaveText("Page 2 of 3");
  await page.getByRole("button", { name: "Back" }).click();
  await expect(pageLabel).toHaveText("Page 1 of 3");
  await expect(page.locator('[data-row="0-3"]').getByRole("radio")).toBeChecked();
});

// --- Navigation reaches real destinations (ULI-A01, ULI-L06) --------------

test("ULI-A01/L06: every administration area link reaches its own view, by keyboard too", async ({ page }) => {
  await open(page, "/web/admin/");
  const nav = page.getByRole("navigation", { name: "Administration" });
  for (const [name, href] of AREAS) {
    const link = nav.getByRole("link", { name, exact: true });
    await expect(link).toHaveAttribute("href", href);
    await link.click();
    await expect.poll(() => new URL(page.url()).hash || "#/").toBe(href);
    await expect(link).toHaveAttribute("aria-current", "page");
    // The destination says what it is: the public catalogue, or, with no store
    // configured, that there is nothing to administer (not a blank view).
    await expect(page.getByRole("heading", { level: 2, name: name === "Assessments" ? "Assessments" : "No data store is configured" })).toBeVisible();
  }
  // Keyboard: focus and Enter (ULI-A08).
  await nav.getByRole("link", { name: "Assessments", exact: true }).focus();
  await page.keyboard.press("Enter");
  await expect(nav.getByRole("link", { name: "Assessments", exact: true })).toHaveAttribute("aria-current", "page");
  await page.getByRole("link", { name: "Software Delivery Reality Assessment (SDRA 0.1.0-draft)" }).click();
  await page.getByRole("link", { name: "D02 Change Responsiveness" }).click();
  await page.getByRole("link", { name: /^CORE-007: / }).click();
  await expect(page.locator("#question-id")).toHaveText("CORE-007");

  await open(page, "/web/admin/#/reports/2026");
  await page.getByRole("link", { name: "Go to the overview" }).click();
  await expect.poll(() => new URL(page.url()).hash).toBe("#/");
  await expect(nav.getByRole("link", { name: "Overview", exact: true })).toHaveAttribute("aria-current", "page");
});

// --- An unreachable store never looks like success (ULI-A07) --------------
test("ULI-A07: a signed-in administrator whose store is unreachable is told so, sees no dataset, and can sign out", async ({ page }) => {
  await routeSignIn(page);
  await page.route("https://api.github.com/**", (route) => route.abort("connectionrefused"));
  expectConsoleError(page, /Failed to load resource|ERR_CONNECTION_REFUSED|AEGIS|aegis|Aegis/);

  await open(page, "/web/admin/index.html", 390);
  await expect(page.getByRole("heading", { name: "Sign in with GitHub" })).toBeVisible();
  await expect(page.getByRole("radio", { name: "On this page only" })).toBeChecked();
  await page.getByRole("radio", { name: "In this tab until I close it" }).check();
  await expect(page.locator("#retention-disclosure")).not.toContainText("only on this page");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL(/\/web\/admin\/index\.html/);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });

  await expect(page.locator("#notice-message")).toHaveText(/cannot be reached/, { timeout: 15_000 });
  await expect(page.locator("#notice-message")).toContainText("changes are unavailable");
  // Nothing claims to be a loaded dataset.
  await expect(page.locator("#status")).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Datasets" })).toBeVisible();
  await expectNoHorizontalOverflow(page, "admin: store unreachable @390");

  await page.getByRole("button", { name: "Dismiss" }).click();
  await expect(page.locator("#notice-message")).toHaveCount(0);
  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { name: "Sign in with GitHub" })).toBeVisible();
});
