---
id: DF-SIGNAL-2026-0024
title: The draft editor explains each fixture's scoring from the one evaluation that scores it; answer-key reasoning comes from the function that produces the keyed number
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
tags: [authoring, scoring, explainability, preview]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T23:07:31.958Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Fixture scoring explained from the one evaluation (WI-0081)"
---

# DF-SIGNAL-2026-0024: Fixture scoring explained from the one evaluation

- **Date:** 2026-10-13
- **Status:** accepted. These are routine design choices, taken under the coordinator's standing approval.
- **Work item:** WI-0081. **Requirements:** AUT-006 §63, AST-005 §22, SCS-017, AUT-003 §§22-23.

## Decision

1. **One evaluation.**
   - `ScoringPreview.explain` renders `SurveyResult.compute`'s own record: section traces, the composite or expression trace, interpretation outcomes, facts, triggered recommendations, coverage and completion. It never scores again (ARX-012, AST-005 §22).
   - The aggregate written out as numerator over denominator uses the trace's included values. A property test holds it equal to the trace's aggregate for mean, sum, weighted mean, weighted sum and percentage of maximum.
2. **Answer keys explain themselves from the same function.**
   - `Template.keyedExplanation` is the single path for single- and multi-choice keys. `keyedNumber` takes its number from it, so the credit and penalty shown are the ones the score used (SCS-004 "Explainability").
3. **The rule trace** names each question or section the answers leave out, with the flow rule that did it, and an early end with its reason. Facts and triggered recommendations are listed with their conditions.
4. **Where it shows:** the draft editor, for each fixture (the first by default, "Explain" on any other). Nothing is stored.
5. **Not here:**
   - A per-response **confidence** sidecar: confidence is a group measure (WI-0059), and the explanation shows coverage.
   - **Any answers the author chooses**, rather than fixtures, and the respondent **preview mode** (AUT-003 §§20-22): these come with the in-console draft preview, the next WI-0081 slice. SCS-017 and AUT-003 stay partial until then.
