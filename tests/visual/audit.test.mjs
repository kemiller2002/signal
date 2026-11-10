// The audit's pure helpers (tests/visual/audit.mjs): WCAG contrast arithmetic,
// alpha composition and the findings it records.
import { test } from "node:test";
import assert from "node:assert/strict";
import { collapseFindings, compose, contrastFindings, contrastRatio, keyboardFindings, required } from "./audit.mjs";

test("contrast ratios match WCAG's definition", () => {
  assert.equal(Math.round(contrastRatio([0, 0, 0], [255, 255, 255]) * 100) / 100, 21);
  assert.equal(contrastRatio([119, 119, 119], [255, 255, 255]) < 4.5, true);
  assert.equal(required(16, 400), 4.5);
  assert.equal(required(24, 400), 3);
  assert.equal(required(19, 700), 3);
});

test("translucent layers are composed over what is under them", () => {
  assert.deepEqual(compose([[0, 0, 0, 0.5], [255, 255, 255, 1]]).map(Math.round), [128, 128, 128]);
});

test("text below AA is a finding; exempt and unmeasurable text is not counted as passing", () => {
  const rows = [
    { element: "p", text: "ok", fg: [23, 26, 24, 1], bg: [[242, 239, 231, 1]], px: 16, weight: 400, exempt: false },
    { element: "p", text: "faint", fg: [180, 180, 180, 1], bg: [[255, 255, 255, 1]], px: 16, weight: 400, exempt: false },
    { element: "button", text: "off", fg: [180, 180, 180, 1], bg: [[255, 255, 255, 1]], px: 16, weight: 400, exempt: true },
    { element: "span", text: "on image", fg: [0, 0, 0, 1], bg: null, px: 16, weight: 400, exempt: false }
  ];
  const found = contrastFindings(rows);
  assert.equal(found.checked, 2);
  assert.equal(found.unmeasurable, 1);
  assert.deepEqual(found.failures.map((f) => f.text), ["faint"]);
});

test("focus stops without an indicator or out of document order are findings", () => {
  const found = keyboardFindings([
    { element: "a", name: "One", visible: true, index: 3 },
    { element: "button", name: "Two", visible: false, index: 9 },
    { element: "a", name: "Three", visible: true, index: 5 }
  ]);
  assert.equal(found.stops, 3);
  assert.deepEqual(found.withoutIndicator, ['button "Two"']);
  assert.deepEqual(found.outOfDocumentOrder, ['a "Three"']);
});

test("opacity fades text toward its background, and every ratio is summarised", () => {
  const rows = [
    { element: "p", text: "faded", fg: [23, 26, 24, 1], bg: [[242, 239, 231, 1]], px: 16, weight: 400, exempt: false, opacity: 0.3 },
    { element: "p", text: "solid", fg: [23, 26, 24, 1], bg: [[242, 239, 231, 1]], px: 16, weight: 400, exempt: false, opacity: 1 }
  ];
  const found = contrastFindings(rows);
  assert.deepEqual(found.failures.map((f) => f.text), ["faded"]);
  assert.equal(found.lowest[0].text, "faded");
  assert.equal(found.bands["7plus"], 1);
  assert.equal((found.bands.below3 ?? 0) + (found.bands["3to4.5"] ?? 0), 1);
});

test("collapsed text is recorded with the width it collapsed at", () => {
  const found = collapseFindings([
    { width: "1440", found: [] },
    { width: "834", found: [{ element: "summary", text: "Technical detail", lines: 14, words: 2, boxWidth: 9 }] }
  ]);
  assert.deepEqual(found, [{ element: "summary", text: "Technical detail", lines: 14, words: 2, boxWidth: 9, width: "834" }]);
});
