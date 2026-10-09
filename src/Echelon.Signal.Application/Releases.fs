/// What an anonymous group shows while responses arrive (WI-0076, ARX-009,
/// ADM-024): a view at n responses and another at n+1 would reveal the
/// newest response by subtraction, so an anonymous group with a minimum above
/// one releases its aggregate only when it differs from the last release by at
/// least the minimum. Between releases the page shows the last released state
/// and how many newer responses are withheld; nothing below the minimum is
/// ever released.
///
/// The release is recorded as the set of contributions it covered (opaque
/// identity keys), so the released state is rebuilt from the same stored
/// contributions, never kept as a second copy of anyone's answers.
module Echelon.Signal.Application.Releases

open System
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin
open Echelon.Signal.Application.Flow
open Echelon.Signal.Application.GroupStore

/// Whether the group needs the release ledger at all.
let private guarded (group: OpenedGroup) =
    group.Config.Mode = AnonymousGroup && group.Config.MinimumReportableCount > 1

let private policy (group: OpenedGroup) = Disclosure.forGroup group.Config.MinimumReportableCount

/// The group as the page may show it: the live state when no ledger is
/// needed or the live state is the released one; otherwise the state of the
/// contributions last released (none when nothing has been).
let shown (group: OpenedGroup) : OpenedGroup =
    let live = group.Accumulator.Accepted |> Map.keys |> Set.ofSeq

    match guarded group, group.Released with
    | false, _ -> group
    // Below the minimum every response-derived value is suppressed anyway.
    | true, _ when live.Count < group.Config.MinimumReportableCount -> group
    | true, Some(release, _) when Set.ofList release.Keys = live -> group
    | true, released ->
        let keys = released |> Option.map (fst >> _.Keys >> Set.ofList) |> Option.defaultValue Set.empty

        let results =
            group.Contributions
            |> Map.toList
            |> List.filter (fun (key, _) -> keys.Contains key)
            |> List.map (fun (_, stored) -> stored.Value.Contribution.Result)

        match Incremental.ofResults group.Definition results with
        | Ok accumulator -> { group with Accumulator = accumulator }
        | Error _ -> { group with Accumulator = Incremental.empty group.Definition }

/// How many accepted responses the page is not showing yet.
let withheld (group: OpenedGroup) =
    group.Accumulator.Accepted.Count - (shown group).Accumulator.Accepted.Count

/// Records a new release when the live state may be released; returns the
/// group as it stands (unchanged when nothing new may be released).
let record (actor: Store.Actor) (now: DateTimeOffset) (group: OpenedGroup) : AsyncResult<OpenedGroup, GroupFailure> =
    let opened = group.Dataset
    let live = group.Accumulator.Accepted |> Map.keys |> List.ofSeq |> List.sort
    let last = group.Released |> Option.map (fun (r, _) -> r.Keys.Length)

    match guarded group, Disclosure.releasable (policy group) group.Config.Mode last live.Length with
    | false, _
    | _, Disclosure.Withhold _ -> lift (Ok group)
    | true, Disclosure.Release _ when last = Some live.Length -> lift (Ok group)
    | true, Disclosure.Release _ ->
        asyncResult {
            let release: GovernanceRecord.StoredRelease = { DatasetId = opened.DatasetId; Group = string group.Config.Group; Keys = live }

            let! operation =
                match GovernanceRecord.releasePath release.Group, GovernanceRecord.encodeRelease release with
                | Ok path, Ok content ->
                    let change =
                        match group.Released with
                        | Some(_, revision) -> Change.Update(path, content, revision)
                        | None -> Change.Create(path, content)

                    Storage.operation opened.Namespace (actor.NewContext()) $"release group {GroupRecord.groupKey group.Config.Group}" [ change ]
                    |> Result.bind (
                        GovernanceRecord.auditedWith
                            opened.DatasetId
                            [ GovernanceRecord.record Audit.AggregateRebuilt [ "group", release.Group ] [] [ "RESULTS-RELEASED"; $"RELEASED-COUNT-{live.Length}" ] None None (Some now) ]
                    )
                | Error p, _
                | _, Error p -> Error [ p ]
                |> Result.mapError Unusable
                |> lift

            let! receipt = call now (opened.Provider.Commit operation)

            let revision =
                GovernanceRecord.releasePath release.Group
                |> Result.toOption
                |> Option.bind (fun path -> receipt.Revisions.TryFind(RelativePath.render path))
                |> Option.flatten

            return
                match revision with
                | Some r -> { group with Released = Some(release, r) }
                | None -> group
        }
