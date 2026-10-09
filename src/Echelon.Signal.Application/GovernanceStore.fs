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

// ---- Retiring a group's sources (ADM-045, ADM-064) ----------------------------------------------

/// Why a group's accepted results are removed.
type Basis =
    /// A deletion request or policy decision, whatever the retention period.
    | DeletionRequest
    /// The retention period for accepted results has passed since finalization.
    | RetentionExpired of Retention.Policy

/// What retiring a group's sources did.
type Retired =
    { /// What may be claimed about the removed records; never more than the provider can prove.
      Claim: Retention.Claim
      Removed: int
      Limitation: Retention.Limitation option }

type RetireFailure =
    | GroupNotFinalized of status: string
    | RetentionNotDue
    | Unrecorded of Problem list
    | RetireNotStored of GroupFailure

let private groupId (group: OpenedGroup) = string group.Config.Group

/// When the group was finalized, from its audit records.
let private finalizedAt (records: GovernanceRecord.StoredAudit list) (group: string) =
    records
    |> List.filter (fun r ->
        r.Record.Event = Audit.GroupConfigurationVersioned
        && r.Record.Ids |> List.contains ("group", group)
        && r.Record.Reasons |> List.contains "LIFECYCLE-FINALIZE")
    |> List.choose _.Record.ClockEvidence
    |> List.tryLast

/// The group's accepted contributions as stored now (an opened group's own
/// map may predate its latest import).
let private storedContributions now (group: OpenedGroup) =
    asyncResult {
        let folder = ResultRecord.groupFolder group.Config.Group
        let! listing, objects = readTree now group.Dataset.Provider group.Dataset.Namespace folder
        let loaded = Loading.load group.Dataset.Verified (ResultRecord.reader group.Definition.Template) folder listing objects
        return loaded.Records |> Map.toList |> List.map snd |> List.filter (fun stored -> stored.Value.Contribution.Group = group.Config.Group)
    }

/// What is held for a group: its accepted results, a stored report state,
/// issued snapshots and the audit trail.
let private holdings now (group: OpenedGroup) (contributions: Loading.Stored<ResultRecord.StoredContribution> list) =
    async {
        let! reports = ReportStore.load now group.Dataset

        let snapshots =
            match reports with
            | Ok stored -> stored.Snapshots |> List.exists (fun s -> s.GroupId = groupId group)
            | Error _ -> false

        let! storedState =
            match ReportState.encodeRecord (reportState group) with
            | Ok(target, _) -> group.Dataset.Provider.Read group.Dataset.Namespace target
            | Error _ -> async.Return(Ok ReadOutcome.Absent)

        return
            set
                [ if not contributions.IsEmpty then Retention.AcceptedResult
                  match storedState with
                  | Ok(ReadOutcome.Found _) -> Retention.ReportState
                  | _ -> ()
                  if snapshots then Retention.ReportSnapshot
                  Retention.AuditRecord ]
    }

/// The removal itself, in one commit: the group's retirement record, so its
/// contributions leave Signal's current state, and the limitation's audit
/// record. Arca never deletes an immutable record, so the contributions stay
/// in the repository tree and its history; the claim says exactly that.
let private remove (actor: Store.Actor) (now: DateTimeOffset) (basis: Basis) (group: OpenedGroup) (contributions: 'a list) held =
    let opened = group.Dataset

    let claim =
        Retention.claim
            { FromApplicationState = true
              FromRepositoryTree = false
              ProviderHistory = None }

    async {
        match Retention.delete (groupId group) (Some now) held (set [ Retention.AcceptedResult ]) with
        | Error problems -> return Error(Unrecorded [ UnstorableRecord("audit", $"%A{problems}") ])
        | Ok(_, None) -> return Ok { Claim = claim; Removed = 0; Limitation = None }
        | Ok(_, Some limited) ->
            let basisCode = match basis with DeletionRequest -> "DELETION-REQUEST" | RetentionExpired _ -> "RETENTION-EXPIRED"
            let evidence = { limited.Evidence with Reasons = limited.Evidence.Reasons @ [ basisCode; "TREE-REMOVAL-UNAVAILABLE" ] }

            let marker: GovernanceRecord.StoredRetirement =
                { DatasetId = opened.DatasetId
                  Group = groupId group
                  Remaining = Retention.reconstructionId limited.After
                  Removed = contributions.Length }

            match
                (match GovernanceRecord.retirementPath marker.Group, GovernanceRecord.encodeRetirement marker with
                 | Ok path, Ok content -> Ok [ Change.Create(path, content) ]
                 | Error p, _
                 | _, Error p -> Error [ p ])
                |> Result.bind (Storage.operation opened.Namespace (actor.NewContext()) $"retire accepted results ({basisCode.ToLowerInvariant()})")
                |> Result.bind (GovernanceRecord.audited opened.DatasetId [ evidence ])
            with
            | Error problems -> return Error(Unrecorded problems)
            | Ok operation ->
                match! opened.Provider.Commit operation with
                | Ok _ -> return Ok { Claim = claim; Removed = contributions.Length; Limitation = Some { limited with Evidence = evidence } }
                | Error failure -> return Error(RetireNotStored(failed now failure))
    }

/// Retires a finalized group's accepted results from Signal's current state,
/// with the PII-free evidence of what that does to reproducibility, in one
/// commit. The claim is "removed from application state": never "removed
/// from the tree" or "permanently erased", which neither Arca nor GitHub can
/// establish here (ADM-045).
let retireSources (actor: Store.Actor) (now: DateTimeOffset) (basis: Basis) (group: OpenedGroup) : Async<Result<Retired, RetireFailure>> =
    let opened = group.Dataset

    let isDue (records: GovernanceRecord.StoredAudit list) =
        match basis with
        | DeletionRequest -> true
        | RetentionExpired policy ->
            finalizedAt records (groupId group)
            |> Option.exists (fun at -> Retention.due policy Retention.AcceptedResult Retention.Retired (now - at))

    async {
        match permitted opened actor Access.ManageStorage, group.Lifecycle.Status with
        | Error failure, _ -> return Error(RetireNotStored failure)
        | Ok _, (GroupLifecycle.Collecting | GroupLifecycle.ClosedIncomplete | GroupLifecycle.Superseded _ as status) ->
            return Error(GroupNotFinalized(GroupLifecycle.statusName status))
        | Ok _, _ ->
            let! records = audit now opened
            let! contributions = storedContributions now group

            match records, contributions with
            | Error failure, _
            | _, Error failure -> return Error(RetireNotStored failure)
            | Ok(records, _), _ when not (isDue records) -> return Error RetentionNotDue
            | Ok _, Ok contributions ->
                let! held = holdings now group contributions
                return! remove actor now basis group contributions held
    }

/// What a group can still be reproduced from, by its recorded limitations
/// (ADM-064): never claimed from what is absent.
let reconstructionOf (records: GovernanceRecord.StoredAudit list) (group: string) =
    records
    |> List.filter (fun r -> r.Record.Event = Audit.ReproducibilityLimited && r.Record.Ids |> List.contains ("group", group))
    |> List.tryLast
    |> Option.bind _.Record.After
    |> Option.defaultValue (Retention.reconstructionId Retention.FullReconstruction)
