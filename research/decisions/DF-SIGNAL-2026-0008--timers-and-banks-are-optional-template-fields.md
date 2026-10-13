---
id: DF-SIGNAL-2026-0008
title: Timers and question banks are optional template fields, written only when present, so existing templates keep their exact bytes and hashes
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
  - research/decisions/DF-SIGNAL-2026-0005--respondents-read-published-templates-from-a-static-site-catalog.md
  - research/decisions/DF-SIGNAL-2026-0007--the-survey-page-names-its-page-in-the-respondent-link.md
  - docs/requirements/implementation-gap-analysis.md
tags: [template, canonical, timers, banking, compatibility]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T09:49:16.277Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Timers and banks as optional template fields (ARX-013, WI-0081)"
---

# DF-SIGNAL-2026-0008: Timers and banks in the template

- **Date:** 2026-10-13
- **Status:** accepted. The Signal agent recommended it and the coordinator
  approved it on 2026-10-13.
- **Work item:** WI-0081. **Requirements:** ARX-013 (timers, content
  banking), CAN-001 (the canonical template form).

## Context

WI-0045 built a pure timer model (`Timers`) and reproducible bank selection
(`Banking`), but a template could not carry either. A published template is
identified by the SHA-256 of its canonical bytes, and the survey page
verifies those bytes against the link's reference (DF-SIGNAL-2026-0005).
Changing the bytes of templates already published would orphan every link
and submission that names them.

## Decision

1. **Optional fields in `presentation`.** A template's presentation can hold
   `timers` and `banks`. Each is written only when non-empty. A template
   without them writes exactly the bytes it wrote before, so its hash and
   reference are unchanged. A test pins the full SHA-256 of every committed
   published template.
2. **Canonical spelling.**
   - Timer: `{id, scope ("survey" | "section" | "page" | "question"), target
     (absent for "survey"; a section id, question id or 1-based page number),
     seconds, warnings (seconds remaining, largest first), expiry
     ("lock-and-advance" | "lock" | "warn" | "end-survey"), enforcement
     ("advisory" | "enforced")}`.
   - Bank: `{id, pool (question ids in template order), select}`.
   - The decoder reads exactly this and the canonical check refuses any other
     spelling.
3. **Checked at publication.** Duplicate timer or bank ids are blockers.
   So are a timer that isn't positive or doesn't count whole seconds, a
   warning outside the duration, and a timer naming a missing section,
   question or page. For banks, a selection out of range, a pool naming a
   missing question, or a question in two banks is a blocker.
4. **Version diffs.** A timer change is `TimersChanged`, with no scoring
   impact. A bank change is `BanksChanged`, which can move scores because it
   changes which questions an instance answers.
5. **Not run yet, so refused.** Until the survey page enforces timers and
   applies bank selection with a recorded seed, it refuses a template with
   either: "This survey uses timers, which this page cannot run yet." Shown
   without them, it would not be the survey that was published.
6. **The selection seed is not decided here.** How an instance's bank
   selection seed is recorded is the next decision. The anonymous submission
   binding gets a fresh id, so the seed cannot simply be the instance id. That
   decision comes with the page wiring.
