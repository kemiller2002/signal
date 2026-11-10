// A group's results, scoring and report in the "foundry ledger" direction
// (SIG-ECHELON-APP-002 (e), WI-0123) against the test-only fake GitHub store.
// Their figures keep whole words and numbers on a narrow screen: each table
// scrolls in a labelled, focusable region instead of setting "Mean",
// "Section" or "75.0" a few letters a line (the capture's audit found them at
// 390 px and at 320 px with 200% text). Scoring says how its figures were
// derived; the report view has one primary action.
import { test, expect } from "./support.js";
import { createGroup, signInWithStore } from "./ui-conformance-fixtures.mjs";

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
  const sections = page.locator(".report-table").filter({ has: page.locator("#report-sections") });
  if (await sections.count()) {
    await expectScrollRegion(sections, await page.locator("#report-section-results-title").innerText());
    // At 390 px each row is a card naming its figures: nothing scrolls
    // sideways inside the region, and no heading is cut off.
    await page.setViewportSize({ width: 390, height: 900 });
    expect(await sections.evaluate((region) => region.scrollWidth - region.clientWidth)).toBeLessThanOrEqual(0);
    const first = sections.locator("tbody tr").first();
    await expect(first.locator("td").first()).toHaveAttribute("data-label", /\S/);
    await page.setViewportSize({ width: 1280, height: 900 });
  }
  await expectWholeWords(page, "report");
});
