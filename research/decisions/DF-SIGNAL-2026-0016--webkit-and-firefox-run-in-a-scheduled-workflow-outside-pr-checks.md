---
id: DF-SIGNAL-2026-0016
title: The survey page runs on real WebKit (iPhone) and Firefox in a scheduled and manual workflow, outside the per-pull-request checks, with bounded apt
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
  - research/decisions/DF-SIGNAL-2026-0014--sample-links-are-checked-on-a-phone-and-against-the-url-budget.md
  - docs/requirements/implementation-gap-analysis.md
tags: [ci, browsers, devices, survey-page]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T12:14:09.584Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-browser workflow outside PR checks (AUT-006 62, WI-0081)"
---

# DF-SIGNAL-2026-0016: Cross-browser sample-link workflow

- **Date:** 2026-10-13
- **Status:** accepted. The coordinator chose option (b) on 2026-10-13.
- **Work item:** WI-0081. **Requirements:** AUT-006 §62, CAN-004 §26.

## Decision

1. **Separate workflow.** `.github/workflows/cross-browser.yml` runs weekly
   and by hand, never on pull requests or pushes, so it never holds up PR
   checks.
2. **Bounded apt.** Browsers download with an 8-minute limit. Their OS
   libraries install with 30-second request timeouts, 3 retries and a
   10-minute overall deadline. A failure there is reported as environmental
   ("could not be installed"), not as a test failure.
3. **What runs.** The survey, timer and device specs run on WebKit with
   iPhone 13 emulation; the survey and timer specs run on Firefox desktop.
   The projects exist only when `SIGNAL_CROSS_BROWSER=1`, so the PR suite is
   unchanged.
4. **The URL budget** (2,000 characters) stays provisional until this
   workflow passes (DF-SIGNAL-2026-0014).
