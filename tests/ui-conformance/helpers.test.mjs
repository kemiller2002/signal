// The pure decisions in tests/browser/ui-conformance-helpers.mjs, without a
// browser: missing evidence is UNKNOWN (never PASS), the 1 CSS px tolerance
// applies only to a declared group, and an action passes only on an observed
// transition (UI-LAYOUT-INTERACTION@1.0.0). Run with `node --test`.
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  STATUS,
  evaluateAlignment,
  evaluateActionOutcome,
  evaluateHorizontalOverflow,
  validateActionContract,
  validateAlignmentGroup
} from "../browser/ui-conformance-helpers.mjs";

const box = (y, x = 0) => ({ x, y, width: 40, height: 20 });
const group = { id: "pair", peers: [{ role: "radio", name: "A" }, { role: "radio", name: "B" }] };

test("a declared group within 1 CSS px passes, beyond it fails", () => {
  const observe = (a, b) => group.peers.map((peer, i) => ({ peer, count: 1, box: box([a, b][i]) }));
  assert.equal(evaluateAlignment(group, observe(10, 11)).status, STATUS.PASS);
  const failed = evaluateAlignment(group, observe(10, 11.5));
  assert.equal(failed.status, STATUS.FAIL);
  assert.equal(failed.deltaCssPx, 1.5);
});

test("a missing, ambiguous or unrendered peer is UNKNOWN, never PASS", () => {
  for (const second of [{ count: 0, box: null }, { count: 2, box: null }, { count: 1, box: null }, { count: 1, box: { x: 0, y: 0, width: 0, height: 0 } }]) {
    const report = evaluateAlignment(group, [{ peer: group.peers[0], count: 1, box: box(0) }, { peer: group.peers[1], ...second }]);
    assert.equal(report.status, STATUS.UNKNOWN);
    assert.equal(report.deltaCssPx, null);
  }
});

test("a group must be declared: an id, two semantic peers and a ULI-L rule", () => {
  assert.throws(() => validateAlignmentGroup({ peers: group.peers }));
  assert.throws(() => validateAlignmentGroup({ id: "x", peers: [group.peers[0]] }));
  assert.throws(() => validateAlignmentGroup({ id: "x", peers: [{ role: "radio" }, { role: "radio", name: "B" }] }));
  assert.throws(() => validateAlignmentGroup({ ...group, ruleId: "ULI-A01" }));
  assert.throws(() => validateAlignmentGroup({ ...group, peers: [{ ...group.peers[0], measure: "box" }, group.peers[1]] }));
  assert.equal(validateAlignmentGroup({ ...group, edge: "left" }).edge, "left");
});

test("an action passes only when the expected post-state appears and differs from the pre-state", () => {
  const contract = validateActionContract({ action: { role: "button", name: "Go" }, outcome: { kind: "text", target: { role: "status", name: "s" }, expected: "done" } });
  const run = (pre, post) => evaluateActionOutcome({ ...contract, pre: { value: pre, missing: false, count: 1 }, post: post === undefined ? null : { value: post } });
  assert.equal(run("idle", "done").status, STATUS.PASS);
  assert.equal(run("done", "done").status, STATUS.FAIL, "a pre-state that already matches proves nothing");
  assert.equal(run("idle", "idle").status, STATUS.FAIL);
  assert.equal(run("idle").status, STATUS.FAIL);
  assert.equal(evaluateActionOutcome({ ...contract, pre: { value: null, missing: true }, post: null }).status, STATUS.UNKNOWN);
  assert.equal(evaluateActionOutcome({ ...contract, controlIssue: "action control is disabled", pre: { value: "idle" }, post: null }).status, STATUS.UNKNOWN);
});

test("an action contract needs a semantic control, a ULI-A rule and an explicit expected post-state", () => {
  assert.throws(() => validateActionContract({ action: { role: "button" }, outcome: { kind: "url", expected: "x" } }));
  assert.throws(() => validateActionContract({ ruleId: "ULI-L01", action: { role: "button", name: "Go" }, outcome: { kind: "url", expected: "x" } }));
  assert.throws(() => validateActionContract({ action: { role: "button", name: "Go" }, outcome: { kind: "visible", target: { role: "alert", name: "a" }, expected: "yes" } }));
});

test("horizontal page scroll fails, none passes, no rendered width is unknown", () => {
  assert.equal(evaluateHorizontalOverflow({ surface: "s", viewportWidth: 320, scrollWidth: 320, clientWidth: 320 }).status, STATUS.PASS);
  assert.equal(evaluateHorizontalOverflow({ surface: "s", viewportWidth: 320, scrollWidth: 340, clientWidth: 320, offenders: ["table"] }).status, STATUS.FAIL);
  assert.equal(evaluateHorizontalOverflow({ surface: "s", viewportWidth: 320, scrollWidth: 0, clientWidth: 0 }).status, STATUS.UNKNOWN);
});
