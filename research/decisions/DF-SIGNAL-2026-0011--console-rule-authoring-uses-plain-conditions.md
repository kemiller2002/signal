---
id: DF-SIGNAL-2026-0011
title: The console authors facts, recommendations and completion from plain conditions (a question's named answer, or a fact); custom expressions stay out of the console
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
tags: [authoring, rules, facts, recommendations, completion]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T10:39:48.947Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Console rule authoring vocabulary (AUT-002, WI-0081)"
---

# DF-SIGNAL-2026-0011: Rule authoring in the console

- **Date:** 2026-10-13
- **Status:** accepted. A routine design choice the Signal agent made under
  the coordinator's standing approval (2026-10-13).
- **Work item:** WI-0081. **Requirements:** AUT-002 (rule, fact,
  recommendation and completion authoring).

## Decision

1. **One condition vocabulary.** Every authored rule uses the condition the
   console already offers for conditional questions: a question has a named
   answer (as respondents see it), or a yes/no fact is true. Conditions are
   not combined, and there are no comparisons or expressions. The engine's
   full rule model is unchanged, and imported templates can still use all of
   it.
2. **Facts** are yes/no facts (`BooleanFact`), ids `F1`, `F2`, and so on.
   **Recommendations** have a priority (informational, suggested, important,
   critical), a title and a description, ids `R1`, `R2`, and so on, with
   category `authored`. A recommendation on a question names that question
   as related.
3. **Completion** can require a whole-number percentage (1 to 100) of
   applicable questions to be answered, and name sections that must be fully
   answered. The base rule always applies: every applicable required
   question must be answered.
4. **Removing a question** removes the facts, recommendations and
   completion condition that mention it, and in turn anything that mentions
   a removed fact. Nothing is left dangling for publication to refuse.
5. **Custom expressions are not authored in the console.** They need a
   reviewable editor and trace preview (AST-005, ALG-004, SCS-017), which
   remain open.
