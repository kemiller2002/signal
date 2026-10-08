# Echelon Signal handoff

## Objective

Build Signal toward the charter's first bounded outcome (template, live URL,
anonymous finalization, import, deterministic results) on the declared
foundations (Limen, Forma, Folio, Aegis, Ordo, Praxis).

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
- Application (`src/Echelon.Signal.Application`): Limen protocol (navigation
  replace and clipboard writeText effects), Aegis dispatch boundary, the
  entropy edge.
- The respondent page keeps the URL equal to the answers, resumes from it,
  and lets an invited respondent submit and copy the submission link.
- There is no administrator UI yet: import and aggregation are domain
  functions with tests only.

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
