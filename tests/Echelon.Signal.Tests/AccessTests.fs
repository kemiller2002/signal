/// Administrators, capabilities and the credential (WI-0040): capability-based
/// authorization, bootstrap administrators by GitHub numeric id, the roster
/// as records changed one command per commit, and ADM-056/070/071/072.
module Echelon.Signal.Tests.AccessTests

open System
open Xunit
open Arca
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Access

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private person id name =
    { PrincipalId = $"github:{id}"
      Kind = Human
      DisplayName = name }

let private octocat = person "583231" "octocat"
let private hubot = person "9919" "hubot"
let private importer = { PrincipalId = "service:importer"; Kind = Service; DisplayName = "Importer" }
let private roster = founded "ds_engagement" octocat

let private refusals result =
    match result with
    | Ok _ -> []
    | Error found -> found |> List.map refusalCode

// ---- Capability-based authorization ----------------------------------------------------

[<Fact>]
let ``every check is a capability in a dataset, never a role name`` () =
    Assert.True(permits roster octocat.PrincipalId ManageAdministrators)
    Assert.Equal(Error(NotAMember hubot.PrincipalId), authorize roster "ds_engagement" hubot.PrincipalId ViewResults)
    Assert.Equal(Error(WrongDataset("ds_hr", "ds_engagement")), authorize roster "ds_hr" octocat.PrincipalId ViewResults)

    let withAnalyst = execute octocat.PrincipalId (Admit(hubot, Grants.analyst)) roster |> ok
    Assert.True(permits withAnalyst hubot.PrincipalId BuildReports)
    Assert.Equal(Error(NotHeld PublishTemplates), authorize withAnalyst "ds_engagement" hubot.PrincipalId PublishTemplates)
    Assert.Equal("This needs PublishTemplates, which you do not hold here.", explain withAnalyst hubot.PrincipalId PublishTemplates)

[<Fact>]
let ``drafting, reviewing and publishing are separate capabilities`` () =
    let r =
        roster
        |> execute octocat.PrincipalId (Admit(hubot, Grants.draftEditor))
        |> ok

    Assert.True(permits r hubot.PrincipalId EditDrafts)
    Assert.False(permits r hubot.PrincipalId ReviewTemplates)
    Assert.False(permits r hubot.PrincipalId PublishTemplates)

[<Fact>]
let ``only people vouch: review, publication, sealing and finalization`` () =
    Assert.Equal<string list>(
        [ "SIGNAL.ACCESS.NOT_FOR_KIND" ],
        execute octocat.PrincipalId (Admit(importer, set [ ImportSubmissions; PublishTemplates ])) roster |> refusals
    )

    let withImporter = execute octocat.PrincipalId (Admit(importer, Grants.importer)) roster |> ok
    Assert.Equal<string list>([ "SIGNAL.ACCESS.NOT_FOR_KIND" ], execute octocat.PrincipalId (Grant(importer.PrincipalId, SealAndFinalize)) withImporter |> refusals)
    Assert.Equal(Grants.forKind Service Grants.administrator |> Set.contains ReviewTemplates, false)

[<Fact>]
let ``the dataset always keeps someone who can manage its administrators`` () =
    Assert.Equal<string list>([ "SIGNAL.ACCESS.LAST_ADMINISTRATOR" ], execute octocat.PrincipalId (Remove octocat.PrincipalId) roster |> refusals)
    Assert.Equal<string list>([ "SIGNAL.ACCESS.LAST_ADMINISTRATOR" ], execute octocat.PrincipalId (Revoke(octocat.PrincipalId, ManageAdministrators)) roster |> refusals)

    let two = execute octocat.PrincipalId (Admit(hubot, Grants.administrator)) roster |> ok
    let one = execute hubot.PrincipalId (Remove octocat.PrincipalId) two |> ok
    Assert.Equal<string list>([ hubot.PrincipalId ], one.Members |> Map.keys |> List.ofSeq)
    // A non-manager changes nothing.
    let analyst = execute octocat.PrincipalId (Admit(hubot, Grants.analyst)) roster |> ok
    Assert.Equal<string list>([ "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" ], execute hubot.PrincipalId (Remove octocat.PrincipalId) analyst |> refusals)

// ---- Bootstrap administrators come from configuration, by GitHub numeric id ------------------

[<Fact>]
let ``only a configured GitHub account number is a bootstrap administrator`` () =
    let dataset: Deployment.DatasetConfig =
        { Id = "ds_engagement"
          Label = "Engagement"
          Profile = "primary"
          Administrators = [ "583231" ] }

    Assert.True(Deployment.isBootstrapAdministrator dataset "github:583231")
    Assert.False(Deployment.isBootstrapAdministrator dataset "github:octocat")
    Assert.False(Deployment.isBootstrapAdministrator dataset "583231")
    Assert.False(Deployment.isBootstrapAdministrator dataset "gitlab:583231")
    Assert.False(Deployment.isBootstrapAdministrator dataset "github:5832310")

// ---- The roster as records ----------------------------------------------------------------------

[<Fact>]
let ``a membership record round-trips and refuses what this version does not know`` () =
    let membership = roster.Members[octocat.PrincipalId]
    let text = AdministratorRecord.encode "ds_engagement" membership |> ok
    let record = Record.decode Record.DefaultMaxBytes text |> ok
    let back = AdministratorRecord.ofBody record.Body |> ok

    Assert.Equal(membership, back.Membership)
    Assert.Equal("records/signal.administrator/github_3a583231.json", AdministratorRecord.path octocat.PrincipalId |> ok |> RelativePath.render)

    let body = Json.canonicalText record.Body
    let reject (edited: string) = Json.parse edited |> ok |> AdministratorRecord.ofBody |> Result.isError

    Assert.True(reject (body.Replace("\"ViewResults\"", "\"RunAnything\"")))
    Assert.True(reject (body.Replace("\"Human\"", "\"Agent\"")))
    Assert.True(reject (body.Replace("\"revision\":1", "\"revision\":0")))
    Assert.True(reject (body.Replace("\"kind\"", "\"email\":\"a@b.c\",\"kind\"")))

[<Fact>]
let ``distinct principals have distinct record ids`` () =
    let ids = [ "github:583231"; "github_583231"; "github-583231"; "service:importer"; "-x" ] |> List.map AdministratorRecord.idOf
    Assert.Equal(ids.Length, (List.distinct ids).Length)
    Assert.All(ids, fun id -> Assert.True(RecordId.create id |> Result.isOk, id))

let private storedRoster (r: Roster) : RosterStore.StoredRoster =
    { Roster = r
      Stored =
        r.Members
        |> Map.map (fun principalId membership ->
            { Value = { DatasetId = r.DatasetId; Membership = membership }
              Path = AdministratorRecord.path principalId |> ok
              Revision = Revision $"rev-{principalId}"
              ContentHash = "" } : Loading.Stored<AdministratorRecord.StoredMembership>)
      Unusable = Map.empty
      Problems = [] }

[<Fact>]
let ``a roster change writes only what changed, conditioned on what was read`` () =
    let two = execute octocat.PrincipalId (Admit(hubot, Grants.analyst)) roster |> ok
    let stored = storedRoster two
    let after = execute octocat.PrincipalId (Grant(hubot.PrincipalId, ExportData)) two |> ok |> execute octocat.PrincipalId (Grant(hubot.PrincipalId, ImportSubmissions)) |> ok

    match RosterStore.changes stored after |> ok with
    | [ Change.Update(path, _, revision) ] ->
        Assert.Equal("records/signal.administrator/github_3a9919.json", RelativePath.render path)
        Assert.Equal(Revision "rev-github:9919", revision)
    | other -> failwith $"%A{other}"

    let removed = execute octocat.PrincipalId (Remove hubot.PrincipalId) two |> ok

    match RosterStore.changes stored removed |> ok with
    | [ Change.Delete(_, revision) ] -> Assert.Equal(Revision "rev-github:9919", revision)
    | other -> failwith $"%A{other}"

    // A membership whose stored record was unusable is repaired before it is changed.
    let broken = { stored with Unusable = Map.ofList [ "records/signal.administrator/github_3a9919.json", Revision "x" ] }
    Assert.True(RosterStore.changes broken after |> Result.isError)

// ---- ADM-056: the credential's state -------------------------------------------------------------

let private snapshot canWrite branch repositoryId =
    { Identity = { Provider = "github"; Subject = "583231"; Login = Some "octocat"; Kind = IdentityKind.User }
      RepositoryId = repositoryId
      Repository = RepositoryRef.create "acme" "signal-data" |> ok
      Visibility = RepositoryVisibility.Private
      CanRead = true
      CanWrite = canWrite
      Archived = false
      Branch = branch }

[<Fact>]
let ``the credential's state distinguishes every case ADM-056 names`` () =
    let state signIn pinned resolved = Credential.assess signIn pinned resolved |> Credential.stateCode
    let signedIn = Credential.SignedIn "583231"

    Assert.Equal("CredentialMissing", state Credential.SignedOut None None)
    Assert.Equal("CredentialExpired", state Credential.Expired None None)
    Assert.Equal("CredentialRejected", state Credential.Revoked None None)
    Assert.Equal("CredentialRejected", state signedIn None (Some(Error Credential.Rejected)))
    Assert.Equal("CredentialExpired", state signedIn None (Some(Error(Credential.NoUsableToken TokenUnavailable.Expired))))
    Assert.Equal("PermissionInsufficient", state signedIn None (Some(Error(Credential.RepositoryNotVisible "acme/signal-data"))))
    Assert.Equal("ProviderIdentityMismatch", state signedIn (Some "R_old") (Some(Ok(snapshot true BranchAccess.Writable "R_new"))))
    Assert.Equal("CredentialValidReadOnly", state signedIn (Some "R_1") (Some(Ok(snapshot false BranchAccess.Writable "R_1"))))
    Assert.Equal("CredentialValidReadOnly", state signedIn None (Some(Ok(snapshot true (BranchAccess.NotWritable [ "reviews" ]) "R_1"))))
    Assert.Equal("CredentialValidReadWrite", state signedIn (Some "R_1") (Some(Ok(snapshot true BranchAccess.Writable "R_1"))))
    Assert.Equal("CredentialUnverifiable", state signedIn None (Some(Error(Credential.Unreachable "offline"))))

[<Fact>]
let ``permissions are recomputed from the credential: less privilege withholds changes`` () =
    let usable state connectivity grant = Credential.usable roster octocat.PrincipalId state connectivity grant |> fst

    Assert.Equal<Set<Capability>>(Grants.administrator, usable Credential.CredentialValidReadWrite Credential.Online true)

    // Read-only: viewing and exporting stay; every change is withheld, with the reason.
    let readOnly, withheld = Credential.usable roster octocat.PrincipalId (Credential.CredentialValidReadOnly "archived") Credential.Online true
    Assert.Equal<Set<Capability>>(set [ ViewResults; ExportData ], readOnly)
    Assert.All(withheld, fun w -> Assert.StartsWith("read-only", w.Reason))

    // A rejected or expired credential keeps nothing, not even what was loaded.
    Assert.Empty(usable Credential.CredentialRejected Credential.Online true)
    Assert.Empty(usable Credential.CredentialExpired Credential.Online true)

    // Unverified stored data keeps the dataset read-only.
    Assert.Equal<Set<Capability>>(set [ ViewResults; ExportData ], usable Credential.CredentialValidReadWrite Credential.Online false)

[<Fact>]
let ``offline, verified data stays viewable and every change is withheld, never queued`` () =
    let offline = Credential.Degraded("the provider cannot be reached", Some(DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero)))
    let held, withheld = Credential.usable roster octocat.PrincipalId (Credential.CredentialUnverifiable "offline") offline true

    Assert.Equal<Set<Capability>>(set [ ViewResults; ExportData ], held)
    Assert.Equal(Access.mutating.Count, withheld.Length)
    Assert.All(withheld, fun w -> Assert.Contains("offline", w.Reason))

// ---- ADM-071 and ADM-072 ----------------------------------------------------------------------------

[<Fact>]
let ``the safest retention is the default and nothing survives a browser restart`` () =
    Assert.Equal(Credential.ThisPage, Credential.defaultRetention)
    Assert.Equal<Credential.Retention list>([ Credential.ThisPage; Credential.ThisTab ], Credential.offeredRetentions)
    Assert.All(Credential.offeredRetentions, fun r -> Assert.NotEmpty(Credential.disclosure r))

[<Fact>]
let ``another tab's notice only ever downgrades`` () =
    Assert.Equal(Credential.CredentialMissing, Credential.afterNotice Credential.SignedOutElsewhere Credential.CredentialValidReadWrite)

    match Credential.afterNotice Credential.RosterChangedElsewhere Credential.CredentialValidReadWrite with
    | Credential.CredentialUnverifiable _ -> ()
    | other -> failwith $"%A{other}"

    // A notice never grants: a rejected credential stays rejected.
    Assert.Equal(Credential.CredentialRejected, Credential.afterNotice Credential.SessionRenewedElsewhere Credential.CredentialRejected)
