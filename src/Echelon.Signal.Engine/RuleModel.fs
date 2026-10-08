/// The shared rule model (CAN-002 §§9-13, ACR-001, ACR-007): one small typed
/// expression language used by derived facts, flow, validation, completion
/// and recommendations. Conditions are three-valued: a comparison with an
/// unanswered question is unknown, never false by implication. Evaluation is
/// in `Rules`; static checks (references, ordering, cycles) are in
/// `RuleChecks`.
module Echelon.Signal.Engine.RuleModel

open Echelon.Signal.Engine.Responses

type Comparison =
    | Equal
    | NotEqual
    | Greater
    | GreaterOrEqual
    | Less
    | LessOrEqual

type NumberExpr =
    | Constant of float
    /// The answer's number (`numeric`); unknown when unanswered, special or
    /// not applicable.
    | AnswerNumber of questionId: string
    /// The section's score; unknown when it is not scored.
    | SectionScore of sectionId: string
    | NumberFact of factId: string

type Condition =
    | Always
    | Answered of questionId: string
    | AnswerIs of questionId: string * AnswerValue
    | AnswerIn of questionId: string * AnswerValue list
    | IsSpecial of questionId: string * SpecialState
    | Compare of NumberExpr * Comparison * NumberExpr
    | FactTrue of factId: string
    | CategoryIs of factId: string * category: string
    | All of Condition list
    | Any of Condition list
    | Not of Condition

/// A named, typed value calculated from answers and other facts (ACR-001
/// §2). Facts are not questions and are never encoded.
type FactExpr =
    | BooleanFact of Condition
    | NumberFactOf of NumberExpr
    /// The first case whose condition is true; the default when none is;
    /// unknown when an earlier case is unknown.
    | CategoryFact of cases: (Condition * string) list * otherwise: string option

type Fact = { Id: string; Expr: FactExpr }

type FlowAction =
    /// The question is applicable only while some Show rule for it is true.
    | ShowQuestion of questionId: string
    | HideQuestion of questionId: string
    | ShowSection of sectionId: string
    | HideSection of sectionId: string
    /// Questions after `fromQuestion` and before `toQuestion` are skipped.
    | SkipToQuestion of fromQuestion: string * toQuestion: string
    /// Questions after `fromQuestion` and before the section are skipped.
    | SkipToSection of fromQuestion: string * toSection: string
    /// Everything after `afterQuestion` is not applicable and the survey ends.
    | Terminate of afterQuestion: string * reason: string

type FlowRule =
    { Id: string
      When: Condition
      Then: FlowAction }

type ValidationCheck =
    /// The question must be answered while the condition is true.
    | RequiredWhen of questionId: string * Condition
    /// The condition must not be true.
    | Prohibited of Condition
    /// The answer's number must lie in [minimum, maximum].
    | AllowedRange of questionId: string * minimum: float * maximum: float

type ValidationRule =
    { Id: string
      Check: ValidationCheck
      Message: string }

/// What completion means, beyond "every applicable required question of
/// every applicable required section is answered and nothing is invalid",
/// which always applies (ACR-001 §4, VER-006).
type CompletionPolicy =
    { /// Of applicable questions, at least this percentage answered.
      MinimumAnsweredPercent: float option
      /// Count don't know / not observed / not applicable as answered for
      /// the percentage (they always count for "required answered").
      SpecialCountsAsAnswered: bool
      /// These sections must be applicable and fully answered.
      RequiredSections: string list
      /// An extra condition that must be true.
      Condition: Condition option }

type Priority =
    | Informational
    | Suggested
    | Important
    | Critical

type RecommendationKind =
    | Recommendation
    /// Stronger than advice: a required remediation or escalation.
    | Action of required: bool

type RecommendationRule =
    { Id: string
      When: Condition
      Kind: RecommendationKind
      Priority: Priority
      Category: string
      Title: string
      Description: string
      RelatedSection: string option
      RelatedQuestion: string option }

type RuleSet =
    { Facts: Fact list
      Flow: FlowRule list
      Validation: ValidationRule list
      Completion: CompletionPolicy
      Recommendations: RecommendationRule list }

let defaultCompletion =
    { MinimumAnsweredPercent = None
      SpecialCountsAsAnswered = true
      RequiredSections = []
      Condition = None }

let noRules =
    { Facts = []
      Flow = []
      Validation = []
      Completion = defaultCompletion
      Recommendations = [] }

/// The value of an evaluated fact. An unknown fact is None, never a default.
type FactValue =
    | BoolValue of bool
    | NumberValue of float
    | CategoryValue of string
