/// The administrator store over Arca (WI-0040), against Arca's in-memory
/// provider: setting a dataset up only for a configured administrator, one
/// command per commit with change tokens, conflicts decided again by
/// Signal's rules, unknown outcomes reconciled before anything is resent,
/// read-only credentials and protected branches, a repository that changed
/// identity, public production repositories, and offline without a queue.
module Echelon.Signal.Tests.AdminStoreTests

open System
open Xunit
open Arca
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Access
open Echelon.Signal.Application

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private run work = Async.RunSynchronously work

let private configText (environment: string) =
    $$$"""{"environment":"{{{environment}}}","environmentName":"{{{environment}}}",
"identity":{"exchange":"https://fides.test","application":"signal-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://signal.test/admin/"},
"profiles":[{"id":"primary","label":"Survey data","provider":"github","location":{"owner":"acme","repository":"signal-data","branch":"main","basePath":"prod"}}],
"datasets":[{"id":"ds_engagement","label":"Engagement","profile":"primary","administrators":["583231"]}]}"""

let private production = Deployment.parse (configText "production") |> ok
let private at = DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.Zero)

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
              CorrelationId = CorrelationId.create "req-store" |> ok
              IdempotencyKey = IdempotencyKey.create $"op-store-{Threading.Interlocked.Increment keys:D6}" |> ok
              At = at } }

/// What the credential resolves to at the repository.
type private Access' =
    { CanWrite: bool
      Branch: BranchAccess
      RepositoryId: string
      Visibility: RepositoryVisibility
      Reachable: bool }

let private writable =
    { CanWrite = true
      Branch = BranchAccess.Writable
      RepositoryId = "R_1"
      Visibility = RepositoryVisibility.Private
      Reachable = true }

let private backend (github: InMemoryStore) (access: unit -> Access') : Store.Backend =
    let unreachable () =
        async.Return(Error(StorageFailure.ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", true, "GitHub could not be reached")))

    { Provider =
        fun _ ->
            let real = github.Provider

            { real with
                Read = fun ns path -> if (access ()).Reachable then real.Read ns path else unreachable ()
                List = fun ns path -> if (access ()).Reachable then real.List ns path else unreachable ()
                ChangeToken = fun ns -> if (access ()).Reachable then real.ChangeToken ns else unreachable ()
                Commit = fun operation -> if (access ()).Reachable then real.Commit operation else unreachable () }
      Resolve =
        fun location ->
            let found = access ()

            if not found.Reachable then
                async.Return(Error(Credential.Unreachable "GitHub could not be reached"))
            else
                async.Return(
                    Ok
                        { Identity = { Provider = "github"; Subject = "583231"; Login = Some "octocat"; Kind = IdentityKind.User }
                          RepositoryId = found.RepositoryId
                          Repository = location.Repository
                          Visibility = found.Visibility
                          CanRead = true
                          CanWrite = found.CanWrite
                          Archived = false
                          Branch = found.Branch }
                ) }

let private openAs (github: InMemoryStore) (access: unit -> Access') (who: Principal) (pinned: string option) =
    Store.openDataset (backend github access) production (actor who) pinned "signal-test" "ds_engagement" at |> run

/// Commits made through the store (the in-memory history starts with the empty repository).
let private commits (github: InMemoryStore) = github.State.History.Length - InMemory.empty.History.Length

// ---- Setting a dataset up ------------------------------------------------------------------

[<Fact>]
let ``a configured administrator sets the dataset up in two commits and administers it`` () =
    let github = InMemoryStore()
    let opened = openAs github (fun () -> writable) octocat None |> ok

    // One commit for Signal's namespace, one for the dataset with its first administrator.
    Assert.Equal(2, commits github)
    Assert.Equal(3, github.State.History.Head.Touched.Count)
    Assert.Equal<string list>([ octocat.PrincipalId ], opened.Roster.Roster.Members |> Map.keys |> List.ofSeq)
    Assert.True(opened.Grant.IsSome)
    Assert.Equal(Credential.CredentialValidReadWrite, opened.Credential)
    Assert.Equal("R_1", opened.RepositoryId)

    // Opening again reads what is there and writes nothing.
    let again = openAs github (fun () -> writable) octocat (Some "R_1") |> ok
    Assert.Equal(2, commits github)
    Assert.Equal(opened.Roster.Roster, again.Roster.Roster)

[<Fact>]
let ``no one else sets a dataset up, and nothing is written for them`` () =
    let github = InMemoryStore()

    match openAs github (fun () -> writable) hubot None with
    | Error(Store.NeedsBootstrapAdministrator "ds_engagement") -> ()
    | other -> failwith $"%A{other}"

    Assert.Equal(0, commits github)

[<Fact>]
let ``production data is not started in a public repository`` () =
    let github = InMemoryStore()

    match openAs github (fun () -> { writable with Visibility = RepositoryVisibility.Public }) octocat None with
    | Error(Store.Refused [ Problems.PublicProductionRepository ]) -> ()
    | other -> failwith $"%A{other}"

    Assert.Equal(0, commits github)

// ---- One command, one commit ------------------------------------------------------------------

[<Fact>]
let ``a roster command is one commit and moves the change token`` () =
    let github = InMemoryStore()
    let opened = openAs github (fun () -> writable) octocat None |> ok
    let before = commits github

    let after = Store.changeRoster (actor octocat) (Admit(hubot, Grants.analyst)) at opened |> run |> ok

    Assert.Equal(before + 1, commits github)
    Assert.NotEqual(opened.Token, after.Token)
    Assert.True(permits after.Roster.Roster hubot.PrincipalId BuildReports)

    // The next command conditions on the new revisions and token.
    let granted = Store.changeRoster (actor octocat) (Grant(hubot.PrincipalId, ExportData)) at after |> run |> ok
    Assert.True(permits granted.Roster.Roster hubot.PrincipalId ExportData)

    // A reader sees the same roster.
    let reopened = openAs github (fun () -> writable) hubot (Some "R_1") |> ok
    Assert.Equal(granted.Roster.Roster, reopened.Roster.Roster)

[<Fact>]
let ``Signal's rules refuse before anything is sent`` () =
    let github = InMemoryStore()
    let opened = openAs github (fun () -> writable) octocat None |> ok
    let before = commits github

    match Store.changeRoster (actor octocat) (Remove octocat.PrincipalId) at opened |> run with
    | Error(Store.RefusedByRules [ LastAdministrator ]) -> ()
    | other -> failwith $"%A{other}"

    Assert.Equal(before, commits github)

// ---- Conflicts are decided again --------------------------------------------------------------

[<Fact>]
let ``two administrators removing each other cannot leave the dataset unmanaged`` () =
    let github = InMemoryStore()
    let first = openAs github (fun () -> writable) octocat None |> ok
    let both = Store.changeRoster (actor octocat) (Admit(hubot, Grants.administrator)) at first |> run |> ok

    // Each opens the dataset in their own tab.
    let mine = openAs github (fun () -> writable) octocat (Some "R_1") |> ok
    let theirs = openAs github (fun () -> writable) hubot (Some "R_1") |> ok
    Assert.Equal(both.Roster.Roster, theirs.Roster.Roster)

    // octocat removes hubot; it lands.
    let afterMine = Store.changeRoster (actor octocat) (Remove hubot.PrincipalId) at mine |> run |> ok
    Assert.Equal<string list>([ octocat.PrincipalId ], afterMine.Roster.Roster.Members |> Map.keys |> List.ofSeq)

    // hubot, from a stale tab, removes octocat: the records differ, but the
    // token moved; reloaded, hubot is no longer an administrator.
    match Store.changeRoster (actor hubot) (Remove octocat.PrincipalId) at theirs |> run with
    | Error(Store.RefusedByRules [ NotAMember _ ]) -> ()
    | other -> failwith $"%A{other}"

    let final = openAs github (fun () -> writable) octocat (Some "R_1") |> ok
    Assert.Equal<string list>([ octocat.PrincipalId ], final.Roster.Roster.Members |> Map.keys |> List.ofSeq)

[<Fact>]
let ``a change made elsewhere is kept: the command is decided again on top of it`` () =
    let github = InMemoryStore()
    let opened = openAs github (fun () -> writable) octocat None |> ok
    let stale = opened

    let other = Store.changeRoster (actor octocat) (Admit(hubot, Grants.analyst)) at opened |> run |> ok
    Assert.True(permits other.Roster.Roster hubot.PrincipalId ViewResults)

    let reviewer = person "4242" "reviewer"
    let after = Store.changeRoster (actor octocat) (Admit(reviewer, Grants.reviewer)) at stale |> run |> ok

    Assert.Equal<string list>(
        [ octocat.PrincipalId; reviewer.PrincipalId; hubot.PrincipalId ] |> List.sort,
        after.Roster.Roster.Members |> Map.keys |> List.ofSeq |> List.sort
    )

// ---- Unknown outcomes ----------------------------------------------------------------------------

[<Fact>]
let ``an unknown outcome that landed is reconciled, never sent twice`` () =
    let github = InMemoryStore()
    let opened = openAs github (fun () -> writable) octocat None |> ok
    let before = commits github

    github.Arrange InMemoryFault.OutcomeUnknownLanded
    let after = Store.changeRoster (actor octocat) (Admit(hubot, Grants.analyst)) at opened |> run |> ok

    Assert.Equal(before + 1, commits github)
    Assert.True(permits after.Roster.Roster hubot.PrincipalId ViewResults)

[<Fact>]
let ``an unknown outcome that did not land is decided and sent again once`` () =
    let github = InMemoryStore()
    let opened = openAs github (fun () -> writable) octocat None |> ok
    let before = commits github

    github.Arrange InMemoryFault.OutcomeUnknownLost
    let after = Store.changeRoster (actor octocat) (Admit(hubot, Grants.analyst)) at opened |> run |> ok

    Assert.Equal(before + 1, commits github)
    Assert.True(permits after.Roster.Roster hubot.PrincipalId ViewResults)

// ---- Read-only, protected, moved, offline ---------------------------------------------------------

[<Fact>]
let ``a read-only credential or a protected branch opens the dataset read-only`` () =
    let github = InMemoryStore()
    openAs github (fun () -> writable) octocat None |> ok |> ignore

    for access in [ { writable with CanWrite = false }; { writable with Branch = BranchAccess.NotWritable [ "pull request required" ] } ] do
        let opened = openAs github (fun () -> access) octocat (Some "R_1") |> ok
        Assert.True(opened.Grant.IsNone)

        match Store.changeRoster (actor octocat) (Admit(hubot, Grants.analyst)) at opened |> run with
        | Error(Store.ReadOnly [ Problems.WritesUnavailable "CredentialValidReadOnly" ]) -> ()
        | other -> failwith $"%A{other}"

[<Fact>]
let ``a configured location that now resolves to another repository is not silently used`` () =
    let github = InMemoryStore()
    openAs github (fun () -> writable) octocat None |> ok |> ignore

    match openAs github (fun () -> { writable with RepositoryId = "R_2" }) octocat (Some "R_1") with
    | Error(Store.CredentialNotUsable(Credential.ProviderIdentityMismatch("R_1", "R_2"))) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``offline, nothing is queued: the change fails and nothing is written later`` () =
    let github = InMemoryStore()
    let mutable reachable = true
    let access () = { writable with Reachable = reachable }
    let opened = openAs github access octocat None |> ok
    let before = commits github

    reachable <- false

    match Store.changeRoster (actor octocat) (Admit(hubot, Grants.analyst)) at opened |> run with
    | Error(Store.Offline meaning) -> Assert.True(meaning.NothingWritten)
    | other -> failwith $"%A{other}"

    // Back online, nothing that was attempted offline appears.
    reachable <- true
    Assert.Equal(before, commits github)
    let reopened = openAs github access octocat (Some "R_1") |> ok
    Assert.False(reopened.Roster.Roster.Members.ContainsKey hubot.PrincipalId)

    // Opening while offline reports it rather than inventing data.
    reachable <- false

    match openAs github access octocat (Some "R_1") with
    | Error(Store.Unreachable _) -> ()
    | other -> failwith $"%A{other}"
