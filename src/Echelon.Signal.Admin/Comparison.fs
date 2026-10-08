/// Comparing groups, waves and versions (ADM-014) and tracing a reported
/// value back to its inputs (ADM-020, ARX-012).
///
/// Comparison rests on explicit semantic compatibility, never on matching
/// labels: the same survey and exact template hash, or a versioned, validated
/// declaration that a later template's metric continues an earlier one; the
/// same identity semantics and the same privacy policy. Anything else is
/// `NotComparable` with reason codes. Comparing never changes either
/// group's results.
///
/// Pure.
module Echelon.Signal.Admin.Comparison

open System
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin.Analysis

/// One side of a comparison.
type Side =
    { Group: string
      SurveyIdentifier: string
      TemplateHash: string
      /// The template's section ids.
      Sections: string list
      Source: Source }

/// A versioned declaration that metrics of a later template continue those
/// of an earlier one (ADM-014).
type ContinuityDeclaration =
    { Version: int
      FromTemplate: string
      ToTemplate: string
      /// Section id in the earlier template, and in the later one.
      Metrics: (string * string) list }

/// Whether two sides compare, and through which metric mapping.
type Comparability =
    /// Section ids of the first side mapped to the second's.
    | Comparable of Map<string, string>
    | NotComparable of reasons: string list

/// Why a declaration is not valid for two templates' sections.
let validateDeclaration (from: string list) (``to``: string list) (declaration: ContinuityDeclaration) =
    [ if declaration.Version < 1 then "DECLARATION_VERSION"
      for a, b in declaration.Metrics do
          if not (List.contains a from) then $"DECLARATION_UNKNOWN_SOURCE_METRIC:{a}"
          if not (List.contains b ``to``) then $"DECLARATION_UNKNOWN_TARGET_METRIC:{b}" ]

/// Whether `a` and `b` are comparable, given the declarations in force.
let comparability (declarations: ContinuityDeclaration list) (a: Side) (b: Side) : Comparability =
    let mapping =
        if a.TemplateHash = b.TemplateHash then
            Ok(a.Sections |> List.map (fun s -> s, s) |> Map.ofList)
        else
            match declarations |> List.tryFind (fun d -> d.FromTemplate = a.TemplateHash && d.ToTemplate = b.TemplateHash) with
            | None -> Error [ "TEMPLATE_DIFFERS_WITHOUT_DECLARATION" ]
            | Some declaration ->
                match validateDeclaration a.Sections b.Sections declaration with
                | [] -> Ok(Map.ofList declaration.Metrics)
                | problems -> Error problems

    let reasons =
        [ if a.SurveyIdentifier <> b.SurveyIdentifier then "SURVEY_DIFFERS"
          if a.Source.Mode <> b.Source.Mode then "IDENTITY_MODE_DIFFERS"
          if a.Source.MinimumReportable <> b.Source.MinimumReportable then "PRIVACY_POLICY_DIFFERS" ]

    match reasons, mapping with
    | [], Ok found -> Comparable found
    | reasons, Ok _ -> NotComparable reasons
    | reasons, Error problems -> NotComparable(reasons @ problems)

/// The change in a section's mean from `a` to `b`, only when they compare.
let change (declarations: ContinuityDeclaration list) (a: Side) (b: Side) (section: string) : Result<Metric, string list> =
    match comparability declarations a b with
    | NotComparable reasons -> Error reasons
    | Comparable mapping ->
        match mapping.TryFind section with
        | None -> Error [ $"NO_CONTINUITY:{section}" ]
        | Some target ->
            match measure a.Source (Some section) Mean, measure b.Source (Some target) Mean with
            | Value x, Value y -> Ok(Value(Math.Round(y - x, 6, MidpointRounding.AwayFromZero)))
            | Unavailable reason, _
            | _, Unavailable reason -> Ok(Unavailable reason)

/// Cohen's d between two comparable sides for a section (pooled standard
/// deviation); needs two scores on each side. Descriptive of size, not cause.
let effectSize (declarations: ContinuityDeclaration list) (a: Side) (b: Side) (section: string) : Result<Metric, string list> =
    match comparability declarations a b with
    | NotComparable reasons -> Error reasons
    | Comparable mapping ->
        match mapping.TryFind section with
        | None -> Error [ $"NO_CONTINUITY:{section}" ]
        | Some target ->
            let scores (side: Side) id = side.Source.Scores.TryFind id |> Option.defaultValue []
            let x = scores a section
            let y = scores b target

            match measure a.Source (Some section) StandardDeviation, measure b.Source (Some target) StandardDeviation with
            | Value sx, Value sy ->
                let pooled = Math.Sqrt(((float x.Length - 1.0) * sx * sx + (float y.Length - 1.0) * sy * sy) / float (x.Length + y.Length - 2))

                if pooled = 0.0 then Ok(Unavailable DivisionByZero)
                else Ok(Value(Math.Round((List.average y - List.average x) / pooled, 6, MidpointRounding.AwayFromZero)))
            | Unavailable reason, _
            | _, Unavailable reason -> Ok(Unavailable reason)

// ---- Lineage (ADM-020, ARX-012) ----------------------------------------------------------------

/// Where a reported value comes from, traced through the same path that
/// computed it.
type Trace =
    { Group: string
      Section: string
      Statistic: string
      Value: Metric
      /// The SurveyGroupResult derivation hash the value belongs to.
      DerivationHash: string
      TemplateHash: string
      ResultVersion: int
      ScoringEngineVersion: int
      ExpressionVersion: int option
      /// The accepted contributions' SubmissionHashes: never identities.
      Contributions: string list
      ReasonCodes: string list }

/// The trace of a section statistic of a group.
let trace (config: GroupRecord.GroupConfig) (accumulator: Incremental.Accumulator) (section: string) (statistic: Measure) (expression: int option) : Trace =
    let result = Incremental.result (GroupRecord.policy config) accumulator
    let src = source config accumulator
    let value = measure src (Some section) statistic

    { Group = GroupRecord.groupKey config.Group
      Section = section
      Statistic = measureName statistic
      Value = value
      DerivationHash = result.Lineage.DerivationHash
      TemplateHash = result.Lineage.TemplateHash
      ResultVersion = result.Lineage.ResultVersion
      ScoringEngineVersion = Template.EngineVersion
      ExpressionVersion = expression
      Contributions = result.Lineage.SubmissionHashes
      ReasonCodes =
        [ match value with
          | Unavailable(Suppressed _) -> "SUPPRESSED_BELOW_MINIMUM"
          | Unavailable(InsufficientSample _) -> "INSUFFICIENT_SAMPLE"
          | Unavailable(NotRetained _) -> "NOT_RETAINED"
          | Unavailable _ -> "UNAVAILABLE"
          | Value _ -> ()
          let unscored = src.Unscored.TryFind section |> Option.defaultValue 0
          if unscored > 0 then $"UNSCORED_RESPONDENTS:{unscored}" ] }
