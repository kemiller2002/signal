// The longest respondent link a published template can have (AUT-006 §62,
// CAN-004 §26): it opens, is answered, survives a reload and submits, on
// every engine the suite runs (Chromium, and WebKit and Firefox in the
// cross-browser workflow). Once it passes on WebKit and Firefox, the
// 2,000-character URL budget is no longer provisional.
import { readFileSync } from "node:fs";
import { test, expect } from "./support.js";

const links = JSON.parse(readFileSync(new URL("./fixtures/max-link.json", import.meta.url), "utf8"));

test("the longest link opens, keeps its answers across a reload, and submits", async ({ page }) => {
  await page.goto(links.maxLink);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("#survey-title")).toHaveText("The longest link");
  expect(page.url().length).toBeGreaterThan(1750);

  // A complete ranking of the first question, positions 1 to 8. (Out-of-order
  // and partial rankings are a known defect from #73, fixed separately.)
  const position = (n) => page.locator(`[data-row="0-n${n}"] input[type="number"]`);
  for (let n = 0; n < 8; n++) {
    await position(n).fill(String(n + 1));
    await position(n).press("Tab");
  }
  await expect.poll(() => page.url()).not.toContain(links.maxLink.split("#r=")[1]);
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  for (let n = 0; n < 8; n++) await expect(position(n)).toHaveValue(String(n + 1));

  await page.locator("#page-next").click();
  await expect(page.locator("#page-label")).toContainText("Page 2 of");
  // Every question is optional: the response submits from the last page.
  while (await page.locator("#page-next").count()) await page.locator("#page-next").click();
  await page.locator("#submit-answers").click();
  await expect(page.locator("#submitted-title")).toHaveText("Submitted");
  expect((await page.locator("#submission-link").inputValue()).length).toBeGreaterThan(1750);
});
