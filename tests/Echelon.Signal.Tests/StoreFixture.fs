/// An in-memory Arca dataset for store tests: the deployment, two people,
/// actors with fresh idempotency keys, and a writable private repository.
module Echelon.Signal.Tests.StoreFixture

open System
open Arca
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Access
open Echelon.Signal.Application

let ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let run work = Async.RunSynchronously work
let at = DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.Zero)

let production =
    """{"environment":"production","environmentName":"production",
"identity":{"exchange":"https://fides.test","application":"signal-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://signal.test/admin/"},
"profiles":[{"id":"primary","label":"Survey data","provider":"github","location":{"owner":"acme","repository":"signal-data","branch":"main","basePath":"prod"}}],
"datasets":[{"id":"ds_engagement","label":"Engagement","profile":"primary","administrators":["583231"]}]}"""
    |> Deployment.parse
    |> ok

let person id name =
    { PrincipalId = $"github:{id}"
      Kind = Human
      DisplayName = name }

let octocat = person "583231" "octocat"
let hubot = person "9919" "hubot"
let keys = ref 0

let actor (principal: Principal) : Store.Actor =
    { Principal = principal
      SignIn = Credential.SignedIn(principal.PrincipalId.Substring 7)
      NewContext =
        fun () ->
            { Actor =
                { Kind = ActorKind.Human
                  Id = ActorId.create principal.PrincipalId |> ok }
              ProviderIdentity = Some(principal.PrincipalId.Substring 7)
              CorrelationId = CorrelationId.create "req-templates" |> ok
              IdempotencyKey = IdempotencyKey.create $"op-templates-{Threading.Interlocked.Increment keys:D6}" |> ok
              At = at } }

let backend (github: InMemoryStore) : Store.Backend =
    { Provider = fun _ -> github.Provider
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

let openAs (github: InMemoryStore) (who: Principal) =
    Store.openDataset (backend github) production (actor who) None "signal-test" "ds_engagement" at |> run |> ok

/// The dataset opened by its administrator, and as hubot holding `grants`.
let dataset (grants: Set<Capability>) =
    let github = InMemoryStore()
    let admin = openAs github octocat
    Store.changeRoster (actor octocat) (Admit(hubot, grants)) at admin |> run |> ok |> ignore
    github, openAs github octocat, openAs github hubot

