---
id: DF-SIGNAL-2026-0015
title: Section and page timer starts and a timed response's submission minute travel in the respondent link's integrity-checked terms; intake flags late submissions and never refuses them
status: accepted
version: 1.1.0
created: 2026-10-13
updated: 2026-10-13
owners:
  - signal
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SIGNAL-2026-0010--a-survey-timer-starts-from-a-minute-in-the-integrity-checked-link.md
  - research/decisions/DF-SIGNAL-2026-0012--results-keep-triggered-recommendation-and-fact-ids.md
  - docs/requirements/SIGNAL-DEEP-LINKING.md
  - docs/requirements/implementation-gap-analysis.md
tags: [timers, respondent, url, integrity, privacy, anonymity, intake]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created, modified]
      at: 2026-10-13T12:01:39.943Z
      last: 2026-10-13T22:09:35.063Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Section and page timers and the submission minute (ARX-013, WI-0081)"
---

# DF-SIGNAL-2026-0015: Section and page timers, and intake's timer flags

- **Date:** 2026-10-13
- **Status:** accepted. The coordinator approved all three recommendations
  on 2026-10-13: section and page timers as recommended, intake flagging
  rather than refusing, and the submission minute.
- **Work item:** WI-0081. **Requirements:** ARX-013 (timers), SIG-LINK-013,
  SIG-LINK-008, LURL-002, ARP-001.

## Decision

1. **Starts per scope.** Link format 2's terms flags gain bit 3: a count
   (1 to 255), then for each started section or page timer a kind byte
   (1 = section, by its position in the template; 2 = layout page, by its
   1-based number), a 2-byte index, and the start as 4 bytes of whole
   minutes since 1970 UTC.
   - Entries are sorted by kind then index, each scope at most once.
   - Any other spelling is refused, and unknown flag bits (5 to 7) are still
     refused.
   - A link without these terms is unchanged, so every older link still
     opens.
2. **Stamped on first reach.** The page stamps a section or page timer's
   start the first time a page holding one of its questions is shown, and
   rewrites the address. A recorded start never moves, so a reload keeps it.
   The survey timer is unchanged (DF-SIGNAL-2026-0010).
3. **Locks by coverage.** An enforced timer that runs out holds the answers
   of the questions it covers: every question for the survey timer, its
   section's for a section timer, its layout page's for a page timer.
   - The page names the nearest deadline among the timers covering the page
     shown.
   - Question timers stay refused, as does more than one timer for the same
     scope.
4. **The submission minute.** When a timed response is submitted, its terms
   gain bit 4: the submission minute (4 bytes). Untimed surveys never carry
   it, so their submission links are byte for byte what they were.
5. **Intake flags, never refuses.**
   - Intake records each enforced timer whose deadline passed strictly
     before the submission minute (`SurveyResult.LateTimers`, stored
     additively as `lateTimers`).
   - Minutes are rounded down, so a submission in the deadline's own minute
     may have been in time and is not flagged.
   - The page already held the answers when time ran out; only the
     submission came later.
   - The group page reports how many accepted responses were late, except
     below an anonymous group's minimum.
6. **Privacy.**
   - An anonymous link gains only the start minutes of the timers it
     reached, and its submission only the submission minute. All are whole
     UTC minutes, stamped on the respondent's device after the invitation
     was issued, so the issuer cannot match them to an invitation.
   - The submission minute tells the recipient roughly what they already
     learn when the submission arrives.
   - Nothing else that could identify the respondent is added; a test holds
     the anonymous terms to exactly these fields.
   - The late count is withheld below an anonymous group's minimum, with
     the scores.
7. **Integrity, not authenticity.** Starts and the submission minute are
   inside the link's integrity check and the submission hash, so editing one
   refuses the link. As in DF-SIGNAL-2026-0006, this is not a signature
   (WI-0060).
8. **Contract.** SIG-LINK 1.4.0 adds SIG-LINK-016.

## §9 Superseding §3 for question timers (2026-10-13, WI-0081)

This section supersedes one sentence of §3, "Question timers stay refused". The original text above is kept as it was decided.

- **What changed:** question timers are supported. The coordinator chose option (a) on 2026-10-13.
- **Why:**
  - ARX-013 lists Question among its recommended timer scopes.
  - The section and page machinery already does everything a question timer needs, and is proven in CI: stamp on first reach, never move a start, lock by coverage, flag at intake.
  - The only thing missing was a way to name a question in the link.
- **How:**
  - A question timer's start is a terms entry of **kind 3**, indexed by the question's position in the template, using the same count, 2-byte index and 4-byte minute as kinds 1 and 2.
  - It is stamped the first time a page showing the question is shown, and an enforced expiry holds that question only.
  - Intake flags a late submission exactly as for section and page timers. The link budget (`Layout.scopedTimerBytes`) counts question timers too.
- **Contract:**
  - SIG-LINK goes to 1.5.0, and SIG-LINK-016 names kind 3.
  - Every existing link fixture decodes and re-encodes byte for byte (`QuestionTimerTests`).
  - A decoder that does not know an entry kind refuses the whole link with `InvalidTerms` and its safe description, never a partial read, as the decoder before this change did for kind 3.
  - Terms with every kind round-trip, as a property.
- **Resolution:**
  - Starts stay whole UTC minutes, so a person can get up to a minute less than a timer's limit from the moment of first display. That matters most for a short question timer.
  - The console's timer help says so, and authors question timers in whole minutes from when the question is first shown (`AreaAuthoring.timerHelp`).
- **Unchanged:** more than one timer on the same part stays refused. No published fixture or sample changed: none has a question timer.

