/// The dataset's audit trail and lifecycle through Arca (WI-0076, ADM-030,
/// ADM-045).
///
/// - **Audit** records are written by every store operation in the commit of
///   the change they describe (`GovernanceRecord.audited`); `audit` reads
///   them back, re-validated as PII-free.
/// - **Lifecycle**: `transition` needs `ManageStorage`, moves the dataset
///   between ADM-045 states and writes the state and its audit record in one
///   commit. Only an active dataset grants other changes (`Store.openDataset`),
///   so an archived or retired dataset is read-only until it is made active
///   again, which this operation alone may do.
module Echelon.Signal.Application.GovernanceStore

open System
open Arca
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Application.Flow
open Echelon.Signal.Application.GroupStore

/// Every audit record in the dataset, oldest clock first (records without a clock last).
let audit (now: DateTimeOffset) (opened: Store.Opened) : AsyncResult<GovernanceRecord.StoredAudit list * Problem list, GroupFailure> =
    asyncResult {
        let folder = GovernanceRecord.folderOf GovernanceRecord.auditType
        let! listing, objects = readTree now opened.Provider opened.Namespace folder
        let loaded = Loading.load opened.Verified GovernanceRecord.auditReader folder listing objects

        let records =
            loaded.Records
            |> Map.toList
            |> List.map (fun (_, r) -> r.Value)
            |> List.sortBy (fun r -> r.Record.ClockEvidence |> Option.map (fun c -> c.UtcTicks) |> Option.defaultValue Int64.MaxValue)

        return records, loaded.Problems
    }

/// Why a lifecycle transition did not happen.
type TransitionFailure =
    | TransitionRefused of Retention.TransitionProblem
    | NotStored of GroupFailure

/// Moves the dataset to `target`; returns its new state.
let transition (actor: Store.Actor) (now: DateTimeOffset) (opened: Store.Opened) (target: Retention.State) : Async<Result<Retention.State, TransitionFailure>> =
    async {
        match Access.authorize opened.Roster.Roster opened.DatasetId actor.Principal.PrincipalId Access.ManageStorage, opened.LifecycleGrant with
        | Error refusal, _ -> return Error(NotStored(NotPermitted refusal))
        | Ok(), None -> return Error(NotStored(ReadOnly opened.ReadOnlyReasons))
        | Ok(), Some _ ->
            match Retention.transition opened.DatasetId (Some now) opened.Lifecycle target with
            | Error problem -> return Error(TransitionRefused problem)
            | Ok(state, record) ->
                let operation =
                    match GovernanceRecord.lifecyclePath (), GovernanceRecord.encodeLifecycle opened.DatasetId state with
                    | Ok path, Ok content ->
                        let change =
                            match opened.LifecycleRevision with
                            | Some revision -> Change.Update(path, content, revision)
                            | None -> Change.Create(path, content)

                        Storage.operation opened.Namespace (actor.NewContext()) $"dataset lifecycle {Retention.stateId state}" [ change ]
                        |> Result.bind (GovernanceRecord.audited opened.DatasetId [ record ])
                    | Error p, _
                    | _, Error p -> Error [ p ]

                match operation with
                | Error problems -> return Error(NotStored(Unusable problems))
                | Ok operation ->
                    match! opened.Provider.Commit operation with
                    | Ok _ -> return Ok state
                    | Error failure -> return Error(NotStored(failed now failure))
    }
