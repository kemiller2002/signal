/// The template catalog through Arca (WI-0057), against Arca's in-memory
/// provider: drafts saved at a revision, publication stored once as an
/// immutable record whose hash is checked on every read, hidden versions
/// that stay resolvable, and the capabilities each step needs.
module Echelon.Signal.Tests.TemplateStoreTests

open System
open Xunit
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Layout
open Echelon.Signal.Engine.Instance
open Echelon.Signal.Engine.Drafts
open Echelon.Signal.Engine.Publication
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Access
open Echelon.Signal.Application

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private run work = Async.RunSynchronously work
let private at = DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.Zero)

let private production =
    """{"environment":"production","environmentName":"production",
"identity":{"exchange":"https://fides.test","application":"signal-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://signal.test/admin/"},
"profiles":[{"id":"primary","label":"Survey data","provider":"github","location":{"owner":"acme","repository":"signal-data","branch":"main","basePath":"prod"}}],
"datasets":[{"id":"ds_engagement","label":"Engagement","profile":"primary","administrators":["583231"]}]}"""
    |> Deployment.parse
    |> ok

let private person id name =
    { PrincipalId = $"github:{id}"
      Kind = Human
      DisplayName = name }

let private octocat = person "583231" "octocat"
let private hubot = person "9919" "hubot"
let private keys = ref 0

let private actor (principal: Principal) : Store.Actor =
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

let private backend (github: InMemoryStore) : Store.Backend =
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

let private openAs (github: InMemoryStore) (who: Principal) =
    Store.openDataset (backend github) production (actor who) None "signal-test" "ds_engagement" at |> run |> ok

/// The dataset opened by its administrator, and as hubot holding `grants`.
let private dataset (grants: Set<Capability>) =
    let github = InMemoryStore()
    let admin = openAs github octocat
    Store.changeRoster (actor octocat) (Admit(hubot, grants)) at admin |> run |> ok |> ignore
    github, openAs github octocat, openAs github hubot

let private q id : Question =
    { Id = id
      Prompt = $"How often does {id} happen?"
      HelpText = None
      Answer = Ordinal 5
      Selector = { Preset = Frequency5; Labels = [ "0"; "1"; "2"; "3"; "4" ] }
      SpecialStates = [ DontKnow ]
      Required = true
      Tags = [] }

let private draft =
    newDraft "team-health" "Team health"
    |> addSection
        { Id = "s"
          Title = "Section s"
          Description = None
          Required = true
          Questions = [ q "q1"; q "q2"; q "q3" ]
          Presentation = defaultSectionPresentation
          Scoring =
            Some
                { Scorer =
                    { Scale = Scoring.Direct
                      Aggregate = Scoring.Mean
                      Transforms = []
                      Missing = { MinimumObservations = 1; Special = Scoring.Exclude }
                      Decimals = 2 }
                  Questions = [] } }
    |> Result.bind (
        addFixture
            { Id = "all-fours"
              Name = "Every answer is 4"
              Answers = Map [ for i in 1..3 -> $"q{i}", Value(Point 4) ]
              Expect = [ ExpectSectionScore("s", Some 4.0); ExpectComplete true ] }
    )
    |> ok

let private warnings (d: Draft) =
    (Validation.validate Validation.defaultPolicy d).Warnings |> List.map _.Code |> Set.ofList

let private publish who opened d =
    TemplateStore.publish (actor who) Validation.defaultPolicy (warnings d) at opened d |> run

let private load opened = TemplateStore.load at opened |> run |> ok

[<Fact>]
let ``a saved draft reloads exactly and a stale revision is a conflict, not a lost write`` () =
    let _, admin, _ = dataset Grants.draftEditor
    TemplateStore.saveDraft (actor octocat) at admin None draft |> run |> ok

    let stored = load admin
    let reloaded, revision = stored.Drafts["team-health"]
    Assert.Equal(draft, reloaded)
    Assert.Empty(stored.Problems)

    let renamed = draft |> editContent (fun c -> { c with Metadata = { c.Metadata with Title = "Team health v2" } })
    TemplateStore.saveDraft (actor octocat) at admin (Some revision) renamed |> run |> ok
    Assert.Equal(renamed, fst (load admin).Drafts["team-health"])

    // The revision the first save was read at is gone: the provider refuses it.
    match TemplateStore.saveDraft (actor octocat) at admin (Some revision) draft |> run with
    | Error(GroupStore.Storage _) -> ()
    | other -> failwith $"%A{other}"

    Assert.Equal(renamed, fst (load admin).Drafts["team-health"])

[<Fact>]
let ``publishing stores the template once; a reread verifies its hash and the next version is 2`` () =
    let github, admin, _ = dataset Grants.draftEditor
    let first = publish octocat admin draft |> ok
    Assert.Equal("1", first.Version)

    let stored = load admin
    Assert.Equal<Published list>([ first ], stored.Catalog.Templates)
    Assert.True(Publication.verify stored.Catalog.Templates.Head)

    // Publishing the same content again is refused by the domain, and nothing is written.
    let before = github.State.History.Length

    match publish octocat admin draft with
    | Error(TemplateStore.Refused(Unchanged "1")) -> ()
    | other -> failwith $"%A{other}"

    Assert.Equal(before, github.State.History.Length)

    let child =
        { deriveDraft first with Content = { first.Content with Metadata = { first.Content.Metadata with Title = "Team health, revised" } } }

    let second = publish octocat admin child |> ok
    Assert.Equal("2", second.Version)
    Assert.Equal<string list>([ "1"; "2" ], (load admin).Catalog.Templates |> List.map _.Version |> List.sort)

[<Fact>]
let ``a hidden version stays resolvable and the hiding survives a reload`` () =
    let _, admin, _ = dataset Grants.draftEditor
    let first = publish octocat admin draft |> ok
    TemplateStore.hide (actor octocat) at admin "team-health" "1" |> run |> ok

    let stored = load admin
    Assert.Equal(HiddenFromDistribution, Publication.visibility stored.Catalog first)
    Assert.Equal(Some first, Publication.resolve stored.Catalog "team-health" "1")

    match TemplateStore.hide (actor octocat) at admin "team-health" "9" |> run with
    | Error(GroupStore.Unusable _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a draft editor saves drafts but cannot publish, and an analyst can do neither`` () =
    let _, _, editor = dataset Grants.draftEditor
    TemplateStore.saveDraft (actor hubot) at editor None draft |> run |> ok

    match publish hubot editor draft with
    | Error(TemplateStore.NotStored(GroupStore.NotPermitted _)) -> ()
    | other -> failwith $"%A{other}"

    let _, _, analyst = dataset Grants.analyst

    match TemplateStore.saveDraft (actor hubot) at analyst None draft |> run with
    | Error(GroupStore.NotPermitted _) -> ()
    | other -> failwith $"%A{other}"

    match TemplateStore.hide (actor hubot) at analyst "team-health" "1" |> run with
    | Error(GroupStore.NotPermitted _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a stored template whose bytes do not hash to its recorded hash is a problem, never a template`` () =
    let _, admin, _ = dataset Grants.draftEditor
    let first = publish octocat admin draft |> ok
    let forged = { first with Version = "7"; Hash = String('0', 64) }

    let operation =
        Storage.operation
            admin.Namespace
            ((actor octocat).NewContext())
            "forge"
            [ Change.Create(TemplateRecord.publishedPath forged |> ok, TemplateRecord.encodePublished admin.DatasetId forged |> ok) ]
        |> ok

    admin.Provider.Commit operation |> run |> ok |> ignore

    let stored = load admin
    Assert.Equal<Published list>([ first ], stored.Catalog.Templates)
    Assert.Single(stored.Problems) |> ignore
    Assert.Equal(None, Publication.resolve stored.Catalog "team-health" "7")
