// Published sample links on a phone (AUT-006 §§61-62): each demo survey the
// site publishes opens from its link, fits the screen without sideways
// scrolling, and is answered and submitted by touch.
import { readFileSync } from "node:fs";
import { test, expect } from "./support.js";

const links = JSON.parse(readFileSync(new URL("./fixtures/survey-links.json", import.meta.url), "utf8"));

async function open(page, link) {
  await page.goto(link);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
}

const fitsTheScreen = (page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);

for (const name of ["identified", "kinds", "compound", "paged", "banked", "timed"]) {
  test(`the ${name} sample link opens on a phone and fits the screen`, async ({ page }) => {
    await open(page, links[name]);
    await expect(page.locator("#questions-title")).toBeVisible();
    await expect(page.locator("#survey-rows .survey-row").first()).toBeVisible();
    expect(await fitsTheScreen(page)).toBe(true);
  });
}

test("a survey is answered and submitted by touch on a phone", async ({ page }) => {
  await open(page, links.identified);
  for (const row of ["0-3", "1-4", "2-6", "3-1", "4-2"]) await page.locator(`[data-row="${row}"] input[data-event="answered"]`).tap();
  await expect(page.locator("#progress")).toHaveText("5 of 5 answered");
  await page.locator("#submit-answers").tap();
  await expect(page.locator("#submitted-title")).toHaveText("Submitted");
  expect(await fitsTheScreen(page)).toBe(true);
});
