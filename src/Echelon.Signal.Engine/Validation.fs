/// Publication validation and fixtures (CAN-005 §28, AUT-003 §§24-31,
/// AUT-004 §§32-43, AUT-005 §§49-51, AUT-006 §§68-69).
///
/// Every finding has a stable reason code, a category and a severity;
/// blockers stop publication and warnings need acknowledgement. Fixtures
/// simulate responses from answer state and their failures are blockers.
module Echelon.Signal.Engine.Validation

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Layout
open Echelon.Signal.Engine.Drafts
open Echelon.Signal.Engine.Findings
open Echelon.Signal.Engine.RuleChecks

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
      LowScoringItemCount: int
      /// Whether templates under this policy may carry custom scoring
      /// expressions (DF-SIGNAL-2026-0002 Q11: on for Echelon-authored
      /// templates; an external-author policy turns it off).
      CustomExpressionsAllowed: bool
      ExpressionLimits: Expression.Limits }

let defaultPolicy =
    { MaximumUrlCharacters = 2000
      BaseUrlAllowance = 200
      RequireFixtures = true
      PiiPromptsBlock = false
      LowScoringItemCount = 3
      CustomExpressionsAllowed = true
      ExpressionLimits = Expression.defaultLimits }

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

let private answers' (draft: Draft) = PrimitiveChecks.check draft.Content

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
                  let keyed = draft.Content.Results.ItemKeys |> List.exists (fun k -> k.Question = q.Id)

                  match q.Answer with
                  | (SingleChoice _ | Hierarchical _) when not keyed && options q.Answer |> List.exists (fun o -> o.Score.IsNone) ->
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
                      let keyed = draft.Content.Results.ItemKeys |> List.exists (fun k -> k.Question = q.Id)

                      let values =
                          match q.Answer with
                          | _ when keyed -> []
                          | Boolean -> [ 0.0; 1.0 ]
                          | Ordinal points -> [ for p in 0 .. points - 1 -> float p ]
                          | BoundedNumber b -> [ for t in 0 .. ticks b - 1 -> tickValue b t ]
                          | SingleChoice _
                          | Hierarchical _ -> options q.Answer |> List.choose _.Score
                          | _ -> []

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

let private sectionText =
    function
    | Rules.SectionScored outcome -> outcomeText outcome
    | Rules.SectionNotApplicable -> "not-applicable"

/// Runs one fixture through the whole evaluation (`Rules.evaluate`), so
/// flow, facts, validation and completion are tested exactly as they run.
/// Invalid answers are failures, not crashes.
let runFixture (content: Content) (fixture: Fixture) : FixtureResult =
    let result = SurveyResult.compute "" content fixture.Answers false
    let e = result.Evaluation
    let problems = e.AnswerProblems |> List.map (sprintf "invalid answer: %A")
    let complete = Rules.isSubmittable e.Completion
    let triggered = e.Recommendations |> List.map _.Id |> Set.ofList

    let failures =
        fixture.Expect
        |> List.choose (fun assertion ->
            match assertion with
            | ExpectComplete expected when expected <> complete -> Some $"completion: expected {expected}, got {e.Completion}"
            | ExpectComplete _ -> None
            | ExpectApplicable(id, expected) when expected <> e.Applicability.Questions.Contains id ->
                Some $"applicability of '{id}': expected {expected}"
            | ExpectApplicable _ -> None
            | ExpectRecommended(id, expected) when expected <> triggered.Contains id -> Some $"recommendation '{id}': expected {expected}"
            | ExpectRecommended _ -> None
            | ExpectOverall expected ->
                let actual = result.Overall |> Option.map _.Outcome

                match expected, actual with
                | Some x, Some(Scoring.Score(v, _, _)) when x = v -> None
                | None, None
                | None, Some(Scoring.NotScored _) -> None
                | _ -> Some $"overall: expected {expected}, got %A{actual}"
            | ExpectInterpretation(id, expected) ->
                match result.Interpretations |> List.tryFind (fun i -> i.Id = id) with
                | Some i when i.Label = expected -> None
                | Some i -> Some $"interpretation '{id}': expected {expected}, got {i.Label}"
                | None -> Some $"interpretation '{id}' is not defined"
            | ExpectFact(id, expected) ->
                match e.Facts |> List.tryFind (fun (f, _) -> f = id) with
                | Some(_, actual) when actual = expected -> None
                | Some(_, actual) -> Some $"fact '{id}': expected {expected}, got {actual}"
                | None -> Some $"fact '{id}' is not defined"
            | ExpectSectionScore(id, expected) ->
                match e.Sections |> List.tryFind (fun (s, _) -> s = id) with
                | None -> Some $"section '{id}' is not scored"
                | Some(_, result) ->
                    match expected, result with
                    | Some x, Rules.SectionScored(Scoring.Score(v, _, _)) when x = v -> None
                    | None, Rules.SectionScored(Scoring.NotScored _)
                    | None, Rules.SectionNotApplicable -> None
                    | _ -> Some $"section '{id}': expected {expected}, got {sectionText result}")

    let canonical =
        [ $"completion={e.Completion}"
          yield! e.Applicability.Questions |> Set.toList |> List.map (sprintf "applicable=%s")
          yield! e.Facts |> List.map (fun (f, v) -> $"fact {f}=%A{v}")
          yield! e.Sections |> List.map (fun (s, r) -> $"{s}={sectionText r}")
          yield! e.Recommendations |> List.map (fun r -> $"recommendation={r.Id}")
          yield! result.Overall |> Option.toList |> List.map (fun o -> $"overall={outcomeText o.Outcome}")
          yield! result.Interpretations |> List.map (fun i -> $"interpretation {i.Id}=%A{i.Label}") ]
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

/// Overall scoring, interpretations and selector-scorer compatibility
/// (SCS-015, AST-002, VER-005).
let private resultChecks (policy: Policy) (draft: Draft) =
    let c = draft.Content

    Compatibility.check c
    @ (match c.Results.Overall with
       | Some(ResultModel.Composite spec) -> Composite.check c spec
       | Some(ResultModel.Custom custom) ->
           [ if not policy.CustomExpressionsAllowed then
                 finding ScoringCategory Blocker "POLICY-CUSTOM-EXPRESSION" "overall" "Custom scoring expressions are not allowed under this authoring policy." ]
           @ Expression.check policy.ExpressionLimits c "overall" custom
       | None -> [])
    @ Interpretation.check c c.Results.Interpretations

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
        @ RuleChecks.check draft.Content
        @ resultChecks policy draft
        @ fixtures policy draft results
      Capacity = capacity draft.Content
      Fixtures = results }

/// Pass or fail per category, for the validation summary (AUT-005 §49).
let summary (report: ValidationReport) =
    [ StructuralCategory; AnswerCategory; ScoringCategory; CompletionCategory; CompatibilityCategory; EncodingCategory; PrivacyCategory; FixturesCategory ]
    |> List.map (fun category ->
        category,
        report.Blockers |> List.exists (fun f -> f.Category = category) |> not)
