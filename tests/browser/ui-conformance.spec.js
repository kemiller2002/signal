// Universal UI layout and interaction conformance for Signal's three pages
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
import { readFileSync } from "node:fs";
import { test, expect, expectConsoleError } from "./support.js";
import {
  VIEWPORT_WIDTHS,
  expectActionOutcome,
  expectNoHorizontalOverflow,
  expectPeerAlignment as expectPeerAlignmentMeasured,
  inventoryVisibleActions,
  settleFontsAndFrames
} from "./ui-conformance-helpers.mjs";

// Every measured group is attached to the test result (JSON reporter, trace)
// as conformance evidence: rule, group, edge, delta and each peer's coordinate.
async function expectPeerAlignment(page, group, options) {
  const report = await expectPeerAlignmentMeasured(page, group, options);
  await test.info().attach(`alignment ${report.group} @${page.viewportSize()?.width}`, {
    body: JSON.stringify({ viewport: page.viewportSize()?.width, ...report }), contentType: "application/json" });
  return report;
}

const links = JSON.parse(readFileSync(new URL("./fixtures/survey-links.json", import.meta.url), "utf8"));
const KERNEL_START_MS = 30_000;

// An anonymous invitation (as in assessment.spec.js): it can be submitted.
const INVITATION = "/web/index.html#r=AQSV3vb9UTM28AoLDA0ODxAREhMUFRYXGBlkZWZnaGlqa2xtbm9wcXJzAA8AAAAAAAAAAGf0i7E";
const FIRST_PROMPT = "Major requirements are expected to be substantially defined before implementation begins.";
const ORDINAL = ["Never or almost never", "Rarely", "Sometimes", "Often", "Almost always"];
const SPECIAL = ["Don't know", "Not observed", "Not applicable"];
const AREAS = [
  ["Overview", "#/"],
  ["Groups", "#/groups"],
  ["Compare", "#/compare"],
  ["Assessments", "#/assessments"],
  ["Reports", "#/reports"],
  ["Administrators", "#/administrators"],
  ["Storage", "#/storage"]
];

async function open(page, url, width = 1280) {
  await page.setViewportSize({ width, height: 900 });
  await page.goto(url);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
}

const question = (page, prompt) => page.getByRole("group", { name: prompt, exact: true });

// Answers every assessment question with the same visible choice, by label.
async function answerAll(page, choice) {
  const groups = page.locator("fieldset.ef-ordinal-scale");
  const count = await groups.count();
  for (let i = 0; i < count; i++) await groups.nth(i).getByRole("radio", { name: choice, exact: true }).check();
  await expect(page.locator("#progress")).toHaveText(`${count} of ${count} answered`);
}

// The enabled, rendered, non-field actions on the page, by name, in order.
const actionNames = async (page) => (await inventoryVisibleActions(page)).map((action) => action.nameHint);

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

const origin = `http://127.0.0.1:${process.env.SIGNAL_TEST_PORT ?? 4321}`;
const redirectUri = `${origin}/web/admin/index.html`;
const deployment = {
  environment: "production",
  environmentName: "production",
  identity: { exchange: "https://fides.test", application: "signal-admin", provider: "github", clientId: "Iv23liTEST", redirectUri },
  profiles: [{ id: "primary", label: "Survey data", provider: "github",
    location: { owner: "acme", repository: "signal-data", branch: "main", basePath: "prod" } }],
  datasets: [{ id: "ds_engagement", label: "Engagement", profile: "primary", administrators: ["583231"] }]
};
const inHours = (hours) => new Date(Date.now() + hours * 3600 * 1000).toISOString().replace(/\.\d+Z$/, "Z");

test("ULI-A07: a signed-in administrator whose store is unreachable is told so, sees no dataset, and can sign out", async ({ page }) => {
  await page.route("**/web/admin/signal.deployment.json", (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(deployment) }));
  await page.route("https://github.com/**", (route) => {
    const state = new URL(route.request().url()).searchParams.get("state");
    return route.fulfill({ status: 302, headers: { location: `${redirectUri}?code=good-code&state=${state}` } });
  });
  const cors = { "access-control-allow-origin": origin, "access-control-allow-methods": "POST", "access-control-allow-headers": "content-type" };
  await page.route("https://fides.test/v1/token", (route) =>
    route.request().method() === "OPTIONS"
      ? route.fulfill({ status: 204, headers: cors })
      : route.fulfill({ status: 200, contentType: "application/json", headers: cors,
          body: JSON.stringify({ accessToken: "gho_BROWSERTESTTOKEN0123456789", accessTokenExpiresAt: inHours(8),
            refreshToken: "ghr_BROWSERTESTREFRESH0123456789", refreshTokenExpiresAt: inHours(24 * 180),
            identity: { provider: "github", subject: "583231", login: "octocat", name: "The Octocat" } }) }));
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

// --- Declared peer-alignment groups (ULI-L01, L02, L07, L09, L10) ---------

for (const width of VIEWPORT_WIDTHS) {
  test(`ULI-L01/L02/L10: declared assessment groups align at ${width} CSS px`, async ({ page }) => {
    await open(page, "/web/index.html", width);
    const scope = question(page, FIRST_PROMPT);
    const ordinal = ORDINAL.map((name) => ({ role: "radio", name, measure: "label" }));
    // The five-point scale is one row from 44rem up and one column below it
    // (Forma's ordinal scale): the declared relationship changes with it.
    const stacked = width < 704;
    await expectPeerAlignment(page, { ruleId: "ULI-L01", id: "assessment.q1.ordinal", peers: ordinal, edge: stacked ? "left" : "top" }, { scope });
    await expectPeerAlignment(page, { ruleId: "ULI-L01", id: "assessment.q1.ordinal.end", peers: ordinal, edge: stacked ? "right" : "bottom" }, { scope });
    if (!stacked) {
      await expectPeerAlignment(page, { ruleId: "ULI-L01", id: "assessment.q1.other",
        peers: SPECIAL.map((name) => ({ role: "radio", name, measure: "label" })) }, { scope });
    }
    // The special choices share a left edge with the scale (one column edge).
    await expectPeerAlignment(page, { ruleId: "ULI-L01", id: "assessment.q1.start", edge: "left",
      peers: [{ role: "radio", name: ORDINAL[0], measure: "label" }, { role: "radio", name: SPECIAL[0], measure: "label" }] }, { scope });

    await answerAll(page, "Often");
    await page.getByRole("button", { name: "See results" }).click();
    await expectPeerAlignment(page, { ruleId: "ULI-L10", id: "assessment.results.actions",
      peers: [{ role: "button", name: "Edit answers" }, { role: "button", name: "Start over" }] });
    await expectPeerAlignment(page, { ruleId: "ULI-L02", id: "assessment.results.actions.height", edge: "bottom",
      peers: [{ role: "button", name: "Edit answers" }, { role: "button", name: "Start over" }] });
  });

  test(`ULI-L01/L09: survey best-worst columns and ranking fields align at ${width} CSS px`, async ({ page }) => {
    await open(page, links.compound, width);
    const rows = ["Calm", "Busy", "Chaotic"];
    for (const column of ["Fits best", "Fits least"]) {
      await expectPeerAlignment(page, { ruleId: "ULI-L09", id: `survey.best-worst.${column}`, edge: "left",
        peers: rows.map((row) => ({ role: "radio", name: `${row} ${column}` })) });
    }
    for (const row of rows) {
      await expectPeerAlignment(page, { ruleId: "ULI-L01", id: `survey.best-worst.${row}`,
        peers: [{ role: "radio", name: `${row} Fits best` }, { role: "radio", name: `${row} Fits least` }] });
    }
    for (const edge of ["left", "right"]) {
      await expectPeerAlignment(page, { ruleId: "ULI-L02", id: `survey.ranking.${edge}`, edge,
        peers: ["Speed", "Quality", "Cost"].map((name) => ({ role: "spinbutton", name })) });
    }
  });

  test(`ULI-L01/L05: administration areas share an edge and stay inside the page gutter at ${width} CSS px`, async ({ page }) => {
    await open(page, "/web/admin/#/assessments", width);
    const scope = page.getByRole("navigation", { name: "Administration" });
    await expectPeerAlignment(page, { ruleId: "ULI-L01", id: "admin.areas", edge: "left",
      peers: AREAS.map(([name]) => ({ role: "link", name })) }, { scope });
    // The page has a gutter (the defect fixed under GH-104: text ran to the
    // viewport edge). Measured from the page heading, not a coordinate.
    const heading = await page.getByRole("heading", { level: 2, name: "Assessments" }).boundingBox();
    expect(heading.x, "the heading starts inside a page gutter").toBeGreaterThanOrEqual(16);
    expect(heading.x + heading.width).toBeLessThanOrEqual(width - 16 + 0.5);
  });
}

// --- Overflow at every width, in every reachable state (ULI-L05, L08, L09)

const SURFACES = [
  ["assessment: questions", (page) => page.goto("/web/index.html"), null],
  ["assessment: refusal", (page) => page.goto("/web/index.html"), (page) => page.getByRole("button", { name: "See results" }).click()],
  ["assessment: results", (page) => page.goto(INVITATION), async (page) => {
    await answerAll(page, "Almost always");
    await page.getByRole("button", { name: "See results" }).click();
    await expect(page.getByRole("heading", { name: "Results" })).toBeVisible();
  }],
  // An empty dataset: nothing numeric, so nothing is scored.
  ["assessment: nothing scored", (page) => page.goto(INVITATION), async (page) => {
    await answerAll(page, "Not applicable");
    await page.getByRole("button", { name: "See results" }).click();
    await expect(page.locator("#scored-count")).toHaveText(/0 of 3/);
  }],
  ["assessment: submitted", (page) => page.goto(INVITATION), async (page) => {
    await answerAll(page, "Rarely");
    await page.getByRole("button", { name: "See results" }).click();
    await page.getByRole("button", { name: "Submit answers" }).click();
    await expect(page.getByRole("heading", { name: "Submitted" })).toBeVisible();
  }],
  ["survey: identified", (page) => page.goto(links.identified), null],
  ["survey: mixed kinds", (page) => page.goto(links.kinds), null],
  ["survey: compound kinds", (page) => page.goto(links.compound), null],
  ["survey: test link", (page) => page.goto(links.test), null],
  ["survey: no link", (page) => page.goto("/web/survey/"), null],
  ["admin: no store", (page) => page.goto("/web/admin/"), null],
  ["admin: question", (page) => page.goto("/web/admin/#/assessments/SDRA/versions/0.1.0-draft/sections/D02/questions/CORE-007"), null],
  ["admin: not found", (page) => page.goto("/web/admin/#/reports/2026"), null]
];

for (const [surface, go, act] of SURFACES) {
  test(`ULI-L05/L09: ${surface} has no horizontal page scroll at 320, 390, 768 and 1280 CSS px`, async ({ page }) => {
    await go(page);
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
    if (act) await act(page);
    for (const width of VIEWPORT_WIDTHS) {
      await page.setViewportSize({ width, height: 900 });
      await expectNoHorizontalOverflow(page, `${surface} @${width}`);
    }
  });
}

// --- Text growth and long labels (ULI-L05, L06) ---------------------------

test("ULI-L05: 200% text keeps every page usable without horizontal scroll at 390 CSS px", async ({ page }) => {
  for (const url of ["/web/index.html", links.compound, "/web/admin/#/assessments/SDRA/versions/0.1.0-draft/sections/D02/questions/CORE-007"]) {
    await open(page, url, 390);
    // CSSOM, not an injected <style>: the survey page's CSP refuses inline style elements.
    await page.evaluate(() => document.documentElement.style.setProperty("font-size", "200%", "important"));
    await expectNoHorizontalOverflow(page, `${url} @390 with 200% text`);
    // The primary action is still reachable and inside the viewport.
    const primary = page.getByRole("button", { name: /^(See results|Submit answers|Copy link)$/ }).first();
    await primary.scrollIntoViewIfNeeded();
    const box = await primary.boundingBox();
    expect(box.x + box.width).toBeLessThanOrEqual(390.5);
  }
});

const LONG = "Verantwortungsübernahmebereitschaft und Anforderungsmanagementprozessdokumentation über mehrere Lieferzyklen hinweg";

test("ULI-L05/L06: long titles, field labels and area names wrap instead of overflowing at 320 CSS px", async ({ page }) => {
  await open(page, links.compound, 320);
  // Rendered text replaced in the page (the engine projects only its own
  // fixtures): a long translated title and long field labels.
  await page.evaluate((text) => {
    document.querySelector("h1").textContent = text;
    for (const label of document.querySelectorAll("label > span:first-child:not(.ef-sr-only)")) label.textContent = text;
  }, LONG);
  await expectNoHorizontalOverflow(page, "survey with long title and field labels @320");
  // The best-worst columns still line up below the longer content.
  await expectPeerAlignment(page, { ruleId: "ULI-L09", id: "survey.best-worst.long-page", edge: "left",
    peers: ["Calm", "Busy", "Chaotic"].map((row) => ({ role: "radio", name: `${row} Fits best` })) });

  await open(page, "/web/admin/#/assessments", 320);
  await page.evaluate((text) => {
    for (const link of document.querySelectorAll("nav[aria-label='Administration'] a")) link.textContent = `${link.textContent} ${text}`;
  }, LONG);
  await expectNoHorizontalOverflow(page, "admin with long area names @320");
  // Each area keeps its readable name (ULI-L06).
  for (const [name] of AREAS) {
    await expect(page.getByRole("navigation", { name: "Administration" }).getByRole("link", { name: new RegExp(`^${name} Verantwortung`) })).toBeVisible();
  }
});

// EXCEPTION (owned by Forma, upstream): a best-worst option whose text holds a
// word longer than its column overflows the row at 320 CSS px. The option
// cell of Forma's .ef-best-worst__row (design-system 0.6.1) has
// minmax(0, 1fr) and no overflow-wrap, so the word escapes its 102 CSS px
// cell (ULI-L05). Signal does not restyle Forma components (L03, and
// FoundationsConformanceTests forbid it here). Owner: Forma; review: on the
// next design-system upgrade. test.fail keeps the defect visible: this test
// starts failing, and must be turned into an ordinary assertion, as soon as
// the pinned Forma wraps the cell.
test("EXCEPTION ULI-L05: a best-worst option with a very long word overflows Forma's row at 320 CSS px", async ({ page }) => {
  test.fail(true, "Forma .ef-best-worst__row option cell does not wrap long words (upstream defect, recorded under GH-104)");
  await open(page, links.compound, 320);
  await page.evaluate((text) => {
    for (const row of document.querySelectorAll("[data-row] > div:has(> label > input[data-event='pickedBest'])")) row.firstElementChild.textContent = text;
  }, LONG);
  await expectNoHorizontalOverflow(page, "survey best-worst with a long option @320");
});

// --- Keyboard (ULI-A08) and reduced motion (ULI-L08) ----------------------

test("ULI-A08: the assessment and the survey are operable from the keyboard alone", async ({ page }) => {
  await open(page, "/web/index.html", 390);
  const scope = question(page, FIRST_PROMPT);
  await scope.getByRole("radio", { name: ORDINAL[0], exact: true }).focus();
  await page.keyboard.press("Space");
  await expect(scope.getByRole("radio", { name: ORDINAL[0], exact: true })).toBeChecked();
  await page.keyboard.press("ArrowDown");
  await expect(scope.getByRole("radio", { name: ORDINAL[1], exact: true })).toBeChecked();
  await expect(page.locator("#progress")).toHaveText("1 of 15 answered");
  await page.getByRole("button", { name: "See results" }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("alert")).toBeVisible();

  await open(page, links.compound, 390);
  await page.getByRole("radio", { name: "Busy Fits least" }).focus();
  await page.keyboard.press("Space");
  await expect(page.getByRole("radio", { name: "Busy Fits least" })).toBeChecked();
  await expect(page.locator("#progress")).toHaveText("0 of 6 answered");
  await page.getByRole("radio", { name: "Calm Fits best" }).focus();
  await page.keyboard.press("Space");
  await expect(page.locator("#progress")).toHaveText("1 of 6 answered");
});

test("ULI-L08: with reduced motion the scale does not animate, and still answers", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await open(page, "/web/index.html");
  const scope = question(page, FIRST_PROMPT);
  const marker = scope.locator(".ef-ordinal-option__marker").first();
  const seconds = await marker.evaluate((element) => Math.max(...getComputedStyle(element).transitionDuration.split(",").map((value) => parseFloat(value))));
  expect(seconds).toBeLessThanOrEqual(0.001);
  await scope.getByRole("radio", { name: "Rarely", exact: true }).check();
  await expect(page.locator("#progress")).toHaveText("1 of 15 answered");
  await settleFontsAndFrames(page);
});
