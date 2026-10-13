---
id: DF-SIGNAL-2026-0021
title: Retired accepted results are erased from the current repository tree when the provider declares Arca's Erase capability, and the claim says only what landed
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
tags: [retention, privacy, storage, erasure]
provenance:
  contributions:
    EXE-20261013T192337276Z-6816d24f:
      operations: [created]
      at: 2026-10-13T19:30:04.039Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Retired results erased from the current tree on Arca 0.5.0 (WI-0077)"
---

# DF-SIGNAL-2026-0021: Retired results are erased from the current tree

- **Date:** 2026-10-13
- **Status:** accepted. This is a routine design choice, taken under the coordinator's standing approval. It uses the erasure path Arca 0.5.0 added (ARCA-INT-005), the one ADM-045 was waiting on, and it never widens a claim beyond what the provider proves.
- **Work item:** WI-0077. **Requirements:** ADM-045 (lifecycle, retention, deletion claims) and ADM-064 (reproducibility after deletion).

## Context

WI-0076 retired a finalized group's accepted results from Signal's state: one commit with a retirement record and the PII-free limitation record. Arca never deletes an immutable record, so the results stayed in the repository tree. The claim was "removed from application state", and the audit gave the reason `TREE-REMOVAL-UNAVAILABLE`.

Arca 0.5.0 (echelon-current 1.19.0) adds `Capability.Erase` and `Erasure`:

- One atomic commit replaces each immutable record's blob in the current tree with a tombstone. The tombstone holds the erased content hash, the revision, the time and the reason, never the content.
- Later reads report `ReadOutcome.Erased`, and the path can never be written or erased again.
- The provider's Git history still holds the content.

## Decision

1. **Retire first, then erase.** The retirement commit is unchanged and comes first, so the results leave Signal's state whatever happens next. Then, if the provider declares `Capability.Erase`, one `Erasure.operation` erases every retired contribution from the current tree.
2. **Erase only what was retired.**
   - Each record is read again and validated (`Integrity.validate`) before its erasure request is made.
   - If its content hash is no longer the one retired, the erasure is refused.
   - Arca itself conditions each tombstone on the erased revision.
3. **The claim follows the outcome** (`GovernanceStore.Retired.Tree`):

   | Outcome | Tree | Claim |
   |---|---|---|
   | The erasure landed | `ErasedFromTree n` | `RemovedFromCurrentTree` |
   | The provider does not declare Erase | `ErasureNotOffered` | `RemovedFromApplicationState` |
   | The erasure was refused or failed | `ErasureFailed reason` | `RemovedFromApplicationState` |
   | The outcome is unknown | `ErasureOutcomeUnknown` | `RemovedFromApplicationState` |

   - History is never claimed (`ProviderHistory = None`), so "permanently erased" is never said. ADM-045's rule stands: GitHub keeps history, so nothing short of verified history erasure is claimed as permanent.
   - Reading the records again settles an unknown outcome, because an erased record reads as erased.
4. **The audit records the intent, decided before anything is written.**
   - The limitation's audit record gives `TREE-ERASURE-REQUESTED` when the provider declares Erase, and `TREE-REMOVAL-UNAVAILABLE` when it does not.
   - It is in the retirement commit, so it can't depend on the erasure that follows. The erasure commit's own trailers and tombstones are the evidence that it landed.
5. **The tombstone's reason** is `retention <basis>`, for example `retention retention-expired` or `retention deletion-request`. It names no person, group or answer.
6. **An erased record is not a damaged one.** Loading already skips erased objects (`Store.present`), so a reopened group with erased results opens without problems.

## Consequences

- Arca's in-memory provider and GitHub both declare Erase. So on both, retiring a group's results now removes them from the current tree, and the claim says so.
- `RetirementTests` covers the three outcomes Signal can produce: erased, not offered, and failed. The earlier retirement test now asserts `RemovedFromCurrentTree` and the tombstones in place of the remaining results. Its application-state assertions moved to the provider-without-Erase test.
- The console's dataset lifecycle and retention screens are the remainder of ADM-045 (WI-0077 slice 2).
