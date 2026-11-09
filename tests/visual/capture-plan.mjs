// Visual-review evidence capture (SIG-VIS-CAPTURE-001, WI-0110): the pure
// half. Which viewports and colour schemes every screen is captured at, how
// each screenshot is named, how the page's observed waiting states are
// reduced to what a person saw, and how the per-scenario records become one
// manifest. No IO here; capture.spec.js and build-manifest.mjs do the IO.
//
// This is evidence tooling, not a test: a screenshot proves what was on the
// screen, never that the screen is correct. Nothing here asserts a design.

export const VIEWPORTS = Object.freeze([
  Object.freeze({ id: "desktop", width: 1440, height: 900 }),
  Object.freeze({ id: "tablet", width: 834, height: 1112 }),
  Object.freeze({ id: "mobile", width: 390, height: 844 })
]);

export const SCHEMES = Object.freeze(["light", "dark"]);

// Every (viewport, scheme) pair, viewport-major, so a reader sees one
// viewport's light and dark side by side.
export const matrix = (viewports = VIEWPORTS, schemes = SCHEMES) =>
  viewports.flatMap((viewport) => schemes.map((scheme) => ({ viewport, scheme })));

// "full" is the whole scrollable page; "viewport" is only what fits the window.
export const shotPath = (screenId, viewportId, scheme, extent = "full") =>
  `screens/${screenId}/${viewportId}-${scheme}-${extent}.png`;

export const slug = (text) =>
  text.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");

// ---------------------------------------------------------------------------
// Observed waiting states.
//
// The page records a snapshot of everything that can say "wait" whenever the
// DOM changes (observe-init.js): the start-up screen's state and visible
// text, every visible status/alert/busy element, the loading paragraphs,
// spinners, how many buttons are disabled, and whether the page shows any
// text at all. These pure functions turn that raw log into the distinct
// states a person would have seen, in order.

// What a person would describe from one snapshot, as short phrases.
export const describeSnapshot = (snapshot) => [
  ...(snapshot.blank ? ["blank page (no visible text)"] : []),
  ...(snapshot.startup && snapshot.startup.visible
    ? [`start-up screen: state "${snapshot.startup.state}"${snapshot.startup.spinner ? ", spinner" : ""}${snapshot.startup.text ? `, "${snapshot.startup.text}"` : ""}`]
    : []),
  ...(snapshot.spinners ?? []).map((text) => `spinner${text ? `: "${text}"` : ""}`),
  ...(snapshot.busy ?? []).map((text) => `aria-busy element${text ? `: "${text}"` : ""}`),
  ...(snapshot.statuses ?? []).map((text) => `status: "${text}"`),
  ...(snapshot.alerts ?? []).map((text) => `alert: "${text}"`),
  ...(snapshot.disabledButtons > 0 ? [`${snapshot.disabledButtons} button(s) disabled`] : []),
  ...(snapshot.kernel ? [`kernel: ${snapshot.kernel}`] : [])
];

const signature = (snapshot) => JSON.stringify(describeSnapshot(snapshot));

// Consecutive identical states collapse into one, keeping the first time it
// was seen and how long it lasted until the next distinct state.
export const distinctStates = (log) =>
  log
    .reduce((states, snapshot) => {
      const last = states[states.length - 1];
      return last && last.signature === signature(snapshot)
        ? states
        : [...states, { signature: signature(snapshot), at: snapshot.t, shown: describeSnapshot(snapshot) }];
    }, [])
    .map((state, index, all) => ({
      atMs: Math.round(state.at),
      forMs: index + 1 < all.length ? Math.round(all[index + 1].at - state.at) : null,
      shown: state.shown
    }));

// The part of the page's log an action produced. A navigation replaces the
// document, and with it the log, so a changed document id means "all of it".
export const logSince = (before, after) =>
  before && after && before.doc === after.doc ? after.log.slice(before.length) : (after?.log ?? []);

// ---------------------------------------------------------------------------
// Manifest.

export const AUTH = Object.freeze({
  none: Object.freeze({ required: false, how: "none: the page needs no sign-in" }),
  fakes: Object.freeze({
    required: true,
    how: "signed in through the real page against test-only network fakes: GitHub's OAuth authorize redirect, Fides' token exchange and the GitHub REST store (tests/browser/ui-conformance-fixtures.mjs, tests/browser/fakes/github-store.js). The demo deployment configures no sign-in (its banner: \"GitHub sign-in not configured\"), so a real sign-in is not reachable there."
  }),
  configuredOnly: Object.freeze({
    required: false,
    how: "a test-only deployment configuration (signal.deployment.json answered locally) that names an identity exchange; nothing is signed in"
  })
});

const countBy = (items, key) =>
  items.reduce((counts, item) => ({ ...counts, [key(item)]: (counts[key(item)] ?? 0) + 1 }), {});

const unique = (items) => [...new Set(items)];

// The loading states per asynchronous operation, across every screen's steps.
export const loadingByOperation = (screens) =>
  screens
    .flatMap((screen) => screen.steps.filter((step) => step.operation).map((step) => ({ screen: screen.id, step })))
    .reduce((byOperation, { screen, step }) => {
      const entry = byOperation[step.operation] ?? { screens: [], observed: [] };
      return {
        ...byOperation,
        [step.operation]: {
          screens: unique([...entry.screens, screen]),
          observed: unique([...entry.observed, ...step.observed.flatMap((state) => state.shown)])
        }
      };
    }, {});

export const buildManifest = (fragments, context) => {
  const screens = fragments.flatMap((fragment) => fragment.screens);
  const unreached = fragments.flatMap((fragment) => fragment.unreached);
  const shots = screens.flatMap((screen) => screen.shots);
  return {
    schema: "signal.visual-capture-manifest/1",
    kind: "visual-review evidence (not functional test evidence)",
    workItem: "WI-0110",
    intake: ".praxis/inbox/documents/2026-11-09-SIG-VIS-CAPTURE-001-visual-review-evidence-capture.md",
    ...context,
    viewports: VIEWPORTS,
    schemes: SCHEMES,
    summary: {
      screens: screens.length,
      screenshots: shots.length,
      byViewport: countBy(shots, (shot) => shot.viewport),
      byScheme: countBy(shots, (shot) => shot.scheme),
      byExtent: countBy(shots, (shot) => shot.extent),
      bySite: countBy(screens, (screen) => screen.site),
      authGated: screens.filter((screen) => screen.auth.required).map((screen) => screen.id),
      unreached: unreached.map((item) => ({ screen: item.screen, reason: item.reason })),
      loadingByOperation: loadingByOperation(screens)
    },
    scenarios: fragments.map((fragment) => ({
      id: fragment.scenario,
      consoleErrors: fragment.consoleErrors,
      durationMs: fragment.durationMs
    })),
    screens,
    unreached
  };
};
