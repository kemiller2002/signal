// The survey page and the site's static template catalog (DF-SIGNAL-2026-0005):
// a link names a published version, the page fetches that one file from its
// own origin and uses it only if it is the canonical template the link names.
// A missing or altered file is refused, with no other survey in its place.
// Runs against the checkout and (project "pages") the assembled Pages site.
import { readFileSync } from "node:fs";
import { test, expect, expectConsoleError } from "./support.js";

const links = JSON.parse(readFileSync(new URL("./fixtures/survey-links.json", import.meta.url), "utf8"));

// Every request the page makes, so a test can prove none leaves the origin.
function requests(page) {
  const seen = [];
  page.on("request", (request) => seen.push(request.url()));
  return seen;
}

async function open(page, link) {
  await page.goto(link);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
}

const pick = (page, row) => page.locator(`[data-row="${row}"] input[type="radio"]`);

test("a published survey is fetched from the site, answered and submitted", async ({ page }) => {
  const seen = requests(page);
  const catalog = page.waitForResponse((response) => response.url().endsWith(links.templateFile));
  await open(page, links.identified);
  expect((await catalog).status()).toBe(200);

  await expect(page.locator("#survey-title")).toHaveText("Signal demo survey");
  await expect(page.locator("#progress")).toHaveText("0 of 5 answered");
  await expect(page.locator('[data-row="3"]')).toContainText("The team released working software in the last eight weeks.");

  // Three five-point questions, a yes/no and a single choice.
  for (const row of ["0-3", "1-4", "2-6", "3-1", "4-2"]) await pick(page, row).check();
  await expect(page.locator("#progress")).toHaveText("5 of 5 answered");
  // The address follows the answers (LURL-001), with no new history entry.
  await expect.poll(() => new URL(page.url()).hash).not.toBe(new URL(links.identified, "http://x/").hash);

  await page.locator("#submit-answers").click();
  await expect(page.locator("#submitted-title")).toHaveText("Submitted");
  await expect(page.locator("#submission-link")).toHaveValue(/\/web\/survey\/#r=[A-Za-z0-9_-]+$/);

  const origin = new URL(page.url()).origin;
  expect(seen.filter((url) => !url.startsWith(origin) && !url.startsWith("data:"))).toEqual([]);
});

test("an altered survey file is refused, and nothing else is shown", async ({ page }) => {
  await page.route(`**/${links.templateFile}`, async (route) => {
    const original = await (await route.fetch()).text();
    await route.fulfill({ status: 200, contentType: "application/json", body: original.replace("In an office", "In an 0ffice") });
  });
  await open(page, links.identified);
  await expect(page.locator("#survey-refused-message")).toContainText("does not match this link");
  await expect(page.locator("#survey-title")).toHaveText("Survey unavailable");
  await expect(page.locator("#questions")).toHaveCount(0);
});

test("a damaged survey file is refused", async ({ page }) => {
  await page.route(`**/${links.templateFile}`, async (route) => {
    const original = await (await route.fetch()).text();
    await route.fulfill({ status: 200, contentType: "application/json", body: original + " " });
  });
  await open(page, links.identified);
  await expect(page.locator("#survey-refused-message")).toContainText("damaged or not in its published form");
  await expect(page.locator("#questions")).toHaveCount(0);
});

test("a version the site does not publish is refused", async ({ page }) => {
  // The browser logs the 404 itself.
  expectConsoleError(page, /404/);
  await open(page, links.unpublished);
  await expect(page.locator("#survey-refused-message")).toContainText("is not published on this site");
  await expect(page.locator("#questions")).toHaveCount(0);
});

test("a page opened without a link asks for one and fetches nothing", async ({ page }) => {
  const seen = requests(page);
  await open(page, "web/survey/");
  await expect(page.locator("#survey-refused-message")).toContainText("This page needs a survey link");
  expect(seen.filter((url) => url.includes("published-templates"))).toEqual([]);
});
