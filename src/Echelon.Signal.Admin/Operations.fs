/// Per-action pending state (SIG-VIS-PENDING-001, design addendum item 7):
/// which operation the page started is still running, and what the store
/// last said about a change. It replaces the page-wide `Busy` flag, so the
/// one control that started an operation can say so ("Saving draft") while
/// the others stay unavailable.
///
/// **The guard (one operation at a time).** The page never runs two store
/// operations at once, writes or reads, exactly as the page-wide flag did:
///
/// - Their answers are not correlated. A store answer (`Failed`, `Noted`,
///   `GroupUpdated`, `DatasetOpened`…) names no operation, so with two in
///   flight the first answer could not be told apart and one operation would
///   be reported as the other's outcome.
/// - They share state. Every write starts from the dataset and group handles
///   the wire last read (`AdminWire.State.Opened`, `Groups`) and the catalog
///   revisions it holds; a read that lands mid-write replaces them, and two
///   writes from one handle race on the same revision, where the second is
///   refused as a conflict or, for an unknown outcome, must be reconciled
///   first (ARX-015: never a blind retry).
///
/// So `start` refuses while anything runs: a second activation of the same
/// button, or any other operation, is ignored and never queued. Purely local
/// actions (editing a draft, navigation, the preview, test links) are not
/// operations and stay available.
///
/// Pure.
module Echelon.Signal.Admin.Operations

/// An operation the page started, by the control that started it. A row's
/// control carries its row's key, so only that row's button shows it.
[<RequireQualifiedAccess>]
type Operation =
    | OpenDataset of datasetId: string
    | Refresh
    | OpenGroup of key: string
    | SaveDraft
    | PublishDraft
    | RequestReview
    | ApproveReview
    | DeleteDraft
    | HideVersion of survey: string * version: string
    | CreateGroup
    | IssueInvitations
    | ImportArtifacts
    /// close, reopen, finalize or seal.
    | TransitionGroup of transition: string
    | Admit
    | RemoveAdministrator of principalId: string
    | RebuildIndex
    | TakeSnapshot
    | ExportSnapshot of snapshotId: string * file: string
    | SaveReportDefinition
    | RetireSources
    | TransitionDataset of target: Retention.State

/// What an operation does to the store, which decides what its outcome may claim.
type Kind =
    /// Changes the store: its outcome is a persistence claim.
    | Write
    /// Reads the store, or prepares something without storing it
    /// (invitation links, an export): its outcome claims nothing about persistence.
    | Read

let kind (operation: Operation) =
    match operation with
    | Operation.OpenDataset _
    | Operation.Refresh
    | Operation.OpenGroup _
    | Operation.ExportSnapshot _
    // Invitation links are made from fresh random ids; nothing is stored (VER-003).
    | Operation.IssueInvitations -> Read
    | Operation.SaveDraft
    | Operation.PublishDraft
    | Operation.RequestReview
    | Operation.ApproveReview
    | Operation.DeleteDraft
    | Operation.HideVersion _
    | Operation.CreateGroup
    | Operation.ImportArtifacts
    | Operation.TransitionGroup _
    | Operation.Admit
    | Operation.RemoveAdministrator _
    | Operation.RebuildIndex
    | Operation.TakeSnapshot
    | Operation.SaveReportDefinition
    | Operation.RetireSources
    | Operation.TransitionDataset _ -> Write

/// What the store said about an operation.
type Outcome =
    /// The store confirmed it.
    | Confirmed
    /// Stored in part: some of it is still to do (an import that stopped).
    | Partial of remaining: int
    /// Not done: refused, or the store could not be reached
    /// (`ProviderContract.FailureMeaning.NothingWritten`); the notice says
    /// why, by this code.
    | NotDone of code: string
    /// The store could not say whether it was done. It is reconciled before
    /// anything is sent again, never resent blindly (ARX-015, ADM-077).
    | Unknown

/// The stable code of an unknown outcome (`ProviderContract.meaning`).
[<Literal>]
let OutcomeUnknownCode = "SIGNAL.STORAGE.OUTCOME_UNKNOWN"

/// The page's operation state.
type Activity =
    { /// The operation in flight, if any.
      Running: Operation option
      /// The latest change's outcome, kept until the next change starts.
      LastWrite: (Operation * Outcome) option
      /// Another tab changed the dataset while an operation ran: reload it
      /// once that operation is answered, not in the middle of it.
      ReloadOwed: bool }

let idle =
    { Running = None
      LastWrite = None
      ReloadOwed = false }

let isRunning (activity: Activity) = activity.Running.IsSome

let isRunningOp (operation: Operation) (activity: Activity) = activity.Running = Some operation

/// Starts an operation, or `None` while one runs: the second activation is
/// ignored, never queued (the guard above). A change clears the previous
/// change's outcome; a read keeps it.
let start (operation: Operation) (activity: Activity) : Activity option =
    match activity.Running with
    | Some _ -> None
    | None ->
        Some
            { activity with
                Running = Some operation
                LastWrite = (if kind operation = Write then None else activity.LastWrite) }

/// The operation in flight was answered. Nothing runs afterwards; a change
/// keeps its outcome for the persistence indicator. An answer with nothing
/// in flight changes nothing.
let settle (outcome: Outcome) (activity: Activity) : Activity =
    match activity.Running with
    | None -> activity
    | Some operation ->
        { activity with
            Running = None
            LastWrite = (if kind operation = Write then Some(operation, outcome) else activity.LastWrite) }

/// An import's outcome from the batch the store reports back: unreconciled
/// work is unknown, unprocessed artifacts leave it partial.
let imported (unreconciled: int) (batch: Intake.BatchSummary option) =
    match batch with
    | _ when unreconciled > 0 -> Unknown
    | Some b when b.Status = Intake.NeedsReconciliation || b.ReconciliationRequiredCount > 0 -> Unknown
    | Some b when b.RemainingCount > 0 -> Partial b.RemainingCount
    | _ -> Confirmed

// ---- Wording -----------------------------------------------------------------------------------

let private datasetTargetParticiple (target: Retention.State) =
    match target with
    | Retention.Active -> "Making active"
    | Retention.Archived -> "Archiving"
    | Retention.RetainedForAudit -> "Retaining for audit"
    | Retention.Superseded -> "Marking superseded"
    | Retention.Retired -> "Retiring"
    | Retention.DeletedFromActiveState -> "Deleting from active state"
    | Retention.ProviderDeletionRequested
    | Retention.ProviderDeletionVerified
    | Retention.ProviderDeletionNotProvable -> "Recording"

/// The control's label while its operation runs: the present participle of
/// the control's own words ("Save draft" → "Saving draft").
let pendingLabel (operation: Operation) =
    match operation with
    | Operation.OpenDataset _ -> "Opening"
    | Operation.Refresh -> "Refreshing"
    | Operation.OpenGroup _ -> "Opening the group"
    | Operation.SaveDraft -> "Saving draft"
    | Operation.PublishDraft -> "Publishing"
    | Operation.RequestReview -> "Submitting for review"
    | Operation.ApproveReview -> "Approving"
    | Operation.DeleteDraft -> "Deleting draft"
    | Operation.HideVersion _ -> "Hiding from new groups"
    | Operation.CreateGroup -> "Creating group"
    | Operation.IssueInvitations -> "Creating links"
    | Operation.ImportArtifacts -> "Importing"
    | Operation.TransitionGroup "close" -> "Closing"
    | Operation.TransitionGroup "reopen" -> "Reopening"
    | Operation.TransitionGroup "finalize" -> "Finalizing"
    | Operation.TransitionGroup _ -> "Sealing"
    | Operation.Admit -> "Admitting"
    | Operation.RemoveAdministrator _ -> "Removing"
    | Operation.RebuildIndex -> "Rebuilding the contribution index"
    | Operation.TakeSnapshot -> "Taking a snapshot"
    | Operation.ExportSnapshot _ -> "Exporting"
    | Operation.SaveReportDefinition -> "Saving definition"
    | Operation.RetireSources -> "Retiring accepted results"
    | Operation.TransitionDataset target -> datasetTargetParticiple target

/// What a change is, as a subject: "the draft", "the group".
let private subject (operation: Operation) =
    match operation with
    | Operation.SaveDraft -> "The draft"
    | Operation.PublishDraft -> "The new version"
    | Operation.RequestReview -> "The review request"
    | Operation.ApproveReview -> "The review"
    | Operation.DeleteDraft -> "The draft's deletion"
    | Operation.HideVersion _ -> "Hiding the version"
    | Operation.CreateGroup -> "The new group"
    | Operation.ImportArtifacts -> "The import"
    | Operation.TransitionGroup _ -> "The group's new state"
    | Operation.Admit -> "The new administrator"
    | Operation.RemoveAdministrator _ -> "The removal"
    | Operation.RebuildIndex -> "The rebuilt index"
    | Operation.TakeSnapshot -> "The snapshot"
    | Operation.SaveReportDefinition -> "The report definition"
    | Operation.RetireSources -> "The retirement"
    | Operation.TransitionDataset _ -> "The dataset's new state"
    | Operation.OpenDataset _
    | Operation.Refresh
    | Operation.OpenGroup _
    | Operation.ExportSnapshot _
    | Operation.IssueInvitations -> "It"

/// The persistence indicator's state: pending, saved, partial, failed or unknown.
let persistenceState (activity: Activity) =
    match activity.Running, activity.LastWrite with
    | Some operation, _ when kind operation = Write -> "pending"
    | _, Some(_, Confirmed) -> "saved"
    | _, Some(_, Partial _) -> "partial"
    | _, Some(_, NotDone _) -> "failed"
    | _, Some(_, Unknown) -> "unknown"
    | _, None -> ""

/// What the persistence indicator says. It never says "saved" before the
/// store confirmed, and never turns an unknown outcome into a failure.
let persistenceText (activity: Activity) =
    match activity.Running, activity.LastWrite with
    | Some operation, _ when kind operation = Write -> $"{pendingLabel operation}: not saved until the store confirms."
    | _, Some(operation, Confirmed) -> $"{subject operation} is saved: the store confirmed it."
    | _, Some(operation, Partial remaining) -> $"{subject operation} is saved in part: {remaining} item(s) are still to do. Import them again to continue."
    | _, Some(operation, NotDone _) -> $"{subject operation} was not saved; the notice says why."
    | _, Some(operation, Unknown) ->
        $"Whether {(subject operation).ToLowerInvariant()} was saved is not known yet. It is checked with the store before anything is sent again; refresh to see what the store holds."
    | _, None -> ""
