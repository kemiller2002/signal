// The pure half of the visual-review evidence capture (WI-0110):
//   node --test tests/visual/capture-plan.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { AUTH, buildManifest, describeSnapshot, distinctStates, logSince, matrix, shotPath, slug } from "./capture-plan.mjs";

test("every screen is planned at three viewports in light and dark", () => {
  const pairs = matrix().map(({ viewport, scheme }) => `${viewport.id}:${viewport.width}x${viewport.height}:${scheme}`);
  assert.deepEqual(pairs, [
    "desktop:1440x900:light", "desktop:1440x900:dark",
    "tablet:834x1112:light", "tablet:834x1112:dark",
    "mobile:390x844:light", "mobile:390x844:dark"
  ]);
});

test("screenshots are named by screen, viewport, scheme and extent", () => {
  assert.equal(shotPath("landing", "mobile", "dark", "viewport"), "screens/landing/mobile-dark-viewport.png");
  assert.equal(shotPath("x", "desktop", "light"), "screens/x/desktop-light-full.png");
  assert.equal(slug("Admin signed in!"), "admin-signed-in");
});

const snap = (t, extra = {}) => ({ t, kernel: null, blank: false, startup: null, spinners: [], busy: [], statuses: [], alerts: [], disabledButtons: 0, ...extra });

test("a snapshot is described as what a person saw, and an empty one as nothing", () => {
  assert.deepEqual(describeSnapshot(snap(0)), []);
  assert.deepEqual(
    describeSnapshot(snap(0, { blank: true, startup: { visible: true, state: "starting", spinner: true, text: "Starting Signal" }, statuses: ["Loading the survey…"], disabledButtons: 2, kernel: "running" })),
    ["blank page (no visible text)", "start-up screen: state \"starting\", spinner, \"Starting Signal\"", "status: \"Loading the survey…\"", "2 button(s) disabled", "kernel: running"]);
  // A hidden start-up screen is not reported.
  assert.deepEqual(describeSnapshot(snap(0, { startup: { visible: false, state: "starting", spinner: true, text: "" } })), []);
});

test("identical consecutive snapshots collapse into one state with its duration", () => {
  const log = [snap(0, { blank: true }), snap(5, { blank: true }), snap(10, { statuses: ["Loading"] }), snap(30, { statuses: ["Loading"] }), snap(42)];
  assert.deepEqual(distinctStates(log), [
    { atMs: 0, forMs: 10, shown: ["blank page (no visible text)"] },
    { atMs: 10, forMs: 32, shown: ["status: \"Loading\""] },
    { atMs: 42, forMs: null, shown: [] }
  ]);
  assert.deepEqual(distinctStates([]), []);
});

test("a step's log is what was added in the same document, or all of a new one", () => {
  const after = { doc: "a", log: [1, 2, 3, 4] };
  assert.deepEqual(logSince({ doc: "a", length: 3 }, after), [4]);
  assert.deepEqual(logSince({ doc: "b", length: 3 }, after), [1, 2, 3, 4]);
  assert.deepEqual(logSince(null, after), [1, 2, 3, 4]);
  assert.deepEqual(logSince({ doc: "a", length: 0 }, null), []);
});

test("the manifest counts shots, lists sign-in screens and unreached ones, and groups waiting states by operation", () => {
  const shots = (screenId) => matrix().map(({ viewport, scheme }) => ({ viewport: viewport.id, scheme, extent: "full", file: shotPath(screenId, viewport.id, scheme) }));
  const fragments = [
    {
      scenario: "a", durationMs: 1, consoleErrors: [],
      screens: [
        { id: "one", site: "checkout", auth: AUTH.none, steps: [{ action: "open", operation: "engine start-up", observed: [{ atMs: 0, forMs: 3, shown: ["blank page (no visible text)"] }] }], shots: shots("one") },
        { id: "two", site: "pages", auth: AUTH.fakes, steps: [{ action: "click", operation: "engine start-up", observed: [{ atMs: 0, forMs: null, shown: ["status: \"Loading\""] }] }, { action: "type", operation: null, observed: [] }], shots: shots("two") }
      ],
      unreached: [{ screen: "three", reason: "timed out" }]
    }
  ];
  const manifest = buildManifest(fragments, { generatedAt: "now" });
  assert.equal(manifest.summary.screens, 2);
  assert.equal(manifest.summary.screenshots, 12);
  assert.deepEqual(manifest.summary.byViewport, { desktop: 4, tablet: 4, mobile: 4 });
  assert.deepEqual(manifest.summary.byScheme, { light: 6, dark: 6 });
  assert.deepEqual(manifest.summary.bySite, { checkout: 1, pages: 1 });
  assert.deepEqual(manifest.summary.authGated, ["two"]);
  assert.deepEqual(manifest.summary.unreached, [{ screen: "three", reason: "timed out" }]);
  assert.deepEqual(manifest.summary.loadingByOperation, {
    "engine start-up": { screens: ["one", "two"], observed: ["blank page (no visible text)", "status: \"Loading\""] }
  });
  assert.equal(manifest.kind, "visual-review evidence (not functional test evidence)");
});
