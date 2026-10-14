// Universal UI layout conformance for Signal's three pages
// (UI-LAYOUT-INTERACTION@1.0.0, signal#104): declared peer-alignment groups
// measured after fonts settle, horizontal overflow at 320/390/768/1280 in
// every reachable state, text growth, long labels, keyboard operation and
// reduced motion. Locators are by role and accessible name; no fixed sleeps,
// no coordinates.
import { test, expect } from "./support.js";
import { VIEWPORT_WIDTHS, expectNoHorizontalOverflow, settleFontsAndFrames } from "./ui-conformance-helpers.mjs";
import {
  AREAS, FIRST_PROMPT, INVITATION, KERNEL_START_MS, ORDINAL, SPECIAL,
  answerAll, expectPeerAlignment, links, open, question
} from "./ui-conformance-fixtures.mjs";

// --- Declared peer-alignment groups (ULI-L01, L02, L07, L09, L10) ---------

for (const width of VIEWPORT_WIDTHS) {
  test(`ULI-L01/L02/L10: declared assessment groups align at ${width} CSS px`, async ({ page }) => {
    await open(page, "/web/index.html", width);
    const scope = question(page, FIRST_PROMPT);
    const ordinal = ORDINAL.map((name) => ({ role: "radio", name, measure: "label" }));
    // The five-point scale is one row from 44rem up and one column below it
    // (Forma's ordinal scale): the declared relationship changes with it.
    const stacked = width < 704;
    await expectPeerAlignment(page, { ruleId: "ULI-L01", id: "assessment.q1.ordinal", peers: ordinal, edge: stacked ? "left" : "top" }, { scope });
    await expectPeerAlignment(page, { ruleId: "ULI-L01", id: "assessment.q1.ordinal.end", peers: ordinal, edge: stacked ? "right" : "bottom" }, { scope });
    if (!stacked) {
      await expectPeerAlignment(page, { ruleId: "ULI-L01", id: "assessment.q1.other",
        peers: SPECIAL.map((name) => ({ role: "radio", name, measure: "label" })) }, { scope });
    }
    // The special choices share a left edge with the scale (one column edge).
    await expectPeerAlignment(page, { ruleId: "ULI-L01", id: "assessment.q1.start", edge: "left",
      peers: [{ role: "radio", name: ORDINAL[0], measure: "label" }, { role: "radio", name: SPECIAL[0], measure: "label" }] }, { scope });

    await answerAll(page, "Often");
    await page.getByRole("button", { name: "See results" }).click();
    await expectPeerAlignment(page, { ruleId: "ULI-L10", id: "assessment.results.actions",
      peers: [{ role: "button", name: "Edit answers" }, { role: "button", name: "Start over" }] });
    await expectPeerAlignment(page, { ruleId: "ULI-L02", id: "assessment.results.actions.height", edge: "bottom",
      peers: [{ role: "button", name: "Edit answers" }, { role: "button", name: "Start over" }] });
  });

  test(`ULI-L01/L09: survey best-worst columns and ranking fields align at ${width} CSS px`, async ({ page }) => {
    await open(page, links.compound, width);
    const rows = ["Calm", "Busy", "Chaotic"];
    for (const column of ["Fits best", "Fits least"]) {
      await expectPeerAlignment(page, { ruleId: "ULI-L09", id: `survey.best-worst.${column}`, edge: "left",
        peers: rows.map((row) => ({ role: "radio", name: `${row} ${column}` })) });
    }
    for (const row of rows) {
      await expectPeerAlignment(page, { ruleId: "ULI-L01", id: `survey.best-worst.${row}`,
        peers: [{ role: "radio", name: `${row} Fits best` }, { role: "radio", name: `${row} Fits least` }] });
    }
    for (const edge of ["left", "right"]) {
      await expectPeerAlignment(page, { ruleId: "ULI-L02", id: `survey.ranking.${edge}`, edge,
        peers: ["Speed", "Quality", "Cost"].map((name) => ({ role: "spinbutton", name })) });
    }
  });

  test(`ULI-L01/L05: administration areas share an edge and stay inside the page gutter at ${width} CSS px`, async ({ page }) => {
    await open(page, "/web/admin/#/assessments", width);
    const scope = page.getByRole("navigation", { name: "Administration" });
    await expectPeerAlignment(page, { ruleId: "ULI-L01", id: "admin.areas", edge: "left",
      peers: AREAS.map(([name]) => ({ role: "link", name })) }, { scope });
    // The page has a gutter (the defect fixed under GH-104: text ran to the
    // viewport edge). Measured from the page heading, not a coordinate.
    const heading = await page.getByRole("heading", { level: 2, name: "Assessments" }).boundingBox();
    expect(heading.x, "the heading starts inside a page gutter").toBeGreaterThanOrEqual(16);
    expect(heading.x + heading.width).toBeLessThanOrEqual(width - 16 + 0.5);
  });
}

// --- Overflow at every width, in every reachable state (ULI-L05, L08, L09)

const SURFACES = [
  ["assessment: questions", (page) => page.goto("/web/index.html"), null],
  ["assessment: refusal", (page) => page.goto("/web/index.html"), (page) => page.getByRole("button", { name: "See results" }).click()],
  ["assessment: results", (page) => page.goto(INVITATION), async (page) => {
    await answerAll(page, "Almost always");
    await page.getByRole("button", { name: "See results" }).click();
    await expect(page.getByRole("heading", { name: "Results" })).toBeVisible();
  }],
  // An empty dataset: nothing numeric, so nothing is scored.
  ["assessment: nothing scored", (page) => page.goto(INVITATION), async (page) => {
    await answerAll(page, "Not applicable");
    await page.getByRole("button", { name: "See results" }).click();
    await expect(page.locator("#scored-count")).toHaveText(/0 of 3/);
  }],
  ["assessment: submitted", (page) => page.goto(INVITATION), async (page) => {
    await answerAll(page, "Rarely");
    await page.getByRole("button", { name: "See results" }).click();
    await page.getByRole("button", { name: "Submit answers" }).click();
    await expect(page.getByRole("heading", { name: "Submitted" })).toBeVisible();
  }],
  ["survey: identified", (page) => page.goto(links.identified), null],
  ["survey: mixed kinds", (page) => page.goto(links.kinds), null],
  ["survey: compound kinds", (page) => page.goto(links.compound), null],
  ["survey: test link", (page) => page.goto(links.test), null],
  ["survey: no link", (page) => page.goto("/web/survey/"), null],
  ["admin: no store", (page) => page.goto("/web/admin/"), null],
  ["admin: question", (page) => page.goto("/web/admin/#/assessments/SDRA/versions/0.1.0-draft/sections/D02/questions/CORE-007"), null],
  ["admin: not found", (page) => page.goto("/web/admin/#/reports/2026"), null]
];

for (const [surface, go, act] of SURFACES) {
  test(`ULI-L05/L09: ${surface} has no horizontal page scroll at 320, 390, 768 and 1280 CSS px`, async ({ page }) => {
    await go(page);
    await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: KERNEL_START_MS });
    if (act) await act(page);
    for (const width of VIEWPORT_WIDTHS) {
      await page.setViewportSize({ width, height: 900 });
      await expectNoHorizontalOverflow(page, `${surface} @${width}`);
    }
  });
}

// --- Text growth and long labels (ULI-L05, L06) ---------------------------

test("ULI-L05: 200% text keeps every page usable without horizontal scroll at 390 CSS px", async ({ page }) => {
  for (const url of ["/web/index.html", links.compound, "/web/admin/#/assessments/SDRA/versions/0.1.0-draft/sections/D02/questions/CORE-007"]) {
    await open(page, url, 390);
    // CSSOM, not an injected <style>: the survey page's CSP refuses inline style elements.
    await page.evaluate(() => document.documentElement.style.setProperty("font-size", "200%", "important"));
    await expectNoHorizontalOverflow(page, `${url} @390 with 200% text`);
    // The primary action is still reachable and inside the viewport.
    const primary = page.getByRole("button", { name: /^(See results|Submit answers|Copy link)$/ }).first();
    await primary.scrollIntoViewIfNeeded();
    const box = await primary.boundingBox();
    expect(box.x + box.width).toBeLessThanOrEqual(390.5);
  }
});

const LONG = "Verantwortungsübernahmebereitschaft und Anforderungsmanagementprozessdokumentation über mehrere Lieferzyklen hinweg";

test("ULI-L05/L06: long titles, field labels and area names wrap instead of overflowing at 320 CSS px", async ({ page }) => {
  await open(page, links.compound, 320);
  // Rendered text replaced in the page (the engine projects only its own
  // fixtures): a long translated title and long field labels.
  await page.evaluate((text) => {
    document.querySelector("h1").textContent = text;
    for (const label of document.querySelectorAll("label > span:first-child:not(.ef-sr-only)")) label.textContent = text;
  }, LONG);
  await expectNoHorizontalOverflow(page, "survey with long title and field labels @320");
  // The best-worst columns still line up below the longer content.
  await expectPeerAlignment(page, { ruleId: "ULI-L09", id: "survey.best-worst.long-page", edge: "left",
    peers: ["Calm", "Busy", "Chaotic"].map((row) => ({ role: "radio", name: `${row} Fits best` })) });

  await open(page, "/web/admin/#/assessments", 320);
  await page.evaluate((text) => {
    for (const link of document.querySelectorAll("nav[aria-label='Administration'] a")) link.textContent = `${link.textContent} ${text}`;
  }, LONG);
  await expectNoHorizontalOverflow(page, "admin with long area names @320");
  // Each area keeps its readable name (ULI-L06).
  for (const [name] of AREAS) {
    await expect(page.getByRole("navigation", { name: "Administration" }).getByRole("link", { name: new RegExp(`^${name} Verantwortung`) })).toBeVisible();
  }
});

// EXCEPTION (owned by Forma, upstream): a best-worst option whose text holds a
// word longer than its column overflows the row at 320 CSS px. The option
// cell of Forma's .ef-best-worst__row (design-system 0.6.1) has
// minmax(0, 1fr) and no overflow-wrap, so the word escapes its 102 CSS px
// cell (ULI-L05). Signal does not restyle Forma components (L03, and
// FoundationsConformanceTests forbid it here). Owner: Forma; review: on the
// next design-system upgrade. test.fail keeps the defect visible: this test
// starts failing, and must be turned into an ordinary assertion, as soon as
// the pinned Forma wraps the cell.
test("EXCEPTION ULI-L05: a best-worst option with a very long word overflows Forma's row at 320 CSS px", async ({ page }) => {
  test.fail(true, "Forma .ef-best-worst__row option cell does not wrap long words (upstream defect, recorded under GH-104)");
  await open(page, links.compound, 320);
  await page.evaluate((text) => {
    for (const row of document.querySelectorAll("[data-row] > div:has(> label > input[data-event='pickedBest'])")) row.firstElementChild.textContent = text;
  }, LONG);
  await expectNoHorizontalOverflow(page, "survey best-worst with a long option @320");
});

// --- Keyboard (ULI-A08) and reduced motion (ULI-L08) ----------------------

test("ULI-A08: the assessment and the survey are operable from the keyboard alone", async ({ page }) => {
  await open(page, "/web/index.html", 390);
  const scope = question(page, FIRST_PROMPT);
  await scope.getByRole("radio", { name: ORDINAL[0], exact: true }).focus();
  await page.keyboard.press("Space");
  await expect(scope.getByRole("radio", { name: ORDINAL[0], exact: true })).toBeChecked();
  await page.keyboard.press("ArrowDown");
  await expect(scope.getByRole("radio", { name: ORDINAL[1], exact: true })).toBeChecked();
  await expect(page.locator("#progress")).toHaveText("1 of 15 answered");
  await page.getByRole("button", { name: "See results" }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("alert")).toBeVisible();

  await open(page, links.compound, 390);
  await page.getByRole("radio", { name: "Busy Fits least" }).focus();
  await page.keyboard.press("Space");
  await expect(page.getByRole("radio", { name: "Busy Fits least" })).toBeChecked();
  await expect(page.locator("#progress")).toHaveText("0 of 6 answered");
  await page.getByRole("radio", { name: "Calm Fits best" }).focus();
  await page.keyboard.press("Space");
  await expect(page.locator("#progress")).toHaveText("1 of 6 answered");
});

test("ULI-L08: with reduced motion the scale does not animate, and still answers", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await open(page, "/web/index.html");
  const scope = question(page, FIRST_PROMPT);
  const marker = scope.locator(".ef-ordinal-option__marker").first();
  const seconds = await marker.evaluate((element) => Math.max(...getComputedStyle(element).transitionDuration.split(",").map((value) => parseFloat(value))));
  expect(seconds).toBeLessThanOrEqual(0.001);
  await scope.getByRole("radio", { name: "Rarely", exact: true }).check();
  await expect(page.locator("#progress")).toHaveText("1 of 15 answered");
  await settleFontsAndFrames(page);
});
