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

let private commit now (opened: Store.Opened) (actor: Store.Actor) (summary: string) (changes: Result<Change list, Problem>) =
    asyncResult {
        let! operation = changes |> Result.bind (fun c -> Storage.operation opened.Namespace (actor.NewContext()) summary c |> Result.mapError List.head) |> Result.mapError (List.singleton >> Unusable) |> lift
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

                match! commit now opened actor $"save report definition {definition.Id} {entry.Definition.Version}" changes with
                | Ok() -> return Ok entry
                | Error failure -> return Error(NotStored failure)
    }

/// Takes a formal snapshot and marks its definition version used, in one commit.
let takeSnapshot (actor: Store.Actor) (now: DateTimeOffset) (opened: Store.Opened) (source: Source) : Async<Result<Snapshot, ReportFailure>> =
    async {
        match! stored now opened actor Access.BuildReports with
        | Error failure -> return Error failure
        | Ok reports ->
            match ReportLibrary.take reports.Library reports.Catalog source with
            | Error problem -> return Error(SnapshotRefused problem)
            | Ok(snapshot, library) ->
                let changes =
                    match ReportRecord.snapshotPath snapshot, ReportRecord.encodeSnapshot opened.DatasetId snapshot, definitionChange opened reports library snapshot.DefinitionId with
                    | Ok path, Ok content, Ok definition -> Ok [ Change.Create(path, content); definition ]
                    | Error p, _, _
                    | _, Error p, _
                    | _, _, Error p -> Error p

                match! commit now opened actor $"snapshot {snapshot.SnapshotId}" changes with
                | Ok() -> return Ok snapshot
                | Error failure -> return Error(NotStored failure)
    }

/// The export files for a snapshot (ADM-023), for an administrator who may export.
let export (actor: Store.Actor) (opened: Store.Opened) (snapshot: Snapshot) (report: ReportData) : Result<(string * string) list, ReportFailure> =
    permitted opened actor Access.ExportData
    |> Result.mapError NotStored
    |> Result.bind (fun _ -> ReportLibrary.export snapshot report |> Result.mapError ExportRefused)
