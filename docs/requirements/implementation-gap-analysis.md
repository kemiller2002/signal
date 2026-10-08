# Requirement implementation gap analysis

Work item: WI-0030 (gap analysis); implementation items WI-0031 through WI-0035, then WI-0042 onward  
Baseline: `main` at `8f2f956` (2026-10-07)  
Authoritative corpus: `input-documents/` and the migration ledger
[`survey-engine-requirements-migration.md`](survey-engine-requirements-migration.md)

## Purpose and method

The migration ledger accounts for every requirement group and assigns it to a
work item, but says nothing about what is built. This record compares each
requirement group against the code under `src/` and the tests under `tests/`,
and gives one of four statuses:

| Status | Meaning |
|---|---|
| `tested` | Implemented for the group's stated scope, with a test that would fail if it regressed. |
| `partial` | Some normative statements are implemented and tested; others are not. The note says which part exists. |
| `missing` | Nothing in `src/` implements the group. |
| `n/a` | The group is a non-goal or scope statement; it is respected rather than built. |

Granularity is the requirement group (the ledger's unit). A group is `tested`
only when every normative statement in it is covered; one covered statement in
a large group makes it `partial`, never `tested`. The Aegis requirements
(AER-*) and the print-profile requirements (SRPP-*) are not in the ledger;
they are assessed here by section range.

The **Baseline** column is the state of `main` at `8f2f956`. The **Current**
column is updated by each change that closes or narrows a gap, with the work
item that did it.

### What existed at baseline

- `Echelon.Signal.Engine`: a literal pilot assessment (SDRA 0.1.0-draft, 3
  dimensions, 15 items), a five-point frequency answer plus three distinct
  non-numeric answers (don't know, not observed, not applicable), mean-based
  dimension scoring with a minimum-numeric-answer rule and an explicit
  `Unscored` result, a respondent session (`Responding`/`Reviewing`) with an
  all-answered completion rule, and a Limen view projection.
- `Echelon.Signal.Application`: the Limen protocol (handshake, events, view),
  an Aegis boundary around every kernel message, and Forma fault presentation.
- `web/`: the assessment page on Forma and a Folio print surface.
- Tests: 33 xUnit tests (scoring, session, boundary, foundations
  conformance, page/engine binding) and a Playwright suite run in CI.

## Summary

| Corpus | Groups | Baseline tested | Baseline partial | Baseline missing | n/a |
|---|---:|---:|---:|---:|---:|
| Core survey engine (AST, ACR, ARP, CAN, LURL, RPT, ANS, ALG, AUT, URLC, ID, VER) | 72 | 0 | 24 | 48 | 0 |
| Advanced stress trial (ARX) | 15 | 0 | 8 | 7 | 0 |
| Administrator console (ADM) | 77 | 0 | 1 | 76 | 0 |
| Scoring and selector completeness (SCS) | 19 | 0 | 6 | 12 | 1 |
| **Ledger total** | **183** | **0** | **39** | **143** | **1** |

The current counts are in [Coverage after this programme](#coverage-after-this-programme).

## Highest-value gaps and the order they are closed

The charter's first bounded outcome (template, live URL, anonymous
finalization, import, deterministic results) defines value. The gaps that
block it, in dependency order:

1. **WI-0031** Template canonical hash and a versioned, integrity-checked URL
   answer-state codec. Nothing can travel in a URL or be imported without it.
2. **WI-0032** Live respondent URL synchronization through Limen navigation,
   so the URL is the authoritative respondent state and a reopened URL resumes.
3. **WI-0033** Portable submission finalization, identified and anonymous,
   with unlinkable entropy at the application edge.
4. **WI-0034** Administrator import pipeline: explicit decode errors,
   template verification, deduplication, SurveyResult and SurveyGroupResult.
5. **WI-0035** The standard built-in scoring catalog with explicit
   missing-data policy.

Everything else stays assigned to the work items the ledger already names
(WI-0002 through WI-0020); those remain captured, not started.

## Core survey engine groups

| Group | Baseline | Current | Evidence or gap | Work items |
|---|---|---|---|---|
| AST-001 | missing | tested | No scoring expression architecture; scoring is one hard-coded mean. **WI-0044:** the hybrid architecture is in place: precompiled catalog scorers, parameterized presets (`Registry.Presets`), and a typed custom expression (`ResultModel.ScoreExpr`) interpreted by precompiled F# (`Expression`); no survey can carry executable code or CLR expression trees (`ScoringAstTests`). | WI-0007, WI-0044 |
| AST-002 | missing | partial | No typed scoring AST or complexity limits. **WI-0044:** numeric expressions and boolean conditions are separate types; evaluation sees only the constrained rule environment; publication checks language version, references, types, literal zero divisors, empty aggregates and depth/node/reference limits (`Expression.check`). Reading the expression from template JSON (deserialization) remains (WI-0057). | WI-0007, WI-0044 |
| AST-003 | partial | tested | Explicit missing-answer policy (minimum numeric answers, `Unscored`) in `Assessment.scoreDimension`, tested in `AssessmentTests`. No scorer registry. **WI-0044:** versioned scorer identities (`Registry.identify`, `mean:v1`), an exhaustive Composite/Custom overall model, explicit missing policy, section and overall scoring, interpretation separate from scoring, selectors separate from scorers; results record scorer and expression-language versions (`SurveyResult.Lineage`). | WI-0007, WI-0044 |
| AST-004 | missing | partial | No AST; characterization tests only for the mean. **WI-0044:** the custom rule example is expressible (conditional contributions, caps), fixtures assert overall scores and interpretations, and property/differential tests cover the evaluator. Characterization of the legacy Agile/SM360 surveys is not possible: they are not in the repository (DF-SIGNAL-2026-0002 Q1). | WI-0007, WI-0044 |
| AST-005 | missing | partial | No score trace/diagnostics. **WI-0044:** explanation traces from the same evaluation (`Scoring.explain`, `Composite.Trace`, `Expression.Node`), domain errors instead of exceptions, the standard/custom boundary and the trust boundary hold. The authoring UI that shows traces remains (WI-0057, WI-0047). | WI-0007, WI-0044 |
| AST-006 | missing | partial | Scoring architecture not built. **WI-0044:** the proposed architecture is implemented end to end from typed template to SurveyResult, interpretation and diagnostics, except the JSON deserialization step (WI-0057). | WI-0007, WI-0044 |
| ACR-001 | partial | partial | Completion = every item answered (`Session.update ResultsRequested`, `SessionTests`). No flow, branching, derived facts or cross-question validation. **WI-0043:** declarative flow (show/hide question and section, skip to question/section, terminate) decided in one deterministic pass in template order; typed derived facts (boolean, number, category); validation separate from scoring (required-when, prohibited combination, allowed range); explicit completion states (NotStarted, InProgress, Invalid, ReadyToSubmit, Terminated) over applicability, specials and validation (`Rules`, `RulesTests`). Restricting choices, presentation changes and selection-count/allocation/ranking validation remain (WI-0045). **WI-0045:** selection-count, mutual-exclusion, allocation-total and ranking-uniqueness validation are primitive rules; matrix answered-row and one-per-column constraints are validation rules. Restricting choices by flow and flow-driven presentation changes remain (WI-0059). | WI-0006, WI-0043, WI-0045 |
| ACR-002 | missing | partial | No respondent/subject/role or group dependency semantics. **WI-0043:** optional respondent roles relative to an opaque subject, required/optional members, ordering, dependencies between instances and explicit group completion rules, with anonymous groups refusing instance-level rules (`Groups`, `GroupsTests`). Group scoring remains (WI-0044). **WI-0044:** group scoring by mean, weakest contribution or role-balanced weights, with anonymous suppression (`GroupScoring`). Shared group metadata remains (WI-0059). | WI-0005, WI-0043, WI-0044 |
| ACR-003 | missing | partial | No template lineage, compatibility or capability metadata. **WI-0042:** lineage (parent version and hash, outside the hash), schema/engine/encoding compatibility and declared capabilities that fail explicitly when unsupported; presets embedded, so a published template is self-contained (`AuthoringTests`). | WI-0003, WI-0042 |
| ACR-004 | missing | partial | No URL payload, version or integrity policy. **WI-0031:** integrity-only policy with corruption reported separately from template mismatch and impossible content (`UrlStateTests`). Signed (authenticity) policy not built. **WI-0045:** the generic envelope uses the same integrity-only policy and error order. A signed (authenticity) policy remains (WI-0060). | WI-0031, WI-0045 |
| ACR-005 | missing | partial | Import-side replay/revision not built. **WI-0034:** import-side replay is idempotent and a different artifact for an accepted instance is rejected, not substituted (URLC-002 refinement). | WI-0034 |
| ACR-006 | partial | partial | Accessible presentation via Forma components, native radios, labelled progress; Playwright suite. No localization or randomization constraints. | WI-0003, WI-0010 |
| ACR-007 | missing | partial | No recommendation/action rules. **WI-0043:** recommendations and actions (required flag, priority, category, related section/question) are engine outputs ordered by priority then declaration; the evaluation order is explicit in `Rules.evaluate`; fact cycles and flow that reads later content are rejected before publication (`RuleChecks`). Survey and group result steps of the order arrive with WI-0044. **WI-0044:** the overall result and group scores are computed after section scores. Recommendations cannot yet read the overall score or group result (WI-0059). | WI-0006, WI-0043, WI-0044 |
| ACR-008 | missing | partial | No publication validation. **WI-0042:** publication gates for identity, structure, answers, scoring, compatibility, encoding, privacy and fixtures; candidate template fields. Instance and group fields remain (WI-0043). | WI-0003, WI-0042 |
| ACR-009 | partial | partial | Scoring is a pure deterministic function of immutable inputs (`AssessmentTests`). Other invariants depend on missing groups. **WI-0042:** the template-plus-answers result is deterministic for generic templates (3,000-sample differential against the SDRA assessment). | WI-0002, WI-0042 |
| ARP-001 | missing | partial | No import pipeline, SurveyResult or SurveyGroupResult. **WI-0034:** pure import pipeline in ARP §1 order, SurveyResult (identity, template hash, SubmissionHash, dimension results, answer counts) and SurveyGroupResult (`Import`, `Aggregation`, `ImportTests`). Facts, recommendations and AdminReportState remain. **WI-0043:** facts and recommendations now exist as engine outputs (`Rules.evaluate`); the import pipeline still runs on the SDRA `Assessment` and does not yet carry them, and AdminReportState remains (WI-0057, WI-0041). | WI-0034, WI-0043 |
| ARP-002 | missing | partial | No counts, deduplication or aggregates. **WI-0034:** expected/accepted/missing counts, one identity mode per group, completion, unique contribution per identity, dimension mean/median/min/max, non-numeric coverage share, template summary, ResultVersion 1. Group confidence remains. | WI-0034 |
| ARP-003 | missing | missing | No AdminReportState. | WI-0008 |
| ARP-004 | missing | missing | No report persistence. | WI-0008 |
| ARP-005 | missing | partial | No import errors or group enforcement. **WI-0034:** typed import errors and group/mode enforcement; rejected imports change nothing. Persistence-model F# types remain. | WI-0034 |
| ARP-006 | missing | partial | Depends on ARP-001. **WI-0034:** raw submission to deterministic, order-independent results is proven; persistence-transparent reporting remains. | WI-0008 |
| CAN-001 | partial | partial | `Assessment`, `Dimension`, `Item`, `Answer` types exist; no selectors or compatibility metadata. **WI-0042:** generic canonical `Template` (identity, compatibility, metadata, presentation, sections, questions with Boolean/Ordinal/SingleChoice answer primitives, selector presets as presentation, distinct special states) with canonical form `signal-template/1` (`TemplateTests`). Multi-choice, bounded integer, ranking and allocation primitives remain (WI-0045). | WI-0002, WI-0042 |
| CAN-002 | partial | partial | Mean scoring and deterministic evaluation; no facts, flow, validation rules or recommendations. **WI-0043:** one shared typed rule model (`RuleModel`) for facts, flow, validation, completion and recommendations, three-valued so unknown is never false by implication, type-checked and acyclic before publication. | WI-0006, WI-0007, WI-0043 |
| CAN-003 | missing | partial | No instance, group or template reference. **WI-0033:** instance, group and template reference travel in the submission. **WI-0042:** catalog resolution of a template by compact reference, hash or identity and version. | WI-0033, WI-0042 |
| CAN-004 | missing | partial | No answer encoding, versioning, integrity or submission hash. **WI-0031:** bit packing, base64url, ResponseEncodingVersion 1, truncated-SHA-256 integrity, golden vector (`UrlStateTests`). Measured URL budgets and the import-side SubmissionHash remain. **WI-0045:** the codec covers every primitive and composite (matrix rows are ordinary slots). Cross-device measured URL budgets remain (WI-0053). | WI-0031, WI-0045 |
| CAN-005 | missing | partial | No publication validation or canonical publishing. **WI-0042:** publication validation with stable reason codes (identity, questions, sections, scoring references/weights/mapping completeness, compatibility, encoding capacity) and the deterministic publish sequence (`Publication.publish`, `AuthoringTests`). Flow, derived-fact, recommendation and group-aggregation gates remain (WI-0043, WI-0044). | WI-0003, WI-0042 |
| CAN-006 | partial | partial | `DimensionResult` carries coverage separately from score; no canonical survey result or explainability. **WI-0044:** canonical `SurveyResult` with status, section outcomes and traces, overall outcome and trace, interpretations, coverage separate from performance, and reproducible lineage. Confidence and the reporting boundary remain (WI-0046, WI-0059). | WI-0007, WI-0044 |
| CAN-007 | partial | partial | Unit and browser tests exist; no representative fixture corpus or reproducibility checks. | WI-0010 |
| CAN-008 | partial | partial | Deterministic interpreter for scoring only. **WI-0042:** invariants 1-5, 9, 10, 15, 21 and 24 hold for generic templates (immutable artifact, deterministic hash, new version per change, superseded versions resolvable, explicit unsupported-feature failure, embedded presets). | WI-0002, WI-0042 |
| LURL-001 | missing | tested | Session state lives only in the WASM process; the URL never changes. **WI-0032:** every accepted change requests one Limen Navigation `replace` of the fragment (no new history entries); Initialize and LocationChanged resume from the URL; the URL holds only the envelope; completion stays derived (`LiveUrlTests`, Playwright live-URL tests). | WI-0032 |
| LURL-002 | missing | tested | No anonymous finalization. **WI-0033:** live anonymous invitation carries the instance for resume; finalization validates completion, draws a fresh AnonymousSubmissionId, removes the instance and verifies no instance bytes survive; identified finalization keeps instance and group (`Submission`, `SubmissionTests`, Playwright submission test). | WI-0033 |
| LURL-003 | missing | partial | No administrator import. **WI-0034:** import pipeline, derived group state and the anonymous duplicate limitation (stated in `Import`). Persistence escalation remains. | WI-0034 |
| LURL-004 | missing | partial | No encoding infrastructure or round-trip property. **WI-0031:** deterministic compact versioned codec; 500-sample seeded round-trip property for every binding (`UrlStateTests`). Incremental evaluation remains. **WI-0045:** 500-sample seeded round-trip property over every primitive and binding for generic templates. Incremental evaluation remains (WI-0053). | WI-0031, WI-0045 |
| LURL-005 | missing | partial | End-to-end lifecycle depends on LURL-001 through LURL-004. **WI-0032:** respondent half of the lifecycle (live URL, resume) is in place. | WI-0032, WI-0034 |
| RPT-001 | partial | partial | Engine projects results; page and Folio print render them without recalculation. No report block model. | WI-0009 |
| RPT-002 | partial | partial | Coverage and methodology are separate from the score (`Session.view`, print surface). No header/aggregate blocks. | WI-0009 |
| RPT-003 | missing | partial | No comparisons, suppression or audit metadata. **WI-0034:** anonymous suppression below the minimum reportable count happens in the result model, before any rendering. | WI-0009 |
| RPT-004 | missing | missing | No declarative report definitions. | WI-0009 |
| RPT-005 | partial | partial | Missing data is shown as "Not scored", never zero; rounding stated. No partial/final, snapshots or privacy classes. | WI-0009 |
| RPT-006 | partial | partial | One individual report (screen and print). No group report or persistence parity. | WI-0009 |
| ANS-001 | partial | tested | Unanswered, don't know, not observed and not applicable are distinct (`AssessmentTests`); no URL state. **WI-0045:** generic answer state lives only in the URL envelope and is interpreted through the template (`GenericEnvelope`); compact primitives; unanswered, don't know, not observed, not applicable and declined stay distinct in state, scoring and encoding (`PrimitivesTests`, `EncodingTests`). | WI-0031, WI-0045 |
| ANS-002 | partial | tested | One selector preset (five-point frequency). **WI-0045:** the full selector preset catalog over reusable primitives, including ranking, allocation, pairwise, best-worst and hierarchical forms, as presentation only (`Selectors`, `PrimitivesTests`). | WI-0004, WI-0045 |
| ANS-003 | partial | tested | Mean only; no direct/reverse/binary/weighted/mapped/normalized catalog. **WI-0035:** `Scoring` catalog: direct, reverse, mapped/boolean, weighted, sum, mean, normalized, percentage scorers as data (`ScoringTests`). Multi-select scoring waits for a multi-select primitive. **WI-0044:** binary/boolean map and progress-state presets with explicit values. Multi-select scoring still waits for the multi-select primitive (WI-0045). **WI-0045:** multi-select scoring through declared item keys (count selected, option weighted, partial credit, any/all/none) inside ordinary section scorers (`StructureTests`). | WI-0035, WI-0044, WI-0045 |
| ANS-004 | missing | partial | No bit packing, cardinality or special-state encoding. **WI-0031:** fixed-width bit packing from template cardinality (4 bits for 8 answers plus not-answered), special states encoded distinctly, `UrlState`, `UrlStateTests`. Separate presence bitmaps and other primitives' cardinality remain. **WI-0045:** every primitive has a finite cardinality and a bijective value index; slots are fixed-width per question with special states in-slot, bit-for-bit the SDRA layout for SDRA (`EncodingTests`). The evaluation of separate presence/N/A bitmaps against in-slot states on realistic surveys remains (WI-0060). | WI-0031, WI-0045 |
| ANS-005 | partial | tested | Scoring is separate from answers and presentation; no explicit scoring declarations. **WI-0035:** the pilot's scoring is now an explicit catalog declaration (`Assessment.dimensionScorer`). **WI-0044:** every scored section declares its scorer, and selectors never imply scoring (`Compatibility`). Decoding generic templates from the URL remains (WI-0045). **WI-0045:** the pipeline is complete for generic templates: decode the URL against the template's answer definitions, build typed answer state, apply declared scoring (`GenericEnvelope.decode`, `SurveyResult.compute`). | WI-0035, WI-0044, WI-0045 |
| ALG-001 | partial | tested | Mean of a dimension only. **WI-0035:** the primary aggregate and normalization algorithms exist as built-ins. **WI-0044:** all twelve primary V1 algorithms and result patterns exist: raw sum, mean, weighted sum/mean, percentage of maximum, normalized, mapped, reverse, threshold/banding (`Interpretation`), pass/fail, section/domain scoring and profile/composite results (`Composite`, `SurveyResult`). | WI-0035, WI-0044 |
| ALG-002 | missing | partial | No advanced algorithms. **WI-0044:** balanced and weighted domain, geometric and harmonic means, minimum-domain rule, maturity stages (gated), rule classification (category facts), gates and conditional scoring (expressions), penalty/count matching (`Keyed`), pairwise, rank-point and allocation scoring. Ipsative, confidence-adjusted and completeness-adjusted scoring remain (WI-0059). | WI-0007, WI-0044 |
| ALG-003 | missing | partial | No weakest-link or composition. **WI-0044:** weakest link, geometric/harmonic means, caps and maturity stages keep weak domains visible; scalar, categorical, profile and composite result shapes; direction on composites and pass/fail. Section scorers do not yet declare a direction (WI-0059). | WI-0007, WI-0044 |
| ALG-004 | missing | partial | No precompiled/custom boundary. **WI-0044:** precompiled built-ins, declarative presets and a validated WASM-safe custom expression form the boundary; presets cover the V1 authoring catalog. The authoring surface that offers them remains (WI-0057). | WI-0007, WI-0044 |
| AUT-001 | missing | partial | Pilot is a literal; no authoring lifecycle. **WI-0042:** draft to published immutable artifact, derived lifecycle (Draft/Validated/Published/Superseded), versions assigned at publication, parent lineage, no PII primitive by construction (`Drafts`, `Validation`, `Publication`, `AuthoringTests`). Workspace areas for flow, facts, recommendations, localization and reporting remain. | WI-0003, WI-0042 |
| AUT-002 | missing | partial | No authoring capabilities. **WI-0042:** pure section and question edits (add, remove, rename, reorder, move between sections) with stable unique ids; selector presets; catalog scoring per section. Rule, fact, recommendation and completion authoring remain (WI-0043, WI-0044). | WI-0003, WI-0042 |
| AUT-003 | missing | partial | No preview, traces or simulation fixtures. **WI-0042:** scoring preview (`Template.scoreSections`), response simulation fixtures with score and completion assertions that block publication on failure, URL capacity diagnostics, PII-like prompt warnings (or blockers by policy). Preview mode UI and rule traces remain. | WI-0003, WI-0042 |
| AUT-004 | missing | partial | No validation or canonicalization preview. **WI-0042:** structural, answer, scoring, basic completion, compatibility, encoding and privacy validation; canonicalization preview (canonical bytes, TemplateHash, layout fingerprint). Flow, fact, recommendation and reporting validation remain. | WI-0003, WI-0031, WI-0042 |
| AUT-005 | missing | partial | No template diff or publication transaction. **WI-0042:** semantic diff with scoring/encoding/presentation impact, comparability (Comparable / ComparableWithCaution / NotComparable with reasons), validation summary per category, blockers separate from acknowledged warnings, publication preview and transaction (`AuthoringTests`). Rule and report-definition diffs remain. | WI-0003, WI-0042 |
| AUT-006 | missing | partial | No supersession or rollback. **WI-0042:** supersession without removal, rollback as a new derived version, hide without unresolvability, published test manifest (fixture result hashes), deterministic publication independent of publisher and time, hash lock (`verify`). Test-mode submission isolation, cross-device checks and role separation remain. | WI-0003, WI-0042 |
| AUT-007 | missing | partial | No authoring invariants. **WI-0042:** invariants 1-8, 14-18, 21-24, 27 and 28 hold and are tested; the rule, recommendation and reporting invariants wait for WI-0043 and WI-0046. | WI-0003, WI-0042 |
| URLC-001 | partial | tested | No server-side respondent state exists (true by construction); the URL transport does not. **WI-0032:** the URL is the transport and resume state; nothing respondent-side is persisted elsewhere (`LiveUrlTests`). | WI-0032 |
| URLC-002 | missing | partial | No logical response/portable submission distinction. **WI-0033:** live response versus finalized portable submission, sealed after submission; completion derived. Import-side replay/revision remains. | WI-0033 |
| URLC-003 | missing | partial | No portable envelope or explicit decode errors. **WI-0031:** self-contained versioned envelope with explicit errors for every URLC-003 §6 case (`DecodeError`, `UrlStateTests`). Optional admin persistence remains. | WI-0031, WI-0033 |
| URLC-004 | missing | partial | No revised canonical entities. **WI-0042:** the template owns meaning (`Template.Content`), the response owns state (`Template.Answers`), and the template-plus-answers result is a pure function. The revised instance/group entities on generic templates remain (WI-0045). | WI-0002, WI-0042 |
| URLC-005 | missing | partial | No URL artifact. **WI-0031:** the URL artifact format exists. **WI-0045:** generic templates use the same artifact (`GenericEnvelope`). Administrator-side persistence of the artifact remains (WI-0057). | WI-0031, WI-0033, WI-0045 |
| ID-001 | missing | tested | No instance identity. **WI-0033:** opaque 16-byte instance and group ids; identified submission keeps them for external mapping. Group metadata remains (WI-0005). **WI-0043:** group metadata (opaque group and subject ids, expected count, members, roles) with no person data. The invitation's binding to an exact template version for generic templates remains (WI-0045). **WI-0045:** an invitation is bound to one exact published template version by its reference and carries only opaque instance and group ids (`GenericEnvelope.invitation`, `EncodingTests`). | WI-0033, WI-0043, WI-0045 |
| ID-002 | missing | tested | No anonymous submission. **WI-0033:** unlinkable anonymous submission from CSPRNG entropy at the edge; identified/anonymous shapes; no PII field exists (`SubmissionTests`). | WI-0033 |
| ID-003 | missing | tested | No counts or import-side deduplication. **WI-0033:** anonymous conversion and entropy tests (distinct draws, no stuck bits). Counts and import deduplication remain (WI-0034). **WI-0034:** accepted/expected/missing counts, import-side deduplication by instance or anonymous id, anonymous small-group suppression under an explicit policy, and anonymous randomness tests (`ImportTests`, `SubmissionTests`). | WI-0034 |
| ID-004 | missing | tested | No identity model. **WI-0033:** identified/anonymous canonical model and the anonymous invariant. Group-level identity configuration remains. **WI-0043:** group-level identity configuration is explicit (`GroupDesign.Mode`) and validated: anonymous groups refuse requirements that would need the removed instance id, and count contributions without inferring who is missing (`GroupsTests`). With WI-0033/WI-0034 every one of the fifteen ID §23 requirements is covered. | WI-0033, WI-0043 |
| VER-001 | partial | partial | Assessment `Id` and `Version`; no TemplateHash. **WI-0031:** deterministic TemplateHash added. **WI-0042:** stable SurveyIdentifier across versions, exact TemplateVersion assigned at publication, deterministic TemplateHash for generic templates. Result definitions and completion rules in the template remain (WI-0043, WI-0046). | WI-0031, WI-0042 |
| VER-002 | missing | tested | No canonical hashing or compact reference. **WI-0031:** canonical form v1 and SHA-256 TemplateHash with golden vector; 8-byte compact reference verified on decode (`Canonical`, `UrlStateTests`). Immutable publication lifecycle remains (WI-0003). **WI-0042:** published templates are immutable values with a hash lock; derived lifecycle; any content change is a new version and unchanged content is refused; canonical hashing of generic templates with a golden vector; resolution by compact reference (`AuthoringTests`, `TemplateTests`). | WI-0031, WI-0042 |
| VER-003 | missing | partial | No instance runtime information. **WI-0042:** template runtime policy (resume, changes after completion default false, show results, result mode) and derived instance status in which completeness alone never completes (`Template.instanceStatus`, `TemplateTests`). Locale and invitation expiry carriage remain. | WI-0003, WI-0042 |
| VER-004 | partial | partial | Dimensions and prompts; no pagination or navigation policy. **WI-0042:** complete interpretive template contents and pagination as a pure function (survey and per-section items per page, explicit page breaks, section-starts-new-page; randomization fixed to none) that leaves the layout and scores unchanged (`TemplateTests`). The respondent page does not yet paginate generic templates. | WI-0003, WI-0042 |
| VER-005 | partial | partial | Dimension (section) scoring; no survey-level hierarchy or applicability. **WI-0044:** survey-level hierarchy: sections first, then an explicit overall composite or expression over them. Per-section direction remains (WI-0059). | WI-0007, WI-0044 |
| VER-006 | partial | partial | Explicit completion; scalar result only. **WI-0043:** group ordering, required/optional surveys, dependencies and explicit group completion; explicit question/section/survey completion and validation. Group scoring and result shapes remain (WI-0044). **WI-0044:** group scoring, scalar/categorical/profile/composite result shapes. Weakest/strongest section and confidence outputs remain (WI-0059). | WI-0006, WI-0007, WI-0043, WI-0044 |
| VER-007 | partial | partial | Minimal response is the answers map; no response model or persistence semantics. **WI-0042:** generic minimal response (question id to value or special state, absence is unanswered) with per-answer validation (`Template.checkAnswers`). | WI-0031, WI-0042 |

## Advanced stress trial (ARX)

| Group | Baseline | Current | Evidence or gap | Work items |
|---|---|---|---|---|
| ARX-001 | partial | partial | CI runs Praxis validation, Limen verify, foundations and browser suites. Framework friction evidence not recorded. | WI-0010 |
| ARX-002 | partial | partial | Session phases with legal transitions; no capability/obligation/unknown-effect model. **WI-0042:** template lifecycle as explicit derived state with capabilities (allowed actions) derived from it, not granted separately. | WI-0002, WI-0042 |
| ARX-003 | partial | partial | F# authority through Limen, tested (`BoundaryTests`, Playwright). Navigation, clipboard and entropy not yet used. **WI-0032:** Navigation now crosses the real F# WASM/Limen boundary with browser evidence. Clipboard and entropy remain. | WI-0032 |
| ARX-004 | missing | partial | No focus, entropy or clock capability. **WI-0033:** cryptographic entropy from the WASM runtime's CSPRNG (Web Crypto) at the application edge, refused if unusable. Not yet a negotiated Limen entropy pack; focus and clock not built. | WI-0033 |
| ARX-005 | missing | missing | No execution plan or incremental evaluator. | WI-0010 |
| ARX-006 | partial | partial | Rounding is explicit and tested; no canonical bytes or layered hashes. **WI-0031:** exact canonical bytes and golden hash/envelope vectors. Layered semantic/presentation/report hashes remain. | WI-0031 |
| ARX-007 | missing | partial | No URL state, so no fragment placement or multi-tab semantics. **WI-0032:** fragment-first placement, replace-not-push history, URL-authoritative LocationChanged, stale/unrequested navigation results refused (`LiveUrlTests`). Multi-tab divergence policy and leakage audit remain. | WI-0032 |
| ARX-008 | missing | partial | No import state machine or idempotency. **WI-0034:** idempotent re-import and explicit accept/already-imported/rejected outcomes. Durable store, optimistic concurrency and OutcomeUnknown reconciliation remain (WI-0013). | WI-0034 |
| ARX-009 | partial | partial | No PII is collected (closed-ended answers only); no anonymity hardening. **WI-0033:** anonymous unlinkability enforced and tested. | WI-0033 |
| ARX-010 | missing | missing | No static analysis. | WI-0010 |
| ARX-011 | partial | partial | Malformed-message tests and a real-browser suite; no differential/model-based tests. | WI-0010 |
| ARX-012 | missing | partial | No derivation lineage. **WI-0034:** group lineage (template hash, sorted SubmissionHashes, policy, derivation hash) that changes exactly when accepted inputs change. Explanations and reason codes remain. | WI-0034 |
| ARX-013 | missing | partial | No timers, pagination or banking. **WI-0045:** pages hold only applicable questions; independent question/section revisit locks with legal navigation and advance checks (`Navigation`); reproducible seeded bank selection with a pinned algorithm version 1 (`Banking`); a pure timer model (`Timers`). Wiring banks and the selection seed into evaluation and the envelope, timer enforcement and browser back/forward through Limen remain (WI-0057). | WI-0004, WI-0045 |
| ARX-014 | partial | partial | Scores are recomputed from answers on every view; no live display policy or AST. **WI-0035:** exhaustive differential test (all 59,049 answer combinations of a dimension) proves the catalog scorer equals the original hand-written scoring. **WI-0044:** typed custom expressions with language version 1, publication-time validation and limits, built-in/custom differential equality (1,000 samples), provisional/final semantics, and a configurable display policy per result kind that filters only the projection (`SurveyResult.respondentView`). Dependency-closure incremental evaluation remains (WI-0053). | WI-0007, WI-0044 |
| ARX-015 | partial | partial | Aegis at the kernel dispatch boundary with collector-sink tests. Other boundaries do not exist yet. | WI-0010 |

## Administrator console (ADM)

No administrator application exists at baseline. ADM-001 through ADM-076 are
`missing`; their work items are those in the ledger (WI-0011 through WI-0020).
The import, deduplication and aggregation core that WI-0034 adds is the pure
domain beneath ADM-008 through ADM-011; it does not by itself satisfy any ADM
group, which also needs storage, UI and concurrency.

| Group | Baseline | Current | Evidence or gap |
|---|---|---|---|
| ADM-001 to ADM-076 | missing | missing | No administrator product, storage provider, analytics, visualization, report builder or lifecycle. |
| ADM-077 | partial | partial | Aegis is required and used at the respondent boundary; no administrator boundary exists. |

## Scoring and selector completeness (SCS)

| Group | Baseline | Current | Evidence or gap | Work items |
|---|---|---|---|---|
| SCS-001 | partial | partial | Closed-ended answers only, no free text or PII field. | WI-0010 |
| SCS-002 | partial | tested | Mean only. **WI-0035:** DirectValue, BooleanMap/MappedChoice/ProgressStateMap (explicit maps), RawSum, CountAnswered, CountAtLeast, WeightedSum, Mean, WeightedMean, Median (even rule), Min, Max, PercentageOfMaximum (usable denominator), PercentageOfRange, LinearNormalize, LinearTransform, Reverse, Clamp, Floor, Ceiling, validated Banding, PassFail. CountSelected, PercentCorrect and Section/Domain/Profile/Composite built-ins remain. **WI-0044:** CountSelected and PercentCorrect (`Keyed`), Section/Domain aggregates, Profile and CompositeResult (`Composite`, `SurveyResult`), rule-based pass conditions (stages, facts). Wiring CountSelected/PercentCorrect to a multi-choice primitive remains (WI-0045). **WI-0045:** CountSelected and PercentCorrect are wired: item keys turn multi-choice and keyed single-choice answers into numbers that ordinary section scorers aggregate (`StructureTests`). | WI-0035, WI-0044, WI-0045 |
| SCS-003 | missing | partial | No median/mode/trimmed mean. **WI-0035:** Mode with explicit ties, TrimmedMean (rejects removing everything), CappedSum, TopN/BottomN, Difference, Ratio (zero denominator explicit), signed/absolute distance, proximity to target, Top/Bottom/Weighted-K box, favorable/unfavorable/net favorable, standard NPS. Balanced/weighted domain, composite index, bonus/penalty and negative marking remain. **WI-0044:** balanced and weighted domain, composite index, bonus/penalty and negative marking with explained contributions (`Keyed`). Preventing an NPS value from being presented for a single response remains (WI-0059). | WI-0035, WI-0044 |
| SCS-004 | missing | partial | No answer-key or quiz scoring. **WI-0044:** single-choice keys (correct/incorrect/blank independent), ExactSetMatch, AnyCorrect, AllRequired with extra-selection policy, NoneForbidden, PartialCredit with penalty/floor/cap, OptionWeighted and PercentageCorrect, each explaining credited and penalized options (`KeyedTests`). Wiring to the multi-choice primitive remains (WI-0045). **WI-0045:** answer keys are template data and score inside sections, with declared blank points. Carrying the key explanation (credited/penalized options) into SurveyResult traces remains (WI-0059). | WI-0007, WI-0044, WI-0045 |
| SCS-005 | missing | tested | No top-box or NPS. **WI-0035:** top-box, top-K, bottom-K, weighted top-K, favorable rates and standard NPS on integer scales. Rating-selector presets remain. **WI-0044:** every named preset (LikertSum/Mean, WeightedLikert, FavorablePercent, Top/Top2/Bottom/Bottom2 box, NetFavorable, NPS, CSAT, Effort/Confidence/Maturity mean, MaturityStage via `Interpretation`) compiles to catalog scorers (`Registry.Presets`). | WI-0035, WI-0044 |
| SCS-006 | missing | tested | No ranking/allocation scoring. **WI-0044:** RankPoints, Borda, InverseRank, TopKRankCredit, PositionWeighted; direct/normalized/weighted/share/distance allocation with explicit zero totals; win count, weighted wins, win-loss; best, worst, best-minus-worst and normalized (`Keyed`). The ranking/allocation/pairwise primitives that feed them are WI-0045. **WI-0045:** ranking, allocation, pairwise (two-option single choice) and best-worst answers are scored through item keys over the WI-0044 functions. | WI-0007, WI-0044, WI-0045 |
| SCS-007 | missing | tested | No benchmark transforms. **WI-0044:** percentile rank (mid-rank ties, no interpolation), z-score (zero deviation explicit) and T-score (declared transform) against a content-hashed, versioned benchmark; every norm score records the benchmark; no benchmark means unavailable, never fabricated; norm scores are a separate type (`Benchmark`, `KeyedTests`). | WI-0007, WI-0044 |
| SCS-008 | partial | partial | Special states never count as zero; denominator and rounding stated. No configurable policy. **WI-0035:** declared Exclude/Substitute special-state policy, minimum observations, usable-item denominators, half-away-from-zero rounding, no NaN/Infinity. Provisional/final semantics remain. **WI-0044:** provisional versus final result status. A distinct PreferNotToAnswer/Declined special state remains (WI-0045). **WI-0045:** PreferNotToAnswer/Declined is a distinct special state everywhere. BlockScore/ReturnUnavailable policies remain (WI-0059). | WI-0035, WI-0044, WI-0045 |
| SCS-009 | partial | tested | Ordinal-5 primitive only. **WI-0045:** Boolean, Ordinal, SingleChoice, MultiChoice, BoundedNumber (integer and quantized decimal), BoundedRange, Ranking, Allocation, BestWorst and hierarchical primitives with publication-time cardinality; matrix as a composite; individually configurable special states including Declined (`Primitives`, `PrimitivesTests`). | WI-0004, WI-0045 |
| SCS-010 | partial | partial | Radio presentation only. **WI-0045:** every listed selector and semantic preset exists as presentation data that fits a primitive and compiles to explicit labels. Their rendering behaviour (untouched toggles, no dropdown auto-select, slider intent, accessible alternatives) is UI work (WI-0057). | WI-0004, WI-0045 |
| SCS-011 | missing | partial | No multi-choice. **WI-0045:** SelectAny/Exactly/AtLeast/AtMost/Between and exclusive options with a declared clear-or-reject policy; the encoded answer is the resulting semantic set (`Primitives.select`). The page explaining the count before failure is UI (WI-0057). | WI-0004, WI-0045 |
| SCS-012 | missing | partial | No matrix selectors. **WI-0045:** matrix single, multi, Likert, side-by-side and other row scales expand to ordinary questions with row N/A, required rows, answered-row bounds and one use per column; scoring equals row scoring (`Matrix`, `StructureTests`). Responsive and screen-reader presentation is UI (WI-0057). | WI-0004, WI-0045 |
| SCS-013 | missing | partial | No ranking/allocation selectors. **WI-0045:** ranking (full/partial), allocation (total, step, bounds), pairwise, best-worst (distinct) and hierarchical single/multi with declared parent rules as primitives. Non-drag keyboard controls are UI (WI-0057). | WI-0004, WI-0045 |
| SCS-014 | partial | partial | Native radios, keyboard operable, no default answer. **WI-0045:** accessible text is required for every option and selector label. Interaction semantics and explicit unavailable-capability surfacing are UI (WI-0057). | WI-0004, WI-0045 |
| SCS-015 | missing | tested | No selector-to-scorer compatibility check. **WI-0044:** publication refuses incompatible scorers for Boolean, Ordinal and SingleChoice questions with `SCORING-INCOMPATIBLE` (standard NPS only on a 0-10 rating, box scores against the ordinal cardinality, reverse bounds, percentage denominators and ranges). The rules for multi-choice, ranking, allocation, pairwise, best-worst and matrix arrive with those primitives (WI-0045). **WI-0045:** compatibility covers every primitive: answers without a single number need an item key whose kind fits (multi-choice, ranking, allocation, best-worst, range width/midpoint/endpoints), and matrices are checked row by row (`Compatibility`). | WI-0007, WI-0044, WI-0045 |
| SCS-016 | missing | tested | No encoding. **WI-0045:** every primitive and matrix has deterministic finite encoding; publication computes worst-case size per question and refuses a template over the URL budget rather than truncating; cardinality beyond an encodable slot is a blocker (`PrimitiveChecks`, `EncodingTests`). | WI-0031, WI-0045 |
| SCS-017 | missing | partial | No authoring obligations. **WI-0042:** ordinary built-in scorers are authored without expressions; scoring preview and fixtures cover included/excluded counts and final values. The contribution-level preview (mapping, weighting, numerator/denominator, bands) is WI-0044's trace. | WI-0003, WI-0042 |
| SCS-018 | missing | partial | No web-component handoff catalog. **WI-0045:** every preset maps to its SCS-018 component family and variant (`Selectors.componentOf`). The components themselves are the shared design-system work (WI-0057). | WI-0004, WI-0045 |
| SCS-019 | n/a | n/a | Non-goals; respected (no custom expression or free text added). | n/a |

## Aegis installation and usage (AER)

| Range | Baseline | Current | Evidence or gap |
|---|---|---|---|
| AER-001, AER-004 to AER-009 | tested | tested | Core package pinned centrally, configured once, validated, stable identity `Signal`, declared stderr sink (`Boundary.configure`, `FoundationsConformanceTests`). |
| AER-002, AER-003, AER-032 | n/a | n/a | Signal calls no GitHub API yet; required when ADM storage lands (WI-0012). |
| AER-010 to AER-020 | tested | tested | Boundary manifest, capture at dispatch, typed refusals not faults, defects fail loud, stable codes, single translation, safe presentation through Forma (`BoundaryTests`). |
| AER-021 to AER-031 | partial | partial | Fault presentation is accessible text; redaction holds (no answer state in faults); no retry, unknown-effect or offline paths exist yet. |
| AER-033 to AER-035 | tested | tested | Collector sinks, fault-path evidence for the declared boundary, happy path unchanged. |
| AER-036 to AER-042 | partial | partial | Foundations gate and AGENTS.md enforce use; no bypass detector beyond the Praxis foundations check. |

## Standard results print profile (SRPP)

| Range | Baseline | Current | Evidence or gap |
|---|---|---|---|
| SRPP-001 to SRPP-010 | partial | partial | Scoring stays in F#; Folio renders engine values only. No format-neutral ReportData or versioned report profile. |
| SRPP-011 to SRPP-028 | missing | missing | No report families, TOC or cover model. |
| SRPP-029 to SRPP-071 | partial | partial | Dimension results, coverage and limitation text next to scores; no findings, cross-dimension or action blocks. |
| SRPP-072 to SRPP-088 | missing | missing | No group distributions or comparisons. |
| SRPP-089 to SRPP-107 | partial | partial | Methodology text; no appendix, provenance or profile immutability. |
| SRPP-108 to SRPP-160 | partial | partial | Folio print primitives, page number and semantic tables; no Letter/A4 profiles, theming or snapshots. |
| SRPP-161 to SRPP-185 | partial | partial | Print-friendly HTML only; no PDF path, JSON export or verification suite. |

## Coverage after this programme

Counts of the **Current** column, recomputed by each change that updates it
(`GapAnalysisTests` holds them to the rows).

| Corpus | Groups | Current tested | Current partial | Current missing | n/a |
|---|---:|---:|---:|---:|---:|
| Core survey engine | 72 | 15 | 54 | 3 | 0 |
| Advanced stress trial | 15 | 0 | 13 | 2 | 0 |
| Administrator console | 77 | 0 | 1 | 76 | 0 |
| Scoring and selector completeness | 19 | 7 | 11 | 0 | 1 |
| **Ledger total** | **183** | **22** | **79** | **81** | **1** |
