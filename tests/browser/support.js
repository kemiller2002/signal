// Shared harness for the browser suite: the page under test, loaded until
// Limen's kernel is running, and a guard that fails the test on any console
// error or page error, since Limen reports a broken engine to the console
// rather than throwing.
import { test as base, expect } from "@playwright/test";

// Console errors a test expects, by page: the Aegis fault record a test
// provoked.
const expected = new WeakMap();
export function expectConsoleError(page, pattern) {
  expected.set(page, [...(expected.get(page) ?? []), pattern]);
}

export const test = base.extend({
  // Fails the test on any unexpected console error or uncaught page error.
  page: async ({ page }, use) => {
    const problems = [];
    page.on("console", (message) => {
      if (message.type() !== "error") return;
      const text = message.text();
      if (!(expected.get(page) ?? []).some((pattern) => pattern.test(text))) problems.push(`console.error: ${text}`);
    });
    page.on("pageerror", (error) => problems.push(`pageerror: ${error.message}`));
    await use(page);
    expect(problems, "the page reported errors").toEqual([]);
  },

  assessment: async ({ page }, use) => {
    await page.goto("/web/index.html");
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
    await use(page);
  }
});

export { expect };

// The radio for one answer of one item.
export const choice = (page, itemId, value) =>
  page.locator(`section[data-item="${itemId}"] input[type="radio"][value="${value}"]`);

// The rendered results as plain rows: [dimension, score, coverage, note].
export async function resultRows(page) {
  return page.locator("#results-body tr").evaluateAll((rows) =>
    rows.map((row) => Array.from(row.querySelectorAll("td")).map((cell) => cell.textContent.trim())));
}
