---
id: DF-SIGNAL-2026-0019
title: A changed interpretation threshold, band bound, direction, stage condition or target is a scoring change and not comparable; a change to labels or order only is comparable with caution
status: accepted
version: 1.0.0
created: 2026-10-13
updated: 2026-10-13
owners:
  - signal
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/implementation-gap-analysis.md
tags: [authoring, diff, comparability, interpretation, defect]
provenance:
  contributions:
    EXE-20261013T144827880Z-0fdeea60:
      operations: [created]
      at: 2026-10-13T14:51:13.709Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Interpretation thresholds not comparable, labels a caution (WI-0088)"
---

# DF-SIGNAL-2026-0019: Interpretation changes and comparability

- **Date:** 2026-10-13
- **Status:** accepted. The coordinator approved the split as a correctness fix that follows the source (AUT-005 §45, §48). The Signal agent chose how each interpretation field is classified.
- **Work item:** WI-0088 (escaped defect from PR #30, WI-0044). **Requirements:** AUT-005 §§44-48 and §55, RPT-003 §23.

## Context

`TemplateDiff` reported any change to a template's interpretations as one kind, `InterpretationsChanged`. It treated that kind as having no scoring impact and as "comparable with caution". Interpretations are the bands, pass/fail and maturity stages that turn a score into an outcome.

The source lists "threshold changed" and "maturity rule changed" among the changes that can alter results (§45). It lists "thresholds changed" as a reason two versions are not comparable (§48). So a version that moved a band bound still showed `Breaking Scoring Changes: No` in the publication summary (§55), and `Report.comparable` let reports compare across it.

## Decision

1. **Two kinds replace one.**
   - `InterpretationThresholdsChanged` has scoring impact. Its comparability is `NotComparable`, with the reason "interpretation thresholds changed".
   - `InterpretationLabelsChanged` has no impact. Its comparability is `ComparableWithCaution`, with the reason "interpretation labels changed".
2. **What decides the outcome is compared without labels.** Each interpretation is reduced, keyed by id, to its target and its rule, and the rule depends on the kind:
   - bands: the bounds;
   - pass/fail: the threshold and direction;
   - stages: the conditions, in order.

   If these differ, the change is to thresholds. If only the full lists differ, the change is to labels.
3. **Each field's classification:**

   | Field changed | Classified as |
   |---|---|
   | Band bound | thresholds |
   | Pass/fail threshold | thresholds |
   | Pass/fail direction | thresholds |
   | Stage condition, or a stage added or removed | thresholds |
   | Target | thresholds |
   | Kind | thresholds |
   | Interpretation added or removed | thresholds |
   | Interpretation id renamed | thresholds |
   | Band label | labels |
   | Stage label | labels |
   | Stage baseline label | labels |
   | Order of interpretations only | labels |

   The id names the result a reader sees, so a rename counts as a removal plus an addition. A new interpretation adds an outcome that an older version's results do not have.
4. **No stored data changes, and no wire format changes.**
   - `TemplateDiff.Change` is computed and never serialized, and no published contract carries it.
   - Comparability is computed when it is asked for: by the publication preview, and by `Report.comparable`.
   - No stored artifact records it:
     - The publication summary is not stored.
     - Every report built by the console passes no comparison inputs (`AdminReportView`, `ReportWork`), and stored snapshots carry an empty `comparisons` array and empty `ComparisonReferences`.
   - Nothing is rewritten and no hash moves.
   - If a later report stores comparisons, it records what was computed when the report was taken. Any re-reading would apply this rule at read time, as DF-SIGNAL-2026-0017 does for CSV.

## Consequences

- A template version that only moves a threshold shows as a breaking scoring change and is not comparable to its parent. One that only renames a band does not.
- `InterpretationDiffTests` covers each row of the table.
