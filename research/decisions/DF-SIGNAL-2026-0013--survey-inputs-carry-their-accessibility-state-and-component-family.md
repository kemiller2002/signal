---
id: DF-SIGNAL-2026-0013
title: Survey page inputs carry their question, required and invalid state programmatically, and each question declares its component family for the design system's handoff
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
tags: [accessibility, selectors, survey-page, design-system]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T11:15:03.989Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Survey page selector semantics and component handoff (SCS-014, SCS-018, WI-0081)"
---

# DF-SIGNAL-2026-0013: Selector semantics on the survey page

- **Date:** 2026-10-13
- **Status:** accepted. A routine design choice the Signal agent made under
  the coordinator's standing approval.
- **Work item:** WI-0081. **Requirements:** SCS-014 (accessibility,
  interaction and state semantics), SCS-018 (web component handoff),
  SCS-010 (no default answers).

## Decision

1. **Programmatic state.**
   - Every survey input names its question through `aria-describedby`
     (`q<n>-prompt`). That reference includes the question's error
     (`q<n>-error`) when it has one.
   - Every input states `aria-required` and `aria-invalid`.
   - Each input's accessible name is its own option label.
2. **Errors in words.**
   - After a refused submit or page move, each unanswered required
     applicable question shows "This question needs an answer." and stays
     marked until it is answered.
   - An optional question shows "(optional)".
   - Nothing relies on color, position or animation alone.
3. **The engine decides.** Required, invalid and applicable come from the
   F# session; the page only binds them.
4. **Component handoff.** Each question row declares `data-component` and
   `data-variant` from `Selectors.componentOf`, so the design system's web
   components can take over a family without changing what the engine
   decides. The components themselves are the design system's work.
5. **No default answers.** No input starts checked or filled; a browser
   test holds this.
