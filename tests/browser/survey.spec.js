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

// Each row holds every input kind, hidden but the one it shows; name the input by its event.
const pick = (page, row) => page.locator(`[data-row="${row}"] input[data-event="answered"]`);

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

test("a test link says it is a test, and what it submits stays marked", async ({ page }) => {
  await open(page, links.test);
  await expect(page.locator("#test-notice")).toContainText("Test link.");
  for (const row of ["0-0", "1-0", "2-0", "3-0", "4-0"]) await pick(page, row).check();
  await page.locator("#submit-answers").click();
  await expect(page.locator("#submitted-title")).toHaveText("Submitted");
  await expect(page.locator("#test-notice")).toBeVisible();
  // The kind byte's test marker survives submission: 0x82 (identified, test) after the version byte.
  const link = await page.locator("#submission-link").inputValue();
  const payload = link.split("#r=")[1].replaceAll("-", "+").replaceAll("_", "/");
  const bytes = Buffer.from(payload, "base64");
  expect(bytes[1]).toBe(0x82);
});

test("other answer kinds and a conditional question work on the page", async ({ page }) => {
  await open(page, links.kinds);
  await expect(page.locator("#survey-title")).toHaveText("Mixed kinds");
  // Q3 is shown only when Q2 is "Yes".
  await expect(page.locator('[data-row="2"]')).toHaveCount(0);
  await expect(page.locator("#progress")).toHaveText("0 of 5 answered");
  await pick(page, "1-1").check();
  await expect(page.locator('[data-row="2"]')).toContainText("How did it ship?");
  await expect(page.locator("#progress")).toHaveText("1 of 6 answered");

  const toggle = (row) => page.locator(`[data-row="${row}"] input[type="checkbox"]`);
  await toggle("3-t0").check();
  await toggle("3-t2").check();
  await toggle("3-t2").uncheck();
  await expect(toggle("3-t0")).toBeChecked();
  await expect(toggle("3-t2")).not.toBeChecked();

  await expect(page.locator('[data-row="4-4"]')).toContainText("5");
  for (const row of ["0-2", "2-1", "4-4", "5-3"]) await pick(page, row).check();
  await expect(page.locator("#progress")).toHaveText("6 of 6 answered");
  await page.locator("#submit-answers").click();
  await expect(page.locator("#submitted-title")).toHaveText("Submitted");
});

test("an expired invitation says so and cannot be filled in", async ({ page }) => {
  await open(page, links.expired);
  await expect(page.locator("#survey-refused-message")).toContainText("This invitation has expired.");
  await expect(page.locator("#questions")).toHaveCount(0);
});

test("an invitation's locale sets the page's language, and stays in the link", async ({ page }) => {
  await open(page, links.localized);
  await expect(page.locator("main")).toHaveAttribute("lang", "fr-CA");
  await pick(page, "3-1").check();
  // Still link format 2 (first byte 2) after the address follows the answer.
  await expect.poll(() => Buffer.from(new URL(page.url()).hash.split("#r=")[1].replaceAll("-", "+").replaceAll("_", "/"), "base64")[0]).toBe(2);
});

test("rankings, allocations, best-worst, ranges and option trees work on the page", async ({ page }) => {
  await open(page, links.compound);
  await expect(page.locator("#survey-title")).toHaveText("Compound kinds");
  const number = (row) => page.locator(`[data-row="${row}"] input[type="number"]`);
  const type = async (row, value) => { await number(row).fill(value); await number(row).press("Tab"); };

  // Ranking: positions 1..3; allocation: 10 points.
  for (const [row, value] of [["0-n0", "2"], ["0-n1", "1"], ["0-n2", "3"], ["1-n0", "5"], ["1-n1", "3"], ["1-n2", "2"]]) await type(row, value);
  await expect(number("0-n1")).toHaveValue("1");
  // Best and least on two scales.
  await page.locator('[data-row="2-p0"] input[data-event="pickedBest"]').check();
  await page.locator('[data-row="2-p2"] input[data-event="pickedWorst"]').check();
  // Range ends, then a leaf of the tree and an area.
  await type("3-n0", "2");
  await type("3-n1", "6");
  await expect(page.locator('[data-row="4-0"]')).toContainText("— Backend");
  await pick(page, "4-0").check();
  await page.locator('[data-row="5-t1"] input[type="checkbox"]').check();

  await expect(page.locator("#progress")).toHaveText("6 of 6 answered");
  await page.locator("#submit-answers").click();
  await expect(page.locator("#submitted-title")).toHaveText("Submitted");
});

test("a survey in pages moves with Back and Next, and a section left behind stays locked", async ({ page }) => {
  await open(page, links.paged);
  await expect(page.locator("#survey-title")).toHaveText("Signal demo survey in pages");
  await expect(page.locator("#page-label")).toHaveText("Page 1 of 3");
  await expect(page.locator("#page-back")).toHaveCount(0);
  await expect(page.locator("#submit-answers")).toHaveCount(0);
  // Only this page's questions are on it.
  await expect(page.locator('[data-row="2"]')).toHaveCount(0);

  // Next first needs this page's required answers.
  await page.locator("#page-next").click();
  await expect(page.locator("#submit-refusal-message")).toContainText("2 required questions on this page need an answer");
  await pick(page, "0-3").check();
  await pick(page, "1-4").check();
  await page.locator("#page-next").click();
  await expect(page.locator("#page-label")).toHaveText("Page 2 of 3");

  // Back within the section, then on again.
  await page.locator("#page-back").click();
  await expect(page.locator("#page-label")).toHaveText("Page 1 of 3");
  await expect(pick(page, "0-3")).toBeChecked();
  await page.locator("#page-next").click();
  await pick(page, "2-6").check();
  await page.locator("#page-next").click();

  // The first section is behind its lock: no Back from here.
  await expect(page.locator("#page-label")).toHaveText("Page 3 of 3");
  await expect(page.locator("#page-back")).toHaveCount(0);
  await pick(page, "3-1").check();
  await pick(page, "4-2").check();
  await expect(page.locator("#progress")).toHaveText("5 of 5 answered");

  // Reopening the link resumes on the last answered page, still locked.
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("#page-label")).toHaveText("Page 3 of 3");
  await expect(page.locator("#page-back")).toHaveCount(0);

  await page.locator("#submit-answers").click();
  await expect(page.locator("#submitted-title")).toHaveText("Submitted");
});

test("the page is in the link: browser Back and Forward move pages, and a link cannot skip a page", async ({ page }) => {
  const pageParam = () => new URLSearchParams(new URL(page.url()).hash.slice(1)).get("p");

  // A cold link naming page 3 with nothing answered opens page 1, and says so.
  await open(page, `${links.paged}&p=3`);
  await expect(page.locator("#page-label")).toHaveText("Page 1 of 3");
  await expect.poll(pageParam).toBe(null);

  await pick(page, "0-3").check();
  await pick(page, "1-4").check();
  await page.locator("#page-next").click();
  await expect(page.locator("#page-label")).toHaveText("Page 2 of 3");
  await expect.poll(pageParam).toBe("2");
  await pick(page, "2-6").check();

  // Browser Back: page 1 again, with every answer held now (page 2's too).
  await page.goBack();
  await expect(page.locator("#page-label")).toHaveText("Page 1 of 3");
  await expect(pick(page, "0-3")).toBeChecked();
  await expect(page.locator("#progress")).toHaveText("3 of 5 answered");
  await page.goForward();
  await expect(page.locator("#page-label")).toHaveText("Page 2 of 3");
  await expect(pick(page, "2-6")).toBeChecked();

  // Past the section lock, browser Back cannot reopen the locked section.
  await page.locator("#page-next").click();
  await expect(page.locator("#page-label")).toHaveText("Page 3 of 3");
  await page.goBack();
  await expect(page.locator("#page-label")).toHaveText("Page 3 of 3");
  await expect.poll(pageParam).toBe("3");
});

test("a bank shows each identified respondent their own selection, and only that is required", async ({ page }) => {
  await open(page, links.banked);
  // Two of the three banked questions, then the rest of the survey.
  const banked = page.locator('[data-row="0"], [data-row="1"], [data-row="2"]');
  await expect(banked).toHaveCount(2);
  await expect(page.locator("#progress")).toHaveText("0 of 4 answered");

  for (const row of ["0", "1", "2"]) {
    if (await page.locator(`[data-row="${row}"]`).count()) await pick(page, `${row}-3`).check();
  }
  await pick(page, "3-1").check();
  await pick(page, "4-2").check();
  await expect(page.locator("#progress")).toHaveText("4 of 4 answered");

  // The same link opens the same selection.
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(banked).toHaveCount(2);
  await page.locator("#submit-answers").click();
  await expect(page.locator("#submitted-title")).toHaveText("Submitted");
});

test("a timed survey's link gains its start minute on first open, and keeps it on reload", async ({ page }) => {
  const formatOf = () => Buffer.from(new URL(page.url()).hash.split("#r=")[1].split("&")[0].replaceAll("-", "+").replaceAll("_", "/"), "base64")[0];
  await open(page, links.timed);
  await expect(page.locator("#timer-notice")).toContainText("Answer by");
  await expect(page.locator("#timer-notice")).toContainText("10 minutes from when you first opened this survey");
  // Format 2: the start is in the link, inside its integrity check.
  await expect.poll(formatOf).toBe(2);
  const stamped = new URL(page.url()).hash;
  await pick(page, "0-3").check();
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(pick(page, "0-3")).toBeChecked();
  // The same start: only the answers moved.
  const startOf = (hash) => Buffer.from(hash.split("#r=")[1].split("&")[0].replaceAll("-", "+").replaceAll("_", "/"), "base64").subarray(43, 47).toString("hex");
  expect(startOf(new URL(page.url()).hash)).toBe(startOf(stamped));
});

test("an enforced timer that ran out holds the answers, and an edited start is refused", async ({ page }) => {
  await open(page, links.timedExpired);
  await expect(page.locator("#timer-notice")).toContainText("Time is up");
  await expect(pick(page, "0-3")).toBeDisabled();

  // Moving the start later by hand breaks the link's integrity check.
  const [path, payload] = links.timedExpired.split("#r=");
  const bytes = Buffer.from(payload.replaceAll("-", "+").replaceAll("_", "/"), "base64");
  bytes[46] = bytes[46] + 1;
  const edited = bytes.toString("base64").replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
  await page.goto(`${path}#r=${edited}`);
  await expect(page.locator("#survey-refused-message")).toBeVisible();
  await expect(page.locator("#timer-notice")).toHaveCount(0);
});
