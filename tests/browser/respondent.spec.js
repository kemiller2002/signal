// The respondent's page in the "foundry ledger" direction (SIG-ECHELON-APP-002
// (d)): one live region (#activity, as #133 made it for the console) that
// announces loading, progress, review, timers, selection guidance, link
// notices and the copy outcome, while each is shown, not live, where it
// belongs; and a layout that keeps whole words at 320 px with 200% text.
import { readFileSync } from "node:fs";
import { test, expect } from "./support.js";
import { pick } from "./survey-answers.mjs";

const links = JSON.parse(readFileSync(new URL("./fixtures/survey-links.json", import.meta.url), "utf8"));

const KERNEL_START_MS = 30_000;
const LIVE = '[aria-live], [role="status"], [role="log"], [role="timer"], [role="marquee"]';

async function open(page, link) {
  await page.goto(link);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
}


// The live regions in the page once the engine runs: only #activity (the
// start-up screen's own status is gone by then).
const liveRegions = (page) => page.locator(LIVE).evaluateAll((elements) =>
  elements.filter((element) => element.getClientRects().length > 0 || element.id === "activity").map((element) => element.id || element.className));

test("the survey's loading is shown with its label inside the page's one live region", async ({ page }) => {
  const held = Promise.withResolvers();
  await page.route(`**/${links.templateFile}`, async (route) => {
    await held.promise;
    await route.fallback();
  });
  await open(page, links.identified);
  const activity = page.locator("#activity");
  await expect(activity).toHaveAttribute("role", "status");
  await expect(activity).toHaveAttribute("aria-live", "polite");
  const loading = activity.locator("#loading");
  await expect(loading).toBeVisible();
  await expect(loading).not.toHaveAttribute("role");
  await expect(loading).toHaveClass(/(^|\s)echelon-loading(\s|$)/);
  await expect(loading.locator(".forma-spinner .forma-spinner__indicator")).toHaveCount(1);
  await expect(loading.locator(".forma-spinner__indicator")).toHaveAttribute("aria-hidden", "true");
  await expect(loading.locator(".forma-spinner__label")).toBeVisible();
  await expect(loading.locator(".forma-spinner__label")).toHaveText("Loading the survey…");
  expect(await liveRegions(page)).toEqual(["activity"]);
  held.resolve();
  await expect(page.locator("#survey-title")).toHaveText("Signal demo survey");
  await expect(page.locator("#loading")).toHaveCount(0);
});

test("while answering, #activity is the one live region and says what the page shows", async ({ page }) => {
  await open(page, links.identified);
  const activity = page.locator("#activity");
  await expect(page.locator("#progress")).toHaveText("0 of 5 answered");
  await expect(page.locator("#progress")).not.toHaveAttribute("role");
  await expect(activity).toContainText("0 of 5 answered");
  expect(await liveRegions(page)).toEqual(["activity"]);

  await pick(page, "0-3").check();
  await expect(page.locator("#progress")).toHaveText("1 of 5 answered");
  await expect(activity).toContainText("1 of 5 answered");
  expect(await liveRegions(page)).toEqual(["activity"]);
});

// The audit's rule (tests/visual/audit.mjs): three or more lines with fewer
// than four characters a line is a word set as a column of letters. With
// `within`, only text inside elements matching it; with `outside`, only text
// that is not.
const collapsedWords = (page, { within = null, outside = null } = {}) => page.evaluate(({ within, outside }) => {
  const found = [];
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const text = node.textContent.trim();
    const parent = node.parentElement;
    if (!text || !parent || parent.closest("code, pre, svg, template, [hidden], .forma-sr-only, .forma-visually-hidden")) continue;
    if (parent.getClientRects().length === 0) continue;
    if (within && !parent.closest(within)) continue;
    if (outside && parent.closest(outside)) continue;
    const range = document.createRange();
    range.selectNodeContents(node);
    const lines = new Set([...range.getClientRects()].filter((r) => r.width > 0).map((r) => Math.round(r.top))).size;
    const perLine = text.replace(/\s/g, "").length / Math.max(lines, 1);
    if (lines >= 3 && perLine < 4) found.push(`${text.slice(0, 30)} (${lines} lines)`);
  }
  return found;
}, { within, outside });
const sideways = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

async function atWidth(page, width, rootFont, link, ready) {
  await page.setViewportSize({ width, height: 900 });
  await open(page, links[link]);
  await ready(page);
  if (rootFont) await page.evaluate((size) => { document.documentElement.style.fontSize = size; }, rootFont);
}

const pagedReady = (page) => expect(page.locator("#page-label")).toHaveText("Page 1 of 3");
const compoundReady = (page) => expect(page.locator("#survey-title")).toHaveText("Compound kinds");

// The paged survey (scale questions as Forma's ordinal scale, special
// answers, a yes/no and a single choice).
test("the respondent page (a paged survey) keeps whole words and does not scroll sideways at 320 px with 200% text", async ({ page }) => {
  await atWidth(page, 320, "200%", "paged", pagedReady);
  expect(await collapsedWords(page)).toEqual([]);
  expect(await sideways(page)).toBeLessThanOrEqual(0);
  // The forward action is reachable on the narrow screen.
  await expect(page.locator("#page-next, #submit-answers").first()).toBeVisible();
});

// The compound kinds at a phone's width: whole words, nothing sideways.
test("the respondent page (the compound kinds) keeps whole words and does not scroll sideways at 390 px", async ({ page }) => {
  await atWidth(page, 390, null, "compound", compoundReady);
  expect(await collapsedWords(page)).toEqual([]);
  expect(await sideways(page)).toBeLessThanOrEqual(0);
  await expect(page.locator("#submit-answers")).toBeVisible();
});

// Forma's best-worst rows, as the survey page shows them (each is the
// component's row, .forma-best-worst__row; the page has no wrapper around
// them). The EXCEPTION below is about the text inside them alone: the
// options' names and the "Fits best" and "Fits least" column names.
const BEST_WORST = ".forma-best-worst__row";

// The compound kinds at 320 px with 200% text: outside Forma's best-worst
// rows (the EXCEPTION below), every word stays whole, and the page does not
// scroll sideways. The rows are on the page, so the EXCEPTION has something
// to check.
test("the respondent page (the compound kinds) keeps every word outside the best-worst rows whole and does not scroll sideways at 320 px with 200% text", async ({ page }) => {
  await atWidth(page, 320, "200%", "compound", compoundReady);
  await expect(page.locator(`${BEST_WORST}:visible`)).toHaveCount(3);
  expect(await collapsedWords(page, { outside: BEST_WORST })).toEqual([]);
  expect(await sideways(page)).toBeLessThanOrEqual(0);
  await expect(page.locator("#submit-answers")).toBeVisible();
});

// EXCEPTION (owned by Forma, upstream; found by #139's review; the Forma fix
// is WI-0139; recorded in docs/verification/ui-conformance-2026-10-13.md): at
// 320 px with 200% text, Forma 1.0.1's narrow best-worst row
// (.forma-best-worst__row below 44rem: minmax(0, 1fr) then two
// minmax(2.75rem, auto) columns) has too little room for its text: the
// options' names ("Calm", "Busy", "Chaotic") and the column names ("Fits
// best", "Fits least") are set a few letters a line. Signal does not restyle
// Forma (its own override of the row's columns was removed), and no wrapper
// can give the row more than the screen's width. The expected failure covers
// the text inside the rows only, and its one assertion is that check; the
// test above holds the rest of the page and the rows' presence. test.fail
// keeps the defect visible; this test starts failing, and must become an
// ordinary assertion, as soon as the pinned Forma lays the row out at this
// width.
test("EXCEPTION ULI-L05: the text in Forma's best-worst rows collapses at 320 px with 200% text", async ({ page }) => {
  test.fail(true, "Forma 1.0.1 .forma-best-worst__row sets its options' and columns' names a few letters a line at 320 px with 200% text (upstream defect)");
  await atWidth(page, 320, "200%", "compound", compoundReady);
  expect(await collapsedWords(page, { within: BEST_WORST })).toEqual([]);
});

// MUST 1 of #139's review: a scale question is Forma's ordinal scale as
// designed (patterns/ordinal-scale.html), not a stack of special-choice boxes.
test("a scale question is Forma's segmented ordinal scale: numbered points, then its special answers, one radio group", async ({ page }) => {
  await open(page, links.identified);
  const scale = page.locator('[data-row="0"] fieldset');
  await expect(scale).toHaveClass("forma-ordinal-scale forma-ordinal-scale--5");
  await expect(page.getByRole("group", { name: /Major requirements are expected to be substantially defined/ })).toHaveCount(1);
  const points = scale.locator(".forma-ordinal-scale__options .forma-ordinal-option:visible");
  await expect(points).toHaveCount(5);
  // Each marker is the value stored for its point (0 to 4), as the console shows it.
  await expect(points.locator(".forma-ordinal-option__marker")).toHaveText(["0", "1", "2", "3", "4"]);
  await expect(points.locator(".forma-ordinal-option__marker").first()).toHaveAttribute("aria-hidden", "true");
  await expect(points.locator(".forma-ordinal-option__label")).toHaveText(["Never or almost never", "Rarely", "Sometimes", "Often", "Almost always"]);
  // Special answers as Forma provides them, inside the same group.
  const specials = scale.locator(".forma-special-choices .forma-special-choice:visible");
  await expect(specials).toHaveText(["Don't know", "Not observed", "Not applicable"]);
  await expect(scale.locator('input[type="radio"]:visible')).toHaveCount(8);
  expect(new Set(await scale.locator('input[type="radio"]:visible').evaluateAll((inputs) => inputs.map((i) => i.name)))).toEqual(new Set(["q0-scale"]));
  // Its choice rows are not shown as well.
  await expect(page.locator('[data-row="0-0"]')).toBeHidden();
  await expect(page.locator('[data-row="0-0"]')).toContainText("Never or almost never");

  // Pointer and keyboard: a radio group, arrows move the choice.
  await scale.getByRole("radio", { name: "Rarely", exact: true }).check();
  await expect(page.locator("#progress")).toHaveText("1 of 5 answered");
  await scale.getByRole("radio", { name: "Rarely", exact: true }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(scale.getByRole("radio", { name: "Sometimes", exact: true })).toBeChecked();
  await expect(scale.getByRole("radio", { name: "Rarely", exact: true })).not.toBeChecked();
  await expect(page.locator("#progress")).toHaveText("1 of 5 answered");
  await scale.getByRole("radio", { name: "Not applicable", exact: true }).check();
  await expect(scale.getByRole("radio", { name: "Sometimes", exact: true })).not.toBeChecked();
  // The answers survive a reload: they are in the link.
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
  await expect(page.locator('[data-row="0"] fieldset').getByRole("radio", { name: "Not applicable", exact: true })).toBeChecked();
});

// SHOULD of #139's review: a new page announces itself in #activity and
// moves focus to the card's first heading (Next becoming Submit lost it).
test("a new page is announced in #activity and focus moves to its first heading, through to the submitted heading", async ({ page }) => {
  await open(page, links.paged);
  const activity = page.locator("#activity");
  const start = page.locator('#survey-rows [data-focus-target="page-start"]');
  await expect(page.locator("#page-section")).toContainText("Plan Commitment");
  await pick(page, "0-3").check();
  await pick(page, "1-4").check();
  await page.locator("#page-next").click();
  await expect(page.locator("#page-label")).toHaveText("Page 2 of 3");
  await expect(activity).toContainText("Page 2 of 3");
  await expect(activity).toContainText("Plan Commitment");
  await expect(start).toHaveCount(1);
  await expect(start).toBeFocused();
  await expect(start).toHaveText("Plan Commitment");
  await expect(start).toHaveAttribute("tabindex", "-1");
  // A visible ring on the heading, however focus got there.
  expect(await start.evaluate((h) => getComputedStyle(h).outlineStyle)).not.toBe("none");

  // The last page: Next becomes Submit; focus is on the heading, not lost.
  await pick(page, "2-6").check();
  await page.locator("#page-next").click();
  await expect(page.locator("#page-label")).toHaveText("Page 3 of 3");
  await expect(page.locator("#submit-answers")).toBeVisible();
  await expect(start).toHaveText("Working context");
  await expect(start).toBeFocused();
  await expect(activity).toContainText("Page 3 of 3");
  await expect(page.locator("#page-section")).toContainText("Working context");

  await pick(page, "3-1").check();
  await pick(page, "4-2").check();
  await page.locator("#submit-answers").click();
  await expect(page.locator("#submitted-title")).toBeFocused();
  expect(await page.locator("#submitted-title").evaluate((h) => getComputedStyle(h).outlineStyle)).not.toBe("none");
});

// SHOULD of #139's review: opening a page reads no list of guidance;
// #activity says a question's guidance only when the respondent changes it.
test("selection guidance is announced only for the question the respondent changes", async ({ page }) => {
  await open(page, links.kinds);
  const activity = page.locator("#activity");
  const label = (row) => page.locator(`[data-row="${row}"] label:has(input[type="checkbox"])`);
  await label("3-t0").click();
  await expect(page.locator('[data-row="3"] .forma-selection-status')).toHaveText("1 chosen.");
  await expect(activity).toContainText("1 chosen.");
  await label("3-t1").click();
  await expect(activity).toContainText("2 chosen.");
  await expect(activity).not.toContainText("1 chosen.");

  // Reopened from its link, the page shows the guidance where it belongs
  // and announces none of it.
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
  await expect(page.locator('[data-row="3"] .forma-selection-status')).toHaveText("2 chosen.");
  await expect(activity).not.toContainText("chosen");
  expect(await liveRegions(page)).toEqual(["activity"]);
});
