# Folio audit

Work item: WI-0100 · Signal pin: `@echelon-foundry/print-components` **0.4.0** · Current verified release: **0.4.1**

## Verdict

**Folio is the only document and print path, and it renders engine-produced data only.** It is behind by one patch release, which is non-breaking. The required document scope (SRPP-001..185) is **partially implemented**, so the Folio platform contract is not yet satisfied for Signal's full report profile.

## Document and print surfaces

| Surface | Folio elements | Data source | Format |
|---|---|---|---|
| Assessment result print (`web/index.html`) | `ef-print-document`, `-header`, `-footer`, `-page-number`, `-section`, `-table` | Engine view (`Assessment` scoring) | Browser print / Save as PDF |
| Group report (`web/admin/`, WI-0062) | `ef-print-document`, `-title-page`, `-toc`, `-section` (×26), `-metric`, `-table` | `ReportData` from `Report`/`ReportExport` (Engine) | Browser print / Save as PDF |
| Snapshot exports (WI-0075) | none: data files | `ReportLibrary.export` | JSON, sections CSV, lineage JSON through `limen.files` |

There is no competing document generator: no server PDF library and no custom print engine. Print styles come only from Folio's `print.css`, plus one isolation rule (`.report-surface`, see FOL-03).

## Contract checks

| Check | Evidence | Status |
|---|---|---|
| Content correctness from the authoritative domain | Report blocks are projections of `ReportData`, and scoring stays in F# (SRPP-001..010) | evidenced |
| No domain logic in the document layer | Folio receives rendered values only; `ReportData` is format-neutral | evidenced |
| Privacy and sensitive-data exposure | Suppression happens before rendering, so no suppressed value exists to leak (SRPP-072..088, `Disclosure`). Exports carry no locator or credential (WI-0050). CSV neutralization: DF-SIGNAL-2026-0017. | evidenced |
| Authorization | Export requires the `ExportData` capability, and snapshots require `BuildReports` (`Access`) | evidenced in F# tests; not against a real provider |
| Pagination, long and empty datasets | No test prints a long or empty report and checks page breaks | **gap** |
| Malformed input | Snapshot load verifies its hash or returns an explicit unresolved state | evidenced |
| Output format validity | JSON and CSV are tested. PDF is the browser's own; no PDF validity check exists. | partial |
| Print and preview behaviour | `report-surface` print isolation | partial: not covered by a Playwright print-media test |
| Automated regression | F# report tests | partial |

## Findings

| ID | Finding | Action |
|---|---|---|
| FOL-01 | 0.4.0 → 0.4.1 changes only print CSS (wrapping, header and footer slots, right-to-left) and adds a `forma-icon-print` export. Nothing breaks. | C1 via Conditor |
| FOL-02 | SRPP ranges 011..028 (TOC, cover, reading order), 029..071 (findings, cross-dimension and action blocks) and dispersion and longitudinal series remain partial (`implementation-gap-analysis.md`). | C4 (WI-0062 remainder) |
| FOL-03 | `admin.css` hides everything except the report while printing (`.admin:has(> .report-view .report-surface) > :not(.report-view)`). "Print only this region" is a generic print capability. | Evaluate a Folio print-region contract. If it is reusable, open a Folio work item and consume the release. |
| FOL-04 | No print-media regression test (long and empty datasets, page breaks, headers and footers). | C4: Playwright `emulateMedia({ media: 'print' })` plus `page.pdf()` structure checks |
