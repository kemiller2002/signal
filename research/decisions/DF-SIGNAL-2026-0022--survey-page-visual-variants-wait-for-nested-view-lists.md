---
id: DF-SIGNAL-2026-0022
title: The survey page's visual selector variants wait for nested view lists in Limen; the native accessible alternatives remain the tested behaviour
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
tags: [survey-page, selectors, design-system, limen, scope]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T19:56:24.062Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Visual selector variants deferred until Limen nested view lists (limen#108); WI-0081"
---

# DF-SIGNAL-2026-0022: Visual selector variants wait for nested view lists

- **Date:** 2026-10-13
- **Status:** accepted. The coordinator chose this option (c), with option (b) as the real fix, and rejected (a).
- **Work items:** WI-0081 (decided here) and WI-0099 (the follow-up). **Requirements:** SCS-010, SCS-012, SCS-018.
- **Upstream:** [kemiller2002/limen#108](https://github.com/kemiller2002/limen/issues/108), nested view lists.

## Context

SCS-010, SCS-012 and SCS-018 ask the survey page to present each selector in its component family and variant: dropdowns, sliders, buttons, cards, chips, stars, responsive matrices, and so on. Forma 0.6.1 ships these as zero-runtime assessment patterns (`.ef-ordinal-scale`, `.ef-binary-choice`, `.ef-choice-group`, `.ef-matrix` and the rest). Every one of them is built around one container per question, with that question's native inputs inside it.

Limen 0.9.0, which renders the page, cannot represent that structure. Its API reference (*View types*) says: "`ViewItem` admits only primitives, so **nested lists are not representable**." That is why the survey page projects one flat `rows` list, in which section headings, questions and options are siblings.

## Options

- **(a) Fixed option slots per question row** (say 12) inside Forma's markup. Rejected: it caps options per question, which the requirements do not, and longer lists would need a second rendering path.
- **(b) Nested view lists in Limen**, then adopt Forma's patterns. This is the real fix. It is requested upstream in limen#108 and tracked by WI-0099.
- **(c) Keep the native accessible alternatives now.** Accepted.

## Decision

1. **The survey page keeps rendering every selector as its native accessible alternative.** That means radios, checkboxes, number inputs, and two radio scales for best-worst. Each question declares its family and variant (`data-component`, `data-variant`) for the handoff (DF-SIGNAL-2026-0013).
2. **These remain the tested behaviour:**
   - every input starts unanswered, and nothing is auto-selected (no dropdown default, no slider value until one is entered);
   - every input is keyboard-operable and named (SCS-014);
   - multi-choice counts and exclusive options hold (SCS-011);
   - matrix rows are ordinary questions with the same answers and scoring (SCS-012's semantics).
3. **The visual-variant remainder is deferred.** That covers the dropdown, slider, button, card, chip, star and icon looks, and the responsive matrix. SCS-010, SCS-012 and SCS-018 **stay partial**. WI-0099 names them, so the gap analysis keeps them planned. WI-0099 starts when limen#108 lands, and must not use fixed slots.

## Consequences

- No requirement is marked tested on the strength of this decision. The rows say exactly what is deferred and why.
- When Limen supports nested lists, `GenericSessionView` projects questions with their options nested, and the page adopts the Forma patterns. The engine's answers, links and scoring do not change: only the projection and markup do.
