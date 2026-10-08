---
id: DF-SIGNAL-2026-0001
title: Signal's administrator storage is implemented through Arca (no Strata), sign-in goes through Fides, and Signal is built after Chrona and Summa
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
  - docs/requirements/implementation-gap-analysis.md
  - docs/requirements/SIGNAL-DATA-LOCATION.md
tags: [storage, build-order, strata, encryption]
provenance:
  contributions:
    EXE-20261008T074805556Z-fd72229f:
      operations: [created]
      at: 2026-10-08T07:49:07.420Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record user decisions of 2026-10-08 and the per-application data-location requirement"
---

# DF-SIGNAL-2026-0001 — Storage through Arca, sign-in through Fides

- **Date:** 2026-10-08
- **Status:** accepted (user decisions of 2026-10-08)

## Decisions

1. **Storage.** Signal's StorageProvider contract and GitHub provider
   (ADM-003..006) are implemented on Arca (`kemiller2002/arca`), which is
   GitHub-only behind a provider-neutral interface. Signal owns its domain
   mapping and its namespace at a per-deployment location
   (SIG-DATALOC-001..005). Signal does **not** use Strata. Only applications
   that use a database use Strata.
2. **Encryption.** ADM-025's optional client-side encryption stays deferred.
   There is no at-rest encryption for now; separate repositories give
   permission separation.
3. **Sign-in.** Administrator credentials come from Fides
   (`kemiller2002/fides`), GitHub only for now.
4. **Build order.** Chrona first, then Summa, then Signal and the rest;
   Helix runs in parallel. Arca's and Fides's minimal slices come first.
5. **Offline.** Signal does not opt in to Arca's offline write queue
   (ADM-070). It uses a read-only degraded mode.
6. **PDFs.** Reports render through Folio.
