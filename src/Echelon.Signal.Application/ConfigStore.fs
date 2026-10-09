/// Configuration packages and policy packs through Arca (WI-0075, ADM-047,
/// ADM-048, ADM-061): a quarantined package is activated explicitly, in one
/// commit with its provenance, only when every item validates; a dataset's
/// configuration exports as a package with no response data.
module Echelon.Signal.Application.ConfigStore

open System
open Arca
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Application.Flow
open Echelon.Signal.Application.GroupStore

/// The dataset's policy packs, with the built-in ones.
let packs (now: DateTimeOffset) (opened: Store.Opened) : AsyncResult<PolicyPack.Pack list * Problem list, GroupFailure> =
    asyncResult {
        let folder = GovernanceRecord.folderOf GovernanceRecord.policyType
        let! listing, objects = readTree now opened.Provider opened.Namespace folder
        let loaded = Loading.load opened.Verified GovernanceRecord.policyReader folder listing objects
        let stored = loaded.Records |> Map.toList |> List.map (fun (_, r) -> r.Value.Pack)
        return PolicyPack.builtIns @ (stored |> List.filter (fun p -> not (PolicyPack.builtIns |> List.exists (fun b -> b.PolicyPackId = p.PolicyPackId && b.Version = p.Version)))), loaded.Problems
    }

type ActivationFailure =
    | NotActivatable of reasons: string list
    | ActivationNotStored of GroupFailure

/// Activates a quarantined package: its report definitions saved (the next
/// version where one is in use), its new policy packs created, and the
/// activation's provenance as an audit record, in one commit.
let activate (actor: Store.Actor) (now: DateTimeOffset) (opened: Store.Opened) (q: ConfigPackage.Quarantined) : Async<Result<ConfigPackage.Impact list, ActivationFailure>> =
    async {
        match permitted opened actor Access.BuildReports with
        | Error failure -> return Error(ActivationNotStored failure)
        | Ok _ ->
            let! reports = ReportStore.load now opened
            let! existing = packs now opened

            match reports, existing with
            | Error failure, _
            | _, Error failure -> return Error(ActivationNotStored failure)
            | Ok reports, Ok(known, _) ->
                match ConfigPackage.activationProblems reports.Library known q with
                | _ :: _ as reasons -> return Error(NotActivatable reasons)
                | [] ->
                    let impact = ConfigPackage.impact reports.Library known q

                    let library, definitionIds =
                        q.Package.Items
                        |> List.fold
                            (fun (library, ids) item ->
                                match item with
                                | ConfigPackage.ReportDefinitionItem entry when not (List.contains (ConfigPackage.SameDefinition entry.Definition.Id) impact) ->
                                    match ReportLibrary.save library entry.Definition entry.Pins with
                                    | Ok(next, _) -> next, ids @ [ entry.Definition.Id ]
                                    | Error _ -> library, ids
                                | _ -> library, ids)
                            (reports.Library, [])

                    let definitionChanges =
                        definitionIds
                        |> List.map (fun id ->
                            match ReportRecord.definitionPath id, ReportRecord.encodeDefinitions opened.DatasetId id (ReportLibrary.versions library id) with
                            | Ok path, Ok content ->
                                Ok(
                                    match reports.Revisions |> Map.tryFind id with
                                    | Some revision -> Change.Update(path, content, revision)
                                    | None -> Change.Create(path, content)
                                )
                            | Error p, _
                            | _, Error p -> Error p)

                    let packChanges =
                        q.Package.Items
                        |> List.choose (function
                            | ConfigPackage.PolicyPackItem pack when List.contains (ConfigPackage.NewPack(pack.PolicyPackId, pack.Version)) impact ->
                                Some(
                                    match GovernanceRecord.policyPath pack, GovernanceRecord.encodePolicy opened.DatasetId pack with
                                    | Ok path, Ok content -> Ok(Change.Create(path, content))
                                    | Error p, _
                                    | _, Error p -> Error p
                                )
                            | _ -> None)

                    let audit =
                        GovernanceRecord.record
                            Audit.ConfigurationActivated
                            []
                            [ "package", q.Hash ]
                            [ $"PACKAGE-SCHEMA-{q.Package.SchemaVersion}"; $"ITEMS-{q.Package.Items.Length}" ]
                            None
                            None
                            (Some now)

                    let changes = definitionChanges @ packChanges

                    match changes |> List.choose (function Error p -> Some p | Ok _ -> None) with
                    | _ :: _ as problems -> return Error(ActivationNotStored(Unusable problems))
                    | [] when changes.IsEmpty -> return Ok impact
                    | [] ->
                        match
                            Storage.operation opened.Namespace (actor.NewContext()) "activate configuration package" (changes |> List.choose Result.toOption)
                            |> Result.bind (GovernanceRecord.auditedWith opened.DatasetId [ audit ])
                        with
                        | Error problems -> return Error(ActivationNotStored(Unusable problems))
                        | Ok operation ->
                            match! opened.Provider.Commit operation with
                            | Ok _ -> return Ok impact
                            | Error failure -> return Error(ActivationNotStored(failed now failure))
    }

/// The dataset's configuration as a package: its saved report definitions and
/// its own policy packs, never responses, results or credentials.
let export (actor: Store.Actor) (now: DateTimeOffset) (opened: Store.Opened) (packageId: string) : Async<Result<string, GroupFailure>> =
    async {
        match permitted opened actor Access.ExportData with
        | Error failure -> return Error failure
        | Ok _ ->
            let! reports = ReportStore.load now opened
            let! known = packs now opened

            match reports, known with
            | Error f, _
            | _, Error f -> return Error f
            | Ok reports, Ok(all, _) ->
                let own = all |> List.filter (fun p -> not (PolicyPack.builtIns |> List.contains p))
                let text = ConfigPackage.encode (ConfigPackage.export packageId (Some "signal-admin/1") reports.Library own)

                let audit =
                    GovernanceRecord.record Audit.ExportCreated [] [ "package", ConfigPackage.hashOf text ] [ "CONFIGURATION-PACKAGE" ] None None (Some now)
                    |> GovernanceRecord.auditOnly opened.Namespace (actor.NewContext()) "export configuration package" opened.DatasetId

                match audit with
                | Error problems -> return Error(Unusable problems)
                | Ok operation ->
                    match! opened.Provider.Commit operation with
                    | Ok _ -> return Ok text
                    | Error failure -> return Error(failed now failure)
    }
