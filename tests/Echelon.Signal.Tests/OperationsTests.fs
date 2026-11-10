/// Per-action pending state (SIG-VIS-PENDING-001): an operation starts, is
/// answered (confirmed, refused, unknown), the guard keeps one in flight,
/// and a second activation is ignored rather than queued. The pure
/// transitions first, then the page's update from a real signed-in model.
module Echelon.Signal.Tests.OperationsTests

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

// ---- The transitions ---------------------------------------------------------------------------

[<Fact>]
let ``an operation starts from idle and runs until it is answered`` () =
    let running = started Operation.SaveDraft idle
    Assert.Equal(Some Operation.SaveDraft, running.Running)
    Assert.True(isRunning running)
    Assert.True(isRunningOp Operation.SaveDraft running)
    Assert.False(isRunningOp Operation.PublishDraft running)
    // Pending is not saved: the indicator says so until the store confirms.
    Assert.Equal("pending", persistenceState running)
    Assert.Equal("Saving draft: not saved until the store confirms.", persistenceText running)

[<Fact>]
let ``a confirmed change is saved, and only then`` () =
    let saved = started Operation.SaveDraft idle |> settle Confirmed
    Assert.True((saved.Running).IsNone)
    Assert.Equal(Some(Operation.SaveDraft, Confirmed), saved.LastWrite)
    Assert.Equal("saved", persistenceState saved)
    Assert.Equal("The draft is saved: the store confirmed it.", persistenceText saved)

[<Fact>]
let ``a refused change is not saved, and says the notice explains`` () =
    let failed = started Operation.PublishDraft idle |> settle (NotDone "SIGNAL.AUTHORING.REFUSED")
    Assert.True((failed.Running).IsNone)
    Assert.Equal(Some(Operation.PublishDraft, NotDone "SIGNAL.AUTHORING.REFUSED"), failed.LastWrite)
    Assert.Equal("failed", persistenceState failed)
    Assert.DoesNotContain("is saved", persistenceText failed)

[<Fact>]
let ``an unknown outcome is unknown, never a failure or a success`` () =
    let unknown = started Operation.CreateGroup idle |> settle Unknown
    Assert.Equal("unknown", persistenceState unknown)
    Assert.Contains("is not known", persistenceText unknown)
    Assert.Contains("Refresh to check what the store holds; changes wait until then.", persistenceText unknown)
    Assert.DoesNotContain("not saved", persistenceText unknown)
    Assert.DoesNotContain("did not complete", persistenceText unknown)

[<Fact>]
let ``an import that stopped is saved in part, and one that needs reconciling is unknown`` () =
    let batch remaining reconcile status : Intake.BatchSummary =
        { BatchId = "b1"
          InputArtifactCount = 4
          ProcessedCount = 4 - remaining
          AcceptedCount = 4 - remaining - reconcile
          RejectedCount = 0
          DuplicateCount = 0
          ReconciliationRequiredCount = reconcile
          BlockedCount = 0
          RemainingCount = remaining
          Status = status }

    Assert.Equal(Confirmed, imported 0 (Some(batch 0 0 Intake.Complete)))
    Assert.Equal(Partial 2, imported 0 (Some(batch 2 0 Intake.Partial)))
    Assert.Equal(Unknown, imported 0 (Some(batch 0 1 Intake.NeedsReconciliation)))
    Assert.Equal(Unknown, imported 1 (Some(batch 0 0 Intake.Complete)))
    Assert.Equal(Confirmed, imported 0 None)

    let partial = started Operation.ImportArtifacts idle |> settle (Partial 2)
    Assert.Equal("partial", persistenceState partial)
    Assert.Contains("2 item(s) are still to do", persistenceText partial)

// SIG-VIS-PENDING-002 changed this: a dataset read that succeeds now clears
// the last change's outcome (it showed the store as it is, so the old line
// would be stale); one that fails keeps it, as before.
[<Fact>]
let ``a read claims nothing about persistence; a failed read keeps the last change's outcome and a successful dataset read clears it`` () =
    let saved = started Operation.SaveDraft idle |> settle Confirmed
    let refreshing = started Operation.Refresh saved
    Assert.Equal(Some(Operation.SaveDraft, Confirmed), refreshing.LastWrite)
    Assert.Equal("saved", persistenceState refreshing)
    let refreshed = settle (NotDone "SIGNAL.STORAGE.OFFLINE") refreshing
    Assert.Equal(Some(Operation.SaveDraft, Confirmed), refreshed.LastWrite)
    let reread = started Operation.Refresh refreshed |> settle Confirmed
    Assert.True(reread.LastWrite.IsNone)
    Assert.Equal("", persistenceText reread)
    // A read of one group or an export does not show the whole store.
    let opened = started (Operation.OpenGroup "g") saved |> settle Confirmed
    Assert.Equal(Some(Operation.SaveDraft, Confirmed), opened.LastWrite)

    for read in [ Operation.OpenDataset "ds"; Operation.Refresh; Operation.OpenGroup "g"; Operation.ExportSnapshot("s", "report.json"); Operation.IssueInvitations ] do
        Assert.Equal(Read, kind read)

[<Fact>]
let ``a new change clears the previous change's outcome`` () =
    let failed = started Operation.SaveDraft idle |> settle (NotDone "X")
    let again = started Operation.SaveDraft failed
    Assert.True((again.LastWrite).IsNone)
    Assert.Equal("pending", persistenceState again)

[<Fact>]
let ``the guard: nothing else starts while an operation runs, the same operation included`` () =
    let running = started Operation.SaveDraft idle
    // A rapid second activation is ignored, never queued.
    Assert.True((Operations.start Operation.SaveDraft running).IsNone)
    // No other write, and no read that would replace the handles a write uses.
    for other in [ Operation.PublishDraft; Operation.CreateGroup; Operation.Refresh; Operation.OpenDataset "ds"; Operation.TransitionDataset Retention.Archived ] do
        Assert.True((Operations.start other running).IsNone)
    // Answered, the next one starts.
    Assert.True((Operations.start Operation.PublishDraft (settle Confirmed running)).IsSome)

[<Fact>]
let ``an answer with nothing in flight changes nothing`` () =
    Assert.Equal(idle, settle Confirmed idle)
    let saved = started Operation.SaveDraft idle |> settle Confirmed
    Assert.Equal(saved, settle (NotDone "late") saved)

[<Fact>]
let ``every operation has a present-participle label`` () =
    let labels =
        [ Operation.SaveDraft, "Saving draft"
          Operation.PublishDraft, "Publishing"
          Operation.RequestReview, "Submitting for review"
          Operation.ImportArtifacts, "Importing"
          Operation.CreateGroup, "Creating group"
          Operation.IssueInvitations, "Creating links"
          Operation.TransitionGroup "close", "Closing"
          Operation.TransitionGroup "reopen", "Reopening"
          Operation.TransitionGroup "finalize", "Finalizing"
          Operation.TransitionGroup "seal", "Sealing"
          Operation.Refresh, "Refreshing"
          Operation.TakeSnapshot, "Taking a snapshot"
          Operation.TransitionDataset Retention.Archived, "Archiving" ]

    for operation, label in labels do
        Assert.Equal(label, pendingLabel operation)

// ---- The page's update, from a real signed-in model ----------------------------------------------

let private ui name key value = AdminApp.Ui(name, key, value)

let private value (name: string) (model: AdminApp.Model) =
    AdminView.project model |> List.pick (fun (key, v) -> if key = name then Some v else None)

/// A signed-in administrator on the Groups area, with nothing running.
let private onGroups () =
    let _, _, page = signedIn ()
    page.Event("navigate", key = "groups")
    page.Event("newGroupExpected", value = "2")
    page.Event("newGroupMinimum", value = "1")
    let model = page.Model.Value
    Assert.False(isRunning model.Activity)
    Assert.True((AdminApp.capabilities model).Contains AdminState.CanCreateGroup)
    page, model

[<Fact>]
let ``a write starts its operation, and the button that started it says so`` () =
    let _, model = onGroups ()
    let creating, effects = AdminUpdate.update (ui "createGroup" None "") model
    Assert.Equal<AdminApp.Effect list>([ AdminApp.CreateGroup(model.NewGroup.Template, model.NewGroup.Mode, 2, 1) ], effects)
    Assert.Equal(Some Operation.CreateGroup, creating.Activity.Running)

    // The triggering button: aria-busy, its present participle, never merely disabled.
    Assert.Equal(View.Value(View.Text "true"), value "createGroupBusy" creating)
    Assert.Equal(View.Value(View.Text "Creating group"), value "createGroupLabel" creating)
    Assert.Equal(View.Value(View.Flag false), value "createGroupDisabled" creating)
    // Every other operation's control is unavailable meanwhile (the guard)...
    for other in [ "refreshDisabled"; "importDisabled"; "inviteDisabled"; "admitDisabled"; "rebuildIndexDisabled"; "closeDisabled" ] do
        Assert.Equal(View.Value(View.Flag true), value other creating)
    // ...and is not busy, and keeps its own words.
    Assert.Equal(View.Value(View.Text "false"), value "refreshBusy" creating)
    Assert.Equal(View.Value(View.Text "Refresh"), value "refreshLabel" creating)
    // The page's activity region names it, and says it is not saved yet.
    Assert.Equal(View.Value(View.Flag true), value "operationRunning" creating)
    Assert.Equal(View.Value(View.Text "pending"), value "persistenceState" creating)
    Assert.Equal(View.Value(View.Flag false), value "hasPersistence" creating)

[<Fact>]
let ``a second click while a write is pending queues no duplicate write`` () =
    let _, model = onGroups ()
    let creating, first = AdminUpdate.update (ui "createGroup" None "") model
    Assert.Equal(1, first.Length)
    let again, second = AdminUpdate.update (ui "createGroup" None "") creating
    Assert.Empty(second)
    Assert.Equal(creating.Activity, again.Activity)

[<Fact>]
let ``the concurrent-write guard: no other operation starts while one is pending`` () =
    let _, model = onGroups ()
    let creating, _ = AdminUpdate.update (ui "createGroup" None "") model

    for name, key, v in [ "refresh", None, ""; "rebuildIndex", None, ""; "openDataset", Some "ds_engagement", ""; "admitAccount", None, "1234" ] do
        let next, effects = AdminUpdate.update (ui name key v) creating
        Assert.Empty(effects)
        Assert.Equal(Some Operation.CreateGroup, next.Activity.Running)

    // A form that would start a write is ignored too, and keeps what was typed.
    let typed, _ = AdminUpdate.update (ui "admitAccount" None "1234") creating
    let admitted, effects = AdminUpdate.update (ui "admit" None "") typed
    Assert.Empty(effects)
    Assert.Equal("1234", admitted.Admit.Account)

[<Fact>]
let ``the store's answer settles the operation: success, failure and unknown outcome`` () =
    let page, model = onGroups ()
    let creating, _ = AdminUpdate.update (ui "createGroup" None "") model

    // Success: the stored group comes back.
    page.Event("createGroup")
    let created = page.Model.Value.Dataset.Value.Groups |> List.head
    let saved, _ = AdminUpdate.update (AdminApp.GroupUpdated created) creating
    Assert.True((saved.Activity.Running).IsNone)
    Assert.Equal(Some(Operation.CreateGroup, Confirmed), saved.Activity.LastWrite)
    Assert.Equal(View.Value(View.Text "saved"), value "persistenceState" saved)
    Assert.Equal(View.Value(View.Flag true), value "hasPersistence" saved)

    // Failure: refused, with its code.
    let refusal: AdminApp.Notice = { Code = "SIGNAL.ADMIN.TEMPLATE_UNAVAILABLE"; Message = "Choose a template from the catalog."; Infrastructure = false }
    let failed, _ = AdminUpdate.update (AdminApp.Failed refusal) creating
    Assert.Equal(Some(Operation.CreateGroup, NotDone "SIGNAL.ADMIN.TEMPLATE_UNAVAILABLE"), failed.Activity.LastWrite)
    Assert.Equal(Some refusal, failed.Notice)
    Assert.Equal(View.Value(View.Text "failed"), value "persistenceState" failed)

    // Offline: not saved, and the store is degraded.
    let offline, _ = AdminUpdate.update (AdminApp.WentOffline "unreachable") creating
    Assert.Equal(Some(Operation.CreateGroup, NotDone "SIGNAL.STORAGE.OFFLINE"), offline.Activity.LastWrite)

    // Unknown: the provider could not say; reconciled, never called a failure.
    let unknownNotice: AdminApp.Notice = { Code = OutcomeUnknownCode; Message = "The provider did not say whether the change was saved."; Infrastructure = true }
    let unknown, _ = AdminUpdate.update (AdminApp.Failed unknownNotice) creating
    Assert.Equal(Some(Operation.CreateGroup, Unknown), unknown.Activity.LastWrite)
    Assert.Equal(View.Value(View.Text "unknown"), value "persistenceState" unknown)

    // After any answer the next operation may start.
    let _, effects = AdminUpdate.update (ui "refresh" None "") unknown
    Assert.Equal<AdminApp.Effect list>([ AdminApp.OpenDataset model.Dataset.Value.DatasetId ], effects)

[<Fact>]
let ``a file handed to the browser answers no operation`` () =
    let _, model = onGroups ()
    let creating, _ = AdminUpdate.update (ui "createGroup" None "") model
    let exported: AdminApp.Notice = { Code = "SIGNAL.REPORT.EXPORTED"; Message = "The export was handed to the browser to save."; Infrastructure = false }
    let still, _ = AdminUpdate.update (AdminApp.Noted exported) creating
    Assert.Equal(Some Operation.CreateGroup, still.Activity.Running)
    let copied, _ = AdminUpdate.update (AdminApp.LinkCopied true) creating
    Assert.Equal(Some Operation.CreateGroup, copied.Activity.Running)

[<Fact>]
let ``another tab's change during an operation is reloaded only on request, after it`` () =
    let _, model = onGroups ()
    let creating, _ = AdminUpdate.update (ui "createGroup" None "") model
    let noticed, effects = AdminUpdate.update (AdminApp.TabNoticed Credential.StorageChangedElsewhere) creating
    // No reload races the write.
    Assert.Empty(effects)
    Assert.Equal(Owed, noticed.Activity.Reload)
    Assert.Equal(View.Value(View.Flag false), value "reloadOwed" noticed)

    let refusal: AdminApp.Notice = { Code = "X"; Message = "x"; Infrastructure = false }
    let answered, _ = AdminUpdate.update (AdminApp.Failed refusal) noticed
    Assert.Equal(View.Value(View.Flag true), value "reloadOwed" answered)

    // Idle, the same notice reloads at once, as before.
    let _, reload = AdminUpdate.update (AdminApp.TabNoticed Credential.StorageChangedElsewhere) model
    Assert.Equal<AdminApp.Effect list>([ AdminApp.OpenDataset model.Dataset.Value.DatasetId ], reload)

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
let ``delete draft keeps its confirmation busy until answered, then closes it and moves focus to the status`` () =
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

    // Refused: closed, focus to the status, the draft still there.
    let refusal: AdminApp.Notice = { Code = "SIGNAL.AUTHORING.NO_DRAFT"; Message = "There is no stored draft of PULSE."; Infrastructure = false }
    let refused, effects = AdminUpdate.update (AdminApp.Failed refusal) deleting
    Assert.Equal(View.Value(View.Flag false), value "confirmingDeleteDraft" refused)
    Assert.Contains(AdminApp.Focus "activity", effects)
    // Deleted: closed, focus to the status, the editor closed. (The listing
    // drops the draft with the catalog reread that precedes this message.)
    let deleted, effects = AdminUpdate.update (AdminApp.DraftDeleted "PULSE") deleting
    Assert.Equal(View.Value(View.Flag false), value "confirmingDeleteDraft" deleted)
    Assert.True(deleted.Authoring.Editor.IsNone)
    Assert.Contains(AdminApp.Focus "activity", effects)

[<Fact>]
let ``retire keeps its confirmation busy until answered, then closes it and moves focus to the status`` () =
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
    Assert.Contains(AdminApp.Focus "activity", effects)

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
let ``a dataset lifecycle change whose reread fails is still confirmed first, and focus moves to the status`` () =
    let browser, _, page = signedIn ()
    page.Event("navigate", key = "storage")
    page.AfterCommits(1, (fun () -> page.GoOffline()))
    page.Event("datasetTransition", value = "state-archived")
    Assert.Equal("saved", page.Text "persistenceState")
    Assert.Contains("dataset's new state is saved", page.Text "persistenceText")
    // The target button no longer applies: focus goes to the activity status (limen.focus).
    let focusRequests = browser.Requests |> Seq.filter (fun r -> r.Contains "\"capability\":\"limen.focus\"") |> List.ofSeq
    Assert.Single(focusRequests) |> ignore
    Assert.Contains("\"name\":\"activity\"", focusRequests.Head)

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
