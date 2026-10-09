/// Saved report definitions and formal snapshots through Arca (WI-0050,
/// ADM-010, ADM-021, ADM-022, ADM-023, ADM-063), in the dataset's namespace
/// and under its roster.
///
/// - **Saving a definition** needs `BuildReports`; it is written at the
///   revision it was read at, so a concurrent edit is a conflict.
/// - **Taking a snapshot** needs `BuildReports`. The snapshot record is
///   created and the definition version marked used in one commit, so a used
///   version and its snapshot never exist apart.
/// - **Exporting** needs `ExportData`; it writes nothing.
module Echelon.Signal.Application.ReportStore

open System
open Arca
open Echelon.Signal.Engine.ReportModel
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.ReportLibrary
open Echelon.Signal.Application.Flow
open Echelon.Signal.Application.GroupStore

/// The stored library, its revisions, the snapshots and the catalog they resolve against.
[<NoComparison; NoEquality>]
type StoredReports =
    { Library: Library
      Revisions: Map<string, Revision>
      Snapshots: Snapshot list
      Catalog: Echelon.Signal.Engine.Publication.Catalog
      Problems: Problem list }

let private loadAll now (opened: Store.Opened) (reader: Loading.RecordReader<'a>) (recordType: RecordType) =
    asyncResult {
        let folder = ReportRecord.folderOf recordType
        let! listing, objects = readTree now opened.Provider opened.Namespace folder
        return Loading.load opened.Verified reader folder listing objects
    }

/// Reads the dataset's report definitions, snapshots and template catalog.
let load (now: DateTimeOffset) (opened: Store.Opened) : AsyncResult<StoredReports, GroupFailure> =
    asyncResult {
        let! definitions = loadAll now opened ReportRecord.definitionReader ReportRecord.definitionType
        let! snapshots = loadAll now opened ReportRecord.snapshotReader ReportRecord.snapshotType
        let! templates = TemplateStore.load now opened
        let stored = definitions.Records |> Map.toList |> List.map snd

        return
            { Library = stored |> List.map (fun d -> d.Value.Id, d.Value.Versions) |> Map.ofList
              Revisions = stored |> List.map (fun d -> d.Value.Id, d.Revision) |> Map.ofList
              Snapshots = snapshots.Records |> Map.toList |> List.map (fun (_, s) -> s.Value.Snapshot) |> List.sortBy _.SnapshotId
              Catalog = templates.Catalog
              Problems = definitions.Problems @ snapshots.Problems @ templates.Problems }
    }

let private commit now (opened: Store.Opened) (actor: Store.Actor) (summary: string) (audits: Result<Audit.Record, Problem> list) (changes: Result<Change list, Problem>) =
    asyncResult {
        let! operation =
            changes
            |> Result.mapError List.singleton
            |> Result.bind (Storage.operation opened.Namespace (actor.NewContext()) summary)
            |> Result.bind (GovernanceRecord.auditedWith opened.DatasetId audits)
            |> Result.mapError Unusable
            |> lift

        let! _ = call now (opened.Provider.Commit operation)
        return ()
    }

let private definitionChange (opened: Store.Opened) (stored: StoredReports) (library: Library) (id: string) =
    match ReportRecord.definitionPath id, ReportRecord.encodeDefinitions opened.DatasetId id (versions library id) with
    | Ok path, Ok content ->
        Ok(
            match stored.Revisions |> Map.tryFind id with
            | Some revision -> Change.Update(path, content, revision)
            | None -> Change.Create(path, content)
        )
    | Error p, _
    | _, Error p -> Error p

/// Why a library step did not happen: the domain's refusal, or storage.
type ReportFailure =
    | LibraryRefused of LibraryProblem
    | SnapshotRefused of SnapshotProblem
    | ExportRefused of ExportProblem
    | NotStored of GroupFailure

let private stored now (opened: Store.Opened) (actor: Store.Actor) capability =
    async {
        match permitted opened actor capability with
        | Error failure -> return Error(NotStored failure)
        | Ok _ ->
            let! loaded = load now opened
            return loaded |> Result.mapError NotStored
    }

/// Saves a definition; returns the version it was saved as.
let saveDefinition (actor: Store.Actor) (now: DateTimeOffset) (opened: Store.Opened) (definition: Definition) (pins: Pins) : Async<Result<Entry, ReportFailure>> =
    async {
        match! stored now opened actor Access.BuildReports with
        | Error failure -> return Error failure
        | Ok reports ->
            match ReportLibrary.save reports.Library definition pins with
            | Error problem -> return Error(LibraryRefused problem)
            | Ok(library, entry) ->
                let changes = definitionChange opened reports library definition.Id |> Result.map List.singleton

                let audits =
                    [ GovernanceRecord.record
                          Audit.ReportDefinitionPublished
                          [ "definition", "def-" + definition.Id ]
                          []
                          [ $"DEFINITION-VERSION-{entry.Definition.Version}" ]
                          None
                          None
                          (Some now) ]

                match! commit now opened actor $"save report definition {definition.Id} {entry.Definition.Version}" audits changes with
                | Ok() -> return Ok entry
                | Error failure -> return Error(NotStored failure)
    }

/// Takes a formal snapshot and marks its definition version used, in one
/// commit. `catalog` holds the templates the console's groups pin (the
/// stored catalog with the built-in templates, WI-0075).
let takeSnapshot (actor: Store.Actor) (now: DateTimeOffset) (opened: Store.Opened) (catalog: Echelon.Signal.Engine.Publication.Catalog) (source: Source) : Async<Result<Snapshot, ReportFailure>> =
    async {
        match! stored now opened actor Access.BuildReports with
        | Error failure -> return Error failure
        | Ok reports ->
            match ReportLibrary.take reports.Library catalog reports.Snapshots source with
            | Error problem -> return Error(SnapshotRefused problem)
            // The same state, definition and dependencies make the same snapshot: it is already stored.
            | Ok(snapshot, _) when reports.Snapshots |> List.exists (fun s -> s.SnapshotId = snapshot.SnapshotId) ->
                return Ok(reports.Snapshots |> List.find (fun s -> s.SnapshotId = snapshot.SnapshotId))
            | Ok(snapshot, library) ->
                let changes =
                    match ReportRecord.snapshotPath snapshot, ReportRecord.encodeSnapshot opened.DatasetId snapshot, definitionChange opened reports library snapshot.DefinitionId with
                    | Ok path, Ok content, Ok definition -> Ok [ Change.Create(path, content); definition ]
                    | Error p, _, _
                    | _, Error p, _
                    | _, _, Error p -> Error p

                let audits =
                    [ GovernanceRecord.record
                          Audit.ReportSnapshotCreated
                          [ "snapshot", snapshot.SnapshotId; "definition", "def-" + snapshot.DefinitionId ]
                          [ "reportData", snapshot.CanonicalReportDataHash ]
                          [ $"DEFINITION-VERSION-{snapshot.DefinitionVersion}" ]
                          None
                          None
                          snapshot.GeneratedAtEvidence ]

                match! commit now opened actor $"snapshot {snapshot.SnapshotId}" audits changes with
                | Ok() -> return Ok snapshot
                | Error failure -> return Error(NotStored failure)
    }

/// The export files for a snapshot (ADM-023), for an administrator who may
/// export. Exporting writes only its audit record (ADM-030).
let export (actor: Store.Actor) (now: DateTimeOffset) (opened: Store.Opened) (snapshot: Snapshot) : Async<Result<(string * string) list, ReportFailure>> =
    async {
        match permitted opened actor Access.ExportData with
        | Error failure -> return Error(NotStored failure)
        | Ok _ ->
            match (if ReportLibrary.intact snapshot then Ok(ReportLibrary.files snapshot) else Error ReportIsNotTheSnapshot) with
            | Error problem -> return Error(ExportRefused problem)
            | Ok files ->
                let operation =
                    GovernanceRecord.record
                        Audit.ExportCreated
                        [ "snapshot", snapshot.SnapshotId ]
                        [ "reportData", snapshot.CanonicalReportDataHash ]
                        (files |> List.map (fst >> GovernanceRecord.codeOf))
                        None
                        None
                        (Some now)
                    |> GovernanceRecord.auditOnly opened.Namespace (actor.NewContext()) $"export {snapshot.SnapshotId}" opened.DatasetId

                match operation with
                | Error problems -> return Error(NotStored(Unusable problems))
                | Ok operation ->
                    match! opened.Provider.Commit operation with
                    | Ok _ -> return Ok files
                    | Error failure -> return Error(NotStored(failed now failure))
    }
