/// SurveyGroupResult: the deterministic aggregation of a group's accepted
/// SurveyResults (ARP-001 §16, ARP-002 §17-31, ID-003, ARX-012 lineage).
///
/// Pure and order-independent: the same accepted results give the same
/// group result whatever order they were imported in. Coverage and
/// completion stay separate from performance (ARP §28), and an anonymous
/// group below its minimum reportable size shows counts but no score
/// aggregates, so a small group cannot be differenced back to a person
/// (ID-003 §22, RPT-003).
module Echelon.Signal.Engine.Aggregation

open System
open System.Security.Cryptography
open System.Text
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import

type GroupCompletion =
    | WaitingForResponses
    | Complete

/// The disclosure policy a group result is computed under.
type Policy =
    { /// Below this many accepted results, an anonymous group's score
      /// aggregates are suppressed.
      MinimumReportableCount: int }

let defaultPolicy = { MinimumReportableCount = 5 }

/// One dimension across the group. Only respondents whose dimension scored
/// enter the score statistics; the rest are counted, never zeroed.
type DimensionStatistics =
    { Scored: int
      Unscored: int
      Mean: float option
      Median: float option
      Minimum: float option
      Maximum: float option }

type DimensionAggregate =
    | Aggregated of DimensionStatistics
    /// Withheld by the disclosure policy; the reason is explicit.
    | Suppressed of accepted: int * minimum: int

type TemplateSummary =
    { SurveyIdentifier: string
      TemplateVersion: string
      TemplateHash: string
      AcceptedCount: int }

/// What the group result was derived from, and a hash over it, so a report
/// can prove which inputs it reflects (ARX-012).
type Lineage =
    { ResultVersion: int
      TemplateHash: string
      /// Accepted SubmissionHashes, sorted.
      SubmissionHashes: string list
      PolicyMinimum: int
      DerivationHash: string }

[<NoComparison>]
type SurveyGroupResult =
    { Group: OpaqueId
      Mode: IdentityMode
      ExpectedCount: int
      AcceptedCount: int
      /// Expected minus accepted, never negative. Which individuals are
      /// missing is never inferred in anonymous mode (ARP §19).
      MissingCount: int
      Completion: GroupCompletion
      Template: TemplateSummary
      Dimensions: (Dimension * DimensionAggregate) list
      /// Share of all answers that were don't know / not observed / not
      /// applicable: coverage, not performance.
      NonNumericShare: float option
      Lineage: Lineage }

let private round1 (value: float) = Math.Round(value, 1, MidpointRounding.AwayFromZero)

let private median (sorted: float list) =
    match sorted.Length with
    | 0 -> None
    | n when n % 2 = 1 -> Some sorted[n / 2]
    | n -> Some(round1 ((sorted[n / 2 - 1] + sorted[n / 2]) / 2.0))

let private statistics (results: SurveyResult list) (dimension: Dimension) =
    let scores =
        results
        |> List.choose (fun result ->
            result.Dimensions
            |> List.tryFind (fun (d, _) -> d.Id = dimension.Id)
            |> Option.bind (fun (_, r) ->
                match r with
                | Scored(score, _, _) -> Some score
                | Unscored _ -> None))
        |> List.sort

    { Scored = scores.Length
      Unscored = results.Length - scores.Length
      Mean = if scores.IsEmpty then None else Some(round1 (List.average scores))
      Median = median scores
      Minimum = List.tryHead scores
      Maximum = List.tryLast scores }

let private lineage (policy: Policy) (templateHash: string) (results: SurveyResult list) =
    let hashes = results |> List.map _.SubmissionHash |> List.sort

    let material =
        String.Join("\n", [ $"result-version:{ResultVersion}"; $"template:{templateHash}"; $"policy-minimum:{policy.MinimumReportableCount}" ] @ hashes)

    { ResultVersion = ResultVersion
      TemplateHash = templateHash
      SubmissionHashes = hashes
      PolicyMinimum = policy.MinimumReportableCount
      DerivationHash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes material)).ToLowerInvariant() }

/// The group result for the current accepted results.
let aggregate (policy: Policy) (state: GroupState) : SurveyGroupResult =
    let definition = state.Definition
    let assessment = definition.Template
    // Map iteration is by key, so the order is the same whatever the
    // import order was.
    let results = state.Results |> Map.toList |> List.map snd
    let accepted = results.Length
    let templateHash = Canonical.templateHash assessment

    let suppressed =
        definition.Mode = AnonymousGroup && accepted < policy.MinimumReportableCount

    let answers = results |> List.sumBy _.AnsweredCount
    let nonNumeric = results |> List.sumBy _.NonNumericCount

    { Group = definition.Group
      Mode = definition.Mode
      ExpectedCount = definition.ExpectedCount
      AcceptedCount = accepted
      MissingCount = max 0 (definition.ExpectedCount - accepted)
      Completion = if accepted >= definition.ExpectedCount then Complete else WaitingForResponses
      Template =
        { SurveyIdentifier = assessment.Id
          TemplateVersion = assessment.Version
          TemplateHash = templateHash
          AcceptedCount = accepted }
      Dimensions =
        assessment.Dimensions
        |> List.map (fun dimension ->
            dimension,
            if suppressed then
                Suppressed(accepted, policy.MinimumReportableCount)
            else
                Aggregated(statistics results dimension))
      NonNumericShare = if answers = 0 then None else Some(round1 (100.0 * float nonNumeric / float answers) / 100.0)
      Lineage = lineage policy templateHash results }
