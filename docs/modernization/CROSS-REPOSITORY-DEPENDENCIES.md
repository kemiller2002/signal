# Cross-repository dependencies

Work item: WI-0100.

Traceability chain: Signal requirement → shared-platform requirement → Praxis work item → implementation → tests → verified release → Signal integration.

## Dependency direction

Signal → {Limen, Forma, Folio, Aegis, Arca, Fides, Praxis, Ordo, Visual Engineering} via Conditor-installed releases. No platform repository depends on Signal. Signal appears only as a listed consumer in `conditor/conformance/conditor-declared-components.json`, which is metadata and not a code dependency. **No circular dependency exists.**

## Shared-platform obligations identified by this audit

WI-0102 filed forma#179, the regression that blocks the upgrade. FRM-02 and FOL-03 are improvements rather than defects; they stay unfiled until the owner authorizes that platform work.

| Signal requirement | Platform | Proposed shared requirement | Status | Signal fallback until released |
|---|---|---|---|---|
| ADM-017 (non-colour data marks), FRM-02 | Forma | A data-mark / chart pattern with tokenized non-colour fills (diagonal, dots, cross) and forced-colours behaviour | **not filed** | `web/admin/admin.css` lines 10–12: a documented temporary exception |
| FOL-03 (print one region) | Folio | A print-region contract: print only a designated document surface | **not filed** | `.report-surface` rule in `admin.css` |
| SIGNAL-VIS-001.1..6 | Forma (#159, #160), Ordo (#92), Visual Engineering (#49) | theme and brand policy; token-validity check; visual-intent governance | already filed upstream (per `SIGNAL-VISUAL-INTENT.md`) | none: blocked |
| SVG/limen nested view lists (DF-SIGNAL-2026-0022) | Limen (#108) | nested view lists for visual selector variants | already filed upstream | visual variants deferred |
| Forma 0.13.0 record-header regression (FRM-07) | Forma | `.ef-record-header__title` narrow-container subgrid fix | **fixed and released: forma#179 → forma#180 (Forma 0.13.1) → conditor#135 (Conditor 0.10.22) → echelon-registry#110 (echelon-current 1.37.1, owner-approved)** | none needed: Signal consumes 0.13.1 |
| SEC-01 integrity on read | Arca | none needed: `Integrity.unchanged` exists; Signal must call it | — | fix in Signal (WI-0101) |

## Upgrades (cohort C1)

Applied on this branch through Conditor in WI-0102 and WI-0103 ([evidence](evidence/c1-upgrade-2026-11-08.md)). Signal is on the owner-approved echelon-current 1.37.1.

| From → to | Reason | Risk |
|---|---|---|
| echelon-current 1.22.0 → 1.37.0 | platform currency (MOD-PLAT-001) | Forma layout changes; Arca 0.6 breaking; Praxis attested completion needs Dokimos |
