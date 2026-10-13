/// The console's dataset lifecycle and retention screens (ADM-045, ADM-064,
/// WI-0077): which lifecycle states the dataset may move to and what each
/// means, retiring a finalized group's accepted results after the person
/// confirms what that costs, and the sentences that say what was done,
/// never more than the store proved (DF-SIGNAL-2026-0021).
///
/// Pure.
module Echelon.Signal.Admin.RetentionScreen

open Echelon.Signal.Admin.AdminTypes
open Echelon.Signal.Admin.AdminApp

/// What a dataset lifecycle state means, in one sentence.
let stateLabel (state: Retention.State) =
    match state with
    | Retention.Active -> "Active: groups take imports and changes."
    | Retention.Archived -> "Archived: read-only; it can be made active again."
    | Retention.RetainedForAudit -> "Retained for audit: read-only, kept for audit and never due for removal."
    | Retention.Superseded -> "Superseded: read-only; another dataset replaces it."
    | Retention.Retired -> "Retired: read-only; no longer in use."
    | Retention.DeletedFromActiveState -> "Deleted from active state: Signal no longer uses it; its records stay in the repository."
    | Retention.ProviderDeletionRequested -> "Provider deletion requested: removal from the repository was asked for."
    | Retention.ProviderDeletionVerified -> "Provider deletion verified: the provider proved the data erased, history included."
    | Retention.ProviderDeletionNotProvable -> "Provider deletion not provable: the provider keeps history, so erasure cannot be proved."

/// The short name of a target, for its button.
let targetLabel (state: Retention.State) =
    match state with
    | Retention.Active -> "Make active"
    | Retention.Archived -> "Archive"
    | Retention.RetainedForAudit -> "Retain for audit"
    | Retention.Superseded -> "Mark superseded"
    | Retention.Retired -> "Retire"
    | Retention.DeletedFromActiveState -> "Delete from active state"
    | Retention.ProviderDeletionRequested -> "Record that provider deletion was requested"
    | Retention.ProviderDeletionVerified -> "Record verified provider deletion"
    | Retention.ProviderDeletionNotProvable -> "Record that provider deletion is not provable"

/// The states the console offers from `state`: every legal transition except
/// verified provider deletion. GitHub keeps history, so a person can never
/// establish that by hand (ADM-045); only a provider's proof may.
let offered (state: Retention.State) =
    Retention.targets state |> List.filter ((<>) Retention.ProviderDeletionVerified)

/// A state by its stable id (`Retention.stateId`).
let ofId (id: string) =
    [ Retention.Active; Retention.Archived; Retention.RetainedForAudit; Retention.Superseded; Retention.Retired
      Retention.DeletedFromActiveState; Retention.ProviderDeletionRequested; Retention.ProviderDeletionVerified; Retention.ProviderDeletionNotProvable ]
    |> List.tryFind (fun s -> Retention.stateId s = id)

/// What reconstruction remains, by its id (ADM-064).
let reconstructionLabel (id: string) =
    match id with
    | "state-full-reconstruction" -> "Everything can still be rebuilt from the accepted results."
    | "state-aggregate-only" -> "Only the group's aggregate remains: individual results cannot be rebuilt."
    | "state-historical-snapshots-only" -> "Only report snapshots already taken remain: nothing can be recalculated."
    | "state-not-reproducible" -> "Nothing remains to rebuild the group's results from."
    | other -> other

/// What retiring a group's results did, as the claim allows it to be said.
let claimText (claim: Retention.Claim) (removed: int) =
    match claim with
    | Retention.PermanentlyErased -> $"{removed} accepted result(s) were erased, the provider's history included."
    | Retention.RemovedFromCurrentTree ->
        $"{removed} accepted result(s) were removed from Signal and erased from the repository's current files. The repository's history still holds them."
    | Retention.RemovedFromApplicationState ->
        $"{removed} accepted result(s) were removed from Signal. They remain in the repository and its history."
    | Retention.Present -> "Nothing was removed."

/// The consequence the person confirms before retiring.
let retireWarning =
    "Retiring removes this group's accepted results from Signal, and erases them from the repository's current files where the storage allows it. "
    + "Its scores can no longer be recalculated or verified from them, and this cannot be undone. The repository's history keeps the content."

let events = set [ "datasetTransition"; "retireSources"; "confirmRetire"; "cancelRetire" ]

/// What a retention event does: the model and the effects it asks for, when
/// `can` grants the capability it needs.
let update (can: AdminState.AdminCapability -> bool) (selected: GroupSummary option) (name: string) (value: string) (model: Model) : Model * Effect list =
    let refuse code message =
        { model with Notice = Some { Code = code; Message = message; Infrastructure = false } }, []

    let busy effects = { model with Busy = true; Notice = None; RetireConfirm = None }, effects

    match name, model.Dataset, selected with
    | "datasetTransition", Some dataset, _ ->
        match ofId value with
        | Some target when not (can AdminState.CanManageLifecycle) ->
            refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" $"Moving the dataset to '{Retention.stateId target}' needs ManageStorage and a credential that can write."
        | Some target when offered dataset.Lifecycle |> List.contains target -> busy [ TransitionDataset target ]
        | _ -> refuse "SIGNAL.LIFECYCLE.ILLEGAL_TRANSITION" $"The dataset cannot move from '{Retention.stateId dataset.Lifecycle}' to '{value}' here."
    | "retireSources", _, Some group when group.SourcesRetired.IsSome -> refuse "SIGNAL.RETENTION.ALREADY_RETIRED" "This group's accepted results were already retired."
    | "retireSources", _, Some group when can AdminState.CanRetireSources -> { model with RetireConfirm = Some group.Key; Notice = None }, []
    | "confirmRetire", _, Some group when model.RetireConfirm = Some group.Key && can AdminState.CanRetireSources -> busy [ RetireSources group.Key ]
    | ("retireSources" | "confirmRetire"), _, Some _ ->
        refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "Retiring accepted results needs ManageStorage, a writable store and a finalized or sealed group."
    | "cancelRetire", _, _ -> { model with RetireConfirm = None }, []
    | _ -> model, []
