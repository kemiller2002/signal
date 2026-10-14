# A021 blind architecture-coding survey acceptance fixture

Status: proposed acceptance fixture for Signal's generic survey-template work  
Related Signal work: WI-0003, WI-0004, WI-0006, WI-0010  
Consumer: Praxis A021 publication package, WI-0075

## Purpose

Use Signal to administer the independent, treatment-blind architecture coding
required by the A021 publication validity gate.

This is deliberately a **closed-ended, no-PII survey**. Signal does not collect
the coder's name, email, employer, account, or free-form rationale. The
companion research evidence worksheet remains outside Signal and is keyed by
the same anonymous question IDs.

The fixture is valuable independently of A021: it exercises a real survey whose
items have different closed-choice domains, no numeric score, explicit
eligibility rules, anonymous finalization, deterministic portable responses,
and machine-readable export.

## Survey identity

- Survey id: `A021-ARCH-CODE-V1`
- Title: **Independent Architecture Coding**
- Version: `1.0.0-draft`
- Identity mode: anonymous
- PII policy: none
- Free-form answer fields: none
- Scoring: none
- Expected primary coders: at least 1, preferably 2
- Treatment mapping: never present in the template, invitation, URL state,
  submission, result, or report.

The draft instrument is:

`assessments/a021-blind-architecture-coding-v1.json`

## Respondent instructions

The respondent is told:

1. This is an independent code-architecture classification exercise.
2. X, Y, M, and N are anonymous implementation labels only.
3. Do not search for experiment names, branch names, prior evaluations,
   publication materials, or treatment mappings.
4. Code each implementation independently before comparing it with its peer.
5. Choose only from the frozen category and confidence choices.
6. Complete the companion evidence worksheet separately using the same item ids.
7. If treatment mapping or prior ratings become known at any point, report the
   exposure and stop.

The survey must not state or imply which result is preferred.

## Eligibility gate

Four required pre-screen questions use a Yes/No domain:

- **ELIG-01**: Have you seen or been told which X/Y/M/N implementation
  corresponds to either experimental treatment?
- **ELIG-02**: Have you read PR #202, the A021 publication manuscript, or a
  summary of its conclusions?
- **ELIG-03**: Have you seen prior ratings or conclusions about the architecture
  of X, Y, M, or N?
- **ELIG-04**: Did you implement, evaluate, or otherwise participate in A021 or
  its R2 execution?

A valid blind-coder submission requires **No** to all four.

Signal should make ineligibility explicit and should not silently treat an
ineligible response as research data. When generic flow/validation exists,
answering Yes to any eligibility item terminates the coding path with a
non-accepted/ineligible result. Until that flow capability exists, the fixture
must at minimum preserve the answers distinctly so import can deterministically
reject the submission.

## Architecture coding

The instrument contains four anonymous implementation sections:

- Study 1 / Arm X
- Study 1 / Arm Y
- Study 2 / Arm M
- Study 2 / Arm N

Each arm has eight dimensions. For every dimension the respondent answers two
required items:

1. **Category**
   - Unified
   - Duplicated-compatible
   - Divergent
   - Missing
   - Not assessable

2. **Confidence**
   - High
   - Medium
   - Low

That produces 64 coding responses.

### Frozen dimensions

**D1 Persistence and store model**  
Does this implementation use one coherent persistence/state model for group
declarations, membership, and checkpoints where a shared model is needed?

**D2 Member admission and repository rule**  
Do create, add, and related admission paths enforce the same eligibility and
repository-placement rules for equivalent work items?

**D3 Lifecycle and member classification**  
Do commands that display or checkpoint group progress classify equivalent
lifecycle states consistently, including active, ready, complete, blocked,
abandoned, or external states where observable?

**D4 Checkpoint ownership and durability**  
Is there one coherent rule for checkpoint ownership, member references,
required fields, durability, and revalidation?

**D5 Error, JSON, and CLI contract**  
Do related commands expose a consistent external contract for equivalent
failures, including error structure, JSON shape, exit codes, invalid input,
and not-found behavior?

**D6 Mutation and concurrency model**  
Do related mutations use a coherent read-decide-write and locking/atomicity
strategy, or do they provide materially different concurrency guarantees?

**D7 Reuse of baseline rules and abstractions**  
When the baseline already provides a relevant rule or abstraction, does this
implementation reuse it consistently rather than reimplementing it with
different behavior?

**D8 Duplicated incompatible abstractions**  
Does this implementation introduce multiple abstractions for the same concept
that encode incompatible rules or behavior?

The definitions of the five categories are part of the frozen survey
instructions and must match the Praxis WI-0075 codebook.

## Post-coding validity items

Two required final items:

- **VALID-01**: During this coding exercise, did you learn or infer the treatment
  mapping or see prior architecture ratings? Yes / No.
- **VALID-02**: Have you completed the companion evidence worksheet for all 32
  architecture category judgments? Yes / No.

A research-valid submission requires:
- ELIG-01..04 = No;
- VALID-01 = No;
- VALID-02 = Yes;
- all 64 architecture category/confidence responses present.

The final validity rule must be deterministic and visible in imported result
state. No answer is silently repaired.

## Signal capability requirements exposed by this fixture

### SIG-A021-001 — Item-specific closed-choice domains

A SurveyTemplate must allow each item to declare its own ordered finite choice
domain. The answer meaning is not a global five-point frequency scale.

Canonical meaning includes:
- option stable id;
- option label;
- option order.

Changing any of those changes the template hash.

### SIG-A021-002 — Unscored surveys

A valid published survey may have no numeric scoring at all.

Signal must not require:
- a numeric item scale;
- a dimension score;
- a minimum numeric observation count.

Completion, validation, import, and deterministic response/export remain fully
defined without scoring.

### SIG-A021-003 — Mixed finite cardinalities

The same template must support:
- 2-choice Yes/No items;
- 5-choice architecture-category items;
- 3-choice confidence items.

URL state encoding must derive per-item answer cardinality from the immutable
template. It must not assume one global answer domain.

Unsupported or impossible states fail explicitly.

### SIG-A021-004 — Sections/instructions are template meaning

The template preserves ordered sections and respondent instructions sufficiently
to present:
- eligibility;
- each anonymous implementation;
- post-coding validity.

Section/instruction changes that can change respondent interpretation are
versioned/published deliberately.

### SIG-A021-005 — Deterministic eligibility/completion validation

The template can declare validation/completion rules sufficient to distinguish:
- complete and research-valid;
- complete but ineligible/contaminated;
- incomplete.

A Yes answer to ELIG-01..04 or VALID-01, or No to VALID-02, must never be
reported as a valid blind coding.

### SIG-A021-006 — Anonymous portable response

The survey can use Signal's anonymous invitation/finalization model:
- no PII;
- no coder name in the response;
- fresh anonymous submission identity;
- invitation instance removed on finalization;
- deterministic template binding/integrity.

An external coordinator may label returned artifacts H1/H2 after receipt; that
label is not respondent identity and need not be encoded by Signal.

### SIG-A021-007 — Raw categorical result/export

Import produces a deterministic raw result retaining, by stable item id:
- selected option id;
- selected option label as resolved from the exact template;
- answered/unanswered state;
- template hash;
- submission hash;
- validation status.

The A021 publication pipeline can transform that raw result into its
`coding-sheet.csv` without scraping rendered HTML.

No agreement statistic needs to be implemented in Signal for this fixture;
Praxis computes inter-rater agreement after the blind submissions are frozen.

### SIG-A021-008 — Companion evidence worksheet boundary

Signal does **not** add a general free-text answer primitive for this fixture.

The coder's source citations and short rationale remain in the external
WI-0075 companion worksheet. The survey item id is the join key.

The survey includes VALID-02 so a response cannot be treated as publication
ready unless the external evidence worksheet is complete.

This preserves Signal's no-free-form/no-PII trial constraint while still using
Signal as the authoritative categorical coding instrument.

### SIG-A021-009 — Blindness preservation

The respondent-facing artifact must not contain:
- treatment names such as grouped/independent/control;
- the treatment mapping;
- prior evaluator conclusions;
- resource/cost results;
- manuscript claims;
- links to PR #202 or public experiment branches.

Tests must scan the published template and generated respondent artifact for a
denylist of treatment-revealing strings.

### SIG-A021-010 — Machine-driven accessibility

Every response control and state transition remains:
- keyboard accessible;
- semantically labelled;
- machine-drivable by Playwright or equivalent;
- deterministic under the same template and answer sequence.

A browser acceptance test completes at least one full valid 70-item response
(4 eligibility + 64 coding + 2 validity), finalizes it anonymously, reopens or
imports the portable artifact, and proves exact answer preservation.

## Question count

- eligibility: 4
- category judgments: 32
- confidence judgments: 32
- final validity: 2

**Total: 70 required closed-ended items.**

## Research handoff

Signal owns:
- survey meaning;
- response state;
- anonymous finalization;
- raw deterministic export.

Praxis WI-0075 owns:
- anonymous code packet;
- human independence protocol;
- companion evidence worksheet;
- mapping reveal;
- agreement calculation;
- publication adjudication.

Neither system should duplicate the other's authority.

## Acceptance

This fixture is usable for WI-0075 only when:

- [ ] a canonical/published Signal template exists from this draft;
- [ ] all 70 items render with their declared item-specific options;
- [ ] eligibility and final validity rules are deterministic;
- [ ] the respondent artifact contains no treatment mapping or prior result;
- [ ] URL/portable response round-trips every allowed answer combination used by the fixture;
- [ ] anonymous finalization retains no invitation instance;
- [ ] raw import/export preserves all 70 stable item ids and selected option ids;
- [ ] a valid response and each invalid eligibility/contamination case have tests;
- [ ] Playwright can complete and finalize the survey without bespoke DOM hacks;
- [ ] the exact published template hash is recorded in the Praxis WI-0075 freeze before a human coder starts.

Until those checks pass, the existing CSV packet remains the scientific
fallback and WI-0075 must not claim that Signal administered the coding.
