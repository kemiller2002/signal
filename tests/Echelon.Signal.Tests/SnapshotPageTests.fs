/// Formal snapshots and exports through the page (WI-0075, ADM-022,
/// ADM-023): a snapshot of the report in view, its definition pinned to the
/// group's exact template, listed with the group, and its export files
/// handed to the browser to save with their lineage.
module Echelon.Signal.Tests.SnapshotPageTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Tests.AdminPageTests
open Echelon.Signal.Tests.AdminLinkTests

[<Fact>]
let ``a snapshot of the report in view is listed and its exports are saved with lineage`` () =
    let browser, _, page = signedIn ()
    page.Event("navigate", key = "/groups")
    page.Event("newGroupExpected", value = "2")
    page.Event("newGroupMinimum", value = "1")
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let group = (OpaqueId.ofBytes (Convert.FromHexString key)).Value
    page.Event("importText", value = String.concat "\n" [ link 1uy group; link 2uy group ])
    page.Event("import")

    changed page $"#/groups/{key}/report"
    Assert.True(page.Flag "canTakeSnapshot", page.ViewText)
    Assert.Empty(page.Items "reportSnapshots")
    page.Event("takeSnapshot")
    Assert.StartsWith("Snapshot snap-", page.Text "notice")

    let snapshots = page.Items "reportSnapshots"
    Assert.Single(snapshots) |> ignore
    let snapshot = snapshots.Head
    let id = snapshot["key"].GetValue<string>()
    Assert.Contains("administrator-group-sdra-0-1-0-draft v1 · 2 responses", snapshot["label"].GetValue<string>())
    Assert.Equal("Verified", snapshot["intact"].GetValue<string>())

    page.Event("exportSnapshot", key = id, value = "sections.csv")
    page.Event("exportSnapshot", key = id, value = "lineage.json")
    Assert.Equal("The export was handed to the browser to save.", page.Text "notice")
    let csvName, csvType, csv = browser.Downloads[0]
    Assert.Equal((id + "-sections.csv", "text/csv"), (csvName, csvType))
    Assert.StartsWith("section,title,mean", csv)
    let _, _, lineage = browser.Downloads[1]
    Assert.Contains($"\"snapshotId\":\"{id}\"", lineage)
    Assert.Contains($"\"groupId\":\"{key}\"", lineage)

    // Another snapshot of the same state is the same snapshot: nothing new is stored.
    page.Event("takeSnapshot")
    Assert.Equal($"Snapshot {id} was taken of 2 response(s).", page.Text "notice")
    Assert.Single(page.Items "reportSnapshots") |> ignore
