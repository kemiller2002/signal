/// A group's report rendered through the page (WI-0062): the group result
/// built from stored scores is the group result of the same answers; the
/// report is ReportData from `Report.build`, localized and in its own
/// direction; withheld families say why; suppression is never shown as a
/// number (RPT-001..006, SRPP, ADM-069).
module Echelon.Signal.Tests.ReportViewTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin
open Echelon.Signal.Tests.AdminPageTests
open Echelon.Signal.Tests.AdminLinkTests

let private answersFor (seed: int) : Answers =
    let choices = [| Rated Never; Rated Rarely; Rated Sometimes; Rated Often; Rated AlmostAlways; Withheld DontKnow; Withheld NotApplicable |]
    Pilot.assessment.Items |> List.mapi (fun i item -> item.Id, choices[(seed * 7 + i * 3) % choices.Length]) |> Map.ofList

[<Fact>]
let ``the group result from stored scores is the group result of the same answers`` () =
    let group = (OpaqueId.ofBytes (Array.init 16 byte)).Value
    let hash = Canonical.templateHash Pilot.assessment
    let definition: GroupDefinition = { Group = group; Mode = AnonymousGroup; ExpectedCount = 8; Template = Pilot.assessment }

    let accepted, stored, full =
        [ 1..6 ]
        |> List.map (fun seed ->
            let answers = answersFor seed
            let url = "https://signal.example" + LiveUrl.urlFor Pilot.assessment "/web/" "" { Binding = Anonymous((OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value, group); Answers = answers }

            match evaluateAgainst definition (fun _ -> None) url with
            | Accepted result ->
                result,
                GroupResult.ofSubmission Pilot.assessment.Items.Length result,
                ({ Role = None; Result = SurveyResult.compute hash Pilot.content (Pilot.answers answers) true }: GroupResult.Contribution)
            | other -> failwith $"%A{other}")
        |> List.unzip3

    let accumulator = accepted |> List.fold (fun acc r -> match Incremental.add r acc with Ok next -> next | Error e -> failwith $"%A{e}") (Incremental.empty definition)

    for minimum in [ 3; 10 ] do
        let fromScores = GroupResult.ofScores hash AnonymousGroup 8 minimum stored
        let fromAnswers = GroupResult.aggregate hash AnonymousGroup 8 minimum full
        Assert.Equal<(string * GroupResult.Aggregate) list>(fromAnswers.Sections, fromScores.Sections)
        Assert.Equal(fromAnswers.Coverage, fromScores.Coverage)
        Assert.Equal(fromAnswers.Hash, fromScores.Hash)
        // And from the administrator's incremental accumulator (ADM-011).
        Assert.Equal(fromAnswers.Hash, (GroupResult.ofAccumulator hash minimum accumulator).Hash)

/// A signed-in page with one anonymous group of two accepted surveys.
let private reportPage (minimum: string) =
    let browser, github, page = signedIn ()
    page.Event("navigate", key = "/groups")
    page.Event("newGroupExpected", value = "4")
    page.Event("newGroupMinimum", value = minimum)
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let group = (OpaqueId.ofBytes (Convert.FromHexString key)).Value
    page.Event("importText", value = String.concat "\n" [ link 1uy group; link 2uy group ])
    page.Event("import")
    browser, github, page, key

let private column (page: Page) (list: string) (field: string) =
    page.Items list |> List.map (fun item -> item[field].GetValue<string>())

[<Fact>]
let ``a report link renders the group's ReportData in its blocks, contents and locale`` () =
    let _, _, page, key = reportPage "1"
    changed page $"#/groups/{key}/report"
    Assert.True(page.Flag "viewReport")
    Assert.True(page.Flag "hasReport", page.ViewText)
    Assert.Equal("ltr", page.Text "reportDir")
    Assert.Equal<string list>([ "D01 Plan Commitment"; "D02 Change Responsiveness"; "D03 Timebox Discipline" ], column page "reportSections" "section")
    // Every answer of both links is "Often" (3 of 4): every section mean is 75.0.
    Assert.Equal<string list>([ "75.0"; "75.0"; "75.0" ], column page "reportSections" "mean")
    Assert.Equal<string list>([ "Responses"; "Summary"; "Overall result"; "Results by section"; "Recommendations"; "Coverage"; "Methodology" ], column page "reportContents" "title")
    Assert.Equal<string list>([ "4"; "2"; "2"; "50%" ], column page "reportCounts" "value")

    // The same report in Arabic: right to left, Arabic separators and labels.
    changed page $"#/groups/{key}/report?locale=ar-EG"
    Assert.Equal("rtl", page.Text "reportDir")
    Assert.Equal("ar-EG", page.Text "reportLang")
    Assert.Equal<string list>([ "75٫0"; "75٫0"; "75٫0" ], column page "reportSections" "mean")
    Assert.Equal("النتائج حسب القسم", page.Text "reportTitleSectionResults")

    changed page $"#/groups/{key}/report?family=audit&locale=de-DE"
    Assert.Equal("Ergebnisse nach Abschnitt", page.Text "reportTitleSectionResults")
    Assert.True(page.Flag "reportHasAuditMetadata")
    Assert.Contains("sha256:", page.Items "reportAudit" |> List.map (fun i -> i["value"].GetValue<string>()) |> String.concat " ")

[<Fact>]
let ``a family the group's privacy rules forbid is withheld with its reason, and links say which are available`` () =
    let _, _, page, key = reportPage "1"
    changed page $"#/groups/{key}/report?family=identified-detail"
    Assert.False(page.Flag "hasReport")
    Assert.True(page.Flag "reportWithheld")
    Assert.Contains("RespondentDetail is shown only for identified groups", page.Text "reportWithheldReason")

    let availability = page.Items "reportFamilies" |> List.map (fun f -> f["id"].GetValue<string>(), f["available"].GetValue<bool>()) |> Map.ofList
    Assert.False(availability["identified-detail"])
    Assert.True(availability["administrator-group"])
    Assert.Contains($"#/groups/{key}/report?family=audit", column page "reportFamilies" "href")

[<Fact>]
let ``below the anonymous minimum every value is withheld, never a number`` () =
    let _, _, page, key = reportPage "3"
    changed page $"#/groups/{key}/report"
    Assert.True(page.Flag "hasReport", page.ViewText)
    Assert.All(column page "reportSections" "mean", fun mean -> Assert.Equal("Withheld to protect anonymity", mean))
    Assert.Contains("anonymous-small-group-suppressed", column page "reportWarnings" "code")

[<Fact>]
let ``every report label has all four locales, and every block has a title`` () =
    for key in ReportLabels.keys do
        let texts = [ "en-US"; "de-DE"; "fr-FR"; "ar-EG" ] |> List.map (fun tag -> ReportLabels.label tag key)
        Assert.All(texts, fun t -> Assert.False(String.IsNullOrWhiteSpace t, key))
        // Translated, not English repeated (a word such as "Audit" or "Median" may be shared).
        Assert.True((texts |> List.distinct |> List.length) >= 3, key)

    let blocks =
        [ ReportModel.Header; ReportModel.Summary; ReportModel.ResponseCounts; ReportModel.OverallResult; ReportModel.SectionResults; ReportModel.GroupDistributions
          ReportModel.Strengths; ReportModel.Weaknesses; ReportModel.Recommendations; ReportModel.CoverageAndConfidence; ReportModel.Comparisons
          ReportModel.RespondentDetail; ReportModel.RoleBreakdown; ReportModel.Methodology; ReportModel.AuditMetadata ]

    for block in blocks do
        Assert.Contains(sprintf "%A" block, ReportLabels.keys)

    for code in [ ReportModel.LowCoverage; ReportModel.LowResponseCount; ReportModel.HighDontKnowRate; ReportModel.IncompleteGroup; ReportModel.NotComparableToPriorVersion; ReportModel.AnonymousSmallGroupSuppressed ] do
        Assert.Contains(ReportModel.warningCode code, ReportLabels.keys)
