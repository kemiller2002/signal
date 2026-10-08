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

/// The template schema this engine reads and writes.
[<Literal>]
let SchemaVersion = 1

/// The engine's own version, compared with a template's MinimumEngineVersion.
[<Literal>]
let EngineVersion = 1

/// The response encoding the layout below is computed for (ResponseEncodingVersion).
[<Literal>]
let EncodingVersion = 1

/// Answer states that are deliberately not values (CAN §6). Unanswered is
/// the absence of an answer and is never declared.
type SpecialState =
    | DontKnow
    | NotObserved
    | NotApplicable

/// Fixed order of special states: part of the canonical form and of the
/// encoding layout (answer states follow values in this order).
let specialStates = [ DontKnow; NotObserved; NotApplicable ]

type ChoiceOption =
    { Id: string
      Label: string
      /// The number this option contributes to scoring. A scored question
      /// whose option has no score is a publication blocker ("mapped scores
      /// complete"); an unscored question may leave it empty.
      Score: float option }

/// The answer primitive: what can be stored, independent of how it looks
/// (CAN §6). Further primitives (multi-choice, bounded integer, ranking,
/// allocation) are WI-0045.
type AnswerDefinition =
    /// Two states: false (0) and true (1).
    | Boolean
    /// `points` ordered values 0..points-1.
    | Ordinal of points: int
    /// One option, by id, from an ordered list.
    | SingleChoice of options: ChoiceOption list

/// Selector presets (CAN §7). A selector is presentation: labels and order
/// for an answer primitive, never scoring.
type SelectorPreset =
    | YesNo
    | Likert3
    | Likert5
    | Likert7
    | Agreement5
    | Frequency5
    | Quality5
    | Confidence5
    | Satisfaction5
    | Maturity5
    | NumericRating
    | SingleSelect
    | ForcedChoice

type Selector =
    { Preset: SelectorPreset
      /// One label per stored value, in value order. Empty for choice
      /// selectors, whose options carry their own labels.
      Labels: string list }

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

type Presentation =
    { ItemsPerPage: int option
      ShowQuestionNumbers: bool
      Progress: ProgressMode
      AllowBackNavigation: bool
      ReviewBeforeSubmit: bool
      SectionStartsOnNewPage: bool }

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

/// Capabilities this engine version supports. Later slices add to it.
let supportedCapabilities: Set<Capability> = Set.empty

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
      Sections: Section list }

let defaultPresentation =
    { ItemsPerPage = None
      ShowQuestionNumbers = true
      Progress = QuestionProgress
      AllowBackNavigation = true
      ReviewBeforeSubmit = false
      SectionStartsOnNewPage = false }

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

/// The number of stored values an answer primitive has (its cardinality).
let cardinality =
    function
    | Boolean -> 2
    | Ordinal points -> points
    | SingleChoice options -> options.Length

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
// Answers (VER-007): the minimal response is a map from question id to a
// state; absence is "unanswered".
// ---------------------------------------------------------------------------

type AnswerValue =
    | Flag of bool
    | Point of int
    | Choice of optionId: string

type AnswerState =
    | Value of AnswerValue
    | Special of SpecialState

type Answers = Map<string, AnswerState>

/// Why an answer does not fit its question.
type AnswerProblem =
    | UnknownQuestion of questionId: string
    | WrongPrimitive of questionId: string
    | OutOfRange of questionId: string * value: int
    | UnknownOption of questionId: string * optionId: string
    | SpecialNotOffered of questionId: string * state: SpecialState

let checkAnswer (content: Content) (questionId: string) (state: AnswerState) : AnswerProblem option =
    match tryQuestion content questionId with
    | None -> Some(UnknownQuestion questionId)
    | Some q ->
        match state, q.Answer with
        | Special s, _ when not (List.contains s q.SpecialStates) -> Some(SpecialNotOffered(questionId, s))
        | Special _, _ -> None
        | Value(Flag _), Boolean -> None
        | Value(Point p), Ordinal points when p < 0 || p >= points -> Some(OutOfRange(questionId, p))
        | Value(Point _), Ordinal _ -> None
        | Value(Choice id), SingleChoice options when options |> List.exists (fun o -> o.Id = id) -> None
        | Value(Choice id), SingleChoice _ -> Some(UnknownOption(questionId, id))
        | Value _, _ -> Some(WrongPrimitive questionId)

let checkAnswers (content: Content) (answers: Answers) : AnswerProblem list =
    answers |> Map.toList |> List.choose (fun (id, state) -> checkAnswer content id state)

/// The number an answered value contributes to scoring, before the scorer's
/// item scale. None when a choice has no declared score.
let numeric (question: Question) (value: AnswerValue) : float option =
    match value, question.Answer with
    | Flag b, _ -> Some(if b then 1.0 else 0.0)
    | Point p, _ -> Some(float p)
    | Choice id, SingleChoice options -> options |> List.tryFind (fun o -> o.Id = id) |> Option.bind _.Score
    | Choice _, _ -> None

let observation (question: Question) (state: AnswerState option) : Scoring.Observation =
    match state with
    | None -> Scoring.Special Scoring.Unanswered
    | Some(Special DontKnow) -> Scoring.Special Scoring.DontKnow
    | Some(Special NotObserved) -> Scoring.Special Scoring.NotObserved
    | Some(Special NotApplicable) -> Scoring.Special Scoring.NotApplicable
    | Some(Value v) ->
        match numeric question v with
        | Some n -> Scoring.Numeric n
        | None -> Scoring.Special Scoring.Unanswered

/// Section scores for a set of answers: the scoring preview (AUT-003 §22)
/// and the deterministic evaluation fixtures assert against.
let scoreSections (content: Content) (answers: Answers) : (Section * Scoring.Outcome) list =
    content.Sections
    |> List.choose (fun section ->
        section.Scoring
        |> Option.map (fun scoring ->
            let observations = scoredQuestions section |> List.map (fun q -> observation q (answers.TryFind q.Id))
            section, Scoring.evaluate scoring.Scorer observations))

/// Explicit completion until WI-0043's rules: every required question of
/// every required section has an answer (a special state is an answer).
let isComplete (content: Content) (answers: Answers) =
    content.Sections
    |> List.filter _.Required
    |> List.collect _.Questions
    |> List.filter _.Required
    |> List.forall (fun q -> answers.ContainsKey q.Id)

// ---------------------------------------------------------------------------
// Pagination (VER-004): presentation only; it never affects scoring or the
// encoding layout, which follow template order.
// ---------------------------------------------------------------------------

type Page =
    { Number: int
      /// Question ids on the page, in template order.
      Questions: string list }

/// Pages, in order. A section starts a new page when the survey or the
/// section says so; a page break starts one before its question; otherwise a
/// page fills to the items-per-page of the section that opened it (the
/// section's override, else the survey default, else unlimited).
let pages (content: Content) : Page list =
    let limitOf (section: Section) =
        section.Presentation.ItemsPerPage |> Option.orElse content.Presentation.ItemsPerPage

    // A page under construction: its limit and its question ids, newest first.
    let step (closed: string list list, current: (int option * string list) option) (section: Section, index: int, question: Question) =
        let startsSection = index = 0

        let forcedBreak =
            (startsSection && (content.Presentation.SectionStartsOnNewPage || section.Presentation.StartOnNewPage))
            || List.contains question.Id section.Presentation.PageBreaksBefore

        match current with
        | None -> closed, Some(limitOf section, [ question.Id ])
        | Some(limit, ids) ->
            let full =
                match limit with
                | Some l -> ids.Length >= l
                | None -> false

            if forcedBreak || full then
                List.rev ids :: closed, Some(limitOf section, [ question.Id ])
            else
                closed, Some(limit, question.Id :: ids)

    let items = content.Sections |> List.collect (fun s -> s.Questions |> List.mapi (fun i q -> s, i, q))
    let closed, last = items |> List.fold step ([], None)

    let all =
        match last with
        | Some(_, ids) -> List.rev ids :: closed
        | None -> closed

    all |> List.rev |> List.mapi (fun i ids -> { Number = i + 1; Questions = ids })

// ---------------------------------------------------------------------------
// Encoding layout and URL capacity (CAN-004 §26, AUT-003 §27-28).
// ---------------------------------------------------------------------------

/// One question's fixed-width slot: state 0 is unanswered, then each value,
/// then each offered special state, in `specialStates` order.
type Slot =
    { QuestionId: string
      States: int
      Bits: int }

let private bitsFor states =
    let rec go n = if (1 <<< n) >= states then n else go (n + 1)
    go 1

let layout (content: Content) : Slot list =
    questions content
    |> List.map (fun (_, q) ->
        let states = 1 + cardinality q.Answer + q.SpecialStates.Length
        { QuestionId = q.Id; States = states; Bits = bitsFor states })

/// Bytes of a ResponseEncodingVersion 1 envelope around the answers: version
/// and binding kind, the 8-byte template reference, the largest binding (two
/// 16-byte ids), the 2-byte item count and the 4-byte integrity check.
[<Literal>]
let EnvelopeOverheadBytes = 2 + 8 + 32 + 2 + 4

type Capacity =
    { Questions: int
      AnswerBits: int
      AnswerBytes: int
      EnvelopeBytes: int
      /// Unpadded base64url characters of the largest envelope.
      EncodedCharacters: int }

let capacity (content: Content) : Capacity =
    let slots = layout content
    let bits = slots |> List.sumBy _.Bits
    let answerBytes = (bits + 7) / 8
    let envelope = answerBytes + EnvelopeOverheadBytes

    { Questions = slots.Length
      AnswerBits = bits
      AnswerBytes = answerBytes
      EnvelopeBytes = envelope
      EncodedCharacters = (envelope * 8 + 5) / 6 }

// ---------------------------------------------------------------------------
// Instance runtime status (VER-003), derived, never stored. The clock is an
// argument; nothing here reads one.
// ---------------------------------------------------------------------------

type InstanceStatus =
    | NotStarted
    | InProgress
    | Completed
    | Expired
    | Cancelled

/// What an instance is now. Completeness alone never makes it Completed:
/// only finalization does (VER-003 "do not assume that response completeness
/// alone is sufficient").
let instanceStatus (cancelled: bool) (finalized: bool) (expiresAt: DateTimeOffset option) (now: DateTimeOffset) (answers: Answers) =
    if cancelled then Cancelled
    elif finalized then Completed
    elif expiresAt |> Option.exists (fun e -> now >= e) then Expired
    elif answers.IsEmpty then NotStarted
    else InProgress

/// Whether answers may still change under the template's runtime policy.
let canChangeAnswers (runtime: RuntimePolicy) (status: InstanceStatus) =
    match status with
    | NotStarted
    | InProgress -> true
    | Completed -> runtime.AllowChangesAfterCompletion
    | Expired
    | Cancelled -> false
