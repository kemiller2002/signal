/// The shared set-up for durable-store tests over Arca's in-memory provider
/// (WI-0041, WI-0077): a deployment, an actor, a dataset with one group, and
/// links to import.
module Echelon.Signal.Tests.DurableFixture

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

let ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let run work = Async.RunSynchronously work
let pilot = Pilot.assessment
let opaque (seed: byte) = (OpaqueId.ofBytes (Array.init 16 (fun i -> seed + byte i))).Value
let groupId = opaque 200uy
let resolve: GroupRecord.TemplateResolver = fun hash -> if hash = Canonical.templateHash pilot then Some(pilot, None) else None

let all (answer: Answer) = pilot.Items |> List.map (fun item -> item.Id, answer) |> Map.ofList

let link (binding: Binding) (answers: Answers) =
    "https://signal.example" + LiveUrl.urlFor pilot "/web/" "" { Binding = binding; Answers = answers }

let anonymous (seed: byte) answer = link (Anonymous(opaque seed, groupId)) (all (Rated answer))
let identified (seed: byte) answer = link (Identified(opaque seed, groupId)) (all (Rated answer))

let configText =
    """{"environment":"production","environmentName":"production",
"identity":{"exchange":"https://fides.test","application":"signal-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://signal.test/admin/"},
"profiles":[{"id":"primary","label":"Survey data","provider":"github","location":{"owner":"acme","repository":"signal-data","branch":"main","basePath":"prod"}}],
"datasets":[{"id":"ds_engagement","label":"Engagement","profile":"primary","administrators":["583231"]}]}"""

let config = Deployment.parse configText |> ok
let at = DateTimeOffset(2026, 10, 8, 16, 0, 0, TimeSpan.Zero)
let octocat = { PrincipalId = "github:583231"; Kind = Human; DisplayName = "octocat" }
let keys = ref 0

let actor: Store.Actor =
    { Principal = octocat
      SignIn = Credential.SignedIn "583231"
      NewContext =
        fun () ->
            { Actor = { Kind = ActorKind.Human; Id = ActorId.create octocat.PrincipalId |> ok }
              ProviderIdentity = None
              CorrelationId = CorrelationId.create "req-import" |> ok
              IdempotencyKey = IdempotencyKey.create $"op-import-{Threading.Interlocked.Increment keys:D6}" |> ok
              At = at } }

let backend (github: InMemoryStore) (reachable: unit -> bool) : Store.Backend =
    let offline () = async.Return(Error(StorageFailure.ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", true, "offline")))

    { Provider =
        fun _ ->
            let real = github.Provider

            { real with
                Read = fun ns p -> if reachable () then real.Read ns p else offline ()
                List = fun ns p -> if reachable () then real.List ns p else offline ()
                Commit = fun o -> if reachable () then real.Commit o else offline () }
      Resolve =
        fun location ->
            async.Return(
                Ok
                    { Identity = { Provider = "github"; Subject = "583231"; Login = Some "octocat"; Kind = IdentityKind.User }
                      RepositoryId = "R_1"
                      Repository = location.Repository
                      Visibility = RepositoryVisibility.Private
                      CanRead = true
                      CanWrite = true
                      Archived = false
                      Branch = BranchAccess.Writable }
            ) }

let groupConfig mode expected retention : GroupRecord.GroupConfig =
    { Group = groupId
      Mode = mode
      ExpectedCount = expected
      SurveyIdentifier = pilot.Id
      TemplateVersion = pilot.Version
      TemplateHash = Canonical.templateHash pilot
      MinimumReportableCount = 2
      Retention = retention
      Revision = 1 }

/// The same backend, with its provider changed by `shape`.
let reshaped (shape: StorageProvider -> StorageProvider) (inner: Store.Backend) : Store.Backend =
    { inner with Provider = fun location -> shape (inner.Provider location) }

/// A dataset with one group, opened, on `store`.
let setUpOn (store: Store.Backend) mode retention =
    let opened = Store.openDataset store config actor None "signal-test" "ds_engagement" at |> run |> ok
    GroupStore.create actor (groupConfig mode 10 retention) at opened |> run |> ok
    GroupStore.openGroup resolve groupId at opened |> run |> ok

let setUp (github: InMemoryStore) (reachable: unit -> bool) mode retention = setUpOn (backend github reachable) mode retention

let import texts group = GroupStore.importBatch actor resolve ResultRecord.MultiPaste texts at group |> run |> ok

let stateOf (texts: string list) mode =
    texts
    |> List.fold (fun s t -> importOne s t |> fst) (empty { Group = groupId; Mode = mode; ExpectedCount = 10; Template = pilot; Generic = None })
