/// The generic group result (ARP-002, RPT-001 inputs, RPT-005 §56): the
/// deterministic aggregation of a group's accepted SurveyResults for any
/// generic template. This is engine logic: reporting consumes it and never
/// recomputes scores (RPT-006 invariants 1 and 19).
///
/// Order-independent; unscored and not-applicable outcomes are counted,
/// never zeroed; an anonymous group below its minimum reportable count
/// carries counts but no score aggregates or distributions, so suppressed
/// values never exist to be rendered (SRPP-078, SRPP-079).
module Echelon.Signal.Engine.GroupResult

open System
open System.Security.Cryptography
open System.Text
open Echelon.Signal.Engine.Import
open Echelon.Signal.Engine.Groups

[<Literal>]
let GroupResultVersion = 1

/// One accepted contribution: the result and, where the group carries it,
/// the respondent's role. Identity never enters the group result.
type Contribution =
    { Role: Role option
      Result: SurveyResult.Result }

type Statistics =
    { Scored: int
      Unscored: int
      Mean: float
      Median: float
      Minimum: float
      Maximum: float }

type Aggregate =
    | Aggregated of Statistics
    /// Nothing scored: every contribution was unscored or not applicable.
    | NoneScored of unscored: int
    /// Withheld by the disclosure policy.
    | Suppressed of accepted: int * minimum: int

type Coverage =
    { Applicable: int
      Answered: int
      Special: int
      Unanswered: int }

type Result =
    { TemplateHash: string
      Mode: IdentityMode
      ExpectedCount: int
      AcceptedCount: int
      MissingCount: int
      Complete: bool
      Overall: Aggregate option
      Sections: (string * Aggregate) list
      /// Label counts per interpretation (category distribution); empty when
      /// suppressed.
      Interpretations: (string * (string * int) list) list
      /// How many contributions triggered each recommendation.
      Recommendations: (string * int) list
      Coverage: Coverage
      /// Role counts (only when every role holds at least the minimum, in
      /// anonymous groups).
      Roles: (Role * int) list
      Hash: string }

let private round (v: float) = Math.Round(v, 6, MidpointRounding.AwayFromZero)

let private statistics (values: float list) (unscored: int) =
    match List.sort values with
    | [] -> NoneScored unscored
    | sorted ->
        let n = sorted.Length
        let median = if n % 2 = 1 then sorted[n / 2] else (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0

        Aggregated
            { Scored = n
              Unscored = unscored
              Mean = round (List.average sorted)
              Median = round median
              Minimum = List.head sorted
              Maximum = List.last sorted }

let private outcomeValue =
    function
    | Scoring.Score(v, _, _) -> Some v
    | Scoring.NotScored _ -> None

/// The canonical text the group hash is computed over: every reported field,
/// in a fixed order.
let private canonical (r: Result) =
    let agg name =
        function
        | Aggregated s -> $"{name}=agg {s.Scored} {s.Unscored} {s.Mean:R} {s.Median:R} {s.Minimum:R} {s.Maximum:R}"
        | NoneScored u -> $"{name}=none {u}"
        | Suppressed(a, m) -> $"{name}=suppressed {a} {m}"

    [ $"group-result/{GroupResultVersion}"
      r.TemplateHash
      $"mode={r.Mode} expected={r.ExpectedCount} accepted={r.AcceptedCount} complete={r.Complete}"
      yield! r.Overall |> Option.toList |> List.map (agg "overall")
      yield! r.Sections |> List.map (fun (id, a) -> agg $"section {id}" a)
      yield! r.Interpretations |> List.map (fun (id, labels) -> $"interpretation {id}=%A{labels}")
      yield! r.Recommendations |> List.map (fun (id, n) -> $"recommendation {id}={n}")
      $"coverage={r.Coverage.Applicable} {r.Coverage.Answered} {r.Coverage.Special} {r.Coverage.Unanswered}"
      yield! r.Roles |> List.map (fun (role, n) -> $"role %A{role}={n}") ]
    |> String.concat "\n"

/// Aggregates a group. `minimumReportable` applies to anonymous groups.
let aggregate
    (templateHash: string)
    (mode: IdentityMode)
    (expectedCount: int)
    (minimumReportable: int)
    (contributions: Contribution list)
    : Result =
    let accepted = contributions.Length
    let suppressed = mode = AnonymousGroup && accepted < minimumReportable
    let results = contributions |> List.map _.Result

    let aggregateOf (values: float option list) =
        if suppressed then
            Suppressed(accepted, minimumReportable)
        else
            statistics (List.choose id values) (values |> List.filter Option.isNone |> List.length)

    let sectionIds =
        results |> List.collect (fun r -> r.Evaluation.Sections |> List.map fst) |> List.distinct

    let sectionValue (id: string) (r: SurveyResult.Result) =
        match r.Evaluation.Sections |> List.tryFind (fun (s, _) -> s = id) with
        | Some(_, Rules.SectionScored outcome) -> outcomeValue outcome
        | _ -> None

    let interpretationIds = results |> List.collect (fun r -> r.Interpretations |> List.map _.Id) |> List.distinct

    let roleCounts =
        contributions |> List.choose _.Role |> List.countBy id |> List.sortBy (fun (role, _) -> sprintf "%A" role)

    let sum f = results |> List.sumBy f

    let result =
        { TemplateHash = templateHash
          Mode = mode
          ExpectedCount = expectedCount
          AcceptedCount = accepted
          MissingCount = max 0 (expectedCount - accepted)
          Complete = accepted >= expectedCount
          Overall =
            if results |> List.exists (fun r -> r.Overall.IsSome) then
                Some(aggregateOf (results |> List.map (fun r -> r.Overall |> Option.bind (fun o -> outcomeValue o.Outcome))))
            else
                None
          Sections = sectionIds |> List.map (fun section -> section, aggregateOf (results |> List.map (sectionValue section)))
          Interpretations =
            if suppressed then
                []
            else
                interpretationIds
                |> List.map (fun interpretation ->
                    interpretation,
                    results
                    |> List.choose (fun r -> r.Interpretations |> List.tryFind (fun i -> i.Id = interpretation) |> Option.bind _.Label)
                    |> List.countBy id
                    |> List.sort)
          Recommendations =
            if suppressed then
                []
            else
                results
                |> List.collect (fun r -> r.Evaluation.Recommendations |> List.map _.Id)
                |> List.countBy id
                |> List.sort
          Coverage =
            { Applicable = sum _.Coverage.Applicable
              Answered = sum _.Coverage.Answered
              Special = sum _.Coverage.Special
              Unanswered = sum _.Coverage.Unanswered }
          Roles =
            if mode = AnonymousGroup && roleCounts |> List.exists (fun (_, n) -> n < minimumReportable) then [] else roleCounts
          Hash = "" }

    { result with Hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical result))).ToLowerInvariant() }

/// One contribution where only its section values and coverage were kept
/// (the administrator's stored contributions keep scores, not answers).
type ScoredContribution =
    { Role: Role option
      /// Section id and its score; None when the section was not scored.
      Sections: (string * float option) list
      Coverage: Coverage }

/// Aggregates a group from each section's scores and unscored count
/// (WI-0062): the same statistics, suppression, role rule and hash as
/// `aggregate`, so a report on stored or accumulated scores is the report on
/// the results they came from. A template with no overall,
/// interpretations or recommendations has none here either.
let ofEvidence
    (templateHash: string)
    (mode: IdentityMode)
    (expectedCount: int)
    (minimumReportable: int)
    (accepted: int)
    (roleCounts: (Role * int) list)
    (sections: (string * float list * int) list)
    (coverage: Coverage)
    : Result =
    let suppressed = mode = AnonymousGroup && accepted < minimumReportable

    let result =
        { TemplateHash = templateHash
          Mode = mode
          ExpectedCount = expectedCount
          AcceptedCount = accepted
          MissingCount = max 0 (expectedCount - accepted)
          Complete = accepted >= expectedCount
          Overall = None
          Sections =
            sections
            |> List.map (fun (section, scores, unscored) -> section, (if suppressed then Suppressed(accepted, minimumReportable) else statistics scores unscored))
          Interpretations = []
          Recommendations = []
          Coverage = coverage
          Roles =
            if mode = AnonymousGroup && roleCounts |> List.exists (fun (_, n) -> n < minimumReportable) then [] else roleCounts
          Hash = "" }

    { result with Hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical result))).ToLowerInvariant() }

/// Aggregates a group from stored section values, one list per contribution.
let ofScores (templateHash: string) (mode: IdentityMode) (expectedCount: int) (minimumReportable: int) (contributions: ScoredContribution list) : Result =
    let sectionIds = contributions |> List.collect (fun c -> c.Sections |> List.map fst) |> List.distinct

    let valuesOf (section: string) =
        contributions |> List.map (fun c -> c.Sections |> List.tryFind (fun (s, _) -> s = section) |> Option.bind snd)

    let sum f = contributions |> List.sumBy f

    ofEvidence
        templateHash
        mode
        expectedCount
        minimumReportable
        contributions.Length
        (contributions |> List.choose _.Role |> List.countBy id |> List.sortBy (fun (role, _) -> sprintf "%A" role))
        (sectionIds |> List.map (fun s -> let values = valuesOf s in s, List.choose id values, values |> List.filter Option.isNone |> List.length))
        { Applicable = sum _.Coverage.Applicable
          Answered = sum _.Coverage.Answered
          Special = sum _.Coverage.Special
          Unanswered = sum _.Coverage.Unanswered }

/// Aggregates a group from the administrator's incremental accumulator
/// (ADM-011): its evidence per dimension, in the template's order, and its
/// answer totals as coverage.
let ofAccumulator (templateHash: string) (minimumReportable: int) (accumulator: Incremental.Accumulator) : Result =
    let definition = accumulator.Definition
    let accepted = accumulator.Accepted.Count
    let applicable = definition.Template.Items.Length * accepted

    ofEvidence
        templateHash
        definition.Mode
        definition.ExpectedCount
        minimumReportable
        accepted
        []
        (definition.Template.Dimensions
         |> List.choose (fun d -> accumulator.Evidence.TryFind d.Id |> Option.map (fun e -> d.Id, e.Scores, e.Unscored))
         |> List.filter (fun _ -> accepted > 0))
        { Applicable = applicable
          Answered = accumulator.Answered - accumulator.NonNumeric
          Special = accumulator.NonNumeric
          Unanswered = max 0 (applicable - accumulator.Answered) }

/// A stored submission result as a scored contribution: its dimension scores
/// as section values (the pilot's generic sections are its dimensions, with
/// the same scores, `Pilot.contentOf`) and its answer counts as coverage.
let ofSubmission (items: int) (result: Import.SurveyResult) : ScoredContribution =
    { Role = None
      Sections =
        result.Dimensions
        |> List.map (fun (dimension, outcome) ->
            dimension.Id,
            match outcome with
            | Assessment.Scored(score, _, _) -> Some score
            | Assessment.Unscored _ -> None)
      Coverage =
        { Applicable = items
          Answered = result.AnsweredCount - result.NonNumericCount
          Special = result.NonNumericCount
          Unanswered = max 0 (items - result.AnsweredCount) } }
