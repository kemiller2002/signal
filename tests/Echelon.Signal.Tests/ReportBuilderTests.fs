/// The report builder (WI-0075, ADM-021, ADM-063): a definition started,
/// edited by closed choices, checked and previewed against a group, saved
/// and used for a group's report and a formal snapshot through the page.
module Echelon.Signal.Tests.ReportBuilderTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.ReportModel
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Admin
open Echelon.Signal.Admin.ReportBuilder
open Echelon.Signal.Tests.AdminPageTests
open Echelon.Signal.Tests.AdminLinkTests

let private step name key value (screen: Screen) =
    update ReportLibrary.empty [ "SDRA" ] name key value screen |> fst

[<Fact>]
let ``the builder edits presentation by closed choices and refuses anything else`` () =
    let screen = emptyScreen |> step "newReportId" None "Quarterly" |> step "newReportDefinition" None ""
    let d () = screen.Builder.Value.Definition
    Assert.Equal("quarterly", d().Id)

    let screen = screen |> step "reportBlock" (Some "RoleBreakdown") "RoleBreakdown" |> step "reportBlockUp" (Some "RoleBreakdown") ""
    let blocks = screen.Builder.Value.Definition.Blocks
    Assert.Equal(RoleBreakdown, blocks[blocks.Length - 2])
    let screen = screen |> step "reportBlock" (Some "Header") ""
    Assert.DoesNotContain(Header, screen.Builder.Value.Definition.Blocks)

    let screen = screen |> step "reportAudience" None "ExecutiveAudience" |> step "reportMinimum" None "8" |> step "reportDecimals" None "2"
    Assert.Equal((ExecutiveAudience, 8, 2), (screen.Builder.Value.Definition.Audience, screen.Builder.Value.Definition.MinimumGroupSize, screen.Builder.Value.Definition.Decimals))

    Assert.True((screen |> step "reportDecimals" None "9").Problem.IsSome)
    Assert.True((screen |> step "reportAudience" None "Everyone").Problem.IsSome)
    Assert.True((screen |> step "reportBlock" (Some "Scoring") "Scoring").Problem.IsSome)

    Assert.True((screen |> step "reportPin" None "latest:NOPE").Problem.IsSome)
    let _, commands = update ReportLibrary.empty [ "SDRA" ] "saveReportDefinition" None "" screen
    match commands with
    | [ SaveDefinition(saved, pins) ] ->
        Assert.Equal("quarterly", saved.Id)
        Assert.Equal(ReportLibrary.LatestTemplate "SDRA", pins.Template)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a saved definition previews against a group, renders its report and is snapshotted`` () =
    let _, _, page = signedIn ()
    page.Event("navigate", key = "/groups")
    page.Event("newGroupExpected", value = "2")
    page.Event("newGroupMinimum", value = "1")
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let group = (OpaqueId.ofBytes (Convert.FromHexString key)).Value
    page.Event("importText", value = String.concat "\n" [ link 1uy group; link 2uy group ])
    page.Event("import")

    changed page "#/reports"
    Assert.True(page.Flag "viewReports", page.ViewText)
    page.Event("newReportId", value = "team-summary")
    page.Event("newReportDefinition")
    Assert.True(page.Flag "hasBuilder")
    page.Event("reportBlock", key = "Strengths", value = "Strengths")
    page.Event("reportMinimum", value = "1")
    page.Event("reportPreviewGroup", value = key)
    Assert.StartsWith("Software Delivery Reality Assessment: 2 of 2 accepted.", (page.Items "builderPreview").Head["text"].GetValue<string>())
    page.Event("saveReportDefinition")
    Assert.Equal("Report definition team-summary was saved as version 1.", page.Text "notice")
    Assert.Contains(page.Items "savedDefinitions", fun i -> i["key"].GetValue<string>() = "team-summary")

    changed page $"#/groups/{key}/report?family=team-summary"
    Assert.True(page.Flag "hasReport", page.ViewText)
    Assert.Contains(page.Items "reportContents", fun i -> i["key"].GetValue<string>() = "Strengths")
    page.Event("takeSnapshot")
    Assert.Contains(page.Items "reportSnapshots", fun i -> i["label"].GetValue<string>().Contains "team-summary v1")

    // Used for a snapshot: the next save is version 2.
    changed page "#/reports"
    page.Event("editReportDefinition", key = "team-summary")
    page.Event("reportDecimals", value = "2")
    page.Event("saveReportDefinition")
    Assert.Equal("Report definition team-summary was saved as version 2.", page.Text "notice")
