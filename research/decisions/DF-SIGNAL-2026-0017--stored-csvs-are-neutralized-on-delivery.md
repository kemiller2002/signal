---
id: DF-SIGNAL-2026-0017
title: A stored snapshot's sections CSV is neutralized when delivered; stored bytes and hashes never change, so delivered CSV bytes may differ from stored ones
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
tags: [security, csv, snapshots, integrity, exports]
provenance:
  contributions:
    EXE-20261013T123454748Z-14971799:
      operations: [created]
      at: 2026-10-13T12:38:30.768Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Stored CSVs neutralized on delivery (WI-0085)"
---

# DF-SIGNAL-2026-0017: Neutralizing stored CSVs on delivery

- **Date:** 2026-10-13
- **Status:** accepted. The coordinator asked for this follow-up to
  WI-0084; the Signal agent made the routine choices within it.
- **Work items:** WI-0085 (follows WI-0084). **Requirements:** security
  (CSV injection), ADM-022 (immutable snapshots), ADM-023 (exports).

## Context

WI-0084 made every newly written CSV spreadsheet-safe. Snapshots stored
before it keep their `sections.csv` as written. A section title that starts
with `=`, `+`, `-` or `@` is in those bytes unneutralized, and a download
would hand it to a spreadsheet as a live formula. Snapshots are immutable,
and their ids hash those bytes.

## Decision

1. **Stored bytes never change.** The stored CSV, the snapshot id and every
   hash over them stay exactly as they are.
2. **Neutralized on delivery.** When a snapshot's `sections.csv` is
   delivered or exported, the stored text is parsed (RFC 4180) and every cell
   rewritten by the safe writer (`Csv.neutralize`). The safe writer's own
   output comes back byte for byte, so snapshots taken since WI-0084 are
   delivered exactly as stored.
3. **Delivered bytes may differ from stored bytes, for CSV only.** The CSV
   is a tabular projection, never the canonical form. The snapshot's
   integrity rests on `report.json`'s hash, which delivery never changes.
   Consumers that compare a delivered CSV with stored bytes must compare
   with the neutralized form, as `ReportLibrary.export` now does.
   - Before this, after WI-0084 such a snapshot would have been refused as
     "not the snapshot".
4. **Said in the download.** When delivery changed an older snapshot's CSV,
   the console notes: "This snapshot's sections CSV was neutralized for
   spreadsheet safety … The stored snapshot and its hashes are unchanged."
5. **Population.** The repository's deployments store no snapshots outside
   tests:
   - The GitHub Pages site runs in demo mode ("Demo — data stays in this
     browser; GitHub sign-in not configured", `deploy/pages/site.json`).
   - The console's deployment configuration is `{"environment": "local"}`,
     with no data repository (`web/admin/signal.deployment.json`).
   - A private operator deployment is not visible from this repository, so
     the delivery guard is the fix regardless.
