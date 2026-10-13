---
id: DF-SIGNAL-2026-0025
title: The draft is previewed in the console by the survey page's own engine, as a test artifact that stores and sends nothing, scored live
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
tags: [authoring, preview, test-mode, scoring]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created, modified]
      at: 2026-10-13T23:38:04.424Z
      last: 2026-10-13T23:43:54.512Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Draft previewed in the console by the survey page engine (WI-0081)"
---

# DF-SIGNAL-2026-0025: The draft previewed in the console

- **Date:** 2026-10-13
- **Status:** accepted. These are routine design choices, taken under the coordinator's standing approval.
- **Work items:** WI-0081 (decided here) and WI-0059 (owns confidence). **Requirements:** AUT-003 §§20-24, SCS-017.

## Decision

1. **The survey page's own engine.**
   - "Preview as a respondent" runs the draft through `GenericSession`, the engine the survey page uses. It starts from a test link to the draft's canonical bytes, under the version name `draft-preview`.
   - The console renders `GenericSessionView.view` under names prefixed `preview`, with the survey page's own row markup.
   - Pagination, section transitions, selectors, conditional questions and sections, validation messages, progress, the review before submitting and completion therefore behave exactly as they will once published.
   - A question kind or timer the page cannot run is refused here as it will be there.
2. **A test artifact that goes nowhere** (§§20-21):
   - The binding is marked test, so production import would refuse it (`TestSubmission`).
   - Its link is never shown or written.
   - Nothing is stored or sent: tests hold the store's commit count unchanged through answering, keeping and submitting.
   - Submitting seals the preview and says that nothing was sent.
3. **Scored live** (§22, SCS-017): `ScoringPreview` explains the preview's answers after every change, as it does for fixtures (DF-SIGNAL-2026-0024). An author can therefore preview every selector and scorer combination publication accepts, with answers of their choosing.
4. **Kept as a fixture** (§24):
   - "Keep these answers as a fixture" adds `preview-n`.
   - The fixture asserts what the answers score now: completion, each scored section, and the overall score. A later change that alters them fails validation until the author accepts it.
5. **A changed draft ends the preview.** It would otherwise show the old draft. Starting again previews the new one.
6. **No clock in the console's update.** Timers run from the dataset's last verification and do not count down in the preview, and the preview says so.

## Scope

- **Confidence** (AUT-003 §22, SCS-017's "coverage/confidence sidecars") is a group measure in Signal, owned by **WI-0059** (group confidence; see `.ros/work/queue.json`). It is not shown per response. Coverage is shown per response, in the preview and in each fixture's explanation.
- AUT-003 and SCS-017 are tested for that stated scope; their rows say so.
