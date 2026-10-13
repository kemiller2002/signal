---
id: DF-SIGNAL-2026-0018
title: Unfinished compound input is held on the survey page, not answered and not in the link, so the respondent link always equals the response; no link-format change
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
  - docs/requirements/SIGNAL-DEEP-LINKING.md
  - docs/requirements/implementation-gap-analysis.md
tags: [respondent, url, compound-answers, defect]
provenance:
  contributions:
    EXE-20261013T133239182Z-77fbd18e:
      operations: [created]
      at: 2026-10-13T13:45:05.839Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Unfinished compound input held, not answered (WI-0086)"
---

# DF-SIGNAL-2026-0018: Unfinished compound input

- **Date:** 2026-10-13
- **Status:** accepted. The coordinator allowed either encoding partial
  answers or not counting them, whichever needs no breaking link change.
  The Signal agent chose not counting them.
- **Work item:** WI-0086 (escaped defects from PR #73). **Requirements:**
  LURL-001 (the URL equals the response), SIG-LINK-013, SCS-013.

## Context

The envelope encodes only answers the decoder accepts:
- a whole ranking;
- a range with both ends in order;
- best and least both picked;
- an allocation that shares exactly its points.

Since #73, the page counted unfinished values as answers. The link silently
left them out (rankings, ranges, best-worst), or carried a value the decoder
then refused, so the whole link was refused on reopening (allocations).
Ranking positions typed out of order also collapsed.

## Decision

1. **No link-format change.** Encoding partial values would need new slots
   and would change every ranking, range and allocation template's layout,
   which is a breaking change. Instead, unfinished input is **held** on the
   page (`Response.Pending`, `GenericInputs.Held`). It is not an answer, is
   not counted and is not in the link. The link therefore always equals the
   response, with exactly the answers the session holds.
2. **"Encodable" is the decoder's rule:** an index exists and
   `Primitives.check` accepts the value. Anything else is held.
3. **Rankings are held as typed positions**, so they can be typed in any
   order. Typing a position another option holds moves it. A ranking becomes
   an answer when every position is filled.
4. **Allocations carry every option**, a blank at the minimum, so a finished
   allocation round-trips exactly.
5. **Said, not hidden.** A held question shows "Not in the link yet: finish
   this answer, or clear it, to keep it.", described by its inputs.
   Submitting is refused while anything is held. Choosing a special state
   such as "Prefer not to answer" replaces what was held.
6. **What a reload loses.** Held input is not in the link, so a reload or a
   copied link opens without it. The page says so while it is held.
