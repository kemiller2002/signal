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
