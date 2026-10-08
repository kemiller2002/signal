---
id: DF-SIGNAL-2026-0002
title: "Answers to the scoring design's twelve open questions (2-12 accepted 2026-10-08; Q1 open)"
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - signal
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - input-documents/echelon-survey-scoring-expression-tree-approach.txt
  - input-documents/survey-engine-scoring-algorithms.txt
  - input-documents/survey-engine-scoring-selector-completeness-requirements.txt
  - input-documents/survey-engine-advanced-ros-ordo-limen-stress-requirements.txt
  - input-documents/survey-instance-template-versioning-requirements.txt
  - research/decisions/DF-SIGNAL-2026-0001--storage-through-arca-sign-in-through-fides-built-after-summa.md
tags: [scoring, open-questions, proposed]
provenance:
  contributions:
    EXE-20261008T145501158Z-2a5cd831:
      operations: [created]
      at: 2026-10-08T14:56:36.665Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Propose answers to the scoring design's 12 open questions ahead of WI-0044, pending owner confirmation"
    EXE-20261008T163354064Z-4de21669:
      operations: [modified]
      at: 2026-10-08T16:34:02.940Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Answers 2-12 accepted by the coordinator on the owner's standing instruction (2026-10-08); Q1 open"
derived_from: [DF-SIGNAL-2026-0001]
---

# DF-SIGNAL-2026-0002: answers to the scoring open questions

- **Date:** 2026-10-08
- **Status:** **accepted for answers 2-12**; **Q1 open.** The coordinator
  accepted the recommendations on 2026-10-08 on the owner's standing
  instruction. Q1 stays open until the legacy Agile and Scrum Master 360
  surveys exist in the repository. Version 0.1.0 of this record was the
  proposal.
- **Source:** §31 "Open Questions" of
  `input-documents/echelon-survey-scoring-expression-tree-approach.txt`. The
  design says these questions "should drive the final AST rather than adding
  speculative language features."

## Why this exists now

WI-0044 (Signal 07: scoring AST, advanced algorithms and explainability)
depends on these answers, and DF-SIGNAL-2026-0001 amendment 1 pulls WI-0044
forward. The coordinator's rule is that WI-0044 may be built against these
recommendations only if each one can be reversed cleanly behind a pure function
or template configuration. The **Reversible through** line for each question
records how that holds. If an answer is overturned, the change is local to that
named function or configuration, and no stored result changes meaning, because
every result names its scorer and expression-language version (Q9).

Abbreviations: **ETA** is the expression-tree approach document; **ALG** is
`survey-engine-scoring-algorithms.txt`; **SCS** and **ARX** are the requirement
groups of the same names; **VER** is the template-versioning requirements.

## Questions and recommended answers

### Q1. What exact formulas are used by the current Agile and Scrum Master 360 implementations?

**Recommended answer:** The repository cannot answer this, and Signal should
not block on it. Neither the four legacy survey files nor their scoring code are
in this repository. ETA §2 itself says the data "is not yet enough to infer the
exact aggregation algorithm." Signal's reference instrument stays SDRA, which is
already characterized: an exhaustive differential test covers all 59,049 answer
combinations of a dimension. If a legacy survey is migrated, it follows ETA §20:
first locate the old implementation and characterize it with fixtures, then
require identical results, and only then add a named catalog scorer.

**Rationale:** ETA §20 forbids guessing a formula that must later reproduce
existing results.

**Reversible through:** no code depends on it. A migrated survey adds a catalog
entry.

### Q2. Does score currently represent a question weight, a maturity level, a direct contribution, or something else?

**Recommended answer:** In Signal, these three things live in three separate
declared places:

- An item's number is a **direct contribution**: the stored answer mapped
  through the scorer's declared `ItemScale` (`Direct`, `Reverse` or `Mapped`).
- A **weight** is a separate, declared aggregate parameter (`WeightedSum` or
  `WeightedMean`).
- A **maturity level** is interpretation (bands or stages) applied after
  scoring, never inside it.

A legacy per-entry "score" such as -1..3 is modelled as a per-item mapped
contribution.

**Rationale:** ALG "Core Design Principle" and ETA §§16-17 require answer
representation, scoring transformation, aggregation and interpretation to stay
separate.

**Reversible through:** `ItemScale` and `Aggregate` are scorer configuration.
Moving a value between contribution and weight is a template edit.

### Q3. How does `yesNoInProgress` transform an entry's score?

**Recommended answer:** It uses a **ProgressStateMap**: an explicit,
template-declared value for each state, which then passes through the item's
contribution. Nothing is defaulted. A state the map does not name is a
publication-time configuration error. ETA §22's illustrative trace (No, Yes,
In Progress mapped to 0.5) is the recommended preset, labelled as a preset, not
an implied rule:

| State | Value |
|---|---|
| No | 0 |
| In Progress | 0.5 |
| Yes | 1 |

The legacy values, if they ever matter, come from Q1's characterization.

**Rationale:** SCS-002 lists ProgressStateMap as an explicit map, and ALG
principle 4 keeps the answer type independent of scoring behaviour.

**Reversible through:** the map is template data
(`Scoring.ItemScale.Mapped`). Changing a value changes one declaration.

### Q4. How are unanswered questions treated?

**Recommended answer:** An unanswered question is never zero by implication.

- **Unanswered** is a distinct state from Don't know, Not observed and Not
  applicable.
- The default policy is **Exclude**: the item is left out of both the numerator
  and the denominator. A scorer also declares a **minimum number of usable
  observations**; below that minimum the result is `NotScored`, not a low score.
- **Substitute** (count the item as a declared value) happens only when the
  template declares it.
- A score computed before completion is **Provisional**. It becomes **Final**
  only through the finalization transition.

**Rationale:** ETA §14, SCS-008 and ALG principle 9 require explicit missing-data
policy, and ARX-014 requires Provisional to become Final only through a legal
transition.

**Reversible through:** `Scoring.MissingPolicy` is per-scorer configuration.
This policy is already implemented and tested in `Scoring.evaluate`.

### Q5. Are sections independently scored and then aggregated?

**Recommended answer:** Yes.

- A section (domain) is a first-class scoring unit, scored independently from
  its own items' observations.
- An **overall** score is optional. When a template declares one, it is an
  explicit **composite of section results**, not a pooled re-aggregation of all
  items.
- A section that is `NotScored` is reported as such and is excluded from the
  composite unless the composite declares otherwise. It is never zeroed.

**Rationale:** ALG principle 7 ("treat domains as first-class scoring units"),
ETA §15, and VER "Section-Level Scoring."

**Reversible through:** the composite is a pure function of section results,
and whether one exists is template configuration. A pooled overall score can be
declared instead as an ordinary scorer over all items.

### Q6. Are section weights needed?

**Recommended answer:** They should be supported but optional, and the default
is **equal (balanced) weighting**.

- Declared weights must be finite and non-negative, and must not sum to zero.
- The composite divides by the weight of the **scored** sections only, which is
  the same usable-denominator rule items follow. An unscored section therefore
  neither drags the result down nor silently reweights the others without
  showing it in the trace.

**Rationale:** ALG lists both Balanced Domain Scoring and Weighted Domain
Scoring, and VER "Question Weights" makes weights a declared section property.

**Reversible through:** weights are composite configuration. Equal weighting is
the empty declaration.

### Q7. Are scores normalized?

**Recommended answer:** Only when the template declares it.

- Normalization is an explicit transform (`LinearNormalize`,
  `PercentageOfMaximum` or `PercentageOfRange`), declared in the scorer, with
  its precision and rounding stage stated.
- The recommended default for ordinal (Likert-style) dimensions is 0-100, as
  SDRA does.
- The raw value remains visible in the explanation trace, and rounding happens
  once, at the declared stage.

**Rationale:** ARX-006 requires normalization precision and the rounding stage
to be specified, and ALG §6 makes the normalized score a named algorithm, not a
global behaviour.

**Reversible through:** `Scoring.Transform` list configuration.

### Q8. Can a survey produce multiple dimensions rather than one overall score?

**Recommended answer:** Yes. The canonical result is a **profile**: one result
per declared dimension or section, plus an optional overall composite (Q5) and
optional interpretations. A single scalar is the special case where the template
declares only an overall score. SDRA already produces three dimension results
and no overall score.

**Rationale:** ETA §15 says the engine "should not unnecessarily constrain
future assessments to a single number," and ALG "Result Shapes" lists scalar,
categorical, profile and composite.

**Reversible through:** the result shape is a superset. Restricting a template
to one scalar is configuration, not a type change.

### Q9. Must historical results always be exactly reproducible?

**Recommended answer:** Yes. A stored result must reproduce exactly from:

- the immutable TemplateHash;
- the scorer identity and **scorer version**;
- the **ScoringExpressionLanguageVersion**;
- the answers.

A change to scoring semantics is a new scorer or language version, never an edit
of an old one. Recalculating an old response under newer semantics produces a
separate, labelled result and never overwrites the original.

**Rationale:** ETA §13, ARX-006, ARX-012 (lineage) and ARX-014 ("published
templates remain bound to the operator semantics of that version").

**Reversible through:** this is the conservative choice. The result only gains
version metadata, and relaxing the rule later costs nothing, whereas adding
reproducibility after results exist would not be possible.

### Q10. Can survey templates change after responses have been encoded or stored?

**Recommended answer:** No. A published template is immutable. Any change, even
a presentation-only one, publishes a new version with a new TemplateHash.
Responses bind to the TemplateHash, and the compact TemplateReference in the URL
is verified against it on decode. This is already implemented (`Canonical`,
`UrlState`). WI-0042 adds the publication lifecycle that enforces it for
authored templates.

**Rationale:** VER "Published Templates Are Immutable" and the authoring workflow
("a published survey template is immutable") already settle the question as an
accepted requirement.

**Reversible through:** not applicable. The requirements already decide this, so
the recommendation only records it.

### Q11. Will custom scoring be authored only by Echelon or eventually by external survey authors?

**Recommended answer:** Design for external authors from the start, and enable
only Echelon authors at first.

- Every custom expression is treated as untrusted data. It is validated against
  the typed AST, is subject to complexity limits (depth, node count, referenced
  items, aggregation size), and contains no executable code, whoever wrote it.
- Whether non-Echelon templates may carry custom expressions is an explicit
  authoring-policy setting. It is off until the authoring surface (WI-0047)
  exists.

**Rationale:** ETA §25 says to treat the format "as data crossing a trust
boundary" even while Echelon controls the definitions, and ARX-014 says
"Authoring MUST NOT require authors to write or upload executable code."

**Reversible through:** the authoring-policy setting is configuration.
Validation and limits apply either way.

### Q12. What level of scoring explanation should be available to authors and respondents?

**Recommended answer:** The engine can always produce a full **explanation trace**
from the same evaluation path that produced the result. The trace covers
per-item contribution, exclusions with reason codes, aggregate, transforms,
rounding, and section-to-composite weighting.

Visibility differs by audience:

- **Authors, tests and administrator diagnostics** get the full trace.
- **Respondents**, by default, see only the result their template's
  ScoreDisplayPolicy allows, its coverage (how many items were counted or
  excluded), and the method statement. They do not see per-item contribution
  traces unless the template opts in.

**Rationale:** ETA §22 says the production UI need not expose the trace to
respondents, ARX-012 requires that explanations "come from the same evaluation
path," and ARX-014 makes display a separate presentation policy.

**Reversible through:** the trace is always computed. Visibility is a projection
setting (an explanation-visibility option alongside ScoreDisplayPolicy).

## Consequences

- Every answer is reversible through configuration or a pure function. Q10 is
  already a requirement, and Q1 has no code dependency. WI-0044 can therefore
  proceed against these recommendations.
- If the owner rejects or changes an answer, update this record, then change the
  named configuration or catalog entry. Stored results keep their meaning
  because of Q9.
- When the owner confirms, this record's status moves from `review` (proposed) to
  `accepted`, and each answer is marked confirmed or amended.

## Acceptance (2026-10-08)

Answers 2-12 are **accepted**. The coordinator accepted them on 2026-10-08 on
the owner's standing instruction. WI-0044 was built against them, so no
change follows from the acceptance.

Q1 is **open**. It is answered when the legacy surveys and their scoring code
are in the repository and have been characterized (ETA §20).
