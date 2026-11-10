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

test("SIG-ECHELON-APP-002 (WI-0123): a group's results, scoring and report keep whole words in labelled table regions", async ({ page }) => {
  await signInWithStore(page);
  await createGroup(page);

  await groupTab(page, "Results");
  const exploration = page.locator("#exploration");
  await expect(exploration.getByRole("heading", { level: 3, name: "Exploration" })).toBeVisible();
  const sectionChart = exploration.locator("#section-chart .admin-table-scroll");
  await expectScrollRegion(sectionChart, await page.locator("#section-chart-caption").innerText());
  await expect(sectionChart.locator("table")).toHaveClass(/admin-table--figures/);
  await expectWholeWords(page, "results");

  await groupTab(page, "Scoring");
  const scoring = page.getByRole("region", { name: "Scoring" });
  await expectScrollRegion(scoring.locator(".admin-table-scroll"), "Measures");
  // How the figures were derived: the engine's own statement and lineage.
  const derivation = scoring.locator('[data-echelon-surface="accent"]');
  await expect(derivation.getByRole("heading", { name: "How this was calculated" })).toBeVisible();
  await expect(derivation).toContainText("Calculated measures, not the canonical result.");
  await expect(derivation.locator("#lineage")).not.toBeEmpty();
  await expectWholeWords(page, "scoring");

  await groupTab(page, "Report");
  await expect(page.locator('[data-echelon-emphasis="primary"]:visible')).toHaveCount(1);
  await expect(page.getByRole("button", { name: "Take a snapshot", exact: true })).toHaveAttribute("data-echelon-emphasis", "primary");
  const sections = page.locator("#report-sections").locator("xpath=..");
  if (await sections.count()) await expectScrollRegion(sections, await page.locator("#report-section-results-title").innerText());
  await expectWholeWords(page, "report");
});
