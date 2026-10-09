# Work Queue

| ID | Work | Status | Tags | Priority |
|---|---|---|---|---|
| ECHELON-UPGRADE-2026-09-21 | Reconcile current Echelon engineering capabilities | complete | tooling,ordo,ros,limen | high |
| FOUNDATIONS-APPLICABILITY | FOUNDATIONS-APPLICABILITY | complete |  |  |
| GH-5 | Prepare Signal implementation baseline | abandoned | readiness,bootstrap | high |
| LIMEN-0-7-0 | LIMEN-0-7-0 | complete |  |  |
| LIMEN-0-7-0-FND | LIMEN-0-7-0-FND | complete |  |  |
| LIMEN-0-7-0-VERIFIER | LIMEN-0-7-0-VERIFIER | complete |  |  |
| ROS-INSTALL-1-2-1-main-16-1 | ROS-INSTALL-1-2-1-main-16-1 | complete |  |  |
| SIGNAL-FRAMEWORK-2026-09-22 | Finalize Echelon Signal current ROS, Ordo SDE, and Limen baseline | complete | framework, migration, ros, ordo, limen | high |
| SIGNAL-SCORING-SELECTORS-2026-09-22 | Complete scoring and selector requirements catalog | complete | requirements, scoring, selectors, web-components | high |
| SIGNAL-SCORING-SELECTORS-ATTRIBUTION-2026-09-22 | Attribute scoring and selector completeness branch diff | complete | requirements, scoring, selectors, governance | high |
| SIGNAL-VERIFY-2026-09-21 | Align verification with current Echelon capability contracts | complete | ci,ordo,ros,sde | high |
| WI-0001 | Migrate authoritative survey requirements from input-documents into traceable ROS work items | complete | requirements, migration, survey | high |
| WI-0002 | Canonical survey domain contracts and F# module boundaries | captured | domain, architecture, survey | high |
| WI-0003 | Template authoring, publication, versioning, and compatibility | captured | authoring, publication, versioning | high |
| WI-0004 | Answer model, URL encoding, and live respondent lifecycle | captured | answers, encoding, url-state | high |
| WI-0005 | Identity, anonymity, roles, and group semantics | captured | identity, privacy, groups | high |
| WI-0006 | Deterministic rules, validation, flow, completion, and recommendations | captured | rules, validation, flow | high |
| WI-0007 | Scoring engine, result semantics, and explainability | captured | scoring, results, expressions | high |
| WI-0008 | Administrator import, aggregation, and report-state persistence | captured | admin, import, persistence | high |
| WI-0009 | Reporting contract, privacy, comparisons, and renderers | captured | reporting, privacy, exports | medium |
| WI-0010 | Cross-cutting test, performance, security, migration, and acceptance program | captured | testing, performance, security | high |
| WI-0011 | Administrator application state, group management, and Limen UX | captured | admin, ordo, limen, ux | high |
| WI-0012 | Storage provider contract and GitHub repository provider | captured | admin, storage, github, provider | high |
| WI-0013 | Administrator import, concurrency, durable aggregation, and indexes | captured | admin, import, aggregation, concurrency | high |
| WI-0014 | Administrator analytics, comparisons, lineage, and dependency invalidation | captured | admin, analytics, lineage, dependencies | high |
| WI-0015 | Typed visualization grammar, dashboards, and accessibility | captured | admin, visualization, accessibility, dashboard | high |
| WI-0016 | Report builder, snapshots, exports, configuration packages, and policy packs | captured | admin, reporting, snapshots, configuration | medium |
| WI-0017 | Administrator privacy, security, audit, retention, and deletion lifecycle | captured | admin, privacy, security, lifecycle | high |
| WI-0018 | Storage migration, backup, schema evolution, and operational recovery | captured | admin, migration, backup, recovery | high |
| WI-0019 | Administrator sandbox, synthetic data, advanced analytics extensions, and phase boundary | captured | admin, sandbox, synthetic-data, experiments | medium |
| WI-0020 | Administrator cross-cutting verification, performance, and acceptance program | captured | admin, testing, performance, acceptance | high |
| WI-0021 | Resolve Limen 0.7.0 LIMEN012: no engine source under src/Echelon.Signal.Engine, so strict verify is not-configured (exit 8) | complete | limen,boundary | high |
| WI-0022 | Replace limen.config.json boundary.notApplicable with engine/kernel paths (src/Echelon.Signal.Engine, src/Echelon.Signal.Browser) when the first F# WASM engine source lands | complete | limen | medium |
| WI-0023 | Restore Aegis to required: true in .echelon/foundations.json, with EchelonFoundry.Aegis.Core, real boundary usage and aegis-boundaries.json, in the change that adds the first .NET/F# host or operational boundary (DF-SIGNAL-FND-2026-0001) | complete | foundations | medium |
| WI-0024 | Restore Forma to required: true in .echelon/foundations.json, with the pinned @echelon-foundry/design-system, in the change that adds the first interactive browser surface (DF-SIGNAL-FND-2026-0001) | complete | foundations | medium |
| WI-0025 | Restore Folio to required: true in .echelon/foundations.json, with the pinned @echelon-foundry/print-components, in the change that adds the first report or document output (DF-SIGNAL-FND-2026-0001) | complete | foundations | medium |
| WI-0026 | Upgrade to Forma 0.4.1 and Limen 0.7.1 and remove the unchecked-radio workaround | complete | foundations, limen | high |
| WI-0027 | Move Signal to Praxis 3.7.1 (ROS -> Praxis rename) and Ordo 1.4.0 | complete | praxis, ordo, toolchain | medium |
| WI-0028 | Move signal to Ordo 1.4.1 | complete | ordo, toolchain | medium |
| WI-0029 | Move signal to Praxis 3.7.2, Ordo 1.4.2, Visual Engineering 1.0.1 and adopt Conditor | complete |  | medium |
| WI-0030 | Requirement gap analysis: compare every requirement group against code and tests | complete | requirements,gap-analysis | high |
| WI-0031 | Template canonical hash and versioned URL answer-state codec (VER-002, ARX-006, LURL-004, ANS-004, CAN-004, ACR-004, URLC-003) | complete | encoding,url-state,versioning | high |
| WI-0032 | Live respondent URL synchronization through Limen navigation (LURL-001, ARX-007, URLC-001) | complete | url-state,limen,respondent | high |
| WI-0033 | Portable submission finalization: identified and anonymous with unlinkable entropy (ID-002, ID-004, LURL-002, URLC-003) | complete | identity,anonymity,submission | high |
| WI-0034 | Administrator import pipeline, deduplication and SurveyResult/SurveyGroupResult aggregation (ARP-001, ARP-002, LURL-003, ARX-008, ID-003) | complete | admin,import,aggregation | high |
| WI-0035 | Standard built-in scoring catalog with explicit missing policy (ANS-003, SCS-002, SCS-003, SCS-005, SCS-008, ALG-001) | complete | scoring | high |
| WI-0036 | Move signal to Ordo 1.5.0 via echelon-current 1.2.0 (conditor upgrade --current) | complete |  | medium |
| WI-0037 | Capture the remaining Signal requirements as dependency-ordered backlog slices; add SIG-DATALOC-001 and record the 2026-10-08 decisions | complete | planning, requirements | high |
| WI-0038 | Signal 01: configurable data location and Signal-owned namespace through Arca (SIG-DATALOC-001, ADM-004 location/namespace, ADM-005, ADM-057) | complete | signal, order:01, data-location, depends:arca | high |
| WI-0039 | Signal 02: storage provider contract implemented on Arca's GitHub provider (ADM-003, ADM-004, ADM-006, ADM-025, ADM-026, ADM-044, ADM-046, ADM-058, ADM-073; AER-002, AER-003, AER-032) | complete | signal, order:02, storage, depends:arca | high |
| WI-0040 | Signal 03: administrator sign-in through Fides - credential lifecycle, browser secret policy, cross-tab coherence (ADM-056, ADM-071, ADM-072) | complete | signal, order:03, auth, depends:fides | high |
| WI-0041 | Signal 04: durable import, concurrency, OutcomeUnknown and rebuildable indexes (ADM-008..011, ADM-027, ADM-060, ADM-061, ADM-067; ARP-003..006; ARX-008; LURL-003, LURL-005) | complete | signal, order:04, import, concurrency, depends:arca | high |
| WI-0042 | Signal 05: canonical domain contracts, template authoring, publication and versioning (CAN-001, CAN-005, CAN-008, URLC-004, ACR-003, ACR-008, VER-003, VER-004, VER-007, AUT-001..007, ARX-002) | complete | signal, order:05, domain, authoring | high |
| WI-0043 | Signal 06: rules, flow, validation, completion and recommendations; group identity semantics (ACR-001, ACR-002, ACR-007, CAN-002, VER-006, ID-001, ID-004 remainders) | complete | signal, order:06, rules, identity | high |
| WI-0044 | Signal 07: scoring AST, advanced algorithms and explainability (AST-001..006, ALG-002..004, SCS-002/003 remainders, SCS-004, SCS-006, SCS-007, SCS-015, CAN-006, VER-005, ARX-014) | complete | signal, order:07, scoring | high |
| WI-0045 | Signal 08: answer primitives and selectors - multi-choice, matrix, ranking/allocation, timers and banking, encodings (ANS-002, ANS-004 remainder, SCS-009..014, SCS-016, SCS-018, ARX-013, CAN-004/LURL-004 remainders) | complete | signal, order:08, selectors, encoding | medium |
| WI-0046 | Signal 09: reporting contract and Folio renderers - report blocks, group reports, comparisons, the standard results print profile (RPT-001..006, SRPP-001..185) | complete | signal, order:09, reporting, folio | medium |
| WI-0047 | Signal 10: administrator application state and UX on Limen/Forma - groups, read-only and degraded modes, conflict workspace, sealing and finalization (ADM-001, ADM-002, ADM-007, ADM-031..033, ADM-035, ADM-062, ADM-065, ADM-066, ADM-070, ADM-077) | complete | signal, order:10, admin, ui, limen, forma | high |
| WI-0048 | Signal 11: analytics, comparisons, lineage, change-impact and dependency invalidation (ADM-012..014, ADM-019, ADM-020, ADM-041, ADM-049, ADM-051, ADM-054; ARX-012 remainder) | complete | signal, order:11, analytics | medium |
| WI-0049 | Signal 12: visualization grammar, dashboards and accessibility (ADM-015..018, ADM-050, ADM-069) | complete | signal, order:12, visualization | medium |
| WI-0050 | Signal 13: report builder, snapshots, exports, configuration packages and policy packs (ADM-021..023, ADM-047, ADM-048, ADM-063) | complete | signal, order:13, reports, exports | medium |
| WI-0051 | Signal 14: administrator privacy, audit without PII, retention and deletion (ADM-024, ADM-030, ADM-045, ADM-064; ARX-009; ID-003 remainder) | complete | signal, order:14, privacy, security | high |
| WI-0052 | Signal 15: storage migration, backup and restore, schema evolution and operational repair (ADM-028, ADM-029, ADM-034, ADM-053, ADM-074..076) | captured | signal, order:15, migration, backup, depends:arca | medium |
| WI-0053 | Signal 16: cross-cutting verification - static analysis, differential and model tests, performance budgets, incremental evaluation, time semantics (ARX-001, ARX-004, ARX-005, ARX-010, ARX-011, ARX-015, CAN-007, ADM-036..038, ADM-059, ADM-068; ACR-006 localization) | captured | signal, order:16, quality | medium |
| WI-0054 | Signal 17: administrator sandbox, synthetic data generator and optional analytics extensions (ADM-039, ADM-040, ADM-042, ADM-043) | captured | signal, order:17, sandbox | low |
| WI-0055 | Stop the Chromium install from hanging the browser-suite CI (unbounded apt-get update in playwright install --with-deps) | complete | ci, playwright, reliability | high |
| WI-0056 | Record the pure-domain pull-forward (WI-0042..WI-0046) in DF-SIGNAL-2026-0001, propose answers to the 12 scoring open questions (DF-SIGNAL-2026-0002), and close out GH-5 | complete | planning, decisions, scoring | high |
| WI-0057 | Signal 05b: persist the template catalog through Arca and finish the authoring remainders WI-0042 left (CAN-001, CAN-005, CAN-008, URLC-004, ACR-003, ACR-008, ACR-009, CAN-003, VER-001, VER-003, VER-004, VER-007, AUT-001..007, ARX-002, SCS-017) | complete | signal, order:05b, authoring, depends:arca | medium |
| WI-0058 | Decompose Authoring.fs (859 lines, SDE-STRUCT-001 under Ordo strict verification) into Drafts, Validation, TemplateDiff and Publication | complete | quality, ordo | high |
| WI-0059 | Signal 07b: scoring remainders after WI-0044 - ipsative, confidence- and completeness-adjusted scoring, per-section direction, weakest/strongest section and confidence outputs, single-response NPS guard, recommendations reading the overall and group result, shared group metadata (ALG-002, ALG-003, SCS-003, CAN-006, VER-005, VER-006, ACR-002, ACR-007, CAN-002) | captured | signal, order:07b, scoring | medium |
| WI-0060 | Signal 08b: encoding remainders after WI-0045 - a signed (authenticity) submission policy beside integrity-only, and an evaluation of separate presence/N/A bitmaps against in-slot special states on realistic surveys (ACR-004, ANS-004) | captured | signal, order:08b, encoding | low |
| WI-0061 | Re-home URLC-005 after WI-0045 completed: GapAnalysisTests failed on PR #31 because no open work item named it | complete | quality, planning | high |
| WI-0062 | Signal 09b: render ReportData through Folio and the page - the standard results print profile, HTML/PDF renderers, localization, respondent/facilitator/360 families, benchmark comparisons and respondent detail in reports (RPT-001..006, SRPP-001..185) | complete | signal, order:09b, reporting, folio | medium |
| WI-0063 | Accept DF-SIGNAL-2026-0002 answers 2-12 (coordinator, on the owner's standing instruction, 2026-10-08); Q1 stays open | complete | decisions, scoring | high |
| WI-0064 | Move Signal to echelon-current 1.9.0 with Conditor 0.6.0 and install Arca 0.2.1 and Fides 0.2.0 through Conditor | complete | platform, conditor, depends:arca, depends:fides | high |
| WI-0065 | Deploy Signal to GitHub Pages in demo mode: Pages workflow, CSP meta, demo banner, docs | complete |  | medium |
| WI-0066 | Deep linking: all navigable state in the URL through Limen-compatible hash routes - route model and codec, .echelon/routes.json inventory, Copy link, sign-in return targets, not-found pages, no answers or respondent data in URLs (SIG-LINK-001..010) | complete | signal, routing, limen, privacy | high |
| WI-0067 | Report-state privacy: the embedded AdminReportState carries per-respondent sorted dimension scores (Evidence) and, in identified groups, invitation-linked identity keys; replace with sufficient statistics and keyed hashes, and keep it out of any URL until then (ID-003, ARX-009, SIG-LINK-008; found in WI-0066) | complete | privacy, signal | high |
| WI-0068 | Pages: the published administrator page (web/admin/) carries no Content-Security-Policy and no demo banner; apply the site's policy and banner to every published page | complete | security, pages | high |
| WI-0069 | Signal 09c: report remainders after WI-0062 - respondent, facilitator and 360-reviewer report families, benchmark comparisons and identified respondent detail in reports, longitudinal series, draft status and caching, and a server-side PDF renderer (RPT-001..006, SRPP-011..028, SRPP-072..088) | captured | signal, reporting, folio | medium |
| WI-0070 | Close DF-SIGNAL-2026-0003 decision 4: the respondent's #r= answer link is an accepted, scoped exception (respondent page only, fragment never logged, never a route, never produced by admin Copy link); the respondent page says plainly the link contains their answers | complete | privacy, signal | high |
| WI-0071 | Move Signal to Limen 0.9.0 (echelon-current 1.11.0): replace vendor/limen-routing with EchelonFoundry.Limen.Routing 0.9.0 through Conditor, bump @echelon-foundry/limen to 0.9.0, keep the inventory byte-equal test and validate .echelon/routes.json against the shipped contract/routes.schema.json | complete | limen, routing, platform | high |
| WI-0072 | Move Signal to echelon-current 1.12.0 through Conditor (repository-only install): Arca 0.3.0, limen-fsharp declared alongside EchelonFoundry.Arca.Limen per arca docs/consuming-arca.md section 5a; the offline write queue stays off (DF-SIGNAL-2026-0001 decision 5) | complete | platform, conditor, arca | high |
| WI-0073 | Signal 05c: the template catalog's remaining wiring and authoring UI after WI-0057 (CAN-001, CAN-003, CAN-005, CAN-008, URLC-004, URLC-005, ACR-003, ACR-008, ACR-009, VER-001, VER-003, VER-004, VER-007, AUT-001..007, ARX-002, ARX-013, ARP-001, AST-005, ALG-004, ADM-046, ADM-055, SCS-010..014, SCS-017, SCS-018) | complete | signal,order:05c,authoring | medium |
| WI-0074 | Record in DF-SIGNAL-2026-0001 decision 5 that Arca 0.3.0's IndexedDB write queue and read cache were evaluated and declined (per-respondent data stays off the device); Arca.Limen stays unreferenced | complete | signal,decision,arca | high |
| WI-0075 | Signal 13b: the report builder, snapshot and export screens, configuration packages and policy packs after WI-0050 (ADM-001, ADM-002, ADM-007, ADM-008, ADM-018, ADM-021, ADM-022, ADM-023, ADM-031, ADM-047, ADM-048, ADM-049, ADM-050, ADM-051, ADM-061, ADM-062, ADM-063, ADM-066, ADM-077) | complete | signal,order:13b,reporting | medium |
| WI-0076 | Signal 14b: apply WI-0051's privacy controls in the store and the page - disclosure ledger on filters, breakdowns and snapshot releases, audit records stored and emitted, retention and retirement enforced (ADM-024, ADM-030, ADM-045, ADM-064, ARX-009, SCS-001) | complete | signal,order:14b,privacy | high |
| WI-0077 | Signal 14c: dataset lifecycle and retention screens, and removal from the repository tree once Arca offers an erasure path (ADM-045) | captured | signal,order:14c,privacy,depends:arca | medium |
| WI-0078 | Signal 05d: generic templates end to end after WI-0073 - the group pipeline and respondent page on generic templates, preview isolation and the remaining authoring surface (CAN-001, CAN-003, CAN-005, CAN-008, URLC-004, URLC-005, ACR-003, ACR-008, ACR-009, VER-001, VER-003, VER-004, VER-007, AUT-001..007, ARX-002, ARX-013, ARP-001, AST-005, ALG-004, ADM-055, SCS-010..014, SCS-017, SCS-018) | complete | signal,order:05d,authoring | medium |
| WI-0079 | Signal 13c: the console's remaining report, dashboard and configuration surfaces after WI-0075 (ADM-001, ADM-002, ADM-007, ADM-008, ADM-018, ADM-021, ADM-022, ADM-023, ADM-031, ADM-047, ADM-048, ADM-049, ADM-050, ADM-051, ADM-061, ADM-062, ADM-063, ADM-066, ADM-077) | captured | signal,order:13c,reporting | medium |
| WI-0080 | Signal: the Pages build pulls published template files from the data repository | captured | signal,deployment,follow-up | low |
| WI-0081 | Signal 05e: the rest of generic templates after WI-0078 - survey page components, pagination, banks, timers and back/forward; ranking, allocation, best-worst and hierarchical answers; locale and invitation expiry carriage; cross-device sample URL checks; fact, recommendation, completion and custom-expression authoring; group results carrying facts and recommendations | captured | signal,order:05e,authoring | medium |
