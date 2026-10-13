---
id: DF-SIGNAL-2026-0007
title: The survey page names the page shown in the respondent link as p, outside the envelope; a link names a page but never grants one
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
  - research/decisions/DF-SIGNAL-2026-0003--deep-linking-through-limen-routing.md
  - research/decisions/DF-SIGNAL-2026-0006--respondent-links-carry-locale-and-expiry-in-link-format-2.md
  - docs/requirements/SIGNAL-DEEP-LINKING.md
  - docs/requirements/implementation-gap-analysis.md
tags: [respondent, url, pagination, navigation, history]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T08:56:25.802Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "The page in the respondent link as p (VER-004, ARX-013, WI-0081)"
---

# DF-SIGNAL-2026-0007 — The page in the respondent link

- **Date:** 2026-10-13
- **Status:** accepted. Decided by the Signal agent on the owner's standing
  instruction ("take the recommendation and continue"), reported to the
  coordinator.
- **Work item:** WI-0081. **Requirements:** VER-004, ARX-013 (browser
  back/forward), SIG-LINK-001 (navigable state in the URL), SIG-LINK-008,
  SIG-LINK-013.

## Context

The survey page shows a paginated template one page at a time (WI-0081).
Which page is shown is navigable state: a reload, a copied link, browser
Back and Forward should bring it back (SIG-LINK-001). ARX-013 asks for
browser back/forward that obeys the same navigation policy as the page's own
Back and Next. The answer envelope (`#r=`) is a contract (SIG-LINK-013) whose
bytes, integrity check and submission hash must not change.

## Decision

1. **`p` beside `r`.** The fragment carries the page shown as `p`, 1-based,
   after the answer code: `#r=<envelope>&p=<n>`. The first page is the
   default and is left out. Only a whole number from 2 without a leading zero,
   written once, names a page. Anything else names no page and is
   rewritten to the page actually shown. The envelope is unchanged: format 1
   and format 2 links are byte for byte what they were, and readers that
   ignore other parameters (`LiveUrl.read`, intake) are unaffected.
2. **A link names a page; it never grants one.** The page is reached by
   moving from the response's resume position one page at a time under the
   template's rules (`GenericPaging.reach`). A link can't skip a required
   answer before a locking boundary or open a page behind a lock. It
   opens the nearest page the rules allow, and the address is rewritten to it.
3. **History.** Back and Next on the page add a history entry (Limen
   Navigation `push`); answering replaces the current entry (LURL-001, as
   before). Browser Back and Forward to a link of the same template and
   binding are moves between pages under the same rules. The answers held
   now stand (an earlier entry holds earlier answers), and the entry is
   rewritten to them. A link with another binding or template starts that
   response, as before.
4. **Submissions carry no page.** A submitted response's link is the envelope
   alone, exactly as before WI-0081, so submission links, their hashes and
   intake are unchanged.
5. **Privacy.** `p` is a page number. It carries no answer and nothing about
   the respondent (SIG-LINK-008).
6. **Contract.** SIG-LINK 1.2.0 adds SIG-LINK-014 for the page parameter.
   SIG-LINK-013's formats are unchanged.

## Consequences

- Reloading, bookmarking or sharing an unfinished response returns to the
  same page.
- The page parameter is not integrity-checked. That is acceptable because
  it grants nothing (decision 2): editing it can only ask for a page the
  rules already allow.
