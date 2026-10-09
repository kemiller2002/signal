# Echelon Signal handoff

## Objective

Build Signal toward the charter's first bounded outcome (template, live URL,
anonymous finalization, import, deterministic results) on the declared
foundations (Limen, Forma, Folio, Aegis, Ordo, Praxis).

## Platform (2026-10-08)

- echelon-current 1.12.0 through Conditor 0.7.0, repository-only install
  path (`conditor init --manifest conditor.json`; Conditor's
  `upgrade --current` plan would also change the shared workstation profile,
  so it is not authorized): Praxis 3.7.2, Ordo 1.5.0, Visual Engineering
  1.0.1, and as attested NuGet release assets in `vendor/nuget` (mapped in
  `NuGet.config`, pinned in `Directory.Packages.props`): Arca 0.3.0 (Core,
  GitHub, Limen), Fides 0.2.0 and limen-fsharp 0.9.0. `conditor verify`
  passes. `EchelonFoundry.Arca.Limen` is pinned, but no project references it:
  Arca's IndexedDB write queue and read cache were evaluated and declined
  (DF-SIGNAL-2026-0001 decision 5, WI-0074): per-respondent data stays off
  the device.
- Administrator domain (`src/Echelon.Signal.Admin`, pure F# over Arca.Core,
  WI-0038 onward): `Deployment` (closed, validated deployment configuration:
  environment, Fides identity, storage profiles, datasets and their bootstrap
  administrators by GitHub numeric id), `Storage` (Signal's namespace
  `<base>/signal`, dataset folders `datasets/<id>` in their profile's
  repository, set-up operations, opening against Arca's manifest),
  `DatasetManifest` (ADM-005 storage manifest), `ProviderContract` (ADM-003
  capability knowledge, ADM-073 write modes, profile verification, ADM-026
  failure meanings with no automatic retry), `Loading` (staged untrusted
  loading: verify manifests, load records, grant writes), `Growth` (ADM-058).
  The application's `StorageFaults` classifies GitHub failures through Aegis's
  GitHub integration (AER-002, AER-032). Tests run against Arca's in-memory
  provider and conformance suite (`StorageTests`, `ProviderTests`,
  `LoadingTests`).
- Administrators and sign-in (WI-0040): `Access` (capabilities per dataset,
  grant templates, person-only capabilities, the last-administrator rule),
  `AdministratorRecord` and `RosterStore` (the roster as Arca records; one
  command, one commit), `Credential` (ADM-056 credential states, memory-only
  retention, offline withholds every mutation with no queue, cross-tab
  notices only downgrade). Application: `Identity` (the real Fides client;
  the administrator is `github:<numeric id>`), `Store` (opening sets the
  namespace and dataset up only for a configured administrator, verifies,
  loads untrusted, pins the repository; roster commands are conditioned on
  revisions and the change token, decided again after a conflict, and
  reconciled after an unknown outcome), `Flow`. There is still no
  administrator page: the Limen host for Arca's GitHub adapter and Fides'
  ports is WI-0047.
- Durable import (WI-0041): `GroupRecord` (group configuration, exact
  template by hash, submission retention), `ResultRecord` (immutable
  contributions at `records/signal.result/<group>/<shard>/<identity>.json`,
  provenance without person identity), `Intake` (quarantine, order-independent
  batches with a stable id, promotion, resumable batch records),
  `Incremental` in the engine (incremental aggregation equal to full
  recomputation), `ReportState` (AdminReportState, embedded-or-stored by
  size, integrity, resume), `ContributionIndex` (Arca derived index).
  Application `GroupStore`: create, open, import in chunks (concurrent
  distinct imports commute; same identity decided again; unknown outcomes
  reconciled; offline stops and resumes, nothing queued), report state
  persistence, index validate/rebuild.
- Administrator page (WI-0047): `web/admin/index.html` on Limen and Forma,
  engine `AdminApp` (state, messages, effects) and `AdminView` (projection)
  in `Echelon.Signal.Admin`, with `GroupLifecycle` (close, reopen, finalize
  with obligations, seal, supersede), `AdminState` (states, capabilities,
  obligations) and `Conflicts` (versioned configuration changes, conflict
  workspace). The application wires it through Limen: `AdminProtocol`
  (Http, Storage, Navigation, `limen.schedule`, `signal.host`), `Bridge`
  (async Fides and Arca work inside the request/reply loop), `AdminPorts`
  (Fides ports and Arca's GitHub host; a 401 is reported to Fides),
  `AdminWork`, `AdminWire`, `GroupAdmin`; `Runtime.dispatchAdmin` and the
  `DispatchAdmin` export. `web-kernel/host.js` is the `signal.host` pack.
  The deployment's `web/admin/signal.deployment.json` is local (no store);
  a real deployment replaces it. Tested end to end in F# (`AdminPageTests`,
  a fake browser, the real Fides client, Arca's in-memory provider) and in
  Chromium (`tests/browser/admin.spec.js`: start-up and GitHub sign-in).
- Analytics (WI-0048): `Analysis` (measures with prerequisites, typed
  unavailability, distributions, AnalysisExpression v1 with limits),
  `Comparison` (semantic comparability with versioned continuity
  declarations, change, effect size, lineage traces), `Dependencies`
  (derived-state graph, closure invalidation, impact preview, semantic diffs,
  template upgrades), `AnalysisState` (URL-safe analysis state, saved
  analyses, inverse roster commands). The page shows each open group's
  calculated measures and lineage.
- Visualization (WI-0049): `Visualization` (typed specification compiled
  and validated in F#: legal shapes, suitability warnings, explicit missing
  values, withheld totals, accessible description and table, palette with
  patterns and symbols, contrast), `Dashboard` (versioned definitions,
  legal edits, device projections, limits on read), `Locale` (presentation
  only: numbers, percentages, dates, plurals, right-to-left), `Regression`
  (semantic assertions and what a change broke). The page explores the open
  group (section chart, drill to a distribution, sort, counts or
  percentages) with the exploration in the address. A checkbox event that
  arrives unchecked carries an empty value (`AdminProtocol`).

## Current state (2026-10-07)

- Requirement coverage is tracked per requirement group in
  [`docs/requirements/implementation-gap-analysis.md`](docs/requirements/implementation-gap-analysis.md)
  (baseline and current columns; `GapAnalysisTests` holds the counts to the
  rows). Ledger groups: 5 tested, 63 partial, 114 missing, 1 n/a (baseline
  0 / 39 / 143 / 1).
- Engine (`src/Echelon.Signal.Engine`, pure F#):
  - `Scoring`: built-in scoring catalog as data (WI-0035).
  - `Assessment`, `Pilot`: the SDRA pilot, scored through the catalog.
  - `Canonical`: canonical template form and TemplateHash (WI-0031).
  - `UrlState`: ResponseEncodingVersion 1 envelope codec (WI-0031).
  - `LiveUrl`: fragment-first live URL and resume decisions (WI-0032).
  - `Submission`: identified/anonymous finalization (WI-0033).
  - `Import`, `Aggregation`: import pipeline and SurveyGroupResult (WI-0034).
  - `Session`: respondent state machine and view.
  - `Template`, `TemplateCanonical`, `Drafts`, `Validation`, `TemplateDiff`,
    `Publication`: the generic canonical
    template, its canonical form `signal-template/1` and hash, and pure
    authoring, validation, fixtures, diff and immutable publication into a
    catalog (WI-0042). `Pilot.content` is SDRA in that form; the respondent
    page still runs on `Assessment`.
  - `RuleModel`, `Rules`, `RuleChecks`, `RuleCanonical`: the shared typed
    rule model (facts, flow, validation, completion, recommendations), its
    deterministic evaluation, publication-time checks and canonical form
    (WI-0043). `Groups`: roles, dependencies and group completion.
  - `ResultModel`, `Expression`, `Composite`, `Interpretation`, `Registry`,
    `Keyed`, `Benchmark`, `Compatibility`, `SurveyResult`, `GroupScoring`:
    overall scoring as an explicit composite or a typed custom expression,
    explanation traces from the same evaluation, interpretation separate from
    scoring, answer-key/ranking/allocation/pairwise scoring, benchmarks,
    selector-scorer compatibility, the canonical SurveyResult with display
    policy, and group scoring (WI-0044), built against the proposed answers in
    DF-SIGNAL-2026-0002.
  - `Primitives`, `Selectors`, `PrimitiveChecks`, `GenericEnvelope`,
    `Matrix`, `Navigation`, `Banking`, `Timers`: every closed-ended answer
    primitive with a bijective value index, the SCS-010 selector catalog and
    SCS-018 component map, item keys that score them in sections, the
    generic URL codec (bit-for-bit the SDRA layout for SDRA), invitations
    bound to an exact version, matrices, forward-only navigation, seeded
    banking and timers (WI-0045). The respondent page does not render
    generic templates yet (WI-0073).
  - `GroupResult`, `ReportModel`, `Report`, `ReportExport`: the generic
    group result with its hash, the format-neutral reporting contract
    (definitions, blocks, privacy classes, value states, warnings, status,
    comparisons), JSON/CSV exports, report snapshots and the V1 report
    families (WI-0046). Rendering through Folio is WI-0062.
- Application (`src/Echelon.Signal.Application`): Limen protocol (navigation
  replace and clipboard writeText effects), Aegis dispatch boundary, the
  entropy edge.
- The respondent page keeps the URL equal to the answers, resumes from it,
  and lets an invited respondent submit and copy the submission link.
- There is no administrator UI yet: import and aggregation are domain
  functions with tests only.

## Deep linking (WI-0066, 2026-10-08)

- Every administrator view has a canonical hash route (SIG-LINK-001..012,
  `docs/requirements/SIGNAL-DEEP-LINKING.md`, DF-SIGNAL-2026-0003). The route
  model and codec are `src/Echelon.Signal.Admin/Routes.fs` and
  `AdminNavigation.fs`; the inventory is `.echelon/routes.json` (a test holds
  it equal to the table).
- Routing is Limen 0.9.0's `EchelonFoundry.Limen.Routing` (WI-0071), installed
  by Conditor from echelon-current 1.11.0 into `vendor/nuget`. Limen's vectors
  (`tests/limen-routing`) test it, and `RouteSchemaTests` validates
  `.echelon/routes.json` against the shipped `contract/routes.schema.json`.
- DF-SIGNAL-2026-0003 decision 4 is closed (WI-0070): the respondent's `#r=`
  answer link is an accepted, scoped, disclosed exception.
- Browser suite: `SIGNAL_TEST_PORT` moves it off 4321 when that port is taken;
  the `pages` project serves the assembled site under `/signal/`.

## Template catalog (WI-0057, 2026-10-08)

- `TemplateDecodeCore`/`TemplateDecode` (Engine) read a template's canonical
  form `signal-template/1` back to the typed `Content`; every form
  round-trips to the same bytes and hash (`TemplateDecodeTests`).
- `TemplateRecord` (Admin) defines three Arca record types: the immutable
  `signal.template` (canonical bytes, re-hashed on every read), the mutable
  `signal.template-draft` per survey, and the mutable
  `signal.template-catalog` holding the hidden versions.
- `TemplateStore` (Application): `load`, `saveDraft` (`EditDrafts`, at a
  revision), `publish` (`PublishTemplates`, `Publication.publish` then one
  `Create`), `hide` (`PublishTemplates`). `TemplateStoreTests` run against
  Arca's in-memory provider.
- Not yet wired: the console still starts groups from the pilot, and there
  are no catalog or draft screens (WI-0073, with the authoring and
  respondent UI remainders).

## Report definitions, snapshots and exports (WI-0050, 2026-10-08)

- `ReportLibrary` (Admin, pure): versioned report definitions (an unused
  version is replaced, a used one never changes, an edit makes the next),
  exact dependency pins (template, or latest while drafting; visualization,
  result, aggregate and export schemas), immutable formal snapshots carrying
  their canonical report data (the clock is evidence outside every hash),
  `openSnapshot` (its own data or an explicit unresolved state, never
  current state) and `export` (JSON, sections CSV and lineage from the same
  ReportData; no locator or credential can reach it).
- `ReportRecord` (Admin): `signal.report-definition` (mutable, every version
  of one definition) and `signal.report-snapshot` (immutable, verified on
  read). `ReportStore` (Application): `load`, `saveDefinition` and
  `takeSnapshot` (`BuildReports`; snapshot and used definition in one
  commit), `export` (`ExportData`).
- Screens, Folio print/PDF, configuration packages and policy packs: WI-0075.

## Administrator privacy (WI-0051, 2026-10-08)

- `Disclosure` (Admin, pure): anonymous-group thresholds for groups, cells,
  distributions (with diversity) and comparisons; complementary suppression;
  a release ledger that withholds a view nesting with an earlier release and
  differing by fewer than the minimum (repeated snapshots freeze until enough
  responses arrive).
- `Audit`: PII-free records; ids must use Signal's prefixes or be OpaqueIds,
  hashes are `sha256:`, reasons are codes, no free text, clock outside the hash.
- `Retention`: the nine ADM-045 states and legal transitions (audited),
  retention per class, provider-honest claims (GitHub deletion is never
  "permanently erased"), retired datasets read-only, and ADM-064
  reconstructability with recorded limitations.
- WI-0076 applies them: every store operation writes its audit record in the
  same commit (`GovernanceRecord`, `GovernanceStore.audit`); the dataset
  lifecycle is stored and only an active dataset grants changes
  (`GovernanceStore.transition`); a finalized group's accepted results can
  be retired (`retireSources`) with the limitation recorded. Arca never
  deletes an immutable record, so retirement removes results from Signal's
  state only (a `signal.group-retirement` marker); removing them from the
  repository tree needs an Arca erasure path.
- Disclosure in the page (WI-0076): an anonymous group with a minimum above
  one shows its last released state (`Releases.shown`, recorded after each
  import as a `signal.group-release` of opaque keys) and says how many newer
  responses are held back; distributions are withheld when they could single
  respondents out; snapshots go through the same ledger.

## Validation

```bash
dotnet build Echelon.Signal.sln -c Release
dotnet test Echelon.Signal.sln --no-build -c Release
npm run build:wasm && npx playwright test
./praxis validate
```

## Unresolved questions

1. Where invitation links are minted: an administrator surface (WI-0011)
   must create `AnonymousInvitation`/`IdentifiedInvitation` envelopes.
2. Whether entropy should move from the WASM runtime's CSPRNG to a
   negotiated Limen entropy capability pack (ARX-004).
3. Measured URL budgets for 25-200 item surveys (CAN-004 §26).

## Next action

Highest-value remaining gaps, in order: administrator import UI and storage
(WI-0011, WI-0012, WI-0013) over the existing `Import`/`Aggregation` core;
template authoring/publication (WI-0003); flow, validation and
recommendations (WI-0006); report data contract (WI-0009, SRPP).
