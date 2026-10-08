/// Durable groups and imports over Arca (WI-0041): ADM-008..011, ADM-060,
/// ADM-061, ADM-067, ARP-003..006, ARX-008, LURL-003.
///
/// - **Creating** a group is one commit of its configuration record and
///   needs `ManageGroups`.
/// - **Opening** a group reads its configuration, resolves its exact
///   template, and loads every accepted contribution as untrusted input;
///   the incremental accumulator is built from them.
/// - **Importing** a batch quarantines its artifacts against the accepted
///   identities, promotes the accepted ones, and commits them with the batch
///   record in chunks, each chunk one commit of `Create`s at the identities'
///   paths. Distinct identities never conflict, so concurrent imports
///   commute; the same identity imported concurrently is a conflict Signal
///   decides again after reloading (a duplicate, never an overwrite). An
///   unknown outcome is reconciled before anything is resent; if it stays
///   unknown the chunk is marked for reconciliation. Offline, the batch stops
///   where it is and resumes later from its stored record; nothing is queued.
/// - Import commits are made by Signal's import service actor, so import
///   provenance never names the person who imported (ADM-067).
module Echelon.Signal.Application.GroupStore

open System
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Application.Flow

/// A group as opened.
[<NoComparison; NoEquality>]
type OpenedGroup =
    { Dataset: Store.Opened
      Config: GroupRecord.GroupConfig
      ConfigRevision: Revision
      Definition: GroupDefinition
      /// Contributions by identity key.
      Contributions: Map<string, Loading.Stored<ResultRecord.StoredContribution>>
      Accumulator: Incremental.Accumulator
      /// The group's lifecycle and the revision it was read at (None: never stored).
      Lifecycle: GroupLifecycle.Lifecycle
      LifecycleRevision: Revision option
      Problems: Problem list }

/// Why a group operation did not happen.
type GroupFailure =
    | NotPermitted of Access.AccessRefusal
    | ReadOnly of Problem list
    | Unusable of Problem list
    /// The group's exact template is not in the catalog.
    | TemplateUnavailable of reason: string
    | Storage of ProviderContract.FailureMeaning
    | Offline of ProviderContract.FailureMeaning
    /// The group's lifecycle refuses it.
    | LifecycleRefused of GroupLifecycle.Refusal

let failed now (failure: StorageFailure) =
    match failure with
    | StorageFailure.ProviderFailed(_, true, _) -> Offline(ProviderContract.meaning now failure)
    | _ -> Storage(ProviderContract.meaning now failure)

/// A provider call, its failure classified (offline or storage).
let call now work = work |> mapError (failed now)

/// The capability's grant, or why it is refused (or read-only).
let permitted (opened: Store.Opened) (actor: Store.Actor) (capability: Access.Capability) =
    match Access.authorize opened.Roster.Roster opened.DatasetId actor.Principal.PrincipalId capability, opened.Grant with
    | Error refusal, _ -> Error(NotPermitted refusal)
    | Ok(), None -> Error(ReadOnly opened.ReadOnlyReasons)
    | Ok(), Some grant -> Ok grant

/// Creates a group: one commit of its configuration.
let create (actor: Store.Actor) (config: GroupRecord.GroupConfig) (now: DateTimeOffset) (opened: Store.Opened) : AsyncResult<unit, GroupFailure> =
    asyncResult {
        let! _ = permitted opened actor Access.ManageGroups |> lift

        let! operation =
            match
                GroupRecord.path config.Group,
                GroupRecord.encode opened.DatasetId { config with Revision = 1 },
                GroupLifecycle.path config.Group,
                GroupLifecycle.encode opened.DatasetId (GroupLifecycle.initial config.Group None)
            with
            | Ok target, Ok content, Ok lifecyclePath, Ok lifecycle ->
                Storage.operation
                    opened.Namespace
                    (actor.NewContext())
                    $"create group {GroupRecord.groupKey config.Group}"
                    [ Change.Create(target, content); Change.Create(lifecyclePath, lifecycle) ]
                |> Result.bind (GovernanceRecord.auditedWith opened.DatasetId [ GovernanceRecord.record Audit.GroupCreated [ "group", string config.Group ] [] [] None None (Some now) ])
            | Error problem, _, _, _
            | _, Error problem, _, _
            | _, _, Error problem, _
            | _, _, _, Error problem -> Error [ problem ]
            |> Result.mapError Unusable
            |> lift

        let! _ = call now (opened.Provider.Commit operation)
        return ()
    }

/// Reads every record file under a folder and its sub-folders (the shards).
let readTree now (provider: StorageProvider) (ns: Namespace) (folder: RelativePath) =
    asyncResult {
        let! top = call now (provider.List ns folder)
        let shards = top.Entries |> List.filter _.IsFolder |> List.map _.Path
        let! listings = shards |> List.map (fun shard -> call now (provider.List ns shard)) |> sequence
        let files = (top :: listings) |> List.collect Loading.recordFiles
        let! objects = files |> List.map (fun path -> call now (provider.Read ns path)) |> sequence

        return
            { Entries = []
              Complete = (top :: listings) |> List.forall _.Complete },
            objects |> List.choose (function ReadOutcome.Found stored -> Some stored | ReadOutcome.Absent -> None)
    }

/// Opens a group: its configuration, template and every accepted contribution.
let openGroup (resolve: GroupRecord.TemplateResolver) (group: UrlState.OpaqueId) (now: DateTimeOffset) (opened: Store.Opened) : AsyncResult<OpenedGroup, GroupFailure> =
    asyncResult {
        let! path = GroupRecord.path group |> Result.mapError (List.singleton >> Unusable) |> lift
        let! stored = call now (opened.Provider.Read opened.Namespace path)

        let configs =
            match stored with
            | ReadOutcome.Found found -> Loading.load opened.Verified GroupRecord.reader GroupRecord.folder { Entries = []; Complete = true } [ found ]
            | ReadOutcome.Absent -> Loading.load opened.Verified GroupRecord.reader GroupRecord.folder { Entries = []; Complete = true } []

        let! found =
            match configs.Records |> Map.tryFind (GroupRecord.groupKey group), configs.Problems with
            | Some found, [] -> Ok found
            | _, [] -> Error(Unusable [ InvalidStoredRecord(RelativePath.render path, "the group is not set up") ])
            | _, problems -> Error(Unusable problems)
            |> lift

        let config = found.Value.Config
        let! lifecyclePath = GroupLifecycle.path group |> Result.mapError (List.singleton >> Unusable) |> lift
        let! storedLifecycle = call now (opened.Provider.Read opened.Namespace lifecyclePath)

        let! lifecycle, lifecycleRevision =
            match storedLifecycle with
            | ReadOutcome.Absent -> Ok(GroupLifecycle.initial group None, None)
            | ReadOutcome.Found found ->
                let loaded = Loading.load opened.Verified GroupLifecycle.reader GroupRecord.folder { Entries = []; Complete = true } [ found ]

                match loaded.Records |> Map.tryFind (GroupRecord.groupKey group), loaded.Problems with
                | Some stored, [] -> Ok(stored.Value.Lifecycle, Some stored.Revision)
                | _, problems -> Error(Unusable problems)
            |> lift

        let! definition = GroupRecord.definition resolve config |> Result.mapError TemplateUnavailable |> lift
        let folder = ResultRecord.groupFolder group
        let! listing, objects = readTree now opened.Provider opened.Namespace folder
        let loaded = Loading.load opened.Verified (ResultRecord.reader definition.Template) folder listing objects

        // A contribution filed under this group must be this group's.
        let own, foreign =
            loaded.Records |> Map.partition (fun _ stored -> stored.Value.Contribution.Group = group)

        let contributions =
            own |> Map.toList |> List.map (fun (_, stored) -> stored.Value.Contribution.Result.Identity.Key, stored) |> Map.ofList

        let! accumulator =
            contributions
            |> Map.toList
            |> List.map (fun (_, stored) -> stored.Value.Contribution.Result)
            |> Incremental.ofResults definition
            |> Result.mapError (fun _ -> Unusable [ InvalidStoredRecord(RelativePath.render folder, "two contributions for one identity") ])
            |> lift

        return
            { Dataset = opened
              Config = config
              ConfigRevision = found.Revision
              Definition = definition
              Contributions = contributions
              Accumulator = accumulator
              Lifecycle = lifecycle
              LifecycleRevision = lifecycleRevision
              Problems =
                loaded.Problems
                @ (foreign |> Map.toList |> List.map (fun (_, stored) -> MisplacedRecord(RelativePath.render stored.Path))) }
    }

/// The group's current result and report state.
let result (group: OpenedGroup) = Incremental.result (GroupRecord.policy group.Config) group.Accumulator
let reportState (group: OpenedGroup) = ReportState.project (GroupRecord.policy group.Config) group.Accumulator

// ---- Importing ---------------------------------------------------------------------------------

/// How many artifacts one commit carries.
[<Literal>]
let ChunkSize = 25

/// The import service actor: commits name Signal, never the person (ADM-067).
let private importContext (actor: Store.Actor) : Storage.OperationContext =
    let context = actor.NewContext()

    { context with
        Actor =
            { Kind = ActorKind.Service
              Id =
                match ActorId.create "signal/import" with
                | Ok id -> id
                | Error _ -> context.Actor.Id }
        ProviderIdentity = None }

let private readBatch now (opened: Store.Opened) (fresh: Intake.Batch) =
    asyncResult {
        let! path = Intake.path fresh |> Result.mapError (List.singleton >> Unusable) |> lift
        let! stored = call now (opened.Provider.Read opened.Namespace path)

        match stored with
        | ReadOutcome.Absent -> return fresh, None
        | ReadOutcome.Found found ->
            let! batch =
                match RecordId.create fresh.BatchId with
                | Error _ -> Error(Unusable [ UnstorableRecord(fresh.BatchId, "not a record id") ])
                | Ok id ->
                    Integrity.validate { Type = Intake.recordType; Partition = []; Id = id } Intake.schema Record.DefaultMaxBytes found
                    |> Result.mapError (fun failure -> Unusable [ InvalidStoredRecord(RelativePath.render found.Path, Codec.describeIntegrity failure) ])
                    |> Result.bind (fun valid ->
                        Intake.ofBody valid.Record.Body
                        |> Result.mapError (fun detail -> Unusable [ InvalidStoredRecord(RelativePath.render found.Path, detail) ]))
                |> lift

            return batch.Batch, Some found.Revision
    }

/// What an import produced: the group after it, and the batch's summary.
[<NoComparison; NoEquality>]
type Imported =
    { Group: OpenedGroup
      Batch: Intake.Batch
      Summary: Intake.BatchSummary
      /// Why the batch stopped before its end, if it did.
      StoppedBecause: GroupFailure option }

/// Imports a batch of artifacts into a group, resuming a stored batch of the
/// same artifacts where it stopped.
let importBatch
    (actor: Store.Actor)
    (resolve: GroupRecord.TemplateResolver)
    (origin: ResultRecord.ImportOrigin)
    (texts: string list)
    (now: DateTimeOffset)
    (group: OpenedGroup)
    : AsyncResult<Imported, GroupFailure> =
    let opened = group.Dataset
    let artifacts = texts |> List.map Intake.artifact |> List.distinctBy _.Hash |> List.map (fun a -> a.Hash, a) |> Map.ofList

    let commitChunk (current: OpenedGroup) (batch: Intake.Batch) (revision: Revision option) (chunk: Intake.Artifact list) =
        let quarantined = Intake.quarantine current.Definition (Incremental.acceptedFor current.Accumulator) chunk
        let outcomes = quarantined |> List.map (fun q -> q.Artifact.Hash, Intake.outcomeOf q)
        let contributions = quarantined |> List.choose (Intake.promote current.Config.Group current.Config.Retention origin batch.BatchId)
        let next = Intake.record outcomes { batch with Revision = (match revision with Some _ -> batch.Revision + 1 | None -> batch.Revision) }

        let audits = GovernanceRecord.importRecords (string current.Config.Group) (outcomes |> List.map (fun (h, o) -> h, Intake.itemCode o)) (Some now)

        Intake.changes opened.DatasetId contributions next revision
        |> Result.bind (Storage.operation opened.Namespace (importContext actor) $"import into group {GroupRecord.groupKey current.Config.Group}")
        |> Result.bind (GovernanceRecord.auditedWith opened.DatasetId audits)
        |> Result.map (fun operation -> operation, contributions, next)

    let applied (current: OpenedGroup) (contributions: ResultRecord.Contribution list) =
        contributions
        |> List.fold (fun (acc: Incremental.Accumulator) c -> Incremental.add c.Result acc |> Result.defaultValue acc) current.Accumulator
        |> fun accumulator -> { current with Accumulator = accumulator }

    let revisionAfter (batch: Intake.Batch) (receipt: CommitReceipt) =
        Intake.path batch |> Result.toOption |> Option.bind (fun p -> receipt.Revisions.TryFind(RelativePath.render p)) |> Option.flatten

    let rec run (round: int) (current: OpenedGroup) (batch: Intake.Batch) (revision: Revision option) =
        async {
            let pending = Intake.remaining batch |> List.choose artifacts.TryFind |> List.truncate ChunkSize

            match pending with
            | [] -> return Ok(current, batch, None)
            | chunk ->
                match commitChunk current batch revision chunk with
                | Error problems -> return Ok(current, batch, Some(Unusable problems))
                | Ok(operation, contributions, next) ->
                    match! opened.Provider.Commit operation with
                    | Ok receipt -> return! run 0 (applied current contributions) next (revisionAfter next receipt)
                    | Error(StorageFailure.Conflicted _) when round = 0 ->
                        // Someone imported meanwhile: reload and decide again.
                        match! openGroup resolve current.Config.Group now opened with
                        | Ok fresh ->
                            match! readBatch now opened batch with
                            | Ok(stored, storedRevision) -> return! run 1 fresh stored storedRevision
                            | Error failure -> return Ok(current, batch, Some failure)
                        | Error failure -> return Ok(current, batch, Some failure)
                    | Error(StorageFailure.OutcomeUnknown pending) ->
                        match! opened.Provider.Reconcile opened.Namespace pending with
                        | Ok(ReconcileOutcome.Landed receipt) -> return! run 0 (applied current contributions) next (revisionAfter next receipt)
                        | Ok ReconcileOutcome.NotLanded when round = 0 -> return! run 1 current batch revision
                        | Ok _
                        | Error _ ->
                            let marked = Intake.record (chunk |> List.map (fun a -> a.Hash, Intake.ReconciliationRequired)) batch
                            return Ok(current, marked, Some(Storage(ProviderContract.meaning now (StorageFailure.OutcomeUnknown pending))))
                    | Error failure -> return Ok(current, batch, Some(failed now failure))
        }

    asyncResult {
        let! _ = permitted opened actor Access.ImportSubmissions |> lift

        do!
            if GroupLifecycle.acceptsContributions group.Lifecycle.Status then
                lift (Ok())
            else
                lift (Error(LifecycleRefused(GroupLifecycle.Illegal(GroupLifecycle.statusName group.Lifecycle.Status, "import"))))

        let fresh = Intake.start group.Config.Group origin (artifacts |> Map.toList |> List.map snd)
        let! stored, revision = readBatch now opened fresh
        let! final, batch, stopped = run 0 group stored revision

        return
            { Group = final
              Batch = batch
              Summary = Intake.summarize batch
              StoppedBecause = stopped }
    }

// ---- Report state and the contribution index ----------------------------------------------------

/// Stores the group's report state externally when it is too large for the
/// administrator URL (ARP-004 §47); a state already stored is not written
/// again. Returns where the state lives.
let persistReportState (actor: Store.Actor) (budget: ReportState.UrlBudget) (now: DateTimeOffset) (group: OpenedGroup) : AsyncResult<ReportState.Persistence, GroupFailure> =
    let state = reportState group

    match ReportState.persistence budget state with
    | ReportState.EmbeddedInUrl _ as embedded -> lift (Ok embedded)
    | ReportState.ExternalStore _ as external ->
        asyncResult {
            let! target, content = ReportState.encodeRecord state |> Result.mapError (List.singleton >> Unusable) |> lift
            let! existing = call now (group.Dataset.Provider.Read group.Dataset.Namespace target)

            match existing with
            | ReadOutcome.Found _ -> return external
            | ReadOutcome.Absent ->
                let! operation =
                    Storage.operation group.Dataset.Namespace (importContext actor) "store report state" [ Change.Create(target, content) ]
                    |> Result.bind (
                        GovernanceRecord.auditedWith
                            group.Dataset.DatasetId
                            [ GovernanceRecord.record Audit.AggregateRebuilt [ "group", string group.Config.Group; "reportState", ReportState.resultId state ] [] [ "REPORT-STATE-STORED" ] None None (Some now) ]
                    )
                    |> Result.mapError Unusable
                    |> lift

                let! _ = call now (group.Dataset.Provider.Commit operation)
                return external
        }

/// Loads an externally stored report state named by an administrator
/// fragment, checking its group and integrity (ARP-004 §50).
let loadReportState (fragment: string) (groupId: UrlState.OpaqueId) (now: DateTimeOffset) (opened: Store.Opened) : AsyncResult<ReportState.AdminReportState, GroupFailure> =
    asyncResult {
        let! id = ReportState.referencedId fragment |> Option.map Ok |> Option.defaultValue (Error(Unusable [ InvalidStoredRecord(fragment, "not a report-state reference") ])) |> lift
        let! target = ReportState.path id |> Result.mapError (List.singleton >> Unusable) |> lift
        let! stored = call now (opened.Provider.Read opened.Namespace target)
        let! state = ReportState.readRecord id stored |> Result.mapError (List.singleton >> Unusable) |> lift

        return!
            ReportState.checkReference fragment groupId state
            |> Result.mapError (fun error -> Unusable [ InvalidStoredRecord(id, $"%A{error}") ])
            |> lift
    }

/// Whether the stored contribution index still reflects the records.
let validateIndex (now: DateTimeOffset) (opened: Store.Opened) : AsyncResult<IndexStatus, GroupFailure> =
    asyncResult {
        let! snapshot = Snapshot.take opened.Provider opened.Namespace 3 |> mapError (fun e -> Unusable [ InvalidStoredRecord("snapshot", $"%A{e}") ])
        let! sources = Derived.sources ContributionIndex.definition snapshot |> Result.mapError (fun e -> Unusable [ InvalidStoredRecord("index", $"%A{e}") ]) |> lift
        let! stored = Derived.read opened.Provider opened.Namespace ContributionIndex.definition |> mapError (fun e -> Unusable [ InvalidStoredRecord("index", $"%A{e}") ])
        return Derived.check ContributionIndex.definition (Derived.sourceSet sources) (stored |> Option.map fst)
    }

/// Rebuilds the contribution index from the records and activates it in one
/// commit, unless it is already current. The index commit is Arca's own, so
/// its audit record (ADM-030) follows in a commit of its own.
let rebuildIndex (actor: Store.Actor) (now: DateTimeOffset) (opened: Store.Opened) : AsyncResult<DerivedIndex, GroupFailure> =
    asyncResult {
        let! index, _ =
            Derived.rebuild opened.Provider opened.Namespace (Storage.metadata (importContext actor) "rebuild contribution index") ContributionIndex.definition
            |> mapError (function
                | DerivedError.Provider failure -> failed now failure
                | other -> Unusable [ InvalidStoredRecord("index", $"%A{other}") ])

        let! operation =
            GovernanceRecord.record Audit.IndexRebuilt [ "index", "idx-contributions" ] [] [ "INDEX-REBUILT" ] None None (Some now)
            |> GovernanceRecord.auditOnly opened.Namespace (importContext actor) "audit contribution index rebuild" opened.DatasetId
            |> Result.mapError Unusable
            |> lift

        let! _ = call now (opened.Provider.Commit operation)
        return index
    }
