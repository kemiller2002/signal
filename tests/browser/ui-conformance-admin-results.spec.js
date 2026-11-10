// A group's results, scoring and report in the "foundry ledger" direction
// (SIG-ECHELON-APP-002 (e), WI-0123) against the test-only fake GitHub store.
// Their figures keep whole words and numbers on a narrow screen: each wide
// table scrolls in a labelled, focusable region, and the report's tables turn
// into named cards, instead of setting "Mean", "Section" or "75.0" a few
// letters a line (the capture's audit found them at
// 390 px and at 320 px with 200% text). Scoring says how its figures were
// derived; the report view has one primary action.
import { test, expect } from "./support.js";
import { STORE_STEP_MS, answerAll, createGroup, open, signInWithStore } from "./ui-conformance-fixtures.mjs";

test.describe.configure({ timeout: 300_000 });

const groupTab = (page, name) => page.getByRole("navigation", { name: "Group" }).getByRole("link", { name, exact: true }).click();

// The audit's rule (tests/visual/audit.mjs): three or more lines with fewer
// than four characters a line is a word set as a column of letters.
const collapsed = (page) => page.evaluate(() => {
  const found = [];
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const text = node.textContent.trim();
    const parent = node.parentElement;
    if (!text || !parent || parent.closest("code, pre, svg, template, [hidden], .forma-sr-only, .forma-visually-hidden")) continue;
    if (parent.getClientRects().length === 0) continue;
    const range = document.createRange();
    range.selectNodeContents(node);
    const lines = new Set([...range.getClientRects()].filter((r) => r.width > 0).map((r) => Math.round(r.top))).size;
    const perLine = text.replace(/\s/g, "").length / Math.max(lines, 1);
    if (parent.getBoundingClientRect().width >= 2 && lines >= 3 && perLine < 4) found.push(`${text.slice(0, 30)} (${lines} lines)`);
  }
  return found;
});

async function expectWholeWords(page, label) {
  for (const width of [834, 390]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await collapsed(page), `${label} at ${width} px`).toEqual([]);
  }
  await page.setViewportSize({ width: 320, height: 900 });
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  expect(await collapsed(page), `${label} at 320 px with 200% text`).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth), `${label} overflow`).toBeLessThanOrEqual(0);
  await page.evaluate(() => { document.documentElement.style.removeProperty("font-size"); });
  await page.setViewportSize({ width: 1280, height: 900 });
}

async function expectScrollRegion(region, name) {
  await expect(region).toHaveAttribute("role", "region");
  await expect(region).toHaveAttribute("tabindex", "0");
  await expect(region).toHaveAccessibleName(name);
}

test("SIG-ECHELON-APP-002 (WI-0123): a group's results, scoring and report keep whole words, by name, in the foundry ledger's composition", async ({ page }) => {
  await signInWithStore(page);
  await createGroup(page);

  await groupTab(page, "Results");
  const exploration = page.locator("#exploration");
  await expect(exploration.getByRole("heading", { level: 3, name: "Section means" })).toBeVisible();
  // Each section by its template name (signal#140 review), with a bar for the
  // engine's value and the value as text; no section ids, no overall score.
  const rows = exploration.locator("#section-chart .admin-means__row");
  await expect(rows.locator(".admin-means__name")).toHaveText(["Plan Commitment", "Change Responsiveness", "Timebox Discipline"]);
  await expect(exploration).not.toContainText(/\bD0[1-3]\b/);
  await expect(exploration).not.toContainText(/overall/i);
  // createGroup imports nothing: the group has no accepted responses, so the
  // engine shows no value, and each row says why instead of drawing a bar
  // (never zero). With imported responses the caption names the highest and
  // lowest section (ResultsProjectionTests; the capture's admin-group-results).
  await expect(page.locator("#section-chart-caption")).toHaveText("Section means: no values can be shown.");
  await expect(rows.locator("meter:visible")).toHaveCount(0);
  for (const value of await rows.locator(".admin-means__value").allInnerTexts()) expect(value.trim()).not.toBe("");
  // The figure fills its panel: no default <figure> inset.
  const inset = await page.locator("#section-chart").evaluate((figure) => {
    const body = figure.parentElement.getBoundingClientRect();
    const box = figure.getBoundingClientRect();
    const style = getComputedStyle(figure.parentElement);
    return Math.abs(box.width - (body.width - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight)));
  });
  expect(inset, "the figure is as wide as its panel's body").toBeLessThanOrEqual(1);
  await expectWholeWords(page, "results");

  await groupTab(page, "Scoring");
  const scoring = page.getByRole("region", { name: "Scoring" });
  await expectScrollRegion(scoring.locator(".admin-table-scroll"), "Measures");
  // Measures and sections by their labels; no engine identifier is shown.
  const measures = scoring.locator("#scoring-measures tbody");
  await expect(measures).toContainText("Standard deviation");
  await expect(measures).toContainText("95% confidence interval, lower bound");
  await expect(scoring).not.toContainText(/StandardDeviation|InterquartileRange|ConfidenceLower95|ConfidenceUpper95/);
  await expect(scoring.locator("#scoring-sections")).toContainText("Plan Commitment");
  // How the figures were derived: the engine's own statement and lineage.
  const derivation = scoring.locator('[data-echelon-surface="accent"]');
  await expect(derivation.getByRole("heading", { name: "How this was calculated" })).toBeVisible();
  await expect(derivation).toContainText("Calculated measures, not the canonical result.");
  await expect(derivation.locator("#lineage")).not.toBeEmpty();
  await expectWholeWords(page, "scoring");

  await groupTab(page, "Report");
  await expect(page.locator('[data-echelon-emphasis="primary"]:visible')).toHaveCount(1);
  await expect(page.getByRole("button", { name: "Take a snapshot", exact: true })).toHaveAttribute("data-echelon-emphasis", "primary");
  // The report's figures as a definition grid, not a run of lines.
  await expect(page.locator("#report-counts")).toHaveCSS("display", "grid");
  // An empty group has no section rows to show: said explicitly, so the
  // cards below are checked on a group with data, never skipped.
  await expect(page.locator("#report-sections tbody tr")).toHaveCount(0);
  await expectWholeWords(page, "report");
});

// With one imported response (an invitation answered and submitted): the
// section means are drawn by name, and on a narrow screen the report's rows
// are cards that name their figures, with nothing scrolling sideways.
test("SIG-ECHELON-APP-002 (WI-0123): with an imported response, the means are drawn by name and the report's rows are named cards at 390 px", async ({ page, context }) => {
  await signInWithStore(page);
  await createGroup(page, { expected: "2" });
  await page.getByRole("link", { name: "Imported surveys" }).click();
  const invitations = page.locator("#invitations");
  await invitations.getByRole("spinbutton", { name: "How many links" }).fill("1");
  await invitations.getByRole("spinbutton", { name: "How many links" }).press("Tab");
  await invitations.getByRole("button", { name: "Create links" }).click();
  await expect(page.locator("#invitation-summary")).toHaveText(/1 link\(s\) issued/, { timeout: STORE_STEP_MS });
  const [link] = await page.locator("#invitation-links code").allTextContents();
  const respondent = await context.newPage();
  await open(respondent, link);
  await answerAll(respondent, "Often");
  await respondent.getByRole("button", { name: "See results" }).click();
  await respondent.getByRole("button", { name: "Submit answers" }).click();
  const submission = await respondent.getByRole("textbox", { name: "Submission link" }).inputValue();
  await respondent.close();
  const imports = page.locator("#import");
  await imports.getByRole("textbox", { name: "Completed survey links, one per line" }).fill(submission);
  await imports.getByRole("button", { name: "Import" }).click();
  await expect(page.locator("#importAccepted li")).toHaveCount(1, { timeout: STORE_STEP_MS });

  await groupTab(page, "Results");
  const rows = page.locator("#section-chart .admin-means__row");
  await expect(rows.locator(".admin-means__name")).toHaveText(["Plan Commitment", "Change Responsiveness", "Timebox Discipline"]);
  await expect(rows.locator("meter:visible")).toHaveCount(3);
  await expect(rows.locator(".admin-means__value")).toHaveText(["75.0", "75.0", "75.0"]);
  await expect(page.locator("#section-chart-caption")).toContainText("Plan Commitment");

  await groupTab(page, "Report");
  const sections = page.locator(".report-table").filter({ has: page.locator("#report-sections") });
  const table = page.getByRole("table", { name: await page.locator("#report-section-results-title").innerText() });
  const body = sections.locator("tbody tr");
  await expect(body).toHaveCount(3);
  // signal#140 review: the cards' grid and `display: contents` would drop
  // the table's semantics, so its roles are explicit; at every width it is
  // still a table of a header row and three section rows, each a row header
  // and five cells. It never scrolls sideways, so its box is not a
  // focusable region at any width. A section title that is one unbroken
  // 75-character word (signal#142 review: it pushed the page 114 to 274 px
  // sideways at 900 to 740 px) breaks inside the table instead.
  const unbroken = "A".repeat(75);
  const secondTitle = table.getByRole("rowheader").nth(1);
  const overflow = () => page.evaluate(() => {
    const box = document.querySelector(".report-table:has(#report-sections)");
    return [box.scrollWidth - box.clientWidth, document.documentElement.scrollWidth - document.documentElement.clientWidth];
  });
  for (const [width, text] of [[1280, "100%"], [900, "100%"], [834, "100%"], [800, "100%"], [740, "100%"], [390, "100%"], [320, "200%"]]) {
    await page.setViewportSize({ width, height: 900 });
    await page.evaluate((size) => { document.documentElement.style.fontSize = size; }, text);
    await expect(table.getByRole("row")).toHaveCount(4);
    await expect(table.getByRole("columnheader")).toHaveCount(6);
    await expect(table.getByRole("rowheader")).toHaveText(["D01 Plan Commitment", "D02 Change Responsiveness", "D03 Timebox Discipline"]);
    await expect(table.getByRole("cell")).toHaveCount(15);
    expect(Math.max(...(await overflow())), `report table at ${width} px, ${text} text`).toBeLessThanOrEqual(0);
    const title = await secondTitle.evaluate((th, word) => { const was = th.textContent; th.textContent = word; return was; }, unbroken);
    expect(Math.max(...(await overflow())), `a 75-character title at ${width} px, ${text} text`).toBeLessThanOrEqual(0);
    await secondTitle.evaluate((th, was) => { th.textContent = was; }, title);
  }
  await page.evaluate(() => { document.documentElement.style.removeProperty("font-size"); });
  await expect(page.locator(".report-table[tabindex], .report-table[role]")).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 900 });
  // Every figure in every row names its column, in the report's language.
  for (const row of await body.all()) {
    const labels = await row.locator("td").evaluateAll((cells) => cells.map((cell) => cell.getAttribute("data-label")));
    expect(labels).toEqual(["Mean", "Median", "Range", "Scored", "Not scored"]);
  }
  // The column headings are not shown as a row to be cut off; each card's
  // section name is its heading.
  await expect(sections.locator("thead")).toHaveCSS("position", "absolute");
  // A card's heading starts where its figures do: the card's padding is its
  // only inset (signal#140 review).
  for (const row of await body.all()) {
    const inset = await row.evaluate((tr) => {
      const range = document.createRange();
      range.selectNodeContents(tr.querySelector("th"));
      const style = getComputedStyle(tr);
      return range.getClientRects()[0].left - (tr.getBoundingClientRect().left + parseFloat(style.borderLeftWidth) + parseFloat(style.paddingLeft));
    });
    expect(Math.abs(inset)).toBeLessThanOrEqual(1);
  }
  // A Recommendations block with nothing to recommend says so, not a heading
  // over nothing (signal#140 review).
  await expect(page.locator("#report-recommendations-none")).toHaveText("None");
  await expect(page.locator("#report-recommendations")).toBeHidden();
  await page.setViewportSize({ width: 1280, height: 900 });
  await expect(sections.locator("thead")).not.toHaveCSS("position", "absolute");

  // The spread of results has its own header row, in the report's language
  // (signal#142 review: its median and range were unnamed at wide sizes).
  await page.evaluate(() => { location.hash = location.hash.split("?")[0] + "?family=audit"; });
  const spread = page.getByRole("table", { name: "Spread of results" });
  await expect(spread.getByRole("columnheader")).toHaveText(["Section", "Median", "Range"]);
  await expect(spread.getByRole("rowheader")).toHaveCount(3);
  await expect(spread.getByRole("cell")).toHaveCount(6);
});
