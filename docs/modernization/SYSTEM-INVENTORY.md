# System inventory

Work item: WI-0100 · Observed: 2026-11-08 · Signal commit: `e19101f` (main)

## Repository

- **Authoritative repository:** `kemiller2002/signal` (public). Its charter, Praxis state and 86 work items live here.
- **"Summa Signal":** `kemiller2002/summa` is a **separate** sibling application, not a former name of Signal. DF-SIGNAL-2026-0001 records the build order Chrona → Summa → Signal. Nothing in either repository says Signal was renamed.
- **Open pull requests:** #26 (draft, 2026-10-08): the A021 blind-coding survey fixture. It is stale and outside this audit's scope.
- **Open issues:** #125 (SIGNAL-VIS-001), #104 (UI layout conformance), #16 (SIGNAL-TEST-001), #9 (SIGNAL-QUAL-001), #7 (distribution), #5 (implementation baseline).
- **Active work held by another executor:** WI-0081 (expression authoring) has an active telemetry execution. Its last checkpoint is `746cfc0` on `feature/wi0081-expression-authoring`, and Praxis reports 11 later meaningful commits on main that no checkpoint covers. WI-0100 does not take it over.

## Application structure (F#, .NET 10)

| Project | Language | Role (four tiers, see ARCHITECTURE-REVIEW.md) | Files |
|---|---|---|---:|
| `src/Echelon.Signal.Engine` | F# | Tiers 1–2: survey semantics. Pure, with no package references. | 71 |
| `src/Echelon.Signal.Admin` | F# | Tiers 1–2: administrator semantics over Arca.Core records and Limen.Routing | 68 |
| `src/Echelon.Signal.Application` | F# | Tiers 3–4: orchestration, Limen protocol, Aegis boundary, Arca GitHub, Fides | 27 |
| `src/Echelon.Signal.Browser` | C# | Tier 4: `[JSExport]` WASM shim. Three forwarding methods. | 1 |
| `tests/Echelon.Signal.Tests` | F# | xUnit (634 tests after WI-0100) | 96 |
| `tests/browser` | JS | Playwright: chromium, ui-conformance, mobile and pages projects. Scheduled WebKit and Firefox runs. | 16 specs |
| `web/` (`index`, `survey/`, `admin/`) | HTML/CSS | Presentation over the Limen kernel, Forma and Folio | — |

## Baseline (WI-0100, this container, commit `e19101f`)

| Check | Result |
|---|---|
| `dotnet build Echelon.Signal.sln -c Release` | passed, 0 warnings (warnings are errors) |
| `dotnet test` | **627/627 passed**. Before `npm ci`, 7 failed because `node_modules` was missing (a precondition, not a defect). |
| `npm run check:limen` (`limen verify --strict`) | passed: 166 engine and 6 kernel files |
| `npm run ui:conformance:unit` | 6/6 passed |
| `npm run build:wasm` | passed (wasm-tools workload not installed, so the publish ran unoptimized) |
| `npx playwright test` (chromium, ui-conformance, mobile, pages) | **138/138 passed** in 6.4 minutes |
| `./praxis validate` (3.11.0) | passed, 0 findings |
| `./praxis sync check --start` | 0 ahead, 0 behind origin/main |
| `conditor verify` (0.10.21, current pins) | passed. Ordo flags one structural review signal on a generated `bin/` file. |
| main CI on `e19101f` (Build, Praxis, Limen, Foundations, Pages, Echelon Verification, Cross-browser) | all succeeded (GitHub Actions) |
| Deployed site (https://signal.echelonfoundry.com) | serves `/web/`, `/web/admin/` and `/web/survey/` with no console errors, **in demo mode** (see APPLICATION-READINESS.md) |

## Echelon systems

The full table, with sources, is in [`evidence/platform-inventory-2026-11-08.md`](evidence/platform-inventory-2026-11-08.md). Conditor's own drift report is [`evidence/conditor-drift-2026-11-08.json`](evidence/conditor-drift-2026-11-08.json).

Signal's resolved release set is **echelon-current 1.22.0**. The registry's current channel is **echelon-current 1.37.0**.

| System | Signal pin | Channel 1.37.0 | Conditor drift | Signal requires it? |
|---|---|---|---|---|
| Conditor | — (applied 1.22.0 with 0.10.1) | installer; latest tag v0.10.21 | — | yes |
| Praxis | 3.11.0 | 3.25.0 | **warn** (8 minor lines) | yes |
| Ordo | 1.7.0 | 1.20.0 | **warn** (5 minor lines) | yes |
| Dokimos | not installed | 0.11.1 (required by the channel) | — | **yes**: Praxis ≥ 3.17 attested completion needs Dokimos ≥ 0.7, and SIGNAL-QUAL-001 needs a quality gate |
| Aegis | 1.0.0 | 1.0.0 | ok | yes (ARX-015, ADM-077) |
| Fides | 0.2.0 | 0.2.0 | ok | yes (administrator sign-in) |
| Arca | 0.5.0 | 0.7.0 | **warn** (0.6.0 is breaking) | yes (storage) |
| Limen (npm kernel) | 0.9.0 | 0.9.1 | info (not declared in `conditor.json`) | yes |
| limen-fsharp | 0.9.0 | 0.9.0 | ok | yes (Routing). Store and Contract are pinned but unreferenced. |
| Forma | 0.6.1 | 0.13.0 | **warn** (5 minor lines) | yes (all UI) |
| Folio | 0.4.0 | 0.4.1 | info | yes (print) |
| Visual Engineering | 1.0.1 | 1.0.1 | ok | yes |
| Communication Engineering | 1.0.0 installed, not declared | 1.0.0 | — | installed |
| Tutela ("Tutors") | not installed | 0.1.0 (required by the channel) | — | not established; record the decision |
| Percepta / percepta-semantic | not installed | 0.1.0 / 0.4.0 (required by the channel) | — | no evidence of need |
| Vigila, Vitium, Forma Studio, Specula, Chrona | — | not channel components (applications) | — | no |
| Iter | — | no release | — | no (Signal routes through limen-fsharp) |

"Required by the channel" means the profile selects the system for every consumer. Whether Signal must install Tutela and Percepta is an **owner decision**: nothing in Signal's requirements needs them. EXECUTION-PLAN.md records it.

## Tooling used by WI-0100 (all isolated from the repository)

.NET SDK 10.0.403 · Node 22 · Playwright 1.63.0 with the preinstalled Chromium · Praxis 3.11.0 (the pinned native release) · Ordo 1.7.0 (the pinned native release) · Conditor 0.10.21. The Conditor upgrade check ran with an isolated `--home`, so no workstation effects were applied.
