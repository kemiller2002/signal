/// The canonical survey template (CAN-001, CAN-008, URLC-004, VER-004,
/// ACR-003): identity, compatibility, metadata, presentation, sections,
/// questions, answer definitions, selectors and section scoring, as data.
///
/// The template owns meaning; a response owns state. Everything here is a
/// value, every function is total and deterministic, and nothing reads a
/// clock, the network or the file system.
///
/// The SDRA pilot still runs on `Assessment`, whose canonical form (version 1,
/// `Canonical`) and URL codec (`UrlState`) are frozen by golden vectors. This
/// module is the generic successor; `Pilot.template` expresses SDRA in it, and
/// a differential test holds the two to the same scores.
module Echelon.Signal.Engine.Template

open System
open System.Text.RegularExpressions
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.ResultModel

/// The template schema this engine reads and writes.
[<Literal>]
let SchemaVersion = 1

/// The engine's own version, compared with a template's MinimumEngineVersion.
[<Literal>]
let EngineVersion = 1

/// The response encoding the layout below is computed for (ResponseEncodingVersion).
[<Literal>]
let EncodingVersion = 1

type Question =
    { Id: string
      Prompt: string
      HelpText: string option
      Answer: AnswerDefinition
      Selector: Selector
      /// Special states this question offers, in `specialStates` order.
      SpecialStates: SpecialState list
      Required: bool
      Tags: string list }

type SectionPresentation =
    { /// Overrides the survey's ItemsPerPage for this section.
      ItemsPerPage: int option
      StartOnNewPage: bool
      /// Questions that start a new page regardless of item count.
      PageBreaksBefore: string list }

/// A section's score, declared from the built-in catalog (Q5 of
/// DF-SIGNAL-2026-0002: sections are scored independently).
type SectionScoring =
    { Scorer: Scoring.Scorer
      /// The scored questions, in section order. Empty means every question
      /// in the section.
      Questions: string list }

type Section =
    { Id: string
      Title: string
      Description: string option
      Required: bool
      Questions: Question list
      Presentation: SectionPresentation
      Scoring: SectionScoring option }

type ProgressMode =
    | NoProgress
    | QuestionProgress
    | PageProgress
    | SectionProgress

/// Forward-only progression (ARX-013): question and section revisit are
/// configured independently and enforced by the engine (`Navigation`).
type QuestionRevisit =
    | AllowPreviousQuestions
    | LockPreviousQuestionsAfterAdvance

type SectionRevisit =
    | AllowPreviousSections
    | LockPreviousSectionsAfterExit

type Revisit =
    { Questions: QuestionRevisit
      Sections: SectionRevisit }

let fullRevisit =
    { Questions = AllowPreviousQuestions
      Sections = AllowPreviousSections }

type Presentation =
    { ItemsPerPage: int option
      ShowQuestionNumbers: bool
      Progress: ProgressMode
      AllowBackNavigation: bool
      ReviewBeforeSubmit: bool
      SectionStartsOnNewPage: bool
      Revisit: Revisit }

/// Declared engine features (CAN §2). A template declares what it uses; an
/// engine refuses what it does not support.
type Capability =
    | UsesBranching
    | UsesDerivedFacts
    | UsesCustomScoring
    | UsesRanking
    | UsesAllocation
    | UsesGroupScoring
    | UsesConditionalSections
    | UsesRecommendations
    | UsesAdvancedValidation
    | UsesLocalization

/// Capabilities this engine version supports (WI-0043 added the rule
/// capabilities, WI-0044 custom scoring). Later slices add to it.
let supportedCapabilities: Set<Capability> =
    Set.ofList [ UsesBranching; UsesConditionalSections; UsesDerivedFacts; UsesRecommendations; UsesAdvancedValidation; UsesCustomScoring ]

type Compatibility =
    { SchemaVersion: int
      MinimumEngineVersion: int
      ResponseEncodingVersion: int
      Capabilities: Capability list }

type ResultMode =
    | Immediate
    | Hidden
    | Deferred
    | External

/// Runtime defaults a published template gives every instance (VER-003).
/// There is no server-side instance store (URLC-001); these are the policy
/// the respondent runtime enforces from the template alone.
type RuntimePolicy =
    { AllowResume: bool
      /// Normally false (VER-003).
      AllowChangesAfterCompletion: bool
      ShowResults: bool
      ResultMode: ResultMode }

type Metadata =
    { Title: string
      ShortTitle: string option
      Description: string option
      Instructions: string option
      Tags: string list }

/// Everything a template means, without its version or provenance. Drafts
/// edit it; publication freezes it.
type Content =
    { Metadata: Metadata
      Compatibility: Compatibility
      Presentation: Presentation
      Runtime: RuntimePolicy
      Sections: Section list
      Rules: RuleSet
      Results: Results }

let defaultPresentation =
    { ItemsPerPage = None
      ShowQuestionNumbers = true
      Progress = QuestionProgress
      AllowBackNavigation = true
      ReviewBeforeSubmit = false
      SectionStartsOnNewPage = false
      Revisit = fullRevisit }

let defaultSectionPresentation =
    { ItemsPerPage = None
      StartOnNewPage = false
      PageBreaksBefore = [] }

let defaultRuntime =
    { AllowResume = true
      AllowChangesAfterCompletion = false
      ShowResults = true
      ResultMode = Immediate }

let defaultCompatibility =
    { SchemaVersion = SchemaVersion
      MinimumEngineVersion = EngineVersion
      ResponseEncodingVersion = EncodingVersion
      Capabilities = [] }

// ---------------------------------------------------------------------------
// Navigation helpers.
// ---------------------------------------------------------------------------

/// Every question in template order, with its section.
let questions (content: Content) =
    content.Sections |> List.collect (fun s -> s.Questions |> List.map (fun q -> s, q))

let tryQuestion (content: Content) (id: string) =
    questions content |> List.tryFind (fun (_, q) -> q.Id = id) |> Option.map snd

/// The scored questions of a section: the declared list, or every question.
let scoredQuestions (section: Section) =
    match section.Scoring with
    | None -> []
    | Some scoring when scoring.Questions.IsEmpty -> section.Questions
    | Some scoring -> scoring.Questions |> List.choose (fun id -> section.Questions |> List.tryFind (fun q -> q.Id = id))

let private identifierPattern = Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)

/// Stable semantic identifiers: 1-64 characters, letters, digits, '.', '_'
/// and '-', starting with a letter or digit.
let isIdentifier (text: string) = identifierPattern.IsMatch text

// ---------------------------------------------------------------------------
// Answer checking.
// ---------------------------------------------------------------------------


/// Why an answer does not fit its question.
type AnswerProblem =
    | UnknownQuestion of questionId: string
    | WrongPrimitive of questionId: string
    | OutOfRange of questionId: string * value: int
    | UnknownOption of questionId: string * optionId: string
    | SpecialNotOffered of questionId: string * state: SpecialState
    /// The value has the right shape but breaks the primitive's rules
    /// (selection count, exclusivity, allocation total, ...).
    | InvalidValue of questionId: string * reason: string

let checkAnswer (content: Content) (questionId: string) (state: AnswerState) : AnswerProblem option =
    match tryQuestion content questionId with
    | None -> Some(UnknownQuestion questionId)
    | Some q ->
        match state with
        | Special s when not (List.contains s q.SpecialStates) -> Some(SpecialNotOffered(questionId, s))
        | Special _ -> None
        | Value v ->
            match Primitives.check q.Answer v, q.Answer, v with
            | None, _, _ -> None
            | Some _, Ordinal _, Point p -> Some(OutOfRange(questionId, p))
            | Some _, (SingleChoice _ | Hierarchical _), Choice id when (options q.Answer |> List.forall (fun o -> o.Id <> id)) ->
                Some(UnknownOption(questionId, id))
            | Some _, _, _ when (toIndex q.Answer v).IsNone && not (Primitives.ofIndex q.Answer 0UL |> Option.exists (fun sample -> sample.GetType() = v.GetType())) ->
                Some(WrongPrimitive questionId)
            | Some reason, _, _ -> Some(InvalidValue(questionId, reason))

let checkAnswers (content: Content) (answers: Answers) : AnswerProblem list =
    answers |> Map.toList |> List.choose (fun (id, state) -> checkAnswer content id state)

/// The number an answered value contributes to scoring, before the scorer's
/// item scale (`Primitives.numeric`).
let numeric (question: Question) (value: AnswerValue) : float option = Primitives.numeric question.Answer value

let observation (question: Question) (state: AnswerState option) : Scoring.Observation =
    match state with
    | None -> Scoring.Special Scoring.Unanswered
    | Some(Special DontKnow) -> Scoring.Special Scoring.DontKnow
    | Some(Special NotObserved) -> Scoring.Special Scoring.NotObserved
    | Some(Special NotApplicable) -> Scoring.Special Scoring.NotApplicable
    | Some(Special Declined) -> Scoring.Special Scoring.Declined
    | Some(Value v) ->
        match numeric question v with
        | Some n -> Scoring.Numeric n
        | None -> Scoring.Special Scoring.Unanswered

let private keyOf (content: Content) (q: Question) =
    content.Results.ItemKeys |> List.tryFind (fun k -> k.Question = q.Id) |> Option.map _.Key

/// The number an answer contributes under the template's item key, if it
/// has one, else `numeric` (SCS-004, SCS-006).
let keyedNumber (content: Content) (q: Question) (value: AnswerValue) : float option =
    let indicator b = if b then 1.0 else 0.0

    match keyOf content q, value, q.Answer with
    | None, _, _ -> numeric q value
    | Some(SingleKeyed k), Choice id, _ -> Some (Keyed.single k (Some id)).Final
    | Some(MultiKeyed k), Choices ids, _ -> Some (Keyed.multi k ids).Final
    | Some(RankKeyed(item, m)), Order ids, Ranking r -> Keyed.rank m r.Options.Length ids item
    | Some(AllocationKeyed m), Allocated steps, Allocation a ->
        match Keyed.allocation m (steps |> Map.map (fun _ n -> float n * a.Step)) with
        | Scoring.Score(v, _, _) -> Some v
        | Scoring.NotScored _ -> None
    | Some(BestWorstKeyed(item, BestOnly)), BestWorstPick(b, _), _ -> Some(indicator (b = item))
    | Some(BestWorstKeyed(item, WorstOnly)), BestWorstPick(_, w), _ -> Some(indicator (w = item))
    | Some(BestWorstKeyed(item, BestMinusWorstOnly)), BestWorstPick(b, w), _ -> Some(indicator (b = item) - indicator (w = item))
    | Some(RangeKeyed measure), TickRange(lo, hi), BoundedRange b ->
        let low, high = tickValue b lo, tickValue b hi

        Some(
            match measure with
            | RangeWidth -> high - low
            | RangeMidpoint -> (low + high) / 2.0
            | RangeLow -> low
            | RangeHigh -> high
        )
    | Some _, _, _ -> None

/// `observation` under the template's item keys: a declared blank score
/// applies to an unanswered keyed question; everything else is unchanged.
let observationIn (content: Content) (question: Question) (state: AnswerState option) : Scoring.Observation =
    match keyOf content question, state with
    | Some(SingleKeyed k), None -> Scoring.Numeric k.PointsBlank
    | _, Some(Value v) ->
        match keyedNumber content question v with
        | Some n -> Scoring.Numeric n
        | None -> Scoring.Special Scoring.Unanswered
    | _ -> observation question state

/// Section scores for a set of answers: the scoring preview (AUT-003 §22)
/// and the deterministic evaluation fixtures assert against.
let scoreSections (content: Content) (answers: Answers) : (Section * Scoring.Outcome) list =
    content.Sections
    |> List.choose (fun section ->
        section.Scoring
        |> Option.map (fun scoring ->
            let observations = scoredQuestions section |> List.map (fun q -> observationIn content q (answers.TryFind q.Id))
            section, Scoring.evaluate scoring.Scorer observations))
