// The respondent's page in the "foundry ledger" direction (SIG-ECHELON-APP-002
// (d)): one live region (#activity, as #133 made it for the console) that
// announces loading, progress, review, timers, selection guidance, link
// notices and the copy outcome, while each is shown, not live, where it
// belongs; and a layout that keeps whole words at 320 px with 200% text.
import { readFileSync } from "node:fs";
import { test, expect } from "./support.js";

const links = JSON.parse(readFileSync(new URL("./fixtures/survey-links.json", import.meta.url), "utf8"));

const KERNEL_START_MS = 30_000;
const LIVE = '[aria-live], [role="status"], [role="log"], [role="timer"], [role="marquee"]';

async function open(page, link) {
  await page.goto(link);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
}

const pick = (page, row) => page.locator(`[data-row="${row}"] input[data-event="answered"]`);

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

test("the respondent page keeps whole words and does not scroll sideways at 320 px with 200% text", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 900 });
  await open(page, links.paged);
  await expect(page.locator("#page-label")).toHaveText("Page 1 of 3");
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  // The audit's rule (tests/visual/audit.mjs): three or more lines with fewer
  // than four characters a line is a word set as a column of letters.
  const collapsed = await page.evaluate(() => {
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
      if (lines >= 3 && perLine < 4) found.push(`${text.slice(0, 30)} (${lines} lines)`);
    }
    return found;
  });
  expect(collapsed).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  // Back and the one forward action are both reachable on the narrow screen.
  await expect(page.locator("#page-next")).toBeVisible();
});
