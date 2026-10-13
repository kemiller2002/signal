---
id: DF-SIGNAL-2026-0009
title: A bank selection's seed comes from ids the link already carries - the instance in identified groups, the group in anonymous groups - so nothing new travels and no anonymous submission links to its invitation
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
  - research/decisions/DF-SIGNAL-2026-0008--timers-and-banks-are-optional-template-fields.md
  - docs/requirements/SIGNAL-DEEP-LINKING.md
  - docs/requirements/implementation-gap-analysis.md
tags: [banking, privacy, anonymity, respondent, intake]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T10:09:14.922Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Bank selection seed from ids the link already carries (ARX-013, LURL-002, WI-0081)"
---

# DF-SIGNAL-2026-0009: The bank selection seed

- **Date:** 2026-10-13
- **Status:** accepted by the Signal agent on the owner's standing
  instruction ("take the recommendation and continue"), and reported to the
  coordinator for review. It is privacy-relevant: see decision 2.
- **Work item:** WI-0081. **Requirements:** ARX-013 (template, algorithm
  version and seed reconstruct the same selection), LURL-002 (an anonymous
  submission is unlinkable to its invitation), SIG-LINK-008.

## Context

A template can now hold question banks (DF-SIGNAL-2026-0008). Each instance
answers a reproducible subset, and intake must reconstruct the same subset
from the submission. The survey page and intake share only the link. An
anonymous submission's binding gets a fresh id at finalization (LURL-002),
so that the person who issued an invitation can't tell which submission
came from it.

## Decision

1. **Identified groups: the seed is the instance.** The seed is
   SHA-256("signal-bank-seed/1" ‖ instance id), first 8 bytes, big-endian.
   The submission keeps the instance id, so intake recomputes it. Each
   respondent answers their own selection. The instance id already
   identifies the response, so this adds no linkage.
2. **Anonymous groups: the seed is the group.** It is the same function of
   the group id. Everyone in an anonymous group answers the same selection.
   A per-respondent seed would have to travel from invitation to submission,
   and the issuer, who knows each invitation's seed, could then link the
   anonymous submission to its invitation. That is refused. The cost is no
   per-respondent variation in anonymous groups.
3. **Nothing new in the link.** The respondent link format (SIG-LINK-013) is
   unchanged, and so are submission links and their hashes.
4. **Applied as flow, not as layout.** The questions a selection leaves out
   are hidden by flow rules added for the instance (`BankSelection.instantiate`,
   ids prefixed `bank-excluded:`, which authored rules may not use). The
   encoding layout, slots and template hash are untouched. Applicability,
   scoring, completion, pagination and the page all follow from it. Intake
   applies the same selection before evaluating.
5. **Test links** seed as the binding they stand in for. A link with no
   group seeds from zero bytes; it can't be submitted.
6. **Algorithm.** Banking algorithm version 1 (SplitMix64 partial
   Fisher-Yates, per-bank FNV-1a salt) with the seed above. A change to
   either is a new version.
