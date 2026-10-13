/// The console's dataset lifecycle and retention work (WI-0077, ADM-045):
/// moving the dataset to another lifecycle state and retiring a finalized
/// group's accepted results, as async work the wire starts. Each tells other
/// tabs storage changed and rereads what it changed, so the page shows what
/// is stored; the notice says only what the store proved
/// (DF-SIGNAL-2026-0021).
module Echelon.Signal.Application.RetentionWork

open System
open Echelon.Signal.Admin
open Echelon.Signal.Application.AdminWork

let private engine msg = ToEngine msg

let private announce (env: Env) (datasetId: string) =
    env.Bridge.Call(Bridge.Announce(Credential.announcement datasetId Credential.StorageChangedElsewhere))

/// Moves the dataset, then reopens it (its grants follow its new state).
let transition (env: Env) (backend: Store.Backend) (config: Deployment.DeploymentConfig) (actor: Store.Actor) (opened: Store.Opened) (target: Retention.State) (now: DateTimeOffset) =
    async {
        match! GovernanceStore.transition actor now opened target with
        | Error(GovernanceStore.TransitionRefused(Retention.IllegalTransition(from, target))) ->
            return
                [ engine (
                      AdminApp.Failed(
                          notice "SIGNAL.LIFECYCLE.ILLEGAL_TRANSITION" $"The dataset cannot move from '{Retention.stateId from}' to '{Retention.stateId target}'." false
                      )
                  ) ]
        | Error(GovernanceStore.TransitionRefused(Retention.NotAudited _)) ->
            return [ engine (AdminApp.Failed(notice "SIGNAL.AUDIT.UNRECORDABLE" "The change could not be audited, so it was not made." true)) ]
        | Error(GovernanceStore.NotStored failure) -> return [ engine (groupFailure failure) ]
        | Ok state ->
            let! _ = announce env opened.DatasetId
            let! reopened = AdminGroupWork.openDataset env backend config actor (Some opened.RepositoryId) opened.DatasetId now

            return
                reopened
                @ [ engine (AdminApp.Noted(notice "SIGNAL.LIFECYCLE.CHANGED" $"The dataset is now {RetentionScreen.stateLabel state}" false)) ]
    }

/// Retires a finalized group's accepted results on a deletion request, then
/// rereads the group.
let retire (env: Env) (catalog: TemplateCatalog.Loaded) (actor: Store.Actor) (group: GroupStore.OpenedGroup) (now: DateTimeOffset) =
    async {
        match! GovernanceStore.retireSources actor now GovernanceStore.DeletionRequest group with
        | Error(GovernanceStore.GroupNotFinalized status) ->
            return [ engine (AdminApp.Failed(notice "SIGNAL.RETENTION.NOT_FINALIZED" $"Only a finalized or sealed group's results can be retired; this group is {status}." false)) ]
        | Error GovernanceStore.RetentionNotDue ->
            return [ engine (AdminApp.Failed(notice "SIGNAL.RETENTION.NOT_DUE" "The retention period has not passed." false)) ]
        | Error(GovernanceStore.Unrecorded problems) ->
            let first = problems |> List.tryHead
            return [ engine (AdminApp.Failed(notice (first |> Option.map Problems.code |> Option.defaultValue "SIGNAL.AUDIT.UNRECORDABLE") "The retirement could not be recorded, so nothing was removed." true)) ]
        | Error(GovernanceStore.RetireNotStored failure) -> return [ engine (groupFailure failure) ]
        | Ok retired ->
            let! _ = announce env group.Dataset.DatasetId
            let! fresh = GroupStore.openGroup (TemplateCatalog.resolver catalog) group.Config.Group now group.Dataset

            let tree =
                match retired.Tree with
                | GovernanceStore.ErasureFailed reason -> $" Erasing them from the repository's current files failed ({reason}); retire again later is not possible, so ask the repository's owner."
                | GovernanceStore.ErasureOutcomeUnknown -> " Whether they were erased from the repository's current files is not known yet; reopen the dataset to see."
                | GovernanceStore.ErasureNotOffered
                | GovernanceStore.ErasedFromTree _ -> ""

            let said = engine (AdminApp.Noted(notice "SIGNAL.RETENTION.RETIRED" (RetentionScreen.claimText retired.Claim retired.Removed + tree) false))

            match fresh with
            | Ok fresh -> return [ GroupReady(fresh, None, 0); said ]
            | Error failure -> return [ said; engine (groupFailure failure) ]
    }
