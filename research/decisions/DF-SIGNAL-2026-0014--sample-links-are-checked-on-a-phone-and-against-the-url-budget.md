---
id: DF-SIGNAL-2026-0014
title: Published sample links are checked on an emulated phone and against the URL budget; the supported browser and device matrix is an open product decision
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
tags: [survey-page, devices, urls, testing]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T11:29:33.596Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Sample link checks on a phone and the URL budget (AUT-006, WI-0081)"
---

# DF-SIGNAL-2026-0014: Cross-device checks of sample links

- **Date:** 2026-10-13
- **Status:** accepted for the parts below. The Signal agent made these
  routine choices under the coordinator's standing approval. Decision 3 is
  the coordinator's to take.
- **Work item:** WI-0081. **Requirements:** AUT-006 §61 (sample URLs for
  phones, email, messaging, copy and paste), §62 (cross-device check
  before an encoding is accepted), CAN-004 §26.

## Decision

1. **Every sample link the console gives is checked**, for every demo
   survey the site publishes:
   - its payload uses only base64url characters, so mail and chat clients
     have nothing to re-encode;
   - it fits the template's capacity;
   - the full respondent URL fits the publication policy's budget
     (`Validation.defaultPolicy.MaximumUrlCharacters`, 2,000).
2. **A phone is emulated in the browser suite.** A `mobile` Playwright
   project (Pixel 7: touch, a narrow viewport, a mobile user agent) opens
   each demo survey's link, checks that it fits without sideways scrolling,
   and answers and submits a survey by touch.
3. **Open: the supported matrix.** Real WebKit (iPhone Safari) and Firefox
   are not run. They need OS libraries the CI image lacks, and installing
   them would bring back apt in CI, which hung the build before (signal#21,
   signal#22). Which browsers and devices Signal supports, and so which
   engines §62 must run, is a product decision. Until it is made, the
   2,000-character budget stays provisional, as `Validation` says.
