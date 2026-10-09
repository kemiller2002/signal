/// The administrator page's dataset and group work (opening a dataset with
/// its template catalog, creating, opening, importing into and moving groups
/// on), as async work the wire starts. Every group resolves its template
/// through the dataset's catalog (WI-0073).
module Echelon.Signal.Application.AdminGroupWork

open System
open Echelon.Signal.Engine
open Echelon.Signal.Admin
open Echelon.Signal.Application.AdminWork

let private engine msg = ToEngine msg

/// Opens a dataset, its template catalog and its groups.
let openDataset (env: Env) (backend: Store.Backend) (config: Deployment.DeploymentConfig) (actor: Store.Actor) (pinned: string option) (datasetId: string) (now: DateTimeOffset) =
    async {
        match! Store.openDataset backend config actor pinned env.ApplicationVersion datasetId now with
        | Error failure -> return [ engine (openFailure failure) ]
        | Ok opened ->
            let! loaded = TemplateCatalog.load env.Catalog now opened
            let catalog = loaded |> Result.defaultValue (TemplateCatalog.builtIn env.Catalog)
            let catalogFailure = match loaded with Error failure -> [ engine (groupFailure failure) ] | Ok _ -> []

            let! snapshots = ReportWork.load now opened

            match! loadGroups env (TemplateCatalog.resolver catalog) opened with
            | Ok groups -> return DatasetReady(opened, groups, catalog) :: catalogFailure @ snapshots
            | Error failure -> return [ DatasetReady(opened, [], catalog); engine (groupFailure failure) ] @ catalogFailure @ snapshots
    }

/// Creates a group from a template the catalog offers.
let create (env: Env) (catalog: TemplateCatalog.Loaded) (actor: Store.Actor) (opened: Store.Opened) (templateHash: string) mode expected minimum (now: DateTimeOffset) =
    async {
        match catalog.Offered |> List.tryFind (fun e -> e.Hash = templateHash), UrlState.OpaqueId.ofBytes (env.RandomBytes UrlState.IdLength) with
        | Some entry, Some group ->
            let config: GroupRecord.GroupConfig =
                { Group = group
                  Mode = mode
                  ExpectedCount = expected
                  SurveyIdentifier = entry.SurveyIdentifier
                  TemplateVersion = entry.Version
                  TemplateHash = entry.Hash
                  MinimumReportableCount = minimum
                  Retention = GroupRecord.NoneAfterImport
                  Revision = 1 }

            match! GroupStore.create actor config now opened with
            | Error failure -> return [ engine (groupFailure failure) ]
            | Ok() ->
                match! GroupStore.openGroup (TemplateCatalog.resolver catalog) group now opened with
                | Ok created -> return [ GroupReady(created, None, 0) ]
                | Error failure -> return [ engine (groupFailure failure) ]
        | _ -> return [ engine (AdminApp.Failed(notice "SIGNAL.ADMIN.TEMPLATE_UNAVAILABLE" "Choose a template from the catalog." false)) ]
    }

/// Rereads a group.
let reopen (catalog: TemplateCatalog.Loaded) (group: GroupStore.OpenedGroup) (now: DateTimeOffset) =
    async {
        match! GroupStore.openGroup (TemplateCatalog.resolver catalog) group.Config.Group now group.Dataset with
        | Ok fresh -> return [ GroupReady(fresh, None, 0) ]
        | Error failure -> return [ engine (groupFailure failure) ]
    }

/// Imports artifacts, records a release if the new state may be shown
/// (ARX-009), and rereads the group so the page shows what is stored.
let import (catalog: TemplateCatalog.Loaded) (actor: Store.Actor) (group: GroupStore.OpenedGroup) origin texts (now: DateTimeOffset) =
    let resolve = TemplateCatalog.resolver catalog

    async {
        match! GroupStore.importBatch actor resolve origin texts now group with
        | Error failure -> return [ engine (groupFailure failure) ]
        | Ok imported ->
            let unreconciled = if imported.Summary.Status = Intake.NeedsReconciliation then 1 else 0
            let stopped = imported.StoppedBecause |> Option.map (groupFailure >> engine) |> Option.toList
            let! released = Releases.record actor now imported.Group
            let! fresh = GroupStore.openGroup resolve group.Config.Group now group.Dataset
            let failures = [ released |> Result.map ignore; fresh |> Result.map ignore ] |> List.choose (function Error f -> Some(engine (groupFailure f)) | Ok() -> None)
            let current = fresh |> Result.defaultValue imported.Group
            return GroupReady(current, Some imported, unreconciled) :: stopped @ failures
    }

/// Moves a group through its lifecycle, telling other tabs storage changed.
let transition (env: Env) (actor: Store.Actor) (group: GroupStore.OpenedGroup) (name: string) (now: DateTimeOffset) =
    async {
        match! GroupAdmin.transition actor name now group with
        | Ok next ->
            let! _ = env.Bridge.Call(Bridge.Announce(Credential.announcement group.Dataset.DatasetId Credential.StorageChangedElsewhere))
            return [ GroupReady(next, None, 0) ]
        | Error failure -> return [ engine (groupFailure failure) ]
    }
