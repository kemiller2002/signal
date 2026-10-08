---
id: DF-SIGNAL-2026-0001
title: Signal's administrator storage is implemented through Arca (no Strata), sign-in goes through Fides, and Signal is built after Chrona and Summa
status: accepted
version: 1.2.0
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
  - docs/requirements/backlog-plan.md
  - research/decisions/DF-SIGNAL-2026-0002--scoring-open-questions-proposed-answers.md
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
    EXE-20261008T145501158Z-2a5cd831:
      operations: [modified]
      at: 2026-10-08T14:56:36.269Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Amendment 1: record the coordinator decision of 2026-10-08 pulling the pure-domain slices WI-0042..WI-0046 forward"
    EXE-20261008T224935854Z-d024938c:
      operations: [modified]
      at: 2026-10-08T22:49:49.542Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Decision 5: record that Arca 0.3.0's IndexedDB write queue and read cache were evaluated and declined (WI-0074)"
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
   - Arca 0.3.0's IndexedDB write queue and read cache (`EchelonFoundry.Arca.Limen`,
     arca `docs/consuming-arca.md` section 5a) were evaluated on 2026-10-08
     and declined (coordinator decision, WI-0074). Administrator data includes
     per-respondent submissions, and report state was just reduced to
     aggregates that pass the group's privacy rules (WI-0067). Caching that
     data on the device in IndexedDB would undo the posture for little gain.
     Conditor declares `arca` as a whole, so the package stays in the vendored
     feed, pinned and unreferenced; no project may reference it without
     revisiting this decision. `limen-fsharp` stays: the administrator routes
     use `EchelonFoundry.Limen.Routing` (DF-SIGNAL-2026-0003).
6. **PDFs.** Reports render through Folio.

## Amendment 1 (2026-10-08): pure-domain slices pulled forward

7. **Pull-forward.** A coordinator decision dated 2026-10-08 starts the Signal
   slices that are pure domain now, ahead of decision 4's order. The user
   accepted the coordinator's recommendation. These slices depend on neither
   Arca nor Fides:
   WI-0042 (Signal 05, canonical domain and authoring), WI-0043 (Signal 06,
   rules and flow), WI-0044 (Signal 07, scoring AST), WI-0045 (Signal 08,
   answer primitives and selectors) and WI-0046 (Signal 09, reporting
   contract). The list was checked against `.ros/work/queue.json` and
   [`backlog-plan.md`](../../docs/requirements/backlog-plan.md): these are
   the only slices whose "depends on" column names no Arca or Fides slice and
   no slice that does.
   - Everything else in decision 4 stands. WI-0038..WI-0041 and WI-0047..WI-0054
     still wait for Arca, Fides or the administrator surface, after Chrona and
     Summa.
   - The pull-forward does not include a platform upgrade. Signal's
     echelon-current upgrade, its Arca and Fides dependencies, and any change
     to `conditor.json`, lockfiles or workflow files remain a later slice of
     their own.
   - WI-0044 builds against the proposed scoring answers in
     [DF-SIGNAL-2026-0002](DF-SIGNAL-2026-0002--scoring-open-questions-proposed-answers.md),
     and only because each answer can be reversed through a pure function or
     template configuration. If the owner changes an answer, the change is a
     configuration or catalog edit, not a rebuild.
