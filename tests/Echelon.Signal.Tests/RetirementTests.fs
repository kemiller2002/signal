/// Retiring a finalized group's sources (ADM-045, ADM-064; WI-0076, WI-0077):
/// out of Signal's state, and out of the current tree where the provider
/// declares Arca's Erase capability, with a claim that never says more.
module Echelon.Signal.Tests.RetirementTests

open System
open Xunit
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Access
open Echelon.Signal.Application
open Echelon.Signal.Tests.DurableFixture

// ---- ADM-045 / ADM-064: retiring a finalized group's sources ------------------------------------

[<Fact>]
let ``retiring a finalized group's results removes them from Signal's state and the current tree, claims no more, and records the limit`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    let imported = import [ anonymous 1uy Often; anonymous 2uy Never; anonymous 3uy Sometimes ] group

    match GovernanceStore.retireSources actor at GovernanceStore.DeletionRequest imported.Group |> run with
    | Error(GovernanceStore.GroupNotFinalized "Collecting") -> ()
    | other -> failwith $"%A{other}"

    GroupStore.rebuildIndex actor at group.Dataset |> run |> ok |> ignore
    let closed = GroupAdmin.transition actor "close" at imported.Group |> run |> ok
    let finalized = GroupAdmin.transition actor "finalize" at closed |> run |> ok

    let thirtyDays = Map [ Retention.AcceptedResult, Retention.KeepForDays 30 ]

    match GovernanceStore.retireSources actor at (GovernanceStore.RetentionExpired thirtyDays) finalized |> run with
    | Error GovernanceStore.RetentionNotDue -> ()
    | other -> failwith $"%A{other}"

    let resultObjects () = github.State.Objects |> Map.filter (fun path _ -> path.Contains "signal.result") |> Map.toList
    let originals = resultObjects () |> List.map (fun (_, o) -> o.Content)
    let retired = GovernanceStore.retireSources actor (at.AddDays 31.0) (GovernanceStore.RetentionExpired thirtyDays) finalized |> run |> ok
    Assert.Equal(3, retired.Removed)
    // The in-memory provider declares Erase (as GitHub does): the results
    // leave the current tree too. History is never claimed.
    Assert.Equal(GovernanceStore.ErasedFromTree 3, retired.Tree)
    Assert.Equal(Retention.RemovedFromCurrentTree, retired.Claim)
    let limitation = Option.get retired.Limitation
    Assert.Equal((Retention.FullReconstruction, Retention.NotReproducible), (limitation.Before, limitation.After))
    Assert.Contains(Retention.GroupAggregateRebuild, limitation.Breaks)
    Assert.True(limitation.LineageClosed)
    Assert.Contains("TREE-ERASURE-REQUESTED", limitation.Evidence.Reasons)
    Assert.DoesNotContain("TREE-REMOVAL-UNAVAILABLE", limitation.Evidence.Reasons)
    Assert.Contains("RETENTION-EXPIRED", limitation.Evidence.Reasons)

    let reopened = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    Assert.Empty(reopened.Contributions)
    // An erased record is not a damaged one: the group opens without problems.
    Assert.Empty(reopened.Problems)
    Assert.Equal(Some "state-not-reproducible", reopened.SourcesRetired |> Option.map _.Remaining)
    Assert.Equal(0, reopened.Accumulator.Accepted.Count)

    // Each result's blob is now a tombstone naming the erased hash and the
    // reason, never the content.
    let erased = resultObjects ()
    Assert.Equal(3, erased.Length)

    for _, stored in erased do
        let tombstone = Tombstone.decode stored.Content |> Option.get
        Assert.Equal("retention retention-expired", tombstone.Reason)
        Assert.StartsWith("sha256:", tombstone.ErasedContentHash)
        Assert.DoesNotContain(stored.Content, originals)

    let records, _ = GovernanceStore.audit at group.Dataset |> run |> ok
    Assert.Equal("state-not-reproducible", GovernanceStore.reconstructionOf records (string groupId))
    Assert.Equal("state-full-reconstruction", GovernanceStore.reconstructionOf records "AAECAwQFBgcICQoLDA0ODw")

/// A finalized group with three accepted results, on `store`.
let private finalizedOn (store: Store.Backend) =
    let group = setUpOn store AnonymousGroup GroupRecord.NoneAfterImport
    let imported = import [ anonymous 1uy Often; anonymous 2uy Never; anonymous 3uy Sometimes ] group
    GroupStore.rebuildIndex actor at group.Dataset |> run |> ok |> ignore
    let closed = GroupAdmin.transition actor "close" at imported.Group |> run |> ok
    group, GroupAdmin.transition actor "finalize" at closed |> run |> ok

let private withoutErase (provider: StorageProvider) =
    { provider with Capabilities = { provider.Capabilities with States = provider.Capabilities.States |> Map.remove Capability.Erase } }

[<Fact>]
let ``a provider without Erase keeps the results in the tree, and the claim and audit say so`` () =
    let github = InMemoryStore()
    let group, finalized = finalizedOn (reshaped withoutErase (backend github (fun () -> true)))
    let before = github.State.Objects |> Map.filter (fun path _ -> path.Contains "signal.result")

    let retired = GovernanceStore.retireSources actor at GovernanceStore.DeletionRequest finalized |> run |> ok
    Assert.Equal(GovernanceStore.ErasureNotOffered, retired.Tree)
    Assert.Equal(Retention.RemovedFromApplicationState, retired.Claim)
    Assert.Contains("TREE-REMOVAL-UNAVAILABLE", (Option.get retired.Limitation).Evidence.Reasons)
    Assert.Equal<Map<string, string>>(before |> Map.map (fun _ o -> o.Content), github.State.Objects |> Map.filter (fun path _ -> path.Contains "signal.result") |> Map.map (fun _ o -> o.Content))
    Assert.Empty((GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok).Contributions)

[<Fact>]
let ``an erasure that fails leaves the claim at application state, with the reason`` () =
    let github = InMemoryStore()
    let refuseErasure (provider: StorageProvider) =
        { provider with
            Commit =
                fun operation ->
                    if operation.IsErasure then async.Return(Error(StorageFailure.ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", false, "refused")))
                    else provider.Commit operation }

    let group, finalized = finalizedOn (reshaped refuseErasure (backend github (fun () -> true)))
    let retired = GovernanceStore.retireSources actor at GovernanceStore.DeletionRequest finalized |> run |> ok
    Assert.True(match retired.Tree with GovernanceStore.ErasureFailed _ -> true | _ -> false)
    Assert.Equal(Retention.RemovedFromApplicationState, retired.Claim)
    // The retirement itself landed: the results left Signal's state.
    Assert.Empty((GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok).Contributions)
    Assert.Equal(3, github.State.Objects |> Map.filter (fun path _ -> path.Contains "signal.result") |> Map.filter (fun _ o -> (Tombstone.decode o.Content).IsNone) |> Map.count)
