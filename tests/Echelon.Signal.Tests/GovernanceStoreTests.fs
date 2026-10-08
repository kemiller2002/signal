/// The audit trail and the dataset lifecycle through Arca (WI-0076):
/// ADM-030 audit records written with the change they describe and read back
/// PII-free, ADM-045 lifecycle states that make a dataset read-only.
module Echelon.Signal.Tests.GovernanceStoreTests

open System
open Xunit
open Arca
open Echelon.Signal.Engine.ReportModel
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Access
open Echelon.Signal.Admin.ReportLibrary
open Echelon.Signal.Application
open Echelon.Signal.Tests.StoreFixture

let private definition = { Echelon.Signal.Engine.ReportExport.anonymousAggregate with Id = "quarterly" }
let private pins = currentPins (LatestTemplate "SDRA")

let private events (opened: Store.Opened) =
    let records, problems = GovernanceStore.audit at opened |> run |> ok
    Assert.Empty(problems)
    records |> List.map (fun r -> Audit.eventName r.Record.Event)

[<Fact>]
let ``setting up and saving a report definition are audited, and no person reaches the records`` () =
    let github, admin, _ = dataset Grants.analyst
    ReportStore.saveDefinition (actor octocat) at admin definition pins |> run |> ok |> ignore

    Assert.Equal<string list>([ "ReportDefinitionPublished"; "StorageConfigured" ], events admin |> List.sort)

    let records, _ = GovernanceStore.audit at admin |> run |> ok
    let published = records |> List.find (fun r -> r.Record.Event = Audit.ReportDefinitionPublished)
    Assert.Equal<(string * string) list>([ "definition", "def-quarterly" ], published.Record.Ids)
    Assert.Equal<string list>([ "DEFINITION-VERSION-1" ], published.Record.Reasons)

    let stored = github.State.Objects |> Map.toList |> List.filter (fun (path, _) -> path.Contains "signal.audit") |> List.map (fun (_, o) -> o.Content)
    Assert.Contains(stored, fun text -> text.Contains "ReportDefinitionPublished")

    for text in stored do
        for forbidden in [ "octocat"; "583231"; "github:"; "hubot" ] do
            Assert.DoesNotContain(forbidden, text)

[<Fact>]
let ``a stored audit record that names a person is refused on read`` () =
    let _, admin, _ = dataset Grants.analyst
    let forged = { (GovernanceRecord.record Audit.ExportCreated [ "snapshot", "snap-0123456789" ] [] [] None None None |> ok) with Ids = [ "by", "octocat" ] }
    let context = (actor octocat).NewContext()
    let key = IdempotencyKey.value context.IdempotencyKey
    let path = GovernanceRecord.auditPath key forged |> ok
    let content = GovernanceRecord.encodeAudit admin.DatasetId key forged |> ok
    let operation = Storage.operation admin.Namespace context "forge" [ Change.Create(path, content) ] |> ok
    admin.Provider.Commit operation |> run |> ok |> ignore

    let records, problems = GovernanceStore.audit at admin |> run |> ok
    Assert.Single(problems) |> ignore
    Assert.DoesNotContain(records, fun r -> r.Record.Ids |> List.exists (snd >> (=) "octocat"))

[<Fact>]
let ``an archived or retired dataset is read-only until it is made active again`` () =
    let github, admin, analyst = dataset Grants.analyst

    match GovernanceStore.transition (actor hubot) at analyst Retention.Archived |> run with
    | Error(GovernanceStore.NotStored(GroupStore.NotPermitted _)) -> ()
    | other -> failwith $"%A{other}"

    Assert.Equal(Retention.Archived, GovernanceStore.transition (actor octocat) at admin Retention.Archived |> run |> ok)

    let archived = openAs github octocat
    Assert.Equal(Retention.Archived, archived.Lifecycle)
    Assert.True(archived.Grant.IsNone)
    Assert.Contains(Problems.DatasetNotActive "state-archived", archived.ReadOnlyReasons)

    match ReportStore.saveDefinition (actor octocat) at archived definition pins |> run with
    | Error(ReportStore.NotStored(GroupStore.ReadOnly reasons)) -> Assert.Contains(Problems.DatasetNotActive "state-archived", reasons)
    | other -> failwith $"%A{other}"

    match GovernanceStore.transition (actor octocat) at archived Retention.ProviderDeletionVerified |> run with
    | Error(GovernanceStore.TransitionRefused(Retention.IllegalTransition _)) -> ()
    | other -> failwith $"%A{other}"

    Assert.Equal(Retention.Active, GovernanceStore.transition (actor octocat) at archived Retention.Active |> run |> ok)
    let active = openAs github octocat
    Assert.True(active.Grant.IsSome)
    ReportStore.saveDefinition (actor octocat) at active definition pins |> run |> ok |> ignore

    Assert.Equal(Retention.Retired, GovernanceStore.transition (actor octocat) at active Retention.Retired |> run |> ok)
    let retired = openAs github octocat
    Assert.True(retired.Grant.IsNone)

    let changes = events retired |> List.filter ((=) "LifecycleChanged")
    Assert.Equal(3, changes.Length)
