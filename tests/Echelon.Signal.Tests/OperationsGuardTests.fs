/// Second review round on per-action pending state (SIG-VIS-PENDING-002):
/// only the running operation's own work settles it, a programming defect
/// never leaves the page running an operation that ended, an edit to a
/// report's template pin during its save stays unsaved, and focus after a
/// change lands beside where it was made.
module Echelon.Signal.Tests.OperationsGuardTests

open System
open Xunit
open Echelon.Signal.Admin
open Echelon.Signal.Tests.AdminPageTests
open Echelon.Signal.Admin.Operations

/// A signed-in administrator with a new, unsaved draft of PULSE open.
let private onNewDraft () =
    let browser, _, page = signedIn ()
    Echelon.Signal.Tests.AdminLinkTests.changed page "#/assessments"
    page.Event("newDraftSurvey", value = "PULSE")
    page.Event("newDraftTitle", value = "Team pulse")
    page.Event("startDraft")
    browser, page

/// Starts a save whose commit waits, unanswered, on a timer wake.
let private holdSave (browser: Browser) (page: Page) (after: unit -> unit) =
    browser.HoldWakes <- true
    page.HoldNextCommit(after)
    page.Event("saveDraft")
    Assert.True(page.Flag "operationRunning", page.ViewText)
    Assert.Equal("true", page.Text "saveDraftBusy")
    Assert.NotEmpty(browser.HeldWakes)

/// Answers the held wakes, so the held commit goes on.
let private release (browser: Browser) (page: Page) =
    browser.HoldWakes <- false
    let held = List.ofSeq browser.HeldWakes
    browser.HeldWakes.Clear()
    held |> List.iter page.Send

let private focusRequests (browser: Browser) =
    browser.Requests |> Seq.filter (fun r -> r.Contains "\"capability\":\"limen.focus\"") |> List.ofSeq

// ---- A: a fault is tied to the running operation ----------------------------------------------

[<Fact>]
let ``a cross-tab receive that throws is shown through Aegis but never settles the held save, and a refresh waits for it`` () =
    let browser, page = onNewDraft ()
    holdSave browser page ignore

    // Another tab's message arrives; receiving it throws (not this save's work).
    page.ReceiveThrows()
    page.Send """{"kind":"CapabilityFact","capability":"signal.host","fact":{"kind":"Broadcast","message":"fides:signal-admin:planted"}}"""
    Assert.True(page.Flag "hasOperationalFault", page.ViewText)
    // The save is still the running operation: still busy, not settled unknown.
    Assert.True(page.Flag "operationRunning", page.ViewText)
    Assert.Equal("true", page.Text "saveDraftBusy")
    Assert.Equal(Some Operation.SaveDraft, page.Model.Value.Activity.Running)
    Assert.True(page.Model.Value.Activity.LastWrite.IsNone)
    Assert.Equal(Clear, page.Model.Value.Activity.Reconcile)

    // A refresh is refused while the save runs: no read is started for it.
    Assert.True(page.Flag "refreshDisabled")
    let _, effects = AdminUpdate.update (AdminApp.Ui("refresh", None, "")) page.Model.Value
    Assert.Empty(effects)
    page.Event("refresh")
    Assert.Equal(Some Operation.SaveDraft, page.Model.Value.Activity.Running)

    // The save answers; then a refresh runs.
    release browser page
    Assert.Equal("SIGNAL.AUTHORING.SAVED", page.Text "noticeCode")
    Assert.Equal("saved", page.Text "persistenceState")
    Assert.False(page.Flag "operationRunning")
    Assert.False(page.Flag "refreshDisabled")
    let _, effects = AdminUpdate.update (AdminApp.Ui("refresh", None, "")) page.Model.Value
    Assert.Contains(effects, fun effect -> match effect with AdminApp.OpenDataset _ -> true | _ -> false)
    page.Event("refresh")
    Assert.True(page.Model.Value.Activity.Running.IsNone)

[<Fact>]
let ``only the operation's own failed work is settled; background failures are reported apart`` () =
    let bridge = Echelon.Signal.Application.Bridge.Bridge<string>()
    bridge.Start(async { return failwith "a sign-out threw" })
    bridge.StartOperation(async { return [ "kept" ] })
    let _, messages, failed = bridge.Drain()
    Assert.Equal<string list>([ "kept" ], messages)
    Assert.Equal<Echelon.Signal.Application.Bridge.Work list>([ Echelon.Signal.Application.Bridge.Work.Background ], failed |> List.map _.Work)
    bridge.StartOperation(async { return failwith "the operation threw" })
    let _, _, failed = bridge.Drain()
    Assert.Equal<Echelon.Signal.Application.Bridge.Work list>([ Echelon.Signal.Application.Bridge.Work.Operation ], failed |> List.map _.Work)

// ---- D: a programming defect never leaves the page running --------------------------------------

[<Fact>]
let ``a programming defect inside a held save still fails loud, and the settled state is kept first`` () =
    let browser, page = onNewDraft ()
    holdSave browser page (fun () -> invalidOp "planted: a programming defect after the wake")

    // Aegis re-raises the defect (it is not an operational fault) ...
    Assert.Throws<InvalidOperationException>(fun () -> release browser page) |> ignore
    // ... but only after the wire kept the settled state: not running, unknown.
    let activity = page.Model.Value.Activity
    Assert.True(activity.Running.IsNone, $"%A{activity}")
    Assert.Equal(Some(Operation.SaveDraft, Unknown), activity.LastWrite)
    Assert.Equal(Owed, activity.Reconcile)

    // The next message renders a usable page: a refresh runs and settles it.
    page.Event("refresh")
    Assert.False(page.Flag "operationRunning", page.ViewText)
    Assert.Equal(Clear, page.Model.Value.Activity.Reconcile)
    Assert.False(page.Flag "saveDraftDisabled")

// ---- E: a template pin changed during a report save stays unsaved -------------------------------

[<Fact>]
let ``a template pin changed while a report definition saves stays unsaved`` () =
    let step name key value (screen: ReportBuilder.Screen) =
        ReportBuilder.update ReportLibrary.empty [ "SDRA"; "PULSE" ] name key value screen |> fst

    let started = ReportBuilder.emptyScreen |> step "newReportId" None "quarterly" |> step "newReportDefinition" None ""
    let sent = started.Builder.Value
    Assert.Equal(ReportLibrary.LatestTemplate "SDRA", sent.Pin)
    // Only the pin changes while the save is out; the definition is the same.
    let repinned = started |> step "reportPin" None "latest:PULSE"
    Assert.Equal(sent.Definition, repinned.Builder.Value.Definition)
    let entry: ReportLibrary.Entry = { Definition = { sent.Definition with Version = sent.Definition.Version + 1 }; Pins = ReportLibrary.currentPins sent.Pin; Used = false }

    let after = ReportBuilder.saved entry repinned
    Assert.True(after.Builder.Value.Unsaved)
    Assert.Equal(ReportLibrary.LatestTemplate "PULSE", after.Builder.Value.Pin)
    Assert.Equal(entry.Definition.Version, after.Builder.Value.Definition.Version)
    // Unchanged, it is saved.
    Assert.False((ReportBuilder.saved entry started).Builder.Value.Unsaved)

// ---- B: focus lands beside where the change was made --------------------------------------------

/// A finalized group with two accepted results, on its page.
let private finalizedGroup () =
    let browser, _, page = signedIn ()
    page.Event("navigate", key = "groups")
    page.Event("newGroupExpected", value = "2")
    page.Event("newGroupMinimum", value = "1")
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let id = (Echelon.Signal.Engine.UrlState.OpaqueId.ofBytes (Convert.FromHexString key)).Value
    page.Event("importText", value = String.concat "\n" [ link 1uy id; link 2uy id ])
    page.Event("import")
    page.Event("navigate", key = "storage")
    page.Event("rebuildIndex")
    page.Event("openGroup", key = key)
    browser, page

[<Fact>]
let ``a group transition moves focus to the group's phase, not the status region`` () =
    let browser, page = finalizedGroup ()
    let before = (focusRequests browser).Length
    page.Event("finalizeGroup")
    Assert.Equal("Finalized", page.Text "groupPhase")
    let requests = focusRequests browser
    Assert.Equal(before + 1, requests.Length)
    Assert.Contains("\"name\":\"group-phase\"", List.last requests)

[<Fact>]
let ``a retire that worked moves focus to the retired results`` () =
    let browser, page = finalizedGroup ()
    page.Event("finalizeGroup")
    page.Event("retireSources")
    page.Event("confirmRetire")
    Assert.Equal("SIGNAL.RETENTION.RETIRED", page.Text "noticeCode")
    Assert.True(page.Flag "sourcesRetired", page.ViewText)
    Assert.Contains("\"name\":\"sources-retired\"", List.last (focusRequests browser))
