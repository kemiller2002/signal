---
id: DF-SIGNAL-2026-0010
title: A survey timer runs from a start minute carried in the respondent link's integrity-checked terms, stamped at first open; anonymous links gain only that minute
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
  - research/decisions/DF-SIGNAL-2026-0006--respondent-links-carry-locale-and-expiry-in-link-format-2.md
  - research/decisions/DF-SIGNAL-2026-0008--timers-and-banks-are-optional-template-fields.md
  - docs/requirements/SIGNAL-DEEP-LINKING.md
  - docs/requirements/implementation-gap-analysis.md
tags: [timers, respondent, url, integrity, privacy, anonymity]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T10:26:07.764Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Survey timer start in the integrity-checked link (ARX-013, WI-0081)"
---

# DF-SIGNAL-2026-0010: The survey timer's start in the link

- **Date:** 2026-10-13
- **Status:** accepted. The coordinator chose option (a) on 2026-10-13: the
  start goes in the integrity-checked link.
- **Work item:** WI-0081. **Requirements:** ARX-013 (timers), VER-003,
  SIG-LINK-013, LURL-002, SIG-LINK-008.

## Context

An enforced timer must survive a reload, and the respondent link is the only
state the survey page and intake share. A start held in the browser could be
reset by clearing storage or switching devices.

## Decision

1. **A new optional term.** Link format 2's terms flags gain bit 2: the
   start, written as 4 bytes, big-endian, counting whole minutes since
   1970-01-01 UTC. It comes after the locale and expiry. Unknown bits (3 to
   7) are still refused. A link without terms is still format 1, byte for
   byte, and every existing format-1 and format-2 link decodes as before.
2. **Stamped at first open.** When a template with a survey timer is opened
   from a link without a start, and the response isn't submitted, the page
   stamps the current UTC minute. It then rewrites the address (a replace, not
   a new history entry). A reload or a copied link carries the same start.
3. **Inside the integrity check.** The start is inside the bytes covered by
   the link's integrity check and the submission hash, so editing it refuses
   the link. This is an integrity check, not a signature (as
   DF-SIGNAL-2026-0006 records): someone who rebuilds the check could forge
   a start. A signed authenticity policy remains WI-0060.
4. **Coarsened for privacy.** The start is a whole UTC minute, never finer.
   An anonymous link gains nothing else; its submission keeps the same
   minute and its fresh unlinkable id (LURL-002). The issuer never sees the
   start, because it is stamped on the respondent's device after the
   invitation is issued.
5. **What the page runs.**
   - It runs one survey-wide timer. It shows the deadline as a UTC time, so
     no ticking clock is needed, and warns when a warning threshold is
     reached.
   - When an enforced timer with lock-and-advance, lock or end-survey expiry
     runs out, every answer is held; submitting stays subject to the
     survey's rules.
   - An advisory timer only says the time ran out.
   - Section, page and question timers would each need their own start, so
     templates with them are still refused, as is a template with more than
     one timer.
6. **Not yet at intake.** A submission doesn't say when it was submitted, so
   intake doesn't judge timers. Answers made after an enforced expiry are
   prevented on the page, not at intake. That remains open.
7. **Contract.** SIG-LINK 1.3.0 adds SIG-LINK-015.
