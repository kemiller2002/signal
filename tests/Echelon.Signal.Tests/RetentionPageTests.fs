/// The console's dataset lifecycle and retention screens (WI-0077 slice 2,
/// ADM-045, ADM-064) through the administrator page's Limen boundary,
/// against Arca's in-memory provider.
module Echelon.Signal.Tests.RetentionPageTests

open System
open Xunit
open Arca
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Tests.AdminPageTests

/// A group with two accepted submissions, finalized when asked.
let private group (finalize: bool) =
    let browser, github, page = signedIn ()
    page.Event("navigate", key = "groups")
    page.Event("newGroupExpected", value = "2")
    page.Event("newGroupMinimum", value = "1")
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let id = (OpaqueId.ofBytes (Convert.FromHexString key)).Value
    page.Event("importText", value = String.concat "\n" [ link 1uy id; link 2uy id ])
    page.Event("import")

    if finalize then
        page.Event("navigate", key = "storage")
        page.Event("rebuildIndex")
        page.Event("openGroup", key = key)
        page.Event("transition", value = "finalize")
        Assert.Equal("Finalized", page.Text "groupPhase")

    browser, github, page, key

let private results (github: InMemoryStore) =
    github.State.Objects |> Map.filter (fun path _ -> path.Contains "signal.result") |> Map.toList |> List.map (fun (_, o) -> o.Content)

[<Fact>]
let ``a finalized group's results are retired only after the person confirms, and the notice claims only what the store proved`` () =
    let _, github, page, _ = group true
    Assert.True(page.Flag "canRetireSources", page.ViewText)
    Assert.False(page.Flag "sourcesRetired")

    // The first press asks; keeping them changes nothing.
    page.Event("retireSources")
    Assert.True(page.Flag "confirmingRetire")
    Assert.False(page.Flag "canRetireSources")
    Assert.Contains("cannot be undone", page.Text "retireWarning")
    Assert.Contains("history keeps the content", page.Text "retireWarning")
    page.Event("cancelRetire")
    Assert.False(page.Flag "confirmingRetire")
    Assert.Equal(2, results github |> List.filter (fun c -> (Tombstone.decode c).IsNone) |> List.length)

    // Confirmed: out of Signal and out of the current tree (the in-memory
    // provider declares Erase, as GitHub does), never claimed from history.
    page.Event("retireSources")
    page.Event("confirmRetire")
    Assert.Equal("SIGNAL.RETENTION.RETIRED", page.Text "noticeCode")
    Assert.Contains("erased from the repository's current files", page.Text "notice")
    Assert.Contains("history still holds them", page.Text "notice")
    Assert.DoesNotContain("permanent", page.Text "notice")
    Assert.True(page.Flag "sourcesRetired")
    Assert.Equal("Nothing remains to rebuild the group's results from.", page.Text "sourcesRetiredText")
    Assert.False(page.Flag "canRetireSources")
    Assert.False(page.Flag "confirmingRetire")
    Assert.Equal("0 of 2 accepted", page.Text "groupProgress")
    Assert.All(results github, fun content -> Assert.True((Tombstone.decode content).IsSome))

    // Asking again says it was done, and sends nothing.
    let commits = github.State.History.Length
    page.Event("retireSources")
    Assert.Equal("SIGNAL.RETENTION.ALREADY_RETIRED", page.Text "noticeCode")
    Assert.Equal(commits, github.State.History.Length)

[<Fact>]
let ``a group still collecting offers no retirement, and asking is refused`` () =
    let _, github, page, _ = group false
    Assert.False(page.Flag "canRetireSources")
    page.Event("retireSources")
    page.Event("confirmRetire")
    Assert.Equal("SIGNAL.ACCESS.CAPABILITY_NOT_HELD", page.Text "noticeCode")
    Assert.Equal(2, results github |> List.filter (fun c -> (Tombstone.decode c).IsNone) |> List.length)

let private targets (page: Page) =
    page.Items "lifecycleTargets" |> List.map (fun item -> item["target"].GetValue<string>())

[<Fact>]
let ``the dataset moves through its lifecycle, read-only while not active, and verified provider deletion is never offered`` () =
    let _, _, page = signedIn ()
    page.Event("navigate", key = "storage")
    Assert.Equal("state-active", page.Text "datasetLifecycle")
    Assert.True(page.Flag "canManageLifecycle", page.ViewText)
    Assert.Equal<string list>([ "state-archived"; "state-retained-for-audit"; "state-superseded"; "state-retired"; "state-deleted-from-active-state" ], targets page)

    // Archived: read-only, nothing else may change, but it can be made active again.
    page.Event("datasetTransition", value = "state-archived")
    Assert.Equal("SIGNAL.LIFECYCLE.CHANGED", page.Text "noticeCode")
    Assert.Equal("state-archived", page.Text "datasetLifecycle")
    Assert.StartsWith("Archived", page.Text "datasetLifecycleText")
    Assert.True(page.Flag "readOnly")
    Assert.False(page.Flag "canCreateGroup")
    Assert.True(page.Flag "canManageLifecycle")
    Assert.Contains("state-active", targets page)

    // As the browser sends it: the row's key, no value (GH-113).
    page.Event("datasetTransition", key = "state-active")
    Assert.Equal("state-active", page.Text "datasetLifecycle")
    Assert.False(page.Flag "readOnly")
    Assert.True(page.Flag "canCreateGroup")

    // Deletion: GitHub keeps history, so only "not provable" is offered.
    page.Event("datasetTransition", value = "state-deleted-from-active-state")
    page.Event("datasetTransition", value = "state-provider-deletion-requested")
    Assert.Equal<string list>([ "state-provider-deletion-not-provable" ], targets page)
    page.Event("datasetTransition", value = "state-provider-deletion-verified")
    Assert.Equal("SIGNAL.LIFECYCLE.ILLEGAL_TRANSITION", page.Text "noticeCode")
    Assert.Equal("state-provider-deletion-requested", page.Text "datasetLifecycle")

    // A state the current one cannot reach is refused before anything is sent.
    page.Event("datasetTransition", value = "state-active")
    Assert.Equal("SIGNAL.LIFECYCLE.ILLEGAL_TRANSITION", page.Text "noticeCode")
