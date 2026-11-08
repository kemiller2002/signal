# Platform contracts

Work item: WI-0100. For each Echelon system Signal depends on: the integration contract, Signal's conformance and the migration to the current verified release. **Status after WI-0102:** Praxis 3.25.0, Ordo 1.20.0, Arca 0.7.0, Forma 0.13.0, Folio 0.4.1 and Limen 0.9.1 are applied on this branch through Conditor ([`evidence/c1-upgrade-2026-11-08.md`](evidence/c1-upgrade-2026-11-08.md)). The table below is the pre-upgrade audit. Source data: [`evidence/platform-inventory-2026-11-08.md`](evidence/platform-inventory-2026-11-08.md) and Conditor's [`upgrade check`](evidence/conditor-upgrade-check-2026-11-08.txt).

| System | Contract | Signal integration | Conformance today | Migration to the channel (1.37.0) |
|---|---|---|---|---|
| **Conditor** | `conditor.json` manifest; `.conditor/authority` resolved set; `vendor/nuget` locks; web-package bindings | 0.10.21 `verify` passes on the 1.22.0 pins | conforms. Limen npm and Communication Engineering are installed but undeclared. | Run `upgrade --current --stepwise` with an isolated `--home`. Declare `limen` (npm) so Conditor governs it. |
| **Praxis** | native CLI; `ros.json`; `.ros/`; work protocol; provenance | 3.11.0 (`./praxis` pins it) | `validate` passes | 3.11.0 → 3.25.0. ≥ 3.17 issues attested completion that needs Dokimos ≥ 0.7. No changelog: verify `praxis upgrade --dry-run` before applying. |
| **Ordo** | `.sde/` method bundle (0.2), `ordo verify` | 1.7.0 | `verify --integrity-only` passes | 1.7.0 → 1.20.0. Same method bundle 0.2, so expect a package-only upgrade. |
| **Dokimos** | `dokimos init` writes `.dokimos/policy.json`, `.github/workflows/dokimos.yml` and a `dokimos-evidence` branch; action `kemiller2002/dokimos/actions/quality-gate` | none | **missing** | Install 0.11.1 after the Praxis upgrade (MOD-DOK-001, proposed). The evidence branch needs `contents: write` on the job: an owner decision for branch-protection settings. |
| **Aegis** | `EchelonFoundry.Aegis.Core`, `.Integration.GitHub`; `aegis-boundaries.json`; ARX-015, ADM-077 | 1.0.0; the `Boundary` dispatch boundary; `StorageFaults` | conforms (AER-002 and AER-032 VERIFIED independently). AER-021..031 and 036..042 partial. | current |
| **Fides** | `EchelonFoundry.Fides`, `.Client`, `.Arca` | 0.2.0; `Identity`, `AdminPorts` | conforms in tests (ADM-056 VERIFIED) | current. Fides.Arca 0.2.0 was built against Arca.Core 0.1, so Arca 0.7 compatibility is **unverified**: test after C1. |
| **Arca** | `EchelonFoundry.Arca.Core`, `.GitHub`; record format, conformance suite | 0.5.0 | conforms, except integrity on read (SEC-01) | 0.5.0 → 0.7.0. 0.6.0 is breaking: queue states and commit receipts are no longer constructible by applications. Signal only reads receipts, so exposure is expected to be low, but this must be verified by build and tests. |
| **Limen** (npm kernel + limen-fsharp) | `limen verify --strict`; protocol; `.echelon/routes.json` against `contract/routes.schema.json` | 0.9.0 / 0.9.0 | conforms (strict verification passes) | npm 0.9.0 → 0.9.1 (patch); limen-fsharp current |
| **Forma** | `@echelon-foundry/design-system` CSS and HTML patterns, tokens, UI-LAYOUT-INTERACTION@1.0.0 | 0.6.1 | partially conforms (FORMA-AUDIT.md: FRM-01..06) | 0.6.1 → 0.13.0. No removals. Layout and line-breaking changes, so re-run ui-conformance and re-baseline. |
| **Folio** | `@echelon-foundry/print-components` `ef-print-*` elements and print CSS | 0.4.0 | partially conforms (FOLIO-AUDIT.md) | 0.4.0 → 0.4.1, CSS only |
| **Visual Engineering** | `.visual-engineering/` research context | 1.0.1 | conforms | current |
| **Tutela** ("Tutors") | repository lifecycle | none | not adopted | owner decision: the channel lists it as required, but no Signal requirement needs it |
| **Percepta** | repository perception tools | none | not adopted | owner decision, same as Tutela |
| Iter, Vigila, Vitium, Forma Studio | — | not used | n/a | Iter is unreleased and the others are applications. Nothing to integrate. |

## Contract rules Signal must keep

1. Signal consumes **released** platform versions only, through Conditor. It never uses a development branch: forma 0.13.1, folio 0.4.2 and aegis 2.0.0 are unreleased candidates and are not used.
2. Shared UI and print capabilities go into Forma or Folio as governed work items (FRM-02, FOL-03), and are never re-implemented in Signal.
3. Typed domain outcomes are never routed through Aegis (ARX-015).
