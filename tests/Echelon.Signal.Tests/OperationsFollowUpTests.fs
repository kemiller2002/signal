/// Review follow-ups to per-action pending state (SIG-VIS-PENDING-002):
/// reconciliation before resend, owed reloads, wording, operations that
/// throw, confirmations held open with focus moved after, edits during a
/// save, and the answer order of writes whose reread fails.
module Echelon.Signal.Tests.OperationsFollowUpTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Admin
open Echelon.Signal.Tests.AdminPageTests
open Echelon.Signal.Admin.Operations

let private started operation activity =
    match Operations.start operation activity with
    | Some next -> next
    | None -> failwith $"{operation} did not start"

let private ui name key value = AdminApp.Ui(name, key, value)

let private value (name: string) (model: AdminApp.Model) =
    AdminView.project model |> List.pick (fun (key, v) -> if key = name then Some v else None)

/// A signed-in administrator on the Groups area, with nothing running.
let private onGroups () =
    let _, _, page = signedIn ()
    page.Event("navigate", key = "groups")
    page.Event("newGroupExpected", value = "2")
    page.Event("newGroupMinimum", value = "1")
    page, page.Model.Value

// ---- Review follow-ups (SIG-VIS-PENDING-002) ---------------------------------------------------

[<Fact>]
let ``after an unknown outcome no change is sent until a dataset read started afterwards succeeds`` () =
    let unknown = started Operation.Admit idle |> settle Unknown
    Assert.Equal(Owed, unknown.Reconcile)
    // Changes wait; reads may run.
    Assert.False(canStart Operation.Admit unknown)
    Assert.False(canStart Operation.CreateGroup unknown)
    Assert.True(canStart Operation.Refresh unknown)
    Assert.True((Operations.start Operation.Admit unknown).IsNone)
    // A failed read leaves it owed; a successful one settles it.
    let checking = started Operation.Refresh unknown
    Assert.Equal(Checking, checking.Reconcile)
    Assert.Equal(Owed, (settle (NotDone "SIGNAL.STORAGE.OFFLINE") checking).Reconcile)
    let reconciled = settle Confirmed checking
    Assert.Equal(Clear, reconciled.Reconcile)
    Assert.True(canStart Operation.Admit reconciled)

[<Fact>]
let ``an owed reload is settled only by a dataset read started after the change elsewhere`` () =
    // A refresh already running when another tab changed the dataset read the old state.
    let refreshing = started Operation.Refresh idle
    let noticed = oweReload refreshing
    let answered = settle Confirmed noticed
    Assert.Equal(Owed, answered.Reload)
    Assert.NotEqual<string>("", reloadText answered)
    // The next one, started afterwards, settles it.
    let again = started Operation.Refresh answered
    Assert.Equal(Checking, again.Reload)
    Assert.Equal("", reloadText again)
    Assert.Equal(Clear, (settle Confirmed again).Reload)

[<Fact>]
let ``each change says in its own words what is not done yet`` () =
    let pending operation = persistenceText (started operation idle)
    Assert.Equal("Deleting draft: not deleted until the store confirms.", pending Operation.DeleteDraft)
    Assert.Equal("Retiring accepted results: nothing is retired until the store confirms.", pending Operation.RetireSources)
    Assert.Equal("Removing: not removed until the store confirms.", pending (Operation.RemoveAdministrator "github:1"))
    Assert.Equal("Publishing: not published until the store confirms.", pending Operation.PublishDraft)
    Assert.Equal("The draft is deleted: the store confirmed it.", persistenceText (started Operation.DeleteDraft idle |> settle Confirmed))
    Assert.Equal("“Delete draft” did not complete; the notice says why.", persistenceText (started Operation.DeleteDraft idle |> settle (NotDone "X")))

[<Fact>]
let ``an exception inside an operation settles it as unknown, never as failed`` () =
    let _, model = onGroups ()
    let creating, _ = AdminUpdate.update (ui "createGroup" None "") model
    let faulted, _ = AdminUpdate.update AdminApp.OperationFaulted creating
    Assert.True(faulted.Activity.Running.IsNone)
    Assert.Equal(Some(Operation.CreateGroup, Unknown), faulted.Activity.LastWrite)
    Assert.Equal(View.Value(View.Text "unknown"), value "persistenceState" faulted)

/// A signed-in administrator editing a stored draft of PULSE.
let private onStoredDraft () =
    let _, _, page = signedIn ()
    Echelon.Signal.Tests.AdminLinkTests.changed page "#/assessments"
    page.Event("newDraftSurvey", value = "PULSE")
    page.Event("newDraftTitle", value = "Team pulse")
    page.Event("startDraft")
    page.Event("saveDraft")
    Assert.Equal("SIGNAL.AUTHORING.SAVED", page.Text "noticeCode")
    page

[<Fact>]
let ``a planted exception in a write is settled unknown through the wire, presented by Aegis, and the page is usable again`` () =
    let page = onStoredDraft ()
    page.Event("draftTitle", value = "Team pulse, again")
    page.FaultNextWrite()
    page.Event("saveDraft")
    // Not stuck busy, never "not saved": unknown, with the fault shown.
    Assert.False(page.Flag "operationRunning", page.ViewText)
    Assert.Equal("unknown", page.Text "persistenceState")
    Assert.True(page.Flag "hasOperationalFault", page.ViewText)
    Assert.Equal("false", page.Text "saveDraftBusy")
    // Changes wait for a refresh; the refresh is allowed and settles it.
    Assert.True(page.Flag "saveDraftDisabled")
    Assert.False(page.Flag "refreshDisabled")
    page.Event("refresh")
    Assert.False(page.Flag "saveDraftDisabled", page.ViewText)
    page.Event("saveDraft")
    Assert.Equal("saved", page.Text "persistenceState")

[<Fact>]
let ``the bridge returns every message of a drain, also when another operation threw`` () =
    let bridge = Echelon.Signal.Application.Bridge.Bridge<string>()
    bridge.Start(async { return [ "kept" ] })
    bridge.Start(async { return failwith "planted" })
    bridge.Start(async { return [ "also kept" ] })
    let calls, messages, failed = bridge.Drain()
    Assert.Empty(calls)
    Assert.Equal<string list>([ "kept"; "also kept" ], messages)
    Assert.Single(failed) |> ignore

[<Fact>]
let ``delete draft keeps its confirmation busy until answered, then closes it and moves focus to the outcome in the draft's place`` () =
    let page = onStoredDraft ()
    page.Event("deleteDraft")
    let model = page.Model.Value
    Assert.Equal(View.Value(View.Flag true), value "confirmingDeleteDraft" model)

    let deleting, effects = AdminUpdate.update (ui "confirmDeleteDraft" None "") model
    Assert.Equal<AdminApp.Effect list>([ AdminApp.DeleteDraft "PULSE" ], effects)
    // Still open, its confirm button busy and labelled, the way out disabled.
    Assert.Equal(View.Value(View.Flag true), value "confirmingDeleteDraft" deleting)
    Assert.Equal(View.Value(View.Text "true"), value "confirmDeleteBusy" deleting)
    Assert.Equal(View.Value(View.Text "Deleting draft"), value "confirmDeleteLabel" deleting)
    Assert.Equal(View.Value(View.Flag true), value "busy" deleting)
    let kept, ignored = AdminUpdate.update (ui "cancelDeleteDraft" None "") deleting
    Assert.Empty(ignored)
    Assert.Equal(View.Value(View.Flag true), value "confirmingDeleteDraft" kept)

    // Refused: closed, focus to the outcome shown beside the draft's actions
    // (.admin-outcome; #activity is at the top of the long page: signal#136
    // review round 2), the draft still there.
    let refusal: AdminApp.Notice = { Code = "SIGNAL.AUTHORING.NO_DRAFT"; Message = "There is no stored draft of PULSE."; Infrastructure = false }
    let refused, effects = AdminUpdate.update (AdminApp.Failed refusal) deleting
    Assert.Equal(View.Value(View.Flag false), value "confirmingDeleteDraft" refused)
    Assert.Contains(AdminApp.Focus "outcome", effects)
    Assert.DoesNotContain(AdminApp.Focus "activity", effects)
    // Deleted: closed, focus to the outcome in the draft's place, the editor closed. (The listing
    // drops the draft with the catalog reread that precedes this message.)
    let deleted, effects = AdminUpdate.update (AdminApp.DraftDeleted "PULSE") deleting
    Assert.Equal(View.Value(View.Flag false), value "confirmingDeleteDraft" deleted)
    Assert.True(deleted.Authoring.Editor.IsNone)
    Assert.Contains(AdminApp.Focus "outcome", effects)
    Assert.DoesNotContain(AdminApp.Focus "activity", effects)

[<Fact>]
let ``retire keeps its confirmation busy until answered, then closes it and moves focus back to its button when refused`` () =
    let _, _, page = signedIn ()
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
    page.Event("transition", value = "finalize")
    page.Event("retireSources")
    let model = page.Model.Value
    Assert.Equal(View.Value(View.Flag true), value "confirmingRetire" model)

    let retiring, effects = AdminUpdate.update (ui "confirmRetire" None "") model
    Assert.Equal<AdminApp.Effect list>([ AdminApp.RetireSources key ], effects)
    Assert.Equal(View.Value(View.Flag true), value "confirmingRetire" retiring)
    Assert.Equal(View.Value(View.Text "true"), value "confirmRetireBusy" retiring)
    Assert.Equal(View.Value(View.Text "Retiring accepted results"), value "confirmRetireLabel" retiring)
    let kept, ignored = AdminUpdate.update (ui "cancelRetire" None "") retiring
    Assert.Empty(ignored)
    Assert.Equal(View.Value(View.Flag true), value "confirmingRetire" kept)

    let refusal: AdminApp.Notice = { Code = "SIGNAL.RETENTION.NOT_DUE"; Message = "The retention period has not passed."; Infrastructure = false }
    let refused, effects = AdminUpdate.update (AdminApp.Failed refusal) retiring
    Assert.Equal(View.Value(View.Flag false), value "confirmingRetire" refused)
    // Beside where it was made, not the distant status region (round 2, B).
    Assert.Contains(AdminApp.Focus "retire-sources", effects)
    Assert.DoesNotContain(AdminApp.Focus "activity", effects)
    Assert.Equal(View.Value(View.Flag true), value "canRetireSources" refused)

[<Fact>]
let ``edits made while a draft saves stay in the editor and stay unsaved`` () =
    let ok = function Ok value -> value | Error error -> failwith $"%A{error}"
    let sent = Authoring.start "PULSE" "Team pulse" |> ok
    let edited = Authoring.apply (Authoring.AddSection "Focus") sent |> ok
    let screen = { Authoring.emptyScreen with Editor = Some edited }
    // The save of the earlier draft is answered after the edit.
    let after = Authoring.saved sent.Draft screen
    Assert.True(after.Editor.Value.Unsaved)
    Assert.Equal(edited.Draft, after.Editor.Value.Draft)
    // A save of what the editor holds is saved.
    Assert.False((Authoring.saved edited.Draft screen).Editor.Value.Unsaved)

[<Fact>]
let ``edits made while a report definition saves are kept, at the stored version, and stay unsaved`` () =
    let step name key value (screen: ReportBuilder.Screen) =
        ReportBuilder.update ReportLibrary.empty [ "SDRA" ] name key value screen |> fst

    let started = ReportBuilder.emptyScreen |> step "newReportId" None "Quarterly" |> step "newReportDefinition" None ""
    let sent = started.Builder.Value.Definition
    let edited = started |> step "reportDecimals" None "3"
    let entry: ReportLibrary.Entry = { Definition = { sent with Version = sent.Version + 1 }; Pins = ReportLibrary.currentPins started.Builder.Value.Pin; Used = false }
    let after = ReportBuilder.saved entry edited
    Assert.True(after.Builder.Value.Unsaved)
    Assert.Equal(3, after.Builder.Value.Definition.Decimals)
    Assert.Equal(sent.Version + 1, after.Builder.Value.Definition.Version)
    let clean = ReportBuilder.saved entry started
    Assert.False(clean.Builder.Value.Unsaved)
    Assert.Equal(entry.Definition, clean.Builder.Value.Definition)

[<Fact>]
let ``a roster change whose outcome stays unknown is reported unknown, and no change is sent until a refresh`` () =
    let _, _, page = signedIn ()
    page.Event("navigate", key = "administrators")
    page.CommitsStayUnknown(true)
    page.Event("admitAccount", value = "1234")
    page.Event("admit")
    Assert.Equal("SIGNAL.STORAGE.OUTCOME_UNKNOWN", page.Text "noticeCode")
    Assert.Equal("unknown", page.Text "persistenceState")
    Assert.True(page.Flag "admitDisabled")
    Assert.True(page.Flag "createGroupDisabled")
    Assert.False(page.Flag "refreshDisabled")
    // A second admit is not sent: nothing reaches the store.
    page.Event("admitAccount", value = "1234")
    page.Event("admit")
    Assert.Equal("unknown", page.Text "persistenceState")
    page.CommitsStayUnknown(false)
    page.Event("refresh")
    Assert.False(page.Flag "admitDisabled", page.ViewText)
    Assert.Equal("", page.Text "persistenceState")

/// A signed-in administrator with a group (no responses).
let private withNewGroup () =
    let browser, github, page = signedIn ()
    page.Event("navigate", key = "groups")
    page.Event("newGroupExpected", value = "2")
    page.Event("newGroupMinimum", value = "1")
    browser, github, page

[<Fact>]
let ``a created group that cannot be read back is confirmed, and one notice keeps both facts`` () =
    let _, _, page = withNewGroup ()
    page.AfterCommits(1, (fun () -> page.GoOffline()))
    page.Event("createGroup")
    Assert.Equal("saved", page.Text "persistenceState")
    Assert.Equal("SIGNAL.GROUP.CREATED_NOT_READ", page.Text "noticeCode")
    Assert.Contains("was created, but it could not be read back", page.Text "notice")
    Assert.Contains("Refresh to open it.", page.Text "notice")

[<Fact>]
let ``a dataset lifecycle change whose reread fails is still confirmed first, and focus moves to the lifecycle heading`` () =
    let browser, _, page = signedIn ()
    page.Event("navigate", key = "storage")
    page.AfterCommits(1, (fun () -> page.GoOffline()))
    page.Event("datasetTransition", value = "state-archived")
    Assert.Equal("saved", page.Text "persistenceState")
    Assert.Contains("dataset's new state is saved", page.Text "persistenceText")
    // The target button no longer applies: focus goes to the dataset's
    // lifecycle heading beside it (limen.focus), not the status region.
    let focusRequests = browser.Requests |> Seq.filter (fun r -> r.Contains "\"capability\":\"limen.focus\"") |> List.ofSeq
    Assert.Single(focusRequests) |> ignore
    Assert.Contains("\"name\":\"dataset-lifecycle\"", focusRequests.Head)

[<Fact>]
let ``the engine selects the focus pack at the installed contract's fingerprint`` () =
    let generated = Echelon.Signal.Tests.Support.readRepoFile "node_modules/@echelon-foundry/limen/dist/capabilities/focus/generated/focus.js"
    Assert.Contains($"\"{Echelon.Signal.Application.AdminProtocol.focus.Fingerprint}\"", generated)
    Assert.Contains("focusCapability()", Echelon.Signal.Tests.Support.readRepoFile "web/admin/admin.js")

[<Fact>]
let ``a report definition whose reread fails is still confirmed first`` () =
    let _, _, page = signedIn ()
    Echelon.Signal.Tests.AdminLinkTests.changed page "#/reports"
    page.Event("newReportId", value = "team-summary")
    page.Event("newReportDefinition")
    page.AfterCommits(1, (fun () -> page.GoOffline()))
    page.Event("saveReportDefinition")
    Assert.Equal("saved", page.Text "persistenceState")
    Assert.Contains("report definition is saved", page.Text "persistenceText")

[<Fact>]
let ``a snapshot whose reread fails is still confirmed first`` () =
    let _, _, page = withNewGroup ()
    page.Event("createGroup")
    let key = page.Text "groupKey"
    Echelon.Signal.Tests.AdminLinkTests.changed page $"#/groups/{key}/report"
    Assert.True(page.Flag "canTakeSnapshot", page.ViewText)
    // Its definition, then the snapshot: offline once the snapshot landed.
    page.AfterCommits(2, (fun () -> page.GoOffline()))
    page.Event("takeSnapshot")
    Assert.Equal("saved", page.Text "persistenceState")
    Assert.Contains("snapshot is saved", page.Text "persistenceText")

[<Fact>]
let ``after a retire, a draft opened next shows no other view's outcome beside its actions (signal#137 review)`` () =
    let _, _, page = signedIn ()
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
    page.Event("transition", value = "finalize")
    page.Event("retireSources")
    page.Event("confirmRetire")
    // The retire is the latest change, and the shell says so.
    match page.Model.Value.Activity.LastWrite with
    | Some(Operation.RetireSources, _) -> ()
    | other -> failwith $"%A{other}"
    Assert.True(page.Flag "hasPersistence", page.ViewText)
    Assert.False(page.Flag "draftOutcome")

    // A draft opened next: its outcome copy shows none of it.
    Echelon.Signal.Tests.AdminLinkTests.changed page "#/assessments"
    page.Event("newDraftSurvey", value = "PULSE")
    page.Event("newDraftTitle", value = "Team pulse")
    page.Event("startDraft")
    Assert.True(page.Flag "hasDraft", page.ViewText)
    Assert.False(page.Flag "draftOutcome")
    Assert.False(page.Flag "assessmentsOutcome")
    Assert.False(page.Flag "reportOutcome")

    // Its own change shows there.
    page.Event("saveDraft")
    Assert.True(page.Flag "draftOutcome")
    Assert.False(page.Flag "assessmentsOutcome")

[<Fact>]
let ``the draft's warnings are named beside their acknowledgement, from its own findings`` () =
    let page = onStoredDraft ()
    page.Event("newSectionTitle", value = "Delivery")
    page.Event("addSection")
    page.Event("newQuestionPrompt", value = "We ship every week.")
    page.Event("addQuestion", key = "S1")
    Assert.True(page.Flag "draftHasWarnings", page.ViewText)
    let warnings =
        page.Items "draftFindings"
        |> List.filter (fun f -> f["severity"].GetValue<string>() = "Warning")
        |> List.map (fun f -> f["code"].GetValue<string>())
    let expected =
        match warnings with
        | [ code ] -> $"1 warning: {code}"
        | codes ->
            let listed = String.concat ", " codes
            $"{codes.Length} warnings: {listed}"
    Assert.NotEmpty(warnings)
    Assert.Equal(expected, page.Text "draftWarningSummary")
