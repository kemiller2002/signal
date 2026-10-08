/// Building ReportData from a group result (RPT-001..RPT-006): select the
/// definition's blocks, apply privacy and minimum-count rules, attach
/// explicit value states, warnings and methodology, and show comparison
/// deltas only when comparable. Reporting never rescores: every number comes
/// from `GroupResult` (RPT-006 invariant 19).
module Echelon.Signal.Engine.Report

open System
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Import
open Echelon.Signal.Engine.ReportModel

type DefinitionProblem =
    /// The block may not appear for this identity mode (RPT-003 §30).
    | BlockNotAllowed of Block * IdentityMode
    | AuditBlockOutsideAuditDetail
    | NoBlocks
    | DuplicateBlock of Block
    | MinimumBelowGroupPolicy of report: int * group: int
    | InvalidDecimals

/// Whether a definition may be used for a group (SRPP-013: a report family
/// is not exposed when its privacy conditions fail).
let check (definition: Definition) (mode: IdentityMode) (groupMinimum: int) : DefinitionProblem list =
    [ if definition.Blocks.IsEmpty then NoBlocks
      for b, n in definition.Blocks |> List.countBy id do
          if n > 1 then DuplicateBlock b
      for b in definition.Blocks do
          match privacyOf b, mode with
          | IdentifiedOnly, AnonymousGroup -> BlockNotAllowed(b, mode)
          | AuditOnly, _ when definition.Detail <> AuditDetail -> AuditBlockOutsideAuditDetail
          | _ -> ()
      if mode = AnonymousGroup && definition.MinimumGroupSize < groupMinimum then
          MinimumBelowGroupPolicy(definition.MinimumGroupSize, groupMinimum)
      if definition.Decimals < 0 || definition.Decimals > 6 then InvalidDecimals ]

/// What the report knows about the survey it reports on.
type Subject =
    { SurveyId: string
      TemplateVersion: string
      Content: Content }

let private present (decimals: int) (v: float) = Shown(Math.Round(v, decimals, MidpointRounding.AwayFromZero))

let private valueOf (decimals: int) (minimum: int) (pick: GroupResult.Statistics -> float) (aggregate: GroupResult.Aggregate) =
    match aggregate with
    | GroupResult.Aggregated s -> present decimals (pick s)
    | GroupResult.NoneScored _ -> NotAvailable
    | GroupResult.Suppressed(have, need) -> SuppressedValue(have, max need minimum)

/// Human-readable methodology derived from the immutable template (RPT-002
/// §34, SRPP-089): never hand-written per report.
let methodology (content: Content) : string list =
    [ for s in content.Sections do
          match s.Scoring with
          | Some sc ->
              let specials =
                  match sc.Scorer.Missing.Special with
                  | Scoring.Exclude -> "excluded from the score"
                  | Scoring.Substitute v -> $"counted as {v}"

              $"{s.Title}: {Registry.text (Registry.identify sc.Scorer)} of its answers; don't know, not observed, not applicable and declined are {specials}; at least {sc.Scorer.Missing.MinimumObservations} usable answer(s) are needed, otherwise it is not scored."
          | None -> ()
      match content.Results.Overall with
      | Some(Composite spec) ->
          $"Overall: {spec.Method} of section results ({spec.Direction}); sections that are not scored are left out, never counted as zero."
      | Some(Custom c) -> $"Overall: a custom scoring expression (language version {c.LanguageVersion})."
      | None -> ()
      for i in content.Results.Interpretations do
          match i.Kind with
          | Bands bands -> $"{i.Id}: " + (bands |> List.map (fun b -> $"{b.Label} {b.From}-{b.To}") |> String.concat ", ")
          | PassFail(t, d) -> $"{i.Id}: pass at {t} ({d})."
          | Stages(baseline, stages) -> $"{i.Id}: {baseline}, then " + (stages |> List.map _.Label |> String.concat ", ") + ", each reached only after the previous one." ]

/// Strengths and weaknesses need a declared direction; without one they are
/// not available rather than guessed (SRPP-029).
let private highlights (content: Content) (count: int) (sections: (string * GroupResult.Aggregate) list) =
    let direction =
        match content.Results.Overall with
        | Some(Composite spec) -> Some spec.Direction
        | _ -> None

    let ranked =
        sections
        |> List.choose (fun (id, a) ->
            match a with
            | GroupResult.Aggregated s -> Some(id, s.Mean)
            | _ -> None)

    match direction with
    | Some HigherIsBetter ->
        let ordered = ranked |> List.sortBy (fun (id, m) -> -m, id)
        ordered |> List.truncate count |> List.map fst, ordered |> List.rev |> List.truncate count |> List.map fst
    | Some HigherIsWorse ->
        let ordered = ranked |> List.sortBy (fun (id, m) -> m, id)
        ordered |> List.truncate count |> List.map fst, ordered |> List.rev |> List.truncate count |> List.map fst
    | _ -> [], []

let private warnings (definition: Definition) (group: GroupResult.Result) =
    let c = group.Coverage
    let share part = if c.Applicable = 0 then 0.0 else float part / float c.Applicable
    let suppressed = group.Sections |> List.exists (fun (_, a) -> match a with GroupResult.Suppressed _ -> true | _ -> false)

    [ if not group.Complete then IncompleteGroup
      if group.AcceptedCount < definition.MinimumGroupSize then LowResponseCount
      if suppressed then AnonymousSmallGroupSuppressed
      if share c.Special > 0.25 then HighDontKnowRate
      if share c.Unanswered > 0.2 then LowCoverage ]

/// Builds ReportData, or the reasons the definition may not be used.
let build
    (definition: Definition)
    (subject: Subject)
    (groupMinimum: int)
    (group: GroupResult.Result)
    (comparisons: ComparisonInput list)
    (generatedAt: DateTimeOffset)
    : Result<ReportData, DefinitionProblem list> =
    match check definition group.Mode groupMinimum with
    | _ :: _ as problems -> Error problems
    | [] ->
        let has b = List.contains b definition.Blocks
        let d = definition.Decimals
        let minimum = definition.MinimumGroupSize
        let title id = subject.Content.Sections |> List.tryFind (fun s -> s.Id = id) |> Option.map _.Title |> Option.defaultValue id
        let strengths, weaknesses = highlights subject.Content definition.HighlightCount group.Sections

        let recommendationRows =
            group.Recommendations
            |> List.choose (fun (id, n) ->
                subject.Content.Rules.Recommendations
                |> List.tryFind (fun r -> r.Id = id)
                |> Option.map (fun r -> { Id = id; Title = r.Title; Priority = sprintf "%A" r.Priority; Frequency = n }))
            |> List.sortBy (fun r -> -r.Frequency, r.Id)
            |> List.truncate definition.HighlightCount

        let warnings = warnings definition group

        let comparisonWarnings =
            if comparisons |> List.exists (fun c -> not c.Comparable) then [ NotComparableToPriorVersion ] else []

        Ok
            { Definition = definition.Id, definition.Version
              Audience = definition.Audience
              Status =
                if group.AcceptedCount = 0 || (group.Mode = AnonymousGroup && group.AcceptedCount < max minimum groupMinimum) then
                    InsufficientData
                elif not group.Complete then
                    Partial(group.AcceptedCount, group.ExpectedCount)
                else
                    Complete
              Title = subject.Content.Metadata.Title
              GeneratedAt = generatedAt
              Blocks = definition.Blocks
              Counts =
                { Expected = group.ExpectedCount
                  Accepted = group.AcceptedCount
                  Missing = group.MissingCount
                  CompletionPercent =
                    if group.ExpectedCount = 0 then 0.0
                    else Math.Round(100.0 * float group.AcceptedCount / float group.ExpectedCount, 1, MidpointRounding.AwayFromZero) }
              Overall =
                match group.Overall with
                | Some a when has OverallResult -> valueOf d minimum _.Mean a
                | Some _ -> NotAvailable
                | None -> NotApplicableValue
              Sections =
                if has SectionResults || has GroupDistributions then
                    group.Sections
                    |> List.map (fun (id, a) ->
                        let scored, unscored =
                            match a with
                            | GroupResult.Aggregated s -> s.Scored, s.Unscored
                            | GroupResult.NoneScored u -> 0, u
                            | GroupResult.Suppressed _ -> 0, 0

                        { Section = id
                          Title = title id
                          Mean = valueOf d minimum _.Mean a
                          Median = if has GroupDistributions then valueOf d minimum _.Median a else NotAvailable
                          Minimum = if has GroupDistributions then valueOf d minimum _.Minimum a else NotAvailable
                          Maximum = if has GroupDistributions then valueOf d minimum _.Maximum a else NotAvailable
                          Scored = scored
                          Unscored = unscored })
                else
                    []
              Strengths = if has Strengths then strengths else []
              Weaknesses = if has Weaknesses then weaknesses else []
              Recommendations = if has Recommendations then recommendationRows else []
              Coverage = group.Coverage
              Warnings = warnings @ comparisonWarnings
              Comparisons =
                if has Comparisons then
                    comparisons
                    |> List.map (fun c ->
                        { Input = c
                          Delta =
                            match c.Comparable, c.Current, c.BaselineValue with
                            | false, _, _ -> NotComparable(c.Reason |> Option.defaultValue "not comparable")
                            | true, Some x, Some y -> present d (x - y)
                            | true, _, _ -> NotAvailable })
                else
                    []
              Roles = if has RoleBreakdown then group.Roles else []
              Methodology = if has Methodology then methodology subject.Content else []
              Audit =
                if has AuditMetadata then
                    Some
                        { SurveyId = subject.SurveyId
                          TemplateVersion = subject.TemplateVersion
                          TemplateHash = group.TemplateHash
                          GroupResultHash = group.Hash
                          GroupResultVersion = GroupResult.GroupResultVersion
                          DefinitionId = definition.Id
                          DefinitionVersion = definition.Version }
                else
                    None }

/// Whether two published versions can be compared (RPT-003 §23): same
/// survey, and a diff that leaves scoring comparable. Never faked.
let comparable (current: Publication.Published) (baseline: Publication.Published) : Result<unit, string> =
    if current.SurveyId <> baseline.SurveyId then
        Error "different surveys"
    else
        match TemplateDiff.comparability (TemplateDiff.diff baseline.Content current.Content) with
        | TemplateDiff.Comparable
        | TemplateDiff.ComparableWithCaution _ -> Ok()
        | TemplateDiff.NotComparable reasons -> Error(String.concat "; " reasons)
