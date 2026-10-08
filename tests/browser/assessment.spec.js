// The assessment page (web/) end to end: DOM event -> Limen kernel -> WASM
// shim -> F# engine -> view -> DOM.
import { test, expect, choice, resultRows } from "./support.js";

const ITEMS = Array.from({ length: 15 }, (_, i) => `CORE-${String(i + 1).padStart(3, "0")}`);

async function answer(page, itemId, value) {
  await choice(page, itemId, value).check();
  await expect(choice(page, itemId, value)).toBeChecked();
}

test("the page says plainly that its address contains the answers", async ({ assessment: page }) => {
  await expect(page.locator("#link-disclosure")).toContainText("This page's address contains your answers");
});

test("the page runs on Limen, Forma and Folio from the pinned packages", async ({ assessment: page }) => {
  await expect(page.getByRole("heading", { level: 1, name: "Software Delivery Reality Assessment" })).toBeVisible();
  await expect(page.locator("#assessment-version")).toHaveText("SDRA 0.1.0-draft");

  // Forma: the stylesheet is the installed package's, and its tokens and
  // component rules are what style the page.
  const sheets = await page.evaluate(() =>
    Array.from(document.styleSheets).flatMap((sheet) =>
      Array.from(sheet.cssRules).filter((rule) => rule instanceof CSSImportRule).map((rule) => rule.href)));
  expect(sheets).toEqual(expect.arrayContaining([
    "../node_modules/@echelon-foundry/design-system/dist/all.css",
    "../node_modules/@echelon-foundry/print-components/src/styles/print.css"
  ]));
  const token = await page.evaluate(() =>
    getComputedStyle(document.documentElement).getPropertyValue("--ef-color-accent-primary").trim());
  expect(token).not.toBe("");
  // A plain <span> is inline; Forma's question-number rule lays it out.
  await expect(page.locator(".ef-question__number").first()).not.toHaveCSS("display", "inline");

  // Folio: its print elements are registered (upgraded), not unknown tags.
  expect(await page.evaluate(() => ["ef-print-document", "ef-print-table", "ef-print-page-number"]
    .every((name) => customElements.get(name) !== undefined))).toBe(true);

  // Forma's authoring wrappers stay inert: never registered as elements.
  expect(await page.evaluate(() => customElements.get("ef-data-grid"))).toBeUndefined();

  await expect(page.locator("section.ef-question")).toHaveCount(15);
  await expect(page.locator("#progress")).toHaveText("0 of 15 answered");
});

test("asking for results too early is refused with an ordinary alert", async ({ assessment: page }) => {
  await answer(page, "CORE-001", "3");
  await page.click("#see-results");
  await expect(page.locator("#refusal-message")).toHaveText(/^14 questions still need an answer\./);
  await expect(page.locator("#operational-fault")).toHaveCount(0);
  await expect(page.locator("#results")).toHaveCount(0);
});

test("answering every question shows scored results, and missing data is not zero", async ({ assessment: page }) => {
  for (const item of ITEMS) await answer(page, item, "3");
  // D02: two numeric answers fewer.
  await answer(page, "CORE-006", "4");
  await answer(page, "CORE-007", "dont-know");
  // D03: too few numeric answers to score.
  await answer(page, "CORE-011", "dont-know");
  await answer(page, "CORE-012", "not-observed");
  await answer(page, "CORE-013", "not-applicable");
  await expect(page.locator("#progress")).toHaveText("15 of 15 answered");
  await expect(page.locator('section[data-item="CORE-011"]')).toHaveAttribute("data-answer-state", "answered");

  await page.click("#see-results");
  await expect(page.locator("#questions")).toHaveCount(0);
  await expect.poll(() => resultRows(page)).toEqual([
    ["Plan Commitment", "75.0", "5 of 5 numeric", ""],
    ["Change Responsiveness", "81.3", "4 of 5 numeric", ""],
    ["Timebox Discipline", "Not scored", "2 of 5 numeric", "Needs at least 3 numeric answers; missing data is not counted as zero."]
  ]);
  await expect(page.locator("#scored-count")).toHaveText("2 of 3 dimensions scored");
  await expect(page.locator('#results-body tr[data-dimension="D03"] .ef-status-lozenge')).toHaveAttribute("data-state", "unscored");

  // Editing keeps the answers; starting over clears them.
  await page.click("#edit-answers");
  await expect(choice(page, "CORE-007", "dont-know")).toBeChecked();
  await page.click("#see-results");
  await page.click("#start-over");
  await expect(page.locator("#progress")).toHaveText("0 of 15 answered");
  await expect(choice(page, "CORE-007", "dont-know")).not.toBeChecked();
});

// Submitting the form re-sends its controls before the form's own event.
// Limen 0.7.0 re-sent every radio, unchecked ones included, as an answer;
// 0.7.1 re-sends only the checked radio of each group (limen#80/#81), so the
// page reads every answer event as the answer, with no `checked` filter.
test("answers survive submitting the form, refused or accepted", async ({ assessment: page }) => {
  const VALUES = ["0", "1", "2", "3", "4", "dont-know", "not-observed", "not-applicable"];
  const chosen = ITEMS.map((item, i) => [item, VALUES[i % VALUES.length]]);
  const checkedValues = () => page.locator('#answers input[type="radio"]:checked').evaluateAll((radios) =>
    radios.map((radio) => [radio.closest("section").dataset.item, radio.value]));

  for (const [item, value] of chosen.slice(0, 7)) await answer(page, item, value);
  await page.click("#see-results");
  await expect(page.locator("#refusal-message")).toHaveText(/^8 questions still need an answer\./);
  await expect(page.locator("#progress")).toHaveText("7 of 15 answered");
  expect(await checkedValues()).toEqual(chosen.slice(0, 7));

  for (const [item, value] of chosen.slice(7)) await answer(page, item, value);
  await page.click("#see-results");
  await expect.poll(() => resultRows(page)).toHaveLength(3);
  await page.click("#edit-answers");
  await expect(page.locator("#progress")).toHaveText("15 of 15 answered");
  expect(await checkedValues()).toEqual(chosen);
});

test("the print surface is Folio's document, projecting the same results", async ({ assessment: page }) => {
  for (const item of ITEMS) await answer(page, item, "2");
  await page.click("#see-results");
  await expect.poll(() => resultRows(page)).toHaveLength(3);

  await expect(page.locator(".print-surface")).toBeHidden();
  await page.emulateMedia({ media: "print" });
  await expect(page.locator(".print-surface")).toBeVisible();
  await expect(page.locator("#results")).toBeHidden();
  const printed = await page.locator(".print-surface ef-print-table").first().locator("tbody tr").evaluateAll((rows) =>
    rows.map((row) => Array.from(row.querySelectorAll("td")).map((cell) => cell.textContent.trim())));
  expect(printed.map((row) => row.slice(0, 2))).toEqual([
    ["Plan Commitment", "50.0"],
    ["Change Responsiveness", "50.0"],
    ["Timebox Discipline", "50.0"]
  ]);
  await expect(page.locator(".print-surface ef-print-table").nth(1).locator("tbody tr")).toHaveCount(15);
  await expect(page.locator(".print-surface ef-print-table").nth(1).locator("tbody tr").first()).toContainText("Sometimes");
});

// LURL-001: the URL is the respondent's state. Each answer replaces the
// current history entry (no new entries), the state lives in the fragment,
// and reopening the URL resumes exactly the same answers.
test("the live URL carries the answers, and reopening it resumes them", async ({ assessment: page }) => {
  const historyLength = await page.evaluate(() => history.length);
  await answer(page, "CORE-001", "3");
  await answer(page, "CORE-007", "dont-know");
  await expect.poll(() => new URL(page.url()).hash).toMatch(/^#r=[A-Za-z0-9_-]+$/);
  expect(new URL(page.url()).search).toBe("");
  expect(await page.evaluate(() => history.length)).toBe(historyLength);

  const link = page.url();
  await page.goto("about:blank");
  await page.goto(link);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("#progress")).toHaveText("2 of 15 answered");
  await expect(choice(page, "CORE-001", "3")).toBeChecked();
  await expect(choice(page, "CORE-007", "dont-know")).toBeChecked();
});

test("a damaged link restores nothing and says so, without an operational fault", async ({ page }) => {
  await page.goto("/web/index.html#r=AQCV3vb9UTM28AAPFQAAAAAAAIC_qXEE");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.locator("#resume-notice-message")).toContainText("integrity check");
  await expect(page.locator("#progress")).toHaveText("0 of 15 answered");
  await expect(page.locator("#operational-fault")).toHaveCount(0);
});

// LURL-002 / ID-002: an anonymous invitation link carries the instance while
// answering; submitting replaces it with a fresh anonymous id, and the
// submission link (the page URL) no longer contains the instance.
const ANONYMOUS_INVITATION = "#r=AQSV3vb9UTM28AoLDA0ODxAREhMUFRYXGBlkZWZnaGlqa2xtbm9wcXJzAA8AAAAAAAAAAGf0i7E";
const INSTANCE_BYTES_B64 = "CgsMDQ4PEBESExQVFhcYGQ"; // the invitation's instance id

test("an invited respondent submits anonymously and copies an unlinkable link", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await page.goto(`/web/index.html${ANONYMOUS_INVITATION}`);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  for (const item of ITEMS) await answer(page, item, "3");
  await page.click("#see-results");
  await page.click("#submit-answers");

  await expect(page.locator("#submitted")).toBeVisible();
  await expect(page.locator("#submission-kind")).toHaveText("anonymous");
  const link = await page.locator("#submission-link").inputValue();
  expect(link).toBe(page.url());
  expect(new URL(link).hash).not.toBe(ANONYMOUS_INVITATION);
  // The instance id's bytes are absent: decode the fragment in the browser.
  const containsInstance = await page.evaluate(([hash, instance]) => {
    const toBytes = (b64) => Uint8Array.from(atob(b64.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((b64.length + 3) % 4)), (c) => c.charCodeAt(0));
    const payload = toBytes(hash.slice(3)), needle = toBytes(instance);
    return payload.some((_, i) => needle.every((b, j) => payload[i + j] === b));
  }, [new URL(link).hash, INSTANCE_BYTES_B64]);
  expect(containsInstance).toBe(false);

  await page.click("#copy-link");
  await expect(page.locator("#copy-notice")).toHaveText(/copied/);
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(link);
});
