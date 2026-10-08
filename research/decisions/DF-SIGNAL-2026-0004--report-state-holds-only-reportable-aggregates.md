---
id: DF-SIGNAL-2026-0004
title: The administrator report state holds only aggregates that pass the group's privacy rules; continuing imports reads the store
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - signal
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SIGNAL-2026-0003--deep-linking-through-limen-routing.md
  - docs/requirements/implementation-gap-analysis.md
tags: [privacy, reporting, report-state, import]
provenance:
  contributions:
    EXE-20261008T215913257Z-0de5cd42:
      operations: [created]
      at: 2026-10-08T22:02:53.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Report state holds only reportable aggregates; continuing imports reads the store (WI-0067)"
---

# DF-SIGNAL-2026-0004 — Report state is aggregates only

- **Date:** 2026-10-08
- **Status:** accepted. Decided by the coordinator on the owner's standing
  instruction, 2026-10-08.
- **Work item:** WI-0067. **Requirements:** ARP-003, ARP-004, SIG-LINK-008,
  ID-003.

## Context

ARP-003 (§53–57) designed `AdminReportState` to carry what continuing an
import needs: the accepted identity keys with their SubmissionHashes, and the
incremental evidence (each respondent's sorted section scores). The state
travels in an administrator URL while it is small (ARP-004). WI-0066 found
that this puts per-respondent scores and, in identified groups,
invitation-linked keys into a shareable artifact. In a small group those
scores can reveal what aggregate suppression withholds.

## Decision

1. **The report state holds only aggregates that pass the group's privacy
   rules.** These are counts, completion, section summaries (mean,
   scored and unscored counts), coverage, the weakest and strongest areas, the
   template hash and the group result's derivation hash.
   - There are no per-respondent scores, identity keys, SubmissionHashes,
     answers or URLs.
   - Below an anonymous group's minimum reportable count, only the counts
     remain: every section is suppressed, and coverage and the weakest and
     strongest areas are absent.
2. **Continuing imports reads the store, not the state.** Accepted
   contributions are stored durably (WI-0041). `GroupStore.openGroup`
   rebuilds the incremental accumulator from them, so duplicate prevention
   and incremental aggregation are unchanged. This replaces ARP-003 §53's
   "resume from the decoded state".
3. **The derivation hash stays.** It is one set-level lineage hash over the
   accepted submissions (ADM-020). It identifies the input set, never a
   respondent or a value. The submission hashes are 256-bit, and the
   respondent identifiers in them are 128-bit random values.
4. **State version 2.** A version 1 state, which carried the evidence, is
   refused when read (`UnsupportedVersion 1`). A report state is a
   projection: an administrator regenerates it by opening the group.

## Consequences

- A report state now has a near-constant size, so it embeds in the URL
  except under small deployment budgets.
- `ReportStateTests` proves the decision:
  - The state is a fixed schema of aggregates.
  - The same answers under other respondents, in another order, give the
    same state, apart from the derivation hash.
  - No identity key, submission hash, item or URL appears in it.
  - Below the anonymous minimum, only the counts remain.
