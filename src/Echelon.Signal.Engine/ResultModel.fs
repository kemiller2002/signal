/// What a template declares about its results beyond section scores
/// (CAN-006, VER-005, ALG-002/003, AST-001..006, ARX-014), as data:
///
/// - an optional overall score, as an explicit composite of section results
///   or as a typed custom expression (DF-SIGNAL-2026-0002 Q5, Q6, Q11);
/// - interpretations (bands, pass/fail, maturity stages) kept separate from
///   scoring (ETA §16);
/// - when the respondent may see each kind of result (ARX-014 display
///   policy, Q12).
///
/// Evaluation lives in `Composite`, `Expression`, `Interpretation` and
/// `SurveyResult`.
module Echelon.Signal.Engine.ResultModel

open Echelon.Signal.Engine.RuleModel

/// Which way is good for a numeric result (CAN-002 §8). Needed by weakest-
/// link and interpretation; never assumed.
type Direction =
    | HigherIsBetter
    | HigherIsWorse
    | Neutral

/// How section results become an overall result (Q5: sections are scored
/// independently; the overall is a declared composite of them).
type CompositeMethod =
    /// Equal influence for every scored section, whatever its item count
    /// (BalancedDomain).
    | BalancedMean
    /// Declared weights (WeightedDomain); renormalized over scored sections
    /// only (Q6).
    | WeightedMean of weights: (string * float) list
    /// The worst section, by the declared direction (weakest link).
    | WeakestLink
    /// Defined only when every included value is positive.
    | GeometricMean
    | HarmonicMean
    /// The balanced mean, capped at `cap` when any section is below
    /// `threshold` (minimum-domain rule: an average cannot hide a critical
    /// deficiency, ALG-003).
    | MinimumDomain of threshold: float * cap: float

type CompositeSpec =
    { Method: CompositeMethod
      /// The sections composed; empty means every scored section.
      Sections: string list
      /// Fewer scored sections than this is not scored, not low.
      MinimumScoredSections: int
      Direction: Direction
      Decimals: int }

/// The custom scoring expression language (AST-002). Version 1 is fixed;
/// a change to any operator's meaning is a new version (Q9, ARX-014).
[<Literal>]
let ExpressionLanguageVersion = 1

/// A typed numeric scoring expression: data, interpreted by precompiled F#
/// (AST-001). Missing values are explicit: arithmetic on a missing value is
/// missing; list aggregates skip missing values and are missing when all
/// are.
type ScoreExpr =
    | Num of float
    /// The answer's number (`Template.numeric`); missing when unanswered,
    /// special or not applicable.
    | Item of questionId: string
    /// The section's score; missing when it is not scored.
    | Section of sectionId: string
    | FactNumber of factId: string
    | Add of ScoreExpr list
    | Subtract of ScoreExpr * ScoreExpr
    | Multiply of ScoreExpr list
    /// A zero divisor is an explicit "undefined", never Infinity.
    | Divide of ScoreExpr * ScoreExpr
    | Sum of ScoreExpr list
    | Mean of ScoreExpr list
    | Min of ScoreExpr list
    | Max of ScoreExpr list
    | Bound of ScoreExpr * minimum: float * maximum: float
    | When of Condition * ScoreExpr * otherwise: ScoreExpr

type CustomScoring =
    { LanguageVersion: int
      Expression: ScoreExpr
      Decimals: int }

type OverallScoring =
    | Composite of CompositeSpec
    | Custom of CustomScoring

type ResultTarget =
    | OverallResult
    | SectionResultOf of sectionId: string

/// One maturity stage: reached when its condition is true and every earlier
/// stage was reached (gated stages).
type Stage = { Label: string; Requires: Condition }

type InterpretationKind =
    /// Contiguous labelled ranges (`Scoring.Band`).
    | Bands of Scoring.Band list
    | PassFail of threshold: float * direction: Direction
    | Stages of baseline: string * stages: Stage list

type Interpretation =
    { Id: string
      Target: ResultTarget
      Kind: InterpretationKind }

/// When the respondent may see a result (ARX-014). Evaluation never
/// depends on it; it only filters the projection.
type DisplayPolicy =
    | Hidden
    | AfterEachResponse
    | AfterPageAdvance
    | AfterSectionComplete
    | FinalOnly

/// Who sees the explanation trace (Q12): authors and diagnostics always;
/// respondents only when the template opts in.
type ExplanationVisibility =
    | AuthorsOnly
    | RespondentsToo

type Display =
    { Overall: DisplayPolicy
      Sections: DisplayPolicy
      Interpretations: DisplayPolicy
      Explanation: ExplanationVisibility }

/// How a bounded range becomes one number.
type RangeMeasure =
    | RangeWidth
    | RangeMidpoint
    | RangeLow
    | RangeHigh

type BestWorstMeasure =
    | BestOnly
    | WorstOnly
    | BestMinusWorstOnly

/// How an answer without a single number (choice keys, multi-choice,
/// ranking, allocation, best-worst, ranges) becomes the number a section
/// scorer aggregates (SCS-004, SCS-006, SCS-015). Keys are part of the
/// immutable template semantics.
type KeyKind =
    | SingleKeyed of Keyed.SingleKey
    | MultiKeyed of Keyed.MultiKey
    | RankKeyed of item: string * method: Keyed.RankMethod
    | AllocationKeyed of Keyed.AllocationMethod
    | BestWorstKeyed of item: string * measure: BestWorstMeasure
    | RangeKeyed of RangeMeasure

type ItemKey = { Question: string; Key: KeyKind }

type Results =
    { Overall: OverallScoring option
      Interpretations: Interpretation list
      Display: Display
      ItemKeys: ItemKey list }

let defaultDisplay =
    { Overall = FinalOnly
      Sections = FinalOnly
      Interpretations = FinalOnly
      Explanation = AuthorsOnly }

let noResults =
    { Overall = None
      Interpretations = []
      Display = defaultDisplay
      ItemKeys = [] }
