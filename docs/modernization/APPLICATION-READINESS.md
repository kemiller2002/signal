# Application readiness gate

Work item: WI-0100 · Date: 2026-11-08 · Deployed environment: GitHub Pages, https://signal.echelonfoundry.com (main `e19101f`, Pages run 39153305842) · Local verification commit: `e19101f`

## Result

> **Application readiness: NOT PASSED. The administrator and storage gates are BLOCKED on owner-provided production configuration.**

The respondent (survey and assessment) path is operational in production. The administrator path, which covers sign-in, storage, import, aggregation and reports, **is not running in any deployed environment**. The deployed admin console says: "No data store is configured. This deployment configures no storage profile, so there is nothing to administer." A green CI pipeline does not change that.

## Gates

| Gate | Environment | Evidence | Result |
|---|---|---|---|
| Startup and initialization | deployed | `/web/`, `/web/admin/` and `/web/survey/` return 200; WASM boots; **0 console errors** (`evidence/deployed-smoke-2026-11-08.json`) | **pass** |
| Authentication and authorization | deployed | No Fides exchange or GitHub App is configured (`deploy/pages/site.json` has no `deployment`). Sign-in is tested only with fakes (`AdminPageTests`, `sign-in-return.spec.js`). | **blocked**: needs owner-provided GitHub App client id and Fides exchange origin (MOD-OPS-001) |
| Core respondent workflow | local + deployed | 138/138 Playwright tests (chromium, mobile, pages sub-path), plus the scheduled WebKit and Firefox run (success, 2026-11-02). The deployed survey page loads and refuses a missing link correctly. | **pass** |
| Core administrator workflows (groups, import, aggregation, reports) | local only | F# end-to-end tests against Arca's **in-memory** provider and the real Fides client. No real GitHub storage. | **blocked**: mock-only evidence cannot establish production integration readiness |
| Data persistence and retrieval | local only | Arca in-memory conformance. SEC-01: records are not hash-verified on load. | **fail**: integrity defect, plus blocked on real provider |
| Error recovery | local | Aegis boundary tests, `OutcomeUnknown` reconciliation tests. ARX-008: the unresolved-outcome path is untested. | partial |
| Forma rendering and interaction | local | ui-conformance project at 320, 390, 768 and 1280 px on Forma 0.6.1 | pass on the pinned version; Forma not current |
| Mobile and desktop usability | local | Pixel 7 project, ui-conformance widths | pass for the respondent path |
| Accessibility | local | markup and keyboard conformance. ADM-017 and SCS-014 PARTIAL (rejection paths, focus visibility, colour-vision). | partial |
| Folio document generation | local | report print surface. No print-media regression test (FOL-04). | partial |
| External integrations | — | GitHub storage and Fides exchange not exercised against real providers | **blocked** |
| Deployment and configuration | deployed | Pages workflow green; site assembled with CSP | pass for demo; no production configuration |
| Operational logging and monitoring | — | Aegis writes to stderr only; no collection or health signal (MOD-OPS-002) | **fail** |
| Security boundaries | deployed + local | CSP served; 0 vulnerable dependencies; SEC-01..05 open | partial |
| CI | GitHub Actions | all workflows on `e19101f` succeeded | pass |

## What would unblock the gates

1. **Owner:** create the GitHub App, the Fides exchange deployment and a data repository, and decide the hosting for header-based security (MOD-SEC-001). Only public identifiers go in `deploy/pages/`, never a secret (docs/deployment.md).
2. **Engineering:** fix SEC-01 (integrity on read) before storage is turned on (WI-0101).
3. **Verification:** run the administrator journeys against the real providers in a non-production dataset, and record the environment, commit and results here.
