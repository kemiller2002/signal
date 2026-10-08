/// The reporting contract (WI-0046): RPT-001..RPT-006, SRPP-001..SRPP-013,
/// SRPP-045..SRPP-051, SRPP-078..SRPP-083, CAN-006 reporting boundary.
module Echelon.Signal.Tests.ReportingTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Import
open Echelon.Signal.Engine.ReportModel

let private at = DateTimeOffset(2026, 10, 8, 16, 0, 0, TimeSpan.Zero)

let private content =
    { Pilot.content with
        Compatibility = { defaultCompatibility with Capabilities = [ UsesRecommendations ] }
        Rules =
            { noRules with
                Recommendations =
                    [ { Id = "timebox"
                        When = Compare(SectionScore "D03", Less, Constant 50.0)
                        Kind = Recommendation
                        Priority = Important
                        Category = "cadence"
                        Title = "Introduce a steady iteration cadence"
                        Description = ""
                        RelatedSection = Some "D03"
                        RelatedQuestion = None } ] }
        Results =
            { noResults with
                Overall =
                    Some(
                        Composite
                            { Method = BalancedMean
                              Sections = []
                              MinimumScoredSections = 1
                              Direction = HigherIsBetter
                              Decimals = 1 }
                    ) } }

let private subject: Report.Subject = { SurveyId = "SDRA"; TemplateVersion = "1"; Content = content }
let private hash = TemplateCanonical.templateHash "SDRA" "1" content

/// A respondent who answers every D01 item `a`, D02 `b`, D03 `c`.
let private respondent (a: int) (b: int) (c: int) role : GroupResult.Contribution =
    let answers =
        Pilot.assessment.Items
        |> List.map (fun i -> i.Id, Value(Point(match i.DimensionId with "D01" -> a | "D02" -> b | _ -> c)))
        |> Map.ofList

    { Role = role; Result = SurveyResult.compute hash content answers true }

let private five =
    [ respondent 4 2 1 (Some Groups.Self)
      respondent 3 2 1 (Some Groups.Peer)
      respondent 4 3 0 (Some Groups.Peer)
      respondent 2 1 2 (Some Groups.Peer)
      respondent 4 4 4 (Some Groups.Manager) ]

let private group mode expected (contributions: GroupResult.Contribution list) =
    GroupResult.aggregate hash mode expected 5 contributions

[<Fact>]
let ``the group result aggregates what the engine scored, order-independently`` () =
    let g = group IdentifiedGroup 6 five

    match List.find (fun (s, _) -> s = "D01") g.Sections |> snd with
    | GroupResult.Aggregated s ->
        // D01 per respondent: 100, 75, 100, 50, 100.
        Assert.Equal(85.0, s.Mean)
        Assert.Equal(100.0, s.Median)
        Assert.Equal(50.0, s.Minimum)
        Assert.Equal(5, s.Scored)
    | other -> failwith $"%A{other}"

    Assert.Equal(1, g.MissingCount)
    Assert.False(g.Complete)
    Assert.Equal<(string * int) list>([ "timebox", 3 ], g.Recommendations) // D03 below 50 for three respondents
    Assert.Equal(g.Hash, (group IdentifiedGroup 6 (List.rev five)).Hash)
    Assert.NotEqual<string>(g.Hash, (group IdentifiedGroup 6 (List.tail five)).Hash)

[<Fact>]
let ``unscored results are counted, never zeroed`` () =
    let empty: GroupResult.Contribution = { Role = None; Result = SurveyResult.compute hash content Map.empty false }

    match List.find (fun (s, _) -> s = "D01") (group IdentifiedGroup 3 [ empty; five.Head ]).Sections |> snd with
    | GroupResult.Aggregated s ->
        Assert.Equal(1, s.Scored)
        Assert.Equal(1, s.Unscored)
        Assert.Equal(100.0, s.Mean)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a small anonymous group carries counts but no values to render`` () =
    let small = group AnonymousGroup 10 (List.take 3 five)
    Assert.True(small.Sections |> List.forall (fun (_, a) -> a = GroupResult.Suppressed(3, 5)))
    Assert.Equal(Some(GroupResult.Suppressed(3, 5)), small.Overall)
    Assert.Empty(small.Recommendations)
    Assert.Empty(small.Interpretations)
    Assert.Empty(small.Roles)

    let report = Report.build ReportExport.anonymousAggregate subject 5 small [] at |> Result.toOption |> Option.get
    Assert.Equal(InsufficientData, report.Status)
    Assert.Equal(SuppressedValue(3, 5), report.Overall)
    Assert.Contains(AnonymousSmallGroupSuppressed, report.Warnings)
    // No suppressed number reaches the export (SRPP-079).
    let json = ReportExport.json report
    Assert.DoesNotContain("\"mean\":", json.Replace("\"mean\":\"suppressed", ""))

    // Roles are hidden when any role is below the minimum.
    let big = group AnonymousGroup 5 five
    Assert.Empty(big.Roles)

[<Fact>]
let ``definitions are checked against the group's identity mode`` () =
    let problems d mode = Report.check d mode 5
    Assert.Contains(Report.BlockNotAllowed(RespondentDetail, AnonymousGroup), problems ReportExport.identifiedDetail AnonymousGroup)
    Assert.Empty(problems ReportExport.identifiedDetail IdentifiedGroup)
    Assert.Contains(Report.AuditBlockOutsideAuditDetail, problems { ReportExport.administratorGroup with Blocks = [ AuditMetadata ] } IdentifiedGroup)
    Assert.Contains(Report.MinimumBelowGroupPolicy(3, 5), problems { ReportExport.anonymousAggregate with MinimumGroupSize = 3 } AnonymousGroup)
    let anonymous = ReportExport.availableFamilies AnonymousGroup 5 |> List.map _.Id
    Assert.DoesNotContain("identified-detail", anonymous)
    Assert.Contains("identified-detail", ReportExport.availableFamilies IdentifiedGroup 5 |> List.map _.Id)

    match Report.build ReportExport.identifiedDetail subject 5 (group AnonymousGroup 5 five) [] at with
    | Error ps -> Assert.NotEmpty ps
    | Ok _ -> failwith "an anonymous group must not produce an identified report"

[<Fact>]
let ``reports select blocks, mark partial groups and keep missing values explicit`` () =
    let partial = Report.build ReportExport.executiveSummary subject 5 (group IdentifiedGroup 6 five) [] at |> Result.toOption |> Option.get
    Assert.Equal(Partial(5, 6), partial.Status)
    Assert.Contains(IncompleteGroup, partial.Warnings)
    Assert.Empty(partial.Sections) // the executive summary has no section block
    Assert.Equal<string list>([ "D01"; "D02"; "D03" ], partial.Strengths) // highest mean first (higher is better)
    Assert.Equal<string list>([ "D03"; "D02"; "D01" ], partial.Weaknesses)
    Assert.Equal<string list>([ "timebox" ], partial.Recommendations |> List.map _.Id)

    let complete = Report.build ReportExport.detailedSection subject 5 (group IdentifiedGroup 5 five) [] at |> Result.toOption |> Option.get
    Assert.Equal(Complete, complete.Status)
    Assert.Equal(Shown 100.0, complete.Sections.Head.Median)
    Assert.Equal(NotApplicableValue, (Report.build ReportExport.detailedSection { subject with Content = Pilot.content } 5 (GroupResult.aggregate hash IdentifiedGroup 5 5 [ for c in five -> { c with Result = SurveyResult.compute hash Pilot.content Map.empty true } ]) [] at |> Result.toOption |> Option.get).Overall)

[<Fact>]
let ``reporting never rescores: report values are the group result's`` () =
    let g = group IdentifiedGroup 5 five
    let report = Report.build ReportExport.administratorGroup subject 5 g [] at |> Result.toOption |> Option.get

    let overallMeans =
        five
        |> List.choose (fun c -> c.Result.Overall |> Option.bind (fun o -> match o.Outcome with Scoring.Score(v, _, _) -> Some v | _ -> None))
        |> List.average

    Assert.Equal(Shown(Math.Round(overallMeans, 1, MidpointRounding.AwayFromZero)), report.Overall)
    Assert.Contains(report.Methodology, fun line -> line.StartsWith "Overall: BalancedMean")

[<Fact>]
let ``deltas appear only for comparable baselines`` () =
    let comparison comparable : ComparisonInput =
        { Kind = PriorGroup
          Label = "Previous quarter"
          Baseline = "group-2026-q2@SDRA/1"
          Metric = "overall"
          Current = Some 70.0
          BaselineValue = Some 64.5
          Comparable = comparable
          Reason = if comparable then None else Some "scoring of 'D03' changed" }

    let report =
        Report.build ReportExport.executiveSummary subject 5 (group IdentifiedGroup 5 five) [ comparison true; comparison false ] at
        |> Result.toOption
        |> Option.get

    Assert.Equal(Shown 5.5, report.Comparisons[0].Delta)
    Assert.Equal(NotComparable "scoring of 'D03' changed", report.Comparisons[1].Delta)
    Assert.Contains(NotComparableToPriorVersion, report.Warnings)

    let published c v : Publication.Published =
        { SurveyId = "SDRA"; Version = v; Hash = ""; Content = c; Parent = None; PublishedAt = at; PublishedBy = "a"; Manifest = []; Fixtures = [] }

    Assert.Equal(Ok(), Report.comparable (published content "2") (published { content with Metadata = { content.Metadata with Title = "Renamed" } } "1"))
    let rescored = { content with Sections = content.Sections |> List.map (fun s -> { s with Scoring = Some { Scorer = Registry.Presets.likertSum; Questions = [] } }) }
    Assert.True(Report.comparable (published rescored "2") (published content "1") |> Result.isError)

[<Fact>]
let ``exports are deterministic and snapshots identify exactly what produced a report`` () =
    let g = group IdentifiedGroup 5 five
    let report = Report.build ReportExport.audit subject 5 g [] at |> Result.toOption |> Option.get
    Assert.Equal(ReportExport.json report, ReportExport.json report)
    Assert.Contains("\"groupResultHash\":\"" + g.Hash + "\"", ReportExport.json report)

    let csv = ReportExport.sectionsCsv report
    Assert.StartsWith("section,title,mean,median,minimum,maximum,scored,unscored\nD01,Plan Commitment,85,100,50,100,5,0", csv)
    Assert.Contains("\"a, \"\"quoted\"\" title\"", ReportExport.sectionsCsv { report with Sections = [ { report.Sections.Head with Title = "a, \"quoted\" title" } ] })

    let s = ReportExport.snapshot report g.Hash "en-US" "folio@0.3.0/standard-results-1"
    Assert.Equal(s.Hash, (ReportExport.snapshot { report with GeneratedAt = at.AddDays 1.0 } g.Hash "en-US" "folio@0.3.0/standard-results-1").Hash)
    Assert.NotEqual<string>(s.Hash, (ReportExport.snapshot report g.Hash "fr-FR" "folio@0.3.0/standard-results-1").Hash)
    Assert.NotEqual<string>(s.Hash, (ReportExport.snapshot report g.Hash "en-US" "folio@0.4.0/standard-results-1").Hash)
    Assert.NotEqual<string>(s.Hash, (ReportExport.snapshot (Report.build ReportExport.audit subject 5 (group IdentifiedGroup 5 (List.tail five)) [] at |> Result.toOption |> Option.get) g.Hash "en-US" "folio@0.3.0/standard-results-1").Hash)
