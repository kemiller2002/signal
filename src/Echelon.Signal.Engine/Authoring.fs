/// Template authoring, validation, testing and immutable publication
/// (AUT-001..AUT-007, CAN-005, ACR-003, ACR-008, ARX-002).
///
/// A draft is a mutable value edited by pure functions; publication is a
/// pure function from a draft and the catalog of what is already published
/// to a new immutable artifact and the next catalog, or a refusal that says
/// exactly why. Nothing is stored here: persistence of the catalog is the
/// storage slices' (WI-0039) job, and it stores these values unchanged.
module Echelon.Signal.Engine.Authoring

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open Echelon.Signal.Engine.Template

// ---------------------------------------------------------------------------
// Drafts and their edits (AUT-001 §§5-9, AUT-002 §§10-11).
// ---------------------------------------------------------------------------

/// The published version a draft was derived from (AUT-001 §9).
type ParentReference = { Version: string; Hash: string }

/// An authored expectation of a fixture (AUT-003 §26).
type Assertion =
    /// The section's score, or None for "not scored".
    | SectionScore of sectionId: string * expected: float option
    | Completion of complete: bool

/// A response simulated directly from answer state (AUT-003 §24).
type Fixture =
    { Id: string
      Name: string
      Answers: Answers
      Expect: Assertion list }

type Draft =
    { SurveyId: string
      Parent: ParentReference option
      Content: Content
      Fixtures: Fixture list }

type EditError =
    | UnknownSection of sectionId: string
    | UnknownQuestionId of questionId: string
    | DuplicateId of id: string
    | PositionOutOfRange of position: int

let newDraft (surveyId: string) (title: string) : Draft =
    { SurveyId = surveyId
      Parent = None
      Content =
        { Metadata =
            { Title = title
              ShortTitle = None
              Description = None
              Instructions = None
              Tags = [] }
          Compatibility = defaultCompatibility
          Presentation = defaultPresentation
          Runtime = defaultRuntime
          Sections = [] }
      Fixtures = [] }

let private sectionIds (content: Content) = content.Sections |> List.map _.Id
let private questionIds (content: Content) = questions content |> List.map (fun (_, q) -> q.Id)

let private withContent (draft: Draft) (content: Content) = { draft with Content = content }

let private mapContent (f: Content -> Result<Content, EditError>) (draft: Draft) =
    f draft.Content |> Result.map (withContent draft)

/// Moves the element at `from` to `position` in a list.
let private move (from: int) (position: int) (items: 'a list) =
    if position < 0 || position >= items.Length then
        Error(PositionOutOfRange position)
    else
        let item = items[from]
        let rest = items |> List.removeAt from
        Ok(rest |> List.insertAt position item)

let addSection (section: Section) =
    mapContent (fun c ->
        if List.contains section.Id (sectionIds c) then
            Error(DuplicateId section.Id)
        else
            match section.Questions |> List.tryFind (fun q -> List.contains q.Id (questionIds c)) with
            | Some q -> Error(DuplicateId q.Id)
            | None -> Ok { c with Sections = c.Sections @ [ section ] })

let private updateSection (sectionId: string) (f: Section -> Result<Section, EditError>) (c: Content) =
    match c.Sections |> List.tryFindIndex (fun s -> s.Id = sectionId) with
    | None -> Error(UnknownSection sectionId)
    | Some i -> f c.Sections[i] |> Result.map (fun s -> { c with Sections = c.Sections |> List.updateAt i s })

let removeSection (sectionId: string) =
    mapContent (fun c ->
        if List.contains sectionId (sectionIds c) then
            Ok { c with Sections = c.Sections |> List.filter (fun s -> s.Id <> sectionId) }
        else
            Error(UnknownSection sectionId))

let moveSection (sectionId: string) (position: int) =
    mapContent (fun c ->
        match c.Sections |> List.tryFindIndex (fun s -> s.Id = sectionId) with
        | None -> Error(UnknownSection sectionId)
        | Some i -> move i position c.Sections |> Result.map (fun sections -> { c with Sections = sections }))

let editSection (sectionId: string) (f: Section -> Section) =
    mapContent (updateSection sectionId (f >> Ok))

let addQuestion (sectionId: string) (question: Question) =
    mapContent (fun c ->
        if List.contains question.Id (questionIds c) then
            Error(DuplicateId question.Id)
        else
            c |> updateSection sectionId (fun s -> Ok { s with Questions = s.Questions @ [ question ] }))

let private sectionOf (c: Content) (questionId: string) =
    c.Sections |> List.tryFind (fun s -> s.Questions |> List.exists (fun q -> q.Id = questionId))

let removeQuestion (questionId: string) =
    mapContent (fun c ->
        match sectionOf c questionId with
        | None -> Error(UnknownQuestionId questionId)
        | Some s -> c |> updateSection s.Id (fun s -> Ok { s with Questions = s.Questions |> List.filter (fun q -> q.Id <> questionId) }))

let editQuestion (questionId: string) (f: Question -> Question) =
    mapContent (fun c ->
        match sectionOf c questionId with
        | None -> Error(UnknownQuestionId questionId)
        | Some s ->
            let edited = s.Questions |> List.find (fun q -> q.Id = questionId) |> f

            // An edit may not give a question another question's id.
            if edited.Id <> questionId && List.contains edited.Id (questionIds c) then
                Error(DuplicateId edited.Id)
            else
                c |> updateSection s.Id (fun s -> Ok { s with Questions = s.Questions |> List.map (fun q -> if q.Id = questionId then edited else q) }))

/// Moves a question to `position` within a section (possibly another one).
let moveQuestion (questionId: string) (toSection: string) (position: int) =
    mapContent (fun c ->
        match sectionOf c questionId, c.Sections |> List.tryFind (fun s -> s.Id = toSection) with
        | None, _ -> Error(UnknownQuestionId questionId)
        | _, None -> Error(UnknownSection toSection)
        | Some source, Some target ->
            let question = source.Questions |> List.find (fun q -> q.Id = questionId)
            let remaining = if source.Id = target.Id then target.Questions |> List.filter (fun q -> q.Id <> questionId) else target.Questions

            if position < 0 || position > remaining.Length then
                Error(PositionOutOfRange position)
            else
                c
                |> updateSection source.Id (fun s -> Ok { s with Questions = s.Questions |> List.filter (fun q -> q.Id <> questionId) })
                |> Result.bind (updateSection target.Id (fun s -> Ok { s with Questions = s.Questions |> List.filter (fun q -> q.Id <> questionId) |> List.insertAt position question })))

let editContent (f: Content -> Content) (draft: Draft) = { draft with Content = f draft.Content }

let addFixture (fixture: Fixture) (draft: Draft) =
    if draft.Fixtures |> List.exists (fun f -> f.Id = fixture.Id) then
        Error(DuplicateId fixture.Id)
    else
        Ok { draft with Fixtures = draft.Fixtures @ [ fixture ] }

// ---------------------------------------------------------------------------
// Validation (CAN-005 §28, AUT-004 §§32-43, AUT-005 §§49-51).
// ---------------------------------------------------------------------------

type Category =
    | StructuralCategory
    | AnswerCategory
    | ScoringCategory
    | CompletionCategory
    | CompatibilityCategory
    | EncodingCategory
    | PrivacyCategory
    | FixturesCategory

type Severity =
    | Blocker
    | Warning

/// A finding with a stable reason code (ARX-012 "stable reason codes").
type Finding =
    { Code: string
      Category: Category
      Severity: Severity
      /// The id the finding is about ("" for the template as a whole).
      Subject: string
      Message: string }

type Policy =
    { /// The longest respondent URL supported, all characters included.
      /// Provisional until CAN-004 §26's cross-device measurements exist.
      MaximumUrlCharacters: int
      /// Characters reserved for the origin, path and fragment marker.
      BaseUrlAllowance: int
      /// Publication needs at least one fixture (AUT-003 §25).
      RequireFixtures: bool
      /// PII-like prompt wording blocks instead of warning (AUT-003 §31).
      PiiPromptsBlock: bool
      /// Fewer scored questions than this in a scored section warns (AUT-005 §51).
      LowScoringItemCount: int }

let defaultPolicy =
    { MaximumUrlCharacters = 2000
      BaseUrlAllowance = 200
      RequireFixtures = true
      PiiPromptsBlock = false
      LowScoringItemCount = 3 }

let private finding category severity code subject message =
    { Code = code
      Category = category
      Severity = severity
      Subject = subject
      Message = message }

let private duplicates (ids: string list) =
    ids |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

let private structural (draft: Draft) =
    let c = draft.Content
    let block = finding StructuralCategory Blocker

    [ if not (isIdentifier draft.SurveyId) then
          block "STRUCT-SURVEY-ID" draft.SurveyId "The survey identifier is missing or not a stable identifier."
      if String.IsNullOrWhiteSpace c.Metadata.Title then
          block "STRUCT-TITLE" "" "The template has no title."
      if c.Sections.IsEmpty then
          block "STRUCT-NO-SECTIONS" "" "The template has no sections."
      for id in duplicates (sectionIds c) do
          block "STRUCT-DUPLICATE-SECTION" id $"Section id '{id}' is used more than once."
      for id in duplicates (questionIds c) do
          block "STRUCT-DUPLICATE-QUESTION" id $"Question id '{id}' is used more than once."
      for s in c.Sections do
          if not (isIdentifier s.Id) then
              block "STRUCT-SECTION-ID" s.Id $"Section id '{s.Id}' is not a stable identifier."
          if s.Questions.IsEmpty then
              block "STRUCT-EMPTY-SECTION" s.Id $"Section '{s.Id}' has no questions."
          for breakId in s.Presentation.PageBreaksBefore do
              if not (s.Questions |> List.exists (fun q -> q.Id = breakId)) then
                  block "STRUCT-PAGE-BREAK-REFERENCE" breakId $"Page break before '{breakId}' names no question of section '{s.Id}'."
          for n in Option.toList s.Presentation.ItemsPerPage do
              if n < 1 then
                  block "STRUCT-ITEMS-PER-PAGE" s.Id "Items per page must be at least 1."
      for n in Option.toList c.Presentation.ItemsPerPage do
          if n < 1 then
              block "STRUCT-ITEMS-PER-PAGE" "" "Items per page must be at least 1."
      for _, q in questions c do
          if not (isIdentifier q.Id) then
              block "STRUCT-QUESTION-ID" q.Id $"Question id '{q.Id}' is not a stable identifier."
          if String.IsNullOrWhiteSpace q.Prompt then
              block "STRUCT-PROMPT" q.Id $"Question '{q.Id}' has no prompt." ]

/// The answer primitive each selector preset presents, and its fixed
/// cardinality where the preset fixes one.
let presetFits (preset: SelectorPreset) (answer: AnswerDefinition) =
    match preset, answer with
    | YesNo, Boolean -> true
    | Likert3, Ordinal 3 -> true
    | (Likert5 | Agreement5 | Frequency5 | Quality5 | Confidence5 | Satisfaction5 | Maturity5), Ordinal 5 -> true
    | Likert7, Ordinal 7 -> true
    | NumericRating, Ordinal _ -> true
    | (SingleSelect | ForcedChoice), SingleChoice _ -> true
    | _ -> false

let private answers' (draft: Draft) =
    let block = finding AnswerCategory Blocker

    [ for _, q in questions draft.Content do
          match q.Answer with
          | Ordinal points when points < 2 || points > 11 ->
              block "ANSWER-ORDINAL-POINTS" q.Id $"Question '{q.Id}' needs 2 to 11 ordinal points."
          | SingleChoice options ->
              if options.Length < 2 then
                  block "ANSWER-TOO-FEW-OPTIONS" q.Id $"Question '{q.Id}' needs at least two options."
              for id in duplicates (options |> List.map _.Id) do
                  block "ANSWER-DUPLICATE-OPTION" q.Id $"Option id '{id}' is used more than once in '{q.Id}'."
              for o in options do
                  if not (isIdentifier o.Id) then
                      block "ANSWER-OPTION-ID" q.Id $"Option id '{o.Id}' in '{q.Id}' is not a stable identifier."
              if options |> List.exists (fun o -> o.Score |> Option.exists (fun s -> Double.IsNaN s || Double.IsInfinity s)) then
                  block "ANSWER-OPTION-SCORE" q.Id $"An option score in '{q.Id}' is not a finite number."
          | _ -> ()

          if not (presetFits q.Selector.Preset q.Answer) then
              block "ANSWER-SELECTOR-INCOMPATIBLE" q.Id $"Selector '{TemplateCanonical.presetName q.Selector.Preset}' does not present the answer of '{q.Id}'."
          else
              match q.Answer with
              | SingleChoice _ when not q.Selector.Labels.IsEmpty ->
                  block "ANSWER-LABELS" q.Id $"Choice question '{q.Id}' takes labels from its options."
              | Boolean
              | Ordinal _ when q.Selector.Labels.Length <> cardinality q.Answer ->
                  block "ANSWER-LABELS" q.Id $"Question '{q.Id}' needs {cardinality q.Answer} selector labels."
              | _ -> ()

          if List.distinct q.SpecialStates <> q.SpecialStates
             || q.SpecialStates <> (specialStates |> List.filter (fun s -> List.contains s q.SpecialStates)) then
              block "ANSWER-SPECIAL-STATES" q.Id $"Special states of '{q.Id}' must be distinct and in canonical order." ]

let private scoring (policy: Policy) (draft: Draft) =
    let block = finding ScoringCategory Blocker
    let warn = finding ScoringCategory Warning

    [ for s in draft.Content.Sections do
          match s.Scoring with
          | None -> ()
          | Some sc ->
              for problem in Scoring.validate sc.Scorer do
                  block "SCORING-INVALID-SCORER" s.Id $"Section '{s.Id}': {problem}."

              for id in sc.Questions do
                  if not (s.Questions |> List.exists (fun q -> q.Id = id)) then
                      block "SCORING-REFERENCE" id $"Section '{s.Id}' scores '{id}', which is not one of its questions."

              for id in duplicates sc.Questions do
                  block "SCORING-REFERENCE" id $"Section '{s.Id}' scores '{id}' more than once."

              let scored = scoredQuestions s

              if scored.IsEmpty then
                  block "SCORING-NO-ITEMS" s.Id $"Section '{s.Id}' is scored but has no scored questions."
              elif scored.Length < policy.LowScoringItemCount then
                  warn "SCORING-LOW-ITEM-COUNT" s.Id $"Section '{s.Id}' is scored from only {scored.Length} question(s)."

              for q in scored do
                  match q.Answer with
                  | SingleChoice options when options |> List.exists (fun o -> o.Score.IsNone) ->
                      block "SCORING-MAPPING-INCOMPLETE" q.Id $"Scored choice question '{q.Id}' has an option without a score."
                  | _ -> ()

              // Positional aggregates must have one weight per scored item.
              match sc.Scorer.Aggregate with
              | Scoring.WeightedSum ws
              | Scoring.WeightedMean ws when ws.Length <> scored.Length ->
                  block "SCORING-WEIGHTS" s.Id $"Section '{s.Id}' has {ws.Length} weights for {scored.Length} scored questions."
              | Scoring.WeightedSum ws
              | Scoring.WeightedMean ws when ws |> List.exists (fun w -> w < 0.0 || Double.IsNaN w || Double.IsInfinity w) ->
                  block "SCORING-WEIGHTS" s.Id $"Section '{s.Id}' has a negative or non-finite weight."
              | _ -> ()

              // A mapped scale must name every value a scored question can store.
              match sc.Scorer.Scale with
              | Scoring.Mapped map ->
                  for q in scored do
                      let values =
                          match q.Answer with
                          | Boolean -> [ 0.0; 1.0 ]
                          | Ordinal points -> [ for p in 0 .. points - 1 -> float p ]
                          | SingleChoice options -> options |> List.choose _.Score

                      for v in values do
                          if not (map.ContainsKey v) then
                              block "SCORING-MAPPING-INCOMPLETE" q.Id $"The mapped scale of '{s.Id}' has no value for {v} of '{q.Id}'."
              | _ -> () ]

let private completion (draft: Draft) =
    let c = draft.Content

    [ if not (c.Sections |> List.exists (fun s -> s.Required && s.Questions |> List.exists _.Required)) then
          finding CompletionCategory Blocker "COMPLETION-NOTHING-REQUIRED" "" "No required question exists, so completion is undefined." ]

let private compatibility (draft: Draft) =
    let k = draft.Content.Compatibility
    let block = finding CompatibilityCategory Blocker

    [ if k.SchemaVersion <> SchemaVersion then
          block "COMPAT-SCHEMA" "" $"Template schema {k.SchemaVersion} is not supported (engine reads {SchemaVersion})."
      if k.MinimumEngineVersion < 1 || k.MinimumEngineVersion > EngineVersion then
          block "COMPAT-ENGINE" "" $"Minimum engine version {k.MinimumEngineVersion} is not valid for engine {EngineVersion}."
      if k.ResponseEncodingVersion <> EncodingVersion then
          block "COMPAT-ENCODING" "" $"Response encoding {k.ResponseEncodingVersion} is not supported."
      for cap in List.distinct k.Capabilities do
          if not (supportedCapabilities.Contains cap) then
              block "COMPAT-CAPABILITY" (TemplateCanonical.capabilityName cap) $"Capability '{TemplateCanonical.capabilityName cap}' is not supported by this engine." ]

let private encoding (policy: Policy) (draft: Draft) =
    let budget = policy.MaximumUrlCharacters - policy.BaseUrlAllowance
    let size = capacity draft.Content

    [ if size.EncodedCharacters > budget then
          finding EncodingCategory Blocker "ENCODING-URL-BUDGET" "" $"The largest response is {size.EncodedCharacters} characters; the budget is {budget}." ]

/// Prompt wording that asks for direct identification (AUT-003 §31). It
/// cannot judge intent; it surfaces the obvious cases.
let private piiPattern =
    Regex(
        @"\b(your\s+(full\s+)?name|e-?mail|phone|mobile\s+number|street|address|postcode|zip\s*code|employee\s*(id|number)|user\s*name|username|account\s+number|social\s+security|passport|national\s+id|date\s+of\s+birth)\b",
        RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant
    )

let isPiiLike (text: string) = piiPattern.IsMatch text

let private privacy (policy: Policy) (draft: Draft) =
    let severity = if policy.PiiPromptsBlock then Blocker else Warning

    [ for _, q in questions draft.Content do
          if isPiiLike q.Prompt || q.HelpText |> Option.exists isPiiLike then
              finding PrivacyCategory severity "PRIVACY-PII-LIKE-PROMPT" q.Id $"Question '{q.Id}' appears to ask for identifying information." ]

// ---------------------------------------------------------------------------
// Fixtures and the published test manifest (AUT-003 §§24-26, AUT-006 §§68-69).
// ---------------------------------------------------------------------------

type FixtureResult =
    { FixtureId: string
      Failures: string list
      /// SHA-256 over the canonical text of what the fixture produced.
      ResultHash: string }

let private outcomeText =
    function
    | Scoring.Score(v, included, excluded) -> $"score {v:R} {included} {excluded}"
    | Scoring.NotScored reason -> $"not-scored {reason}"

/// Runs one fixture. Invalid answers are failures, not crashes.
let runFixture (content: Content) (fixture: Fixture) : FixtureResult =
    let problems = checkAnswers content fixture.Answers |> List.map (sprintf "invalid answer: %A")
    let sections = scoreSections content fixture.Answers
    let complete = isComplete content fixture.Answers

    let failures =
        fixture.Expect
        |> List.choose (fun assertion ->
            match assertion with
            | Completion expected when expected <> complete -> Some $"completion: expected {expected}, got {complete}"
            | Completion _ -> None
            | SectionScore(id, expected) ->
                match sections |> List.tryFind (fun (s, _) -> s.Id = id) with
                | None -> Some $"section '{id}' is not scored"
                | Some(_, outcome) ->
                    match expected, outcome with
                    | Some e, Scoring.Score(v, _, _) when e = v -> None
                    | None, Scoring.NotScored _ -> None
                    | _ -> Some $"section '{id}': expected {expected}, got {outcomeText outcome}")

    let canonical =
        sections
        |> List.map (fun (s, o) -> $"{s.Id}={outcomeText o}")
        |> List.append [ $"complete={complete}" ]
        |> String.concat "\n"

    { FixtureId = fixture.Id
      Failures = problems @ failures
      ResultHash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes canonical)).ToLowerInvariant() }

let private fixtures (policy: Policy) (draft: Draft) (results: FixtureResult list) =
    [ if policy.RequireFixtures && draft.Fixtures.IsEmpty then
          finding FixturesCategory Blocker "FIXTURES-NONE" "" "Publication needs at least one test fixture."
      for id in duplicates (draft.Fixtures |> List.map _.Id) do
          finding FixturesCategory Blocker "FIXTURES-DUPLICATE" id $"Fixture id '{id}' is used more than once."
      for r in results do
          for failure in r.Failures do
              finding FixturesCategory Blocker "FIXTURES-FAILED" r.FixtureId failure ]

type ValidationReport =
    { Findings: Finding list
      Capacity: Capacity
      Fixtures: FixtureResult list }

    member this.Blockers = this.Findings |> List.filter (fun f -> f.Severity = Blocker)
    member this.Warnings = this.Findings |> List.filter (fun f -> f.Severity = Warning)
    member this.Passes = this.Blockers.IsEmpty

/// Every check, in a fixed order. Warnings are kept apart from blockers.
let validate (policy: Policy) (draft: Draft) : ValidationReport =
    let results = draft.Fixtures |> List.map (runFixture draft.Content)

    { Findings =
        structural draft
        @ answers' draft
        @ scoring policy draft
        @ completion draft
        @ compatibility draft
        @ encoding policy draft
        @ privacy policy draft
        @ fixtures policy draft results
      Capacity = capacity draft.Content
      Fixtures = results }

/// Pass or fail per category, for the validation summary (AUT-005 §49).
let summary (report: ValidationReport) =
    [ StructuralCategory; AnswerCategory; ScoringCategory; CompletionCategory; CompatibilityCategory; EncodingCategory; PrivacyCategory; FixturesCategory ]
    |> List.map (fun category ->
        category,
        report.Blockers |> List.exists (fun f -> f.Category = category) |> not)

// ---------------------------------------------------------------------------
// Semantic diff and comparability (AUT-005 §§44-48).
// ---------------------------------------------------------------------------

type Change =
    | MetadataChanged
    | PresentationChanged
    | RuntimeChanged
    | CompatibilityChanged
    | SectionAdded of sectionId: string
    | SectionRemoved of sectionId: string
    | SectionsReordered
    | SectionTextChanged of sectionId: string
    | SectionRequirementChanged of sectionId: string
    | SectionPresentationChanged of sectionId: string
    | SectionScoringChanged of sectionId: string
    | QuestionAdded of questionId: string
    | QuestionRemoved of questionId: string
    | QuestionsReordered
    | QuestionMovedSection of questionId: string
    | QuestionWordingChanged of questionId: string
    | QuestionRequirementChanged of questionId: string
    | AnswerChanged of questionId: string
    | OptionsChanged of questionId: string
    | OptionScoresChanged of questionId: string
    | SpecialStatesChanged of questionId: string
    | SelectorChanged of questionId: string

/// Whether a change can alter scores, alter how stored answers are read, or
/// only how the survey looks.
type Impact =
    { Scoring: bool
      Encoding: bool }

let impact (change: Change) =
    match change with
    | MetadataChanged
    | PresentationChanged
    | RuntimeChanged
    | SectionTextChanged _
    | SectionPresentationChanged _
    | SelectorChanged _ -> { Scoring = false; Encoding = false }
    | QuestionWordingChanged _
    | SectionRequirementChanged _
    | QuestionRequirementChanged _
    | CompatibilityChanged -> { Scoring = false; Encoding = false }
    | SectionScoringChanged _
    | OptionScoresChanged _ -> { Scoring = true; Encoding = false }
    | SectionAdded _
    | SectionRemoved _
    | QuestionAdded _
    | QuestionRemoved _
    | QuestionMovedSection _
    | AnswerChanged _
    | SpecialStatesChanged _ -> { Scoring = true; Encoding = true }
    | SectionsReordered
    | QuestionsReordered
    | OptionsChanged _ -> { Scoring = false; Encoding = true }

let diff (before: Content) (after: Content) : Change list =
    let beforeSections = before.Sections |> List.map (fun s -> s.Id, s) |> Map.ofList
    let afterSections = after.Sections |> List.map (fun s -> s.Id, s) |> Map.ofList
    let beforeQuestions = questions before |> List.map (fun (s, q) -> q.Id, (s, q)) |> Map.ofList
    let afterQuestions = questions after |> List.map (fun (s, q) -> q.Id, (s, q)) |> Map.ofList

    let common (a: Map<string, 'a>) (b: Map<string, 'b>) =
        a |> Map.toList |> List.map fst |> List.filter b.ContainsKey

    let order ids (keep: string -> bool) = ids |> List.filter keep

    let beforeQuestionOrder = questions before |> List.map (fun (_, q) -> q.Id)
    let afterQuestionOrder = questions after |> List.map (fun (_, q) -> q.Id)

    [ if before.Metadata <> after.Metadata then MetadataChanged
      if before.Presentation <> after.Presentation then PresentationChanged
      if before.Runtime <> after.Runtime then RuntimeChanged
      if before.Compatibility <> after.Compatibility then CompatibilityChanged

      for s in before.Sections do
          if not (afterSections.ContainsKey s.Id) then SectionRemoved s.Id
      for s in after.Sections do
          if not (beforeSections.ContainsKey s.Id) then SectionAdded s.Id

      let shared = common beforeSections afterSections

      if order (before.Sections |> List.map _.Id) (fun id -> List.contains id shared)
         <> order (after.Sections |> List.map _.Id) (fun id -> List.contains id shared) then
          SectionsReordered

      for id in shared do
          let a, b = beforeSections[id], afterSections[id]
          if a.Title <> b.Title || a.Description <> b.Description then SectionTextChanged id
          if a.Required <> b.Required then SectionRequirementChanged id
          if a.Presentation <> b.Presentation then SectionPresentationChanged id
          if a.Scoring <> b.Scoring then SectionScoringChanged id

      for id in beforeQuestionOrder do
          if not (afterQuestions.ContainsKey id) then QuestionRemoved id
      for id in afterQuestionOrder do
          if not (beforeQuestions.ContainsKey id) then QuestionAdded id

      let sharedQuestions = common beforeQuestions afterQuestions

      if order beforeQuestionOrder (fun id -> List.contains id sharedQuestions)
         <> order afterQuestionOrder (fun id -> List.contains id sharedQuestions) then
          QuestionsReordered

      for id in sharedQuestions do
          let (sa, a), (sb, b) = beforeQuestions[id], afterQuestions[id]
          if sa.Id <> sb.Id then QuestionMovedSection id
          if a.Prompt <> b.Prompt || a.HelpText <> b.HelpText then QuestionWordingChanged id
          if a.Required <> b.Required then QuestionRequirementChanged id
          if a.Selector <> b.Selector then SelectorChanged id
          if a.SpecialStates <> b.SpecialStates then SpecialStatesChanged id

          match a.Answer, b.Answer with
          | SingleChoice x, SingleChoice y ->
              if (x |> List.map _.Id) <> (y |> List.map _.Id) then OptionsChanged id
              if (x |> List.map (fun o -> o.Id, o.Score) |> Map.ofList) <> (y |> List.map (fun o -> o.Id, o.Score) |> Map.ofList) then
                  OptionScoresChanged id
          | x, y when x <> y -> AnswerChanged id
          | _ -> () ]

type Comparability =
    | Comparable
    | ComparableWithCaution of reasons: string list
    | NotComparable of reasons: string list

/// Whether results of two versions can be compared (AUT-005 §48): a change
/// to what is scored or how is not comparable; a wording or requirement
/// change is comparable with caution; presentation alone is comparable.
let comparability (changes: Change list) =
    let notComparable =
        changes
        |> List.choose (function
            | SectionScoringChanged id -> Some $"scoring of '{id}' changed"
            | OptionScoresChanged id -> Some $"option scores of '{id}' changed"
            | AnswerChanged id -> Some $"answer definition of '{id}' changed"
            | SpecialStatesChanged id -> Some $"special states of '{id}' changed"
            | QuestionAdded id -> Some $"question '{id}' added"
            | QuestionRemoved id -> Some $"question '{id}' removed"
            | QuestionMovedSection id -> Some $"question '{id}' moved section"
            | SectionAdded id -> Some $"section '{id}' added"
            | SectionRemoved id -> Some $"section '{id}' removed"
            | _ -> None)

    let caution =
        changes
        |> List.choose (function
            | QuestionWordingChanged id -> Some $"wording of '{id}' changed"
            | QuestionRequirementChanged id -> Some $"requirement of '{id}' changed"
            | SectionRequirementChanged id -> Some $"requirement of section '{id}' changed"
            | OptionsChanged id -> Some $"options of '{id}' reordered or relabelled"
            | _ -> None)

    match notComparable, caution with
    | [], [] -> Comparable
    | [], reasons -> ComparableWithCaution reasons
    | reasons, _ -> NotComparable reasons

// ---------------------------------------------------------------------------
// Published artifacts, the catalog, lifecycle and publication
// (AUT-005 §§52-55, AUT-006 §§56-59, CAN-005 §29, ARX-002).
// ---------------------------------------------------------------------------

type ManifestEntry =
    { FixtureId: string
      ExpectedResultHash: string
      PassedAtPublication: bool }

/// An immutable published template. The record has no update function in
/// this module; a change is a new draft and a new version.
type Published =
    { SurveyId: string
      /// Assigned by publication: "1", "2", ... per survey identifier.
      Version: string
      Hash: string
      Content: Content
      /// Provenance, outside the hash (AUT-006 §70).
      Parent: ParentReference option
      PublishedAt: DateTimeOffset
      PublishedBy: string
      Manifest: ManifestEntry list
      Fixtures: Fixture list }

type Visibility =
    | Listed
    /// Hidden from new authoring and distribution; still resolvable,
    /// scorable and reportable (AUT-006 §58).
    | HiddenFromDistribution

type Catalog =
    { Templates: Published list
      Hidden: Set<string * string> }

let emptyCatalog = { Templates = []; Hidden = Set.empty }

let versionsOf (catalog: Catalog) (surveyId: string) =
    catalog.Templates |> List.filter (fun t -> t.SurveyId = surveyId) |> List.sortBy (fun t -> int t.Version)

let resolve (catalog: Catalog) (surveyId: string) (version: string) =
    catalog.Templates |> List.tryFind (fun t -> t.SurveyId = surveyId && t.Version = version)

let resolveHash (catalog: Catalog) (hash: string) =
    catalog.Templates |> List.tryFind (fun t -> t.Hash = hash)

/// The template a URL's compact reference names, if exactly one does.
let resolveReference (catalog: Catalog) (reference: byte[]) =
    match
        catalog.Templates
        |> List.filter (fun t -> TemplateCanonical.reference t.SurveyId t.Version t.Content = reference)
    with
    | [ single ] -> Some single
    | _ -> None

/// The lifecycle state (AUT-001 §2), derived, never stored: a draft is
/// Validated while its current content passes; a published version is
/// Superseded once a later version of the same survey exists.
type Lifecycle =
    | DraftState
    | ValidatedState
    | PublishedState
    | SupersededState

let draftState (policy: Policy) (draft: Draft) =
    if (validate policy draft).Passes then ValidatedState else DraftState

let publishedState (catalog: Catalog) (template: Published) =
    if versionsOf catalog template.SurveyId |> List.exists (fun t -> int t.Version > int template.Version) then
        SupersededState
    else
        PublishedState

/// What may be done in each state (ARX-002: capabilities are derived from
/// state, not granted separately).
type Action =
    | Edit
    | Validate
    | Publish
    | DeleteDraft
    | DeriveDraft
    | Hide

let allowedActions =
    function
    | DraftState -> [ Edit; Validate; DeleteDraft ]
    | ValidatedState -> [ Edit; Validate; Publish; DeleteDraft ]
    | PublishedState -> [ DeriveDraft; Hide ]
    | SupersededState -> [ DeriveDraft; Hide ]

/// A new draft from a published version: a new version, or a rollback to an
/// older design published as a new version (AUT-001 §8, AUT-006 §57).
let deriveDraft (template: Published) : Draft =
    { SurveyId = template.SurveyId
      Parent = Some { Version = template.Version; Hash = template.Hash }
      Content = template.Content
      Fixtures = template.Fixtures }

let hide (template: Published) (catalog: Catalog) =
    { catalog with Hidden = catalog.Hidden.Add(template.SurveyId, template.Version) }

let visibility (catalog: Catalog) (template: Published) =
    if catalog.Hidden.Contains(template.SurveyId, template.Version) then HiddenFromDistribution else Listed

type Refusal =
    /// Validation blockers, with the full report.
    | Blocked of ValidationReport
    /// Warnings the publisher did not acknowledge (AUT-005 §51).
    | UnacknowledgedWarnings of Finding list
    /// The draft's parent is not a published version of this survey.
    | UnknownParent of ParentReference
    /// The content is identical to an already published version.
    | Unchanged of version: string

/// The publication summary shown before confirming (AUT-005 §55).
type PublicationSummary =
    { SurveyId: string
      NewVersion: string
      ParentVersion: string option
      TemplateHash: string
      LayoutFingerprint: string
      QuestionCount: int
      SectionCount: int
      MaximumUrlCharacters: int
      BreakingScoringChanges: bool
      EncodingChanges: bool
      Comparability: Comparability option }

let private nextVersion (catalog: Catalog) (surveyId: string) =
    match versionsOf catalog surveyId with
    | [] -> "1"
    | versions -> string (int (List.last versions).Version + 1)

let private parentOf (catalog: Catalog) (draft: Draft) =
    match draft.Parent with
    | None -> Ok None
    | Some p ->
        match resolve catalog draft.SurveyId p.Version with
        | Some t when t.Hash = p.Hash -> Ok(Some t)
        | _ -> Error(UnknownParent p)

/// What publishing the draft now would produce, without publishing it.
let preview (policy: Policy) (catalog: Catalog) (draft: Draft) : Result<PublicationSummary, Refusal> =
    parentOf catalog draft
    |> Result.map (fun parent ->
        let version = nextVersion catalog draft.SurveyId
        let changes = parent |> Option.map (fun p -> diff p.Content draft.Content)

        { SurveyId = draft.SurveyId
          NewVersion = version
          ParentVersion = parent |> Option.map _.Version
          TemplateHash = TemplateCanonical.templateHash draft.SurveyId version draft.Content
          LayoutFingerprint = TemplateCanonical.layoutFingerprint draft.Content
          QuestionCount = (questions draft.Content).Length
          SectionCount = draft.Content.Sections.Length
          MaximumUrlCharacters = (capacity draft.Content).EncodedCharacters + policy.BaseUrlAllowance
          BreakingScoringChanges = changes |> Option.exists (List.exists (fun c -> (impact c).Scoring))
          EncodingChanges = changes |> Option.exists (List.exists (fun c -> (impact c).Encoding))
          Comparability = changes |> Option.map comparability })

/// Publishes a draft (CAN-005 §29 sequence: validate, test, check capacity
/// and privacy, compare to the parent, canonicalize, hash, assign the
/// version, lock). `acknowledged` names the warning codes the publisher
/// accepted. The returned catalog holds the new version; the previous
/// latest version becomes Superseded by derivation, and nothing is removed.
let publish
    (policy: Policy)
    (catalog: Catalog)
    (acknowledged: Set<string>)
    (publishedAt: DateTimeOffset)
    (publishedBy: string)
    (draft: Draft)
    : Result<Published * Catalog, Refusal> =
    let report = validate policy draft

    if not report.Passes then
        Error(Blocked report)
    else
        match report.Warnings |> List.filter (fun w -> not (acknowledged.Contains w.Code)) with
        | _ :: _ as unacknowledged -> Error(UnacknowledgedWarnings unacknowledged)
        | [] ->
            match versionsOf catalog draft.SurveyId |> List.tryFind (fun t -> t.Content = draft.Content) with
            | Some same -> Error(Unchanged same.Version)
            | None ->
                parentOf catalog draft
                |> Result.map (fun _ ->
                    let version = nextVersion catalog draft.SurveyId

                    let published =
                        { SurveyId = draft.SurveyId
                          Version = version
                          Hash = TemplateCanonical.templateHash draft.SurveyId version draft.Content
                          Content = draft.Content
                          Parent = draft.Parent
                          PublishedAt = publishedAt
                          PublishedBy = publishedBy
                          Manifest =
                            report.Fixtures
                            |> List.map (fun r ->
                                { FixtureId = r.FixtureId
                                  ExpectedResultHash = r.ResultHash
                                  PassedAtPublication = r.Failures.IsEmpty })
                          Fixtures = draft.Fixtures }

                    published, { catalog with Templates = catalog.Templates @ [ published ] })

/// Re-derives a published artifact's hash from its content: true when the
/// artifact is exactly what was published (AUT-006 §54, hash lock).
let verify (template: Published) =
    TemplateCanonical.templateHash template.SurveyId template.Version template.Content = template.Hash
