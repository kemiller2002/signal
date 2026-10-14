// Playwright conformance helpers for UI-LAYOUT-INTERACTION@1.0.0
// (kemiller2002/visual-engineering framework/standards/UI-LAYOUT-INTERACTION.md;
// rule IDs ULI-L01..L13, ULI-A01..A10). Signal cites that contract; it does
// not restate it.
//
// This module mirrors the public API of Forma's reference helpers
// (kemiller2002/forma tests/site-browser/ui-conformance-helpers.mjs, branch
// feat/universal-ui-conformance-2026-10) so it can be swapped for the
// published package once Forma ships it. Differences, all additive:
//   - measureAlignment/expectPeerAlignment accept { scope } (a Locator) so a
//     group can be declared inside one question, form or row, where the same
//     accessible name repeats across the page;
//   - a peer may declare measure: "label" to measure the visible label of a
//     visually hidden native input (Forma's ordinal and choice controls);
//   - measureHorizontalOverflow/expectNoHorizontalOverflow (ULI-L05).
//
// Statuses follow the contract: PASS, FAIL, UNKNOWN (missing evidence).
// NOT_APPLICABLE and EXCEPTION are declared, owned decisions made by callers.
// The helpers never guess intent and never report PASS without an
// observation.
import { expect } from "@playwright/test";

export const STATUS = Object.freeze({ PASS: "PASS", FAIL: "FAIL", UNKNOWN: "UNKNOWN" });
export const STANDARD = "UI-LAYOUT-INTERACTION@1.0.0";
export const DEFAULT_TOLERANCE_CSS_PX = 1;
export const DEFAULT_TIMEOUT_MS = 5000;
export const VIEWPORT_WIDTHS = Object.freeze([320, 390, 768, 1280]);

const EDGES = Object.freeze({
  top: (box) => box.y,
  bottom: (box) => box.y + box.height,
  center: (box) => box.y + box.height / 2,
  left: (box) => box.x,
  right: (box) => box.x + box.width
});

// ---------------------------------------------------------------------------
// Pure helpers (no browser)
// ---------------------------------------------------------------------------

const describeTarget = ({ role, name }) => `${role} "${name instanceof RegExp ? name.source : name}"`;

const requireSemanticTarget = (target, label) => {
  if (!target?.role || target.name === undefined || target.name === "") {
    throw new Error(`${label} must specify both accessible role and name.`);
  }
  return target;
};

export const edgeCoordinate = (box, edge) => {
  const read = EDGES[edge];
  if (!read) throw new Error(`Unsupported alignment edge "${edge}". Use one of: ${Object.keys(EDGES).join(", ")}.`);
  return read(box);
};

export const classifyPeerObservation = ({ peer, count, box }) => {
  const element = describeTarget(peer);
  if (count === 0) return { element, status: STATUS.UNKNOWN, reason: "element not found in the accessibility tree (missing or hidden)" };
  if (count > 1) return { element, status: STATUS.UNKNOWN, reason: `accessible name is ambiguous (${count} matches)` };
  if (!box) return { element, status: STATUS.UNKNOWN, reason: "no bounding box (not rendered)" };
  if (!(box.width > 0) || !(box.height > 0)) {
    return { element, status: STATUS.UNKNOWN, reason: `zero-size box (${box.width}x${box.height})`, box };
  }
  return { element, status: STATUS.PASS, box };
};

export const validateAlignmentGroup = (group) => {
  const { ruleId = "ULI-L01", id, peers, edge = "top", toleranceCssPx = DEFAULT_TOLERANCE_CSS_PX } = group ?? {};
  if (!id) throw new Error("A declared alignment group requires an id.");
  if (!Array.isArray(peers) || peers.length < 2) throw new Error(`Alignment group ${id} requires at least two declared peers.`);
  if (!/^ULI-L\d{2}$/.test(ruleId)) throw new Error(`Alignment group ${id} must cite a ULI-L rule ID.`);
  if (!(toleranceCssPx >= 0)) throw new Error(`Alignment group ${id} tolerance must be a non-negative number.`);
  peers.forEach((peer, index) => {
    requireSemanticTarget(peer, `Peer ${index + 1} of ${id}`);
    if (peer.measure !== undefined && peer.measure !== "label") throw new Error(`Peer ${index + 1} of ${id}: measure must be "label" when given.`);
  });
  edgeCoordinate({ x: 0, y: 0, width: 0, height: 0 }, edge);
  return Object.freeze({ ruleId, id, peers, edge, toleranceCssPx });
};

// Missing evidence is UNKNOWN, never PASS; a delta above tolerance is FAIL.
export const evaluateAlignment = (group, observations) => {
  const { ruleId, id, edge, toleranceCssPx } = validateAlignmentGroup(group);
  const measurements = observations.map(classifyPeerObservation).map((item) =>
    item.status === STATUS.PASS ? { ...item, coordinate: edgeCoordinate(item.box, edge) } : item);
  const base = { ruleId, group: id, edge, toleranceCssPx, measurements };
  const unknown = measurements.filter((item) => item.status !== STATUS.PASS);
  if (unknown.length > 0) {
    return { ...base, status: STATUS.UNKNOWN, deltaCssPx: null, reason: unknown.map((item) => `${item.element}: ${item.reason}`).join("; ") };
  }
  const coordinates = measurements.map((item) => item.coordinate);
  const deltaCssPx = Math.max(...coordinates) - Math.min(...coordinates);
  const status = deltaCssPx <= toleranceCssPx ? STATUS.PASS : STATUS.FAIL;
  return {
    ...base,
    status,
    deltaCssPx,
    reason: `${edge}-edge delta ${deltaCssPx.toFixed(2)} CSS px ${status === STATUS.PASS ? "<=" : ">"} tolerance ${toleranceCssPx} CSS px`
  };
};

export const formatReport = (report) => {
  const details = (report.measurements ?? [])
    .map((item) => `  - ${item.element}: ${item.status}${item.coordinate !== undefined ? ` ${report.edge}=${item.coordinate.toFixed(2)}px` : ""}${item.reason ? ` (${item.reason})` : ""}`)
    .join("\n");
  const subject = report.group ? `group ${report.group}` : report.action ?? report.surface;
  return `${report.ruleId} ${report.status}: ${subject}: ${report.reason}${details ? `\n${details}` : ""}`;
};

const sameObservations = (a, b) =>
  a.length === b.length && a.every((item, index) => {
    const other = b[index];
    if (item.count !== other.count) return false;
    if (!item.box || !other.box) return item.box === other.box;
    return ["x", "y", "width", "height"].every((key) => Math.abs(item.box[key] - other.box[key]) < 0.01);
  });

const normalizeText = (value) => (value ?? "").replace(/\s+/g, " ").trim();

export const matchesExpected = (observed, expected) => {
  if (observed === null || observed === undefined) return false;
  if (expected instanceof RegExp) return expected.test(String(observed));
  return observed === expected;
};

const OUTCOME_KINDS = Object.freeze({
  text: { needsTarget: true, expectedType: "string-or-regexp" },
  visible: { needsTarget: true, expectedType: "boolean" },
  checked: { needsTarget: true, expectedType: "boolean" },
  attribute: { needsTarget: true, expectedType: "string-or-regexp" },
  url: { needsTarget: false, expectedType: "string-or-regexp" }
});

export const validateActionContract = (contract) => {
  const { ruleId = "ULI-A02", action, outcome, timeoutMs = DEFAULT_TIMEOUT_MS } = contract ?? {};
  requireSemanticTarget(action, "Action control");
  if (!/^ULI-A\d{2}$/.test(ruleId)) throw new Error("An action assertion must cite a ULI-A rule ID.");
  const kind = OUTCOME_KINDS[outcome?.kind];
  if (!kind) throw new Error(`An action requires an observable outcome kind: ${Object.keys(OUTCOME_KINDS).join(", ")}.`);
  if (kind.needsTarget) requireSemanticTarget(outcome.target, `Outcome target for ${outcome.kind}`);
  if (outcome.kind === "attribute" && !outcome.attribute) throw new Error("Attribute outcome requires an attribute name.");
  const typeOk = kind.expectedType === "boolean"
    ? typeof outcome.expected === "boolean"
    : typeof outcome.expected === "string" || outcome.expected instanceof RegExp;
  if (!typeOk) throw new Error(`Outcome ${outcome.kind} requires an explicit expected ${kind.expectedType} post-state.`);
  if (!(timeoutMs > 0 && Number.isFinite(timeoutMs))) throw new Error("Action assertions require a bounded positive timeoutMs.");
  return Object.freeze({ ruleId, action, outcome, timeoutMs });
};

// Null when the action can demonstrate a transition; otherwise a report:
// UNKNOWN for missing evidence, FAIL when the expected post-state is already
// present before activation.
export const evaluateActionPremise = ({ ruleId, action, outcome, pre, controlIssue }) => {
  const base = { ruleId, action: describeTarget(action), outcome: outcome.kind, expected: String(outcome.expected), pre, post: null };
  if (controlIssue) return { ...base, status: STATUS.UNKNOWN, reason: controlIssue };
  if (pre.missing && outcome.kind !== "visible") return { ...base, status: STATUS.UNKNOWN, reason: "outcome target not found before activation" };
  if (pre.count > 1) return { ...base, status: STATUS.UNKNOWN, reason: `outcome target name is ambiguous (${pre.count} matches)` };
  if (matchesExpected(pre.value, outcome.expected)) {
    return { ...base, status: STATUS.FAIL, reason: "pre-state already satisfies the expected post-state; no transition can be demonstrated" };
  }
  return null;
};

// PASS only when the pre-state did not already satisfy the expectation, the
// expected post-state was observed, and the observable value changed.
export const evaluateActionOutcome = ({ ruleId, action, outcome, pre, post, controlIssue }) => {
  const premise = evaluateActionPremise({ ruleId, action, outcome, pre, controlIssue });
  if (premise) return premise;
  const base = { ruleId, action: describeTarget(action), outcome: outcome.kind, expected: String(outcome.expected), pre, post };
  if (!post) return { ...base, status: STATUS.FAIL, reason: "no post-state observed" };
  if (!matchesExpected(post.value, outcome.expected)) return { ...base, status: STATUS.FAIL, reason: "expected post-state never appeared within the timeout" };
  if (post.value === pre.value) return { ...base, status: STATUS.FAIL, reason: "post-state equals pre-state" };
  return { ...base, status: STATUS.PASS, reason: "expected post-state observed after activation" };
};

// ULI-L05: horizontal page scroll is FAIL; an unrendered page is UNKNOWN.
export const evaluateHorizontalOverflow = ({ surface, viewportWidth, scrollWidth, clientWidth, offenders = [] }) => {
  const base = { ruleId: "ULI-L05", surface, viewportWidth, scrollWidth, clientWidth, offenders };
  if (!(clientWidth > 0) || !(scrollWidth > 0)) return { ...base, status: STATUS.UNKNOWN, reason: "the page has no rendered width" };
  const excess = scrollWidth - clientWidth;
  if (excess > 0) {
    return { ...base, status: STATUS.FAIL, reason: `the page scrolls horizontally by ${excess} CSS px at ${viewportWidth} CSS px${offenders.length ? `: ${offenders.join(", ")}` : ""}` };
  }
  return { ...base, status: STATUS.PASS, reason: `no horizontal page scroll at ${viewportWidth} CSS px` };
};

// ---------------------------------------------------------------------------
// Browser adapters (observation only)
// ---------------------------------------------------------------------------

const semantic = (root, { role, name }) => root.getByRole(role, { name, exact: typeof name === "string" });

export const settleFontsAndFrames = (page) =>
  page.evaluate(() => document.fonts.ready.then(() =>
    new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())))));

const observePeer = async (root, peer, timeoutMs) => {
  const locator = semantic(root, peer);
  const count = await locator.count();
  if (count !== 1) return { peer, count, box: null };
  if (peer.measure === "label") {
    const label = locator.locator("xpath=ancestor::label[1]");
    return { peer, count, box: (await label.count()) === 1 ? await label.boundingBox({ timeout: timeoutMs }) : null };
  }
  return { peer, count, box: await locator.boundingBox({ timeout: timeoutMs }) };
};

const observePeers = (root, peers, timeoutMs) => Promise.all(peers.map((peer) => observePeer(root, peer, timeoutMs)));

// Re-read until two frame-separated observations agree, bounded by a deadline.
const readStableObservations = async (page, root, peers, timeoutMs, deadline, previous) => {
  await settleFontsAndFrames(page);
  const current = await observePeers(root, peers, timeoutMs);
  if (previous && sameObservations(previous, current)) return current;
  if (Date.now() >= deadline) return null;
  return readStableObservations(page, root, peers, timeoutMs, deadline, current);
};

// Measures a declared group once fonts are ready and layout is stable.
// Never throws on a geometry failure; a group that never settles is UNKNOWN.
export const measureAlignment = async (page, group, { timeoutMs = DEFAULT_TIMEOUT_MS, scope } = {}) => {
  const declared = validateAlignmentGroup(group);
  const stable = await readStableObservations(page, scope ?? page, declared.peers, timeoutMs, Date.now() + timeoutMs, null);
  if (!stable) {
    return { ruleId: declared.ruleId, group: declared.id, edge: declared.edge, toleranceCssPx: declared.toleranceCssPx,
      measurements: [], status: STATUS.UNKNOWN, deltaCssPx: null, reason: "layout did not stabilize within the timeout" };
  }
  return evaluateAlignment(declared, stable);
};

export const expectPeerAlignment = async (page, group, options) => {
  const report = await measureAlignment(page, group, options);
  expect(report.status, formatReport(report)).toBe(STATUS.PASS);
  return report;
};

export const measureHorizontalOverflow = async (page, surface) => {
  await settleFontsAndFrames(page);
  const observed = await page.evaluate(() => {
    const root = document.documentElement;
    const offenders = [];
    for (const element of document.body.querySelectorAll("*")) {
      const box = element.getBoundingClientRect();
      if (box.width > 0 && box.right > root.clientWidth + 0.5 && offenders.length < 5) {
        offenders.push(`${element.tagName.toLowerCase()}${element.id ? `#${element.id}` : ""}`);
      }
    }
    return { scrollWidth: root.scrollWidth, clientWidth: root.clientWidth, offenders };
  });
  return evaluateHorizontalOverflow({ surface, viewportWidth: page.viewportSize()?.width, ...observed });
};

export const expectNoHorizontalOverflow = async (page, surface) => {
  const report = await measureHorizontalOverflow(page, surface);
  expect(report.status, formatReport(report)).toBe(STATUS.PASS);
  return report;
};

const observeOutcome = async (page, outcome, timeoutMs) => {
  if (outcome.kind === "url") return { value: page.url(), missing: false };
  const target = semantic(page, outcome.target);
  const count = await target.count();
  if (outcome.kind === "visible") return { value: count === 1 ? await target.isVisible() : false, missing: count === 0, count };
  if (count !== 1) return { value: null, missing: count === 0, count };
  switch (outcome.kind) {
    case "text": return { value: normalizeText(await target.textContent({ timeout: timeoutMs })), missing: false, count };
    case "checked": return { value: await target.isChecked({ timeout: timeoutMs }), missing: false, count };
    case "attribute": return { value: await target.getAttribute(outcome.attribute, { timeout: timeoutMs }), missing: false, count };
    default: throw new Error(`Unsupported observable outcome: ${outcome.kind}`);
  }
};

const inspectControl = async (control) => {
  const count = await control.count();
  if (count === 0) return "action control not found";
  if (count > 1) return `action control name is ambiguous (${count} matches)`;
  if (!(await control.isVisible())) return "action control is not visible";
  if (!(await control.isEnabled())) return "action control is disabled";
  return null;
};

// Captures the pre-state, activates the control by role and name, waits for
// the expected post-state (bounded, no fixed sleep), and returns a report.
export const runActionAssertion = async (page, contract) => {
  const { ruleId, action, outcome, timeoutMs } = validateActionContract(contract);
  const control = semantic(page, action);
  const controlIssue = await inspectControl(control);
  const pre = await observeOutcome(page, outcome, timeoutMs);
  const premise = evaluateActionPremise({ ruleId, action, outcome, pre, controlIssue });
  if (premise) return premise;

  await control.click({ timeout: timeoutMs });

  const appeared = await expect(async () => {
    const observed = await observeOutcome(page, outcome, timeoutMs);
    expect(matchesExpected(observed.value, outcome.expected), `observed ${JSON.stringify(observed.value)}`).toBe(true);
  }).toPass({ timeout: timeoutMs }).then(() => true, () => false);
  const post = await observeOutcome(page, outcome, timeoutMs);
  return { ...evaluateActionOutcome({ ruleId, action, outcome, pre, post }), waitedForExpected: appeared };
};

export const expectActionOutcome = async (page, contract) => {
  const report = await runActionAssertion(page, contract);
  expect(report.status, `${formatReport(report)} (pre=${JSON.stringify(report.pre?.value)}, post=${JSON.stringify(report.post?.value)})`).toBe(STATUS.PASS);
  return report;
};

// Diagnostic inventory, not a no-op detector: the enabled, rendered action
// controls on the page, by role and accessible name, for comparison against a
// declared inventory. Matching an element proves nothing about its handler.
export const inventoryVisibleActions = (page) =>
  page.locator('button:not([disabled]), input[type="button"]:not([disabled]), input[type="submit"]:not([disabled]), a[href], a[role="button"]:not([aria-disabled="true"]), summary')
    .evaluateAll((elements) => elements.filter((el) => {
      const rect = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none";
    }).map((el) => ({
      tag: el.tagName.toLowerCase(),
      nameHint: (el.getAttribute("aria-label") || el.textContent || el.getAttribute("value") || "").replace(/\s+/g, " ").trim(),
      type: el.getAttribute("type") || "",
      url: el.getAttribute("href") || null
    })));
