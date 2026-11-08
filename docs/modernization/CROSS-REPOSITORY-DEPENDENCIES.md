# Cross-repository dependencies

Work item: WI-0100.

Traceability chain: Signal requirement → shared-platform requirement → Praxis work item → implementation → tests → verified release → Signal integration.

## Dependency direction

Signal → {Limen, Forma, Folio, Aegis, Arca, Fides, Praxis, Ordo, Visual Engineering} via Conditor-installed releases. No platform repository depends on Signal. Signal appears only as a listed consumer in `conditor/conformance/conditor-declared-components.json`, which is metadata and not a code dependency. **No circular dependency exists.**

## Shared-platform obligations identified by this audit

None of these has been filed yet. Filing a work item in another repository is an outward-facing action, and this session's GitHub scope covers only `kemiller2002/signal`. Each row is recorded here so the owner or a session scoped to that repository can file it.

| Signal requirement | Platform | Proposed shared requirement | Status | Signal fallback until released |
|---|---|---|---|---|
| ADM-017 (non-colour data marks), FRM-02 | Forma | A data-mark / chart pattern with tokenized non-colour fills (diagonal, dots, cross) and forced-colours behaviour | **not filed** | `web/admin/admin.css` lines 10–12: a documented temporary exception |
| FOL-03 (print one region) | Folio | A print-region contract: print only a designated document surface | **not filed** | `.report-surface` rule in `admin.css` |
| SIGNAL-VIS-001.1..6 | Forma (#159, #160), Ordo (#92), Visual Engineering (#49) | theme and brand policy; token-validity check; visual-intent governance | already filed upstream (per `SIGNAL-VISUAL-INTENT.md`) | none: blocked |
| SVG/limen nested view lists (DF-SIGNAL-2026-0022) | Limen (#108) | nested view lists for visual selector variants | already filed upstream | visual variants deferred |
| SEC-01 integrity on read | Arca | none needed: `Integrity.unchanged` exists; Signal must call it | — | fix in Signal (WI-0101) |

## Pending upgrades

These are cohort C1, through Conditor.

| From → to | Reason | Risk |
|---|---|---|
| echelon-current 1.22.0 → 1.37.0 | platform currency (MOD-PLAT-001) | Forma layout changes; Arca 0.6 breaking; Praxis attested completion needs Dokimos |
