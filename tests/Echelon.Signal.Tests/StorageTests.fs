/// Data location, Signal's namespace, storage profiles and the storage
/// manifest (WI-0038): SIG-DATALOC-001..005, ADM-004 (location and
/// namespace), ADM-005 and ADM-057, proven against Arca's in-memory provider.
module Echelon.Signal.Tests.StorageTests

open System
open Xunit
open Arca
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Storage

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private codes (result: Result<'a, Problem list>) =
    match result with
    | Ok _ -> []
    | Error problems -> problems |> List.map code

let private codeOf (result: Result<'a, Problem>) =
    match result with
    | Ok _ -> "ok"
    | Error problem -> code problem

let private identity =
    """{"exchange":"https://fides.test","application":"signal-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://signal.test/admin/"}"""

let private location (owner: string) (repository: string) (basePath: string) =
    $$"""{"owner":"{{owner}}","repository":"{{repository}}","branch":"main","basePath":"{{basePath}}"}"""

/// A deployment in `environment` with a primary profile at owner/repository/base
/// and a second profile in its own repository.
let private configText (environment: string) (owner: string) (repository: string) (basePath: string) =
    $$"""{"environment":"{{environment}}","environmentName":"{{environment}}","identity":{{identity}},
"profiles":[{"id":"primary","label":"Survey data","provider":"github","location":{{location owner repository basePath}}},
            {"id":"hr","label":"HR surveys","provider":"github","location":{{location "acme-hr" "signal-hr" ""}}}],
"datasets":[{"id":"ds_engagement","label":"Engagement","profile":"primary","administrators":["583231"]},
            {"id":"ds_hr","label":"HR pulse","profile":"hr","administrators":["583231","9919"]}]}"""

let private production = configText "production" "acme" "signal-data" "deployments/prod" |> Deployment.parse |> ok

let private at = DateTimeOffset(2026, 10, 8, 14, 30, 0, TimeSpan.Zero)

let private context (key: string) =
    { Actor =
        { Kind = ActorKind.Human
          Id = ActorId.create "github:583231" |> ok }
      ProviderIdentity = Some "583231"
      CorrelationId = CorrelationId.create "req-1" |> ok
      IdempotencyKey = IdempotencyKey.create $"op-{key}-0001" |> ok
      At = at }

let private committed (operation: Operation) (state: InMemoryState) =
    match InMemory.commit operation state with
    | Ok _, next -> next
    | Error failure, _ -> failwith $"%A{failure}"

let private read ns path state = InMemory.read ns path state |> fst |> ok

let private manifestPath = Layout.manifestPath |> ok

/// A deployment with Signal's namespace and the engagement dataset set up.
let private initialized (config: Deployment.DeploymentConfig) =
    let binding = binding config |> ok

    let state =
        InMemory.empty
        |> committed (initializeApplication binding RepositoryVisibility.Private None (context "app") |> ok)
        |> committed (initializeDataset config binding RepositoryVisibility.Private None (context "ds") "signal-test" "ds_engagement" [] |> ok)

    binding, state

// ---- SIG-DATALOC-001: the location is configuration ----------------------------------

[<Fact>]
let ``the data location is the deployment's configuration, never a constant`` () =
    let elsewhere = configText "production" "globex" "survey-store" "apps/signal-prod" |> Deployment.parse |> ok

    let rootOf config =
        binding config |> Result.bind applicationNamespace |> ok |> fun ns -> ns.Location.Repository.ToString(), root ns

    Assert.Equal(("acme/signal-data", "deployments/prod/signal"), rootOf production)
    Assert.Equal(("globex/survey-store", "apps/signal-prod/signal"), rootOf elsewhere)

[<Fact>]
let ``no repository, owner, branch or base path is written into Signal's source`` () =
    let sources =
        IO.Directory.GetFiles(Support.repoFile "src/Echelon.Signal.Admin", "*.fs")
        |> Array.map IO.File.ReadAllText
        |> String.concat "\n"

    // The documentation example aside, no code names a location.
    let code = sources.Split('\n') |> Array.filter (fun line -> not (line.TrimStart().StartsWith "///")) |> String.concat "\n"
    Assert.DoesNotContain("signal-data", code)
    Assert.DoesNotContain("\"main\"", code)
    Assert.DoesNotContain("kemiller2002", code)
    Assert.DoesNotContain("github.com", code)

[<Fact>]
let ``a configuration that is not valid is refused with the reason`` () =
    let refused (text: string) = Deployment.parse text |> codeOf
    let invalid = "SIGNAL.STORAGE.INVALID_CONFIGURATION"
    let valid = configText "production" "acme" "signal-data" "deployments/prod"

    Assert.Equal("ok", refused valid)
    // A token has no place in the configuration (ADM-004): unknown fields are refused.
    Assert.Equal(invalid, refused (valid.Replace("\"label\":\"Survey data\",", "\"label\":\"Survey data\",\"token\":\"ghp_x\",")))
    // A dataset may not live in a profile that is not configured (ADM-057).
    Assert.Equal(invalid, refused (valid.Replace("\"profile\":\"hr\"", "\"profile\":\"archive\"")))
    // An installable service is a future provider (ADM-006): refused, not simulated.
    Assert.Equal(invalid, refused (valid.Replace("\"provider\":\"github\",\"location\"", "\"provider\":\"service\",\"location\"")))
    // Administrators are GitHub account numbers, never logins.
    Assert.Equal(invalid, refused (valid.Replace("\"583231\",\"9919\"", "\"octocat\"")))
    Assert.Equal(invalid, refused (valid.Replace("\"583231\",\"9919\"", "\"9919\",\"9919\"")))
    Assert.Equal("SIGNAL.STORAGE.INVALID_LOCATION", refused (valid.Replace("\"owner\":\"acme\"", "\"owner\":\"-acme\"")))
    Assert.Equal(invalid, refused (valid.Replace("\"id\":\"primary\"", "\"id\":\"hr\"")))
    Assert.Equal("SIGNAL.STORAGE.INVALID_DATASET_ID", refused (valid.Replace("ds_engagement", "ds engagement")))
    // Stored data needs someone signed in to write it.
    Assert.Equal(invalid, refused (valid.Replace($"\"identity\":{identity},", "")))
    // A local deployment stores nothing and needs none of it.
    Assert.Equal("ok", refused """{"environment":"local","environmentName":"local"}""")

// ---- SIG-DATALOC-002/003: Signal's own namespace in a shared repository --------------------

[<Fact>]
let ``Signal owns base-path/signal and every dataset folder is inside it`` () =
    let binding = binding production |> ok
    let app = applicationNamespace binding |> ok
    let engagement = datasetNamespace production binding "ds_engagement" |> ok

    Assert.Equal("deployments/prod/signal", root app)
    Assert.Equal("deployments/prod/signal/datasets/ds_engagement", root engagement)
    Assert.Equal(Some(DatasetId.create "ds_engagement" |> ok), engagement.Dataset)
    Assert.Equal("UNKNOWN_DATASET", datasetNamespace production binding "ds_other" |> codeOf |> fun c -> c.Substring 15)

[<Fact>]
let ``setting Signal up beside another application's data leaves that data alone`` () =
    let binding = binding production |> ok
    let primary = binding.Location

    let shared =
        InMemory.empty
        |> InMemory.writeExternally primary "deployments/prod/summa/arca-manifest.json" (Some "summa's manifest")
        |> InMemory.writeExternally primary "README.md" (Some "the repository's own file")

    let state =
        shared
        |> committed (initializeApplication binding RepositoryVisibility.Private None (context "app") |> ok)
        |> committed (initializeDataset production binding RepositoryVisibility.Private None (context "ds") "signal-test" "ds_engagement" [] |> ok)

    let other = Namespace.ofApplication { binding with Application = AppId.create "summa" |> ok } |> ok

    match read other manifestPath state with
    | ReadOutcome.Found found -> Assert.Equal("summa's manifest", found.Content)
    | ReadOutcome.Absent -> failwith "the other application's file is gone"

    // Every operation Signal builds stays inside its namespace: one outside is refused by Arca.
    let app = applicationNamespace binding |> ok
    let outside = RelativePath.parse "../summa/arca-manifest.json"
    Assert.True(Result.isError outside || Result.isError (Namespace.resolve app (ok outside)))

// ---- SIG-DATALOC-004 / ADM-057: separable permissions through profiles ------------------

[<Fact>]
let ``a dataset in its own profile lives in that profile's repository`` () =
    let binding = binding production |> ok
    let hr = datasetNamespace production binding "ds_hr" |> ok

    Assert.Equal("acme-hr/signal-hr", hr.Location.Repository.ToString())
    Assert.Equal("signal/datasets/ds_hr", root hr)

[<Fact>]
let ``a profile's label is presentation: relabelling moves nothing`` () =
    let relabelled =
        (configText "production" "acme" "signal-data" "deployments/prod").Replace("Survey data", "All surveys").Replace("HR pulse", "People pulse")
        |> Deployment.parse
        |> ok

    let rootsOf config =
        let binding = binding config |> ok
        [ "ds_engagement"; "ds_hr" ] |> List.map (datasetNamespace config binding >> ok >> fun ns -> ns.Location, root ns)

    Assert.Equal<(DataLocation * string) list>(rootsOf production, rootsOf relabelled)

[<Fact>]
let ``pointing a dataset at another location is a migration, never an edit`` () =
    let _, state = initialized production

    // The operator edits the profile to another repository; the data did not move.
    let moved = configText "production" "acme" "signal-archive" "deployments/prod" |> Deployment.parse |> ok
    let movedBinding = binding moved |> ok
    let ns = datasetNamespace moved movedBinding "ds_engagement" |> ok
    let original = datasetNamespace production (binding production |> ok) "ds_engagement" |> ok

    // At the new location nothing was set up.
    Assert.Equal<string list>([ "SIGNAL.STORAGE.NOT_INITIALIZED" ], openNamespace ns (read ns manifestPath state) |> codes)

    // A copy of the folder's manifest at the new place names where the data really is.
    let copied = InMemory.writeExternally ns.Location "deployments/prod/signal/datasets/ds_engagement/arca-manifest.json" (Some((read original manifestPath state |> function ReadOutcome.Found f -> f.Content | _ -> ""))) state
    Assert.Equal<string list>([ "SIGNAL.STORAGE.NAMESPACE_UNUSABLE" ], openNamespace ns (read ns manifestPath copied) |> codes)

[<Fact>]
let ``a dataset in the middle of a migration is not mistaken for an active store`` () =
    let binding, state = initialized production
    let ns = datasetNamespace production binding "ds_engagement" |> ok

    let migrating =
        match read ns manifestPath state with
        | ReadOutcome.Found found ->
            let manifest = Manifest.decode found.Content |> ok
            let text = Manifest.encode { manifest with Migration = Some { MigrationId = "mig-1"; Phase = MigrationPhase.Copying } }
            ReadOutcome.Found { found with Content = text }
        | ReadOutcome.Absent -> failwith "no manifest"

    match openNamespace ns migrating with
    | Error [ NamespaceUnusable(_, detail) ] -> Assert.Contains("migration mig-1 is in progress", detail)
    | other -> failwith $"%A{other}"

// ---- ARCA-LOC-008: no production data in a public repository ----------------------------

[<Fact>]
let ``production data is not started in a public repository without a recorded reason`` () =
    let binding = binding production |> ok
    let start visibility decision = initializeApplication binding visibility decision (context "app") |> codes

    Assert.Equal<string list>([ "SIGNAL.STORAGE.PUBLIC_PRODUCTION_REPOSITORY" ], start RepositoryVisibility.Public None)
    Assert.Equal<string list>([ "SIGNAL.STORAGE.OVERRIDE_WITHOUT_REASON" ], start RepositoryVisibility.Public (Some { Reason = " " }))
    Assert.Empty(start RepositoryVisibility.Public (Some { Reason = "synthetic demonstration data only" }))
    Assert.Empty(start RepositoryVisibility.Private None)

    let staging = configText "staging" "acme" "signal-data" "deployments/staging" |> Deployment.parse |> ok
    Assert.Empty(initializeApplication (Storage.binding staging |> ok) RepositoryVisibility.Public None (context "app") |> codes)

// ---- Setting up and opening -------------------------------------------------------------------

[<Fact>]
let ``a set-up dataset opens, and setting it up again is a conflict, never an overwrite`` () =
    let binding, state = initialized production
    let app = applicationNamespace binding |> ok
    let ns = datasetNamespace production binding "ds_engagement" |> ok

    Assert.True(openNamespace app (read app manifestPath state) |> Result.isOk)
    let manifest = openNamespace ns (read ns manifestPath state) |> ok
    Assert.Equal(ManifestScope.Dataset(DatasetId.create "ds_engagement" |> ok), manifest.Scope)

    let again = initializeDataset production binding RepositoryVisibility.Private None (context "again") "signal-test" "ds_engagement" [] |> ok

    match InMemory.commit again state with
    | Error(StorageFailure.Conflicted _), _ -> ()
    | other, _ -> failwith $"setting up twice was not a conflict: %A{other}"

[<Fact>]
let ``a folder that belongs to another application is not opened`` () =
    let binding, state = initialized production
    let ns = datasetNamespace production binding "ds_engagement" |> ok
    let summa = Namespace.ofApplication { binding with Application = AppId.create "summa" |> ok } |> ok

    let foreign =
        InMemory.writeExternally binding.Location "deployments/prod/summa/arca-manifest.json" (read ns manifestPath state |> function ReadOutcome.Found f -> Some f.Content | _ -> None) state

    // Signal's dataset manifest copied into another application's folder:
    // wrong application and wrong scope, each named.
    let found = openNamespace summa (read summa manifestPath foreign) |> codes
    Assert.NotEmpty(found)
    Assert.All(found, fun c -> Assert.Equal("SIGNAL.STORAGE.NAMESPACE_UNUSABLE", c))

// ---- ADM-005: the storage manifest -------------------------------------------------------------

let private manifestOf (config: Deployment.DeploymentConfig) (state: InMemoryState) =
    let binding = binding config |> ok
    let ns = datasetNamespace config binding "ds_engagement" |> ok
    ns, read ns (DatasetManifest.path "ds_engagement" |> ok) state

[<Fact>]
let ``the storage manifest names the dataset, its folder and every format version`` () =
    let _, state = initialized production
    let ns, stored = manifestOf production state
    let manifest = DatasetManifest.read DatasetManifest.current "ds_engagement" (root ns) stored |> ok

    Assert.Equal("ds_engagement", manifest.DatasetId)
    Assert.Equal("deployments/prod/signal/datasets/ds_engagement", manifest.RootNamespace)
    Assert.Equal(DatasetManifest.current, manifest.Versions)
    Assert.Equal("signal-test", manifest.CreatedFromApplicationVersion)
    Assert.Equal<int list>([ 1 ], manifest.Versions.SupportedEncodings)

[<Fact>]
let ``the storage manifest holds no personal data`` () =
    let _, state = initialized production
    let _, stored = manifestOf production state

    match stored with
    | ReadOutcome.Found found ->
        let body = (Record.decode Record.DefaultMaxBytes found.Content |> ok).Body

        let keys =
            match body with
            | Json.Object members -> members |> List.map fst
            | _ -> []

        Assert.Equal<string list>([ "createdFromApplicationVersion"; "datasetId"; "rootNamespace"; "versions" ], keys |> List.sort)
        Assert.DoesNotContain("583231", Json.canonicalText body)
    | ReadOutcome.Absent -> failwith "no manifest"

[<Fact>]
let ``a newer format, another dataset's manifest or another folder fails explicitly`` () =
    let _, state = initialized production
    let ns, stored = manifestOf production state
    let older = { DatasetManifest.current with StorageLayout = 0; SupportedEncodings = [] }
    let check supported datasetId rootText = DatasetManifest.read supported datasetId rootText stored |> codeOf

    // This Signal reads layout 0 at most: the stored layout 1 is from the future.
    Assert.Equal("SIGNAL.STORAGE.MANIFEST_UNUSABLE", check older "ds_engagement" (root ns))
    Assert.Equal("SIGNAL.STORAGE.MANIFEST_UNUSABLE", check DatasetManifest.current "ds_engagement" "deployments/prod/signal/datasets/ds_hr")
    Assert.Equal("SIGNAL.STORAGE.DATASET_NOT_INITIALIZED", DatasetManifest.read DatasetManifest.current "ds_engagement" (root ns) ReadOutcome.Absent |> codeOf)

    // An encoding the running Signal does not know.
    let incompatible = DatasetManifest.incompatibilities DatasetManifest.current { DatasetManifest.current with SupportedEncodings = [ 1; 2 ] }
    Assert.Equal<string list>([ "response encoding 2 is not one this Signal reads" ], incompatible)

[<Fact>]
let ``a tampered or misplaced storage manifest is refused, never trusted`` () =
    let _, state = initialized production
    let ns, stored = manifestOf production state

    let edited (change: string -> string) =
        match stored with
        | ReadOutcome.Found found -> ReadOutcome.Found { found with Content = change found.Content }
        | ReadOutcome.Absent -> failwith "no manifest"

    let check outcome = DatasetManifest.read DatasetManifest.current "ds_engagement" (root ns) outcome |> codeOf

    Assert.Equal("SIGNAL.STORAGE.INVALID_RECORD", check (edited (fun text -> text.Replace("ds_engagement", "ds_hr"))))
    Assert.Equal("SIGNAL.STORAGE.INVALID_RECORD", check (edited (fun text -> text.Substring(0, text.Length / 2))))
    Assert.Equal("SIGNAL.STORAGE.INVALID_RECORD", check (edited (fun text -> text.Replace("\"storageLayout\":1", "\"storageLayout\":1,\"note\":\"x\""))))
    Assert.Equal("SIGNAL.STORAGE.INVALID_RECORD", check (edited (fun text -> " " + text)))

[<Fact>]
let ``nothing that looks like a credential is written`` () =
    let binding = binding production |> ok

    let leaked =
        initializeDataset production binding RepositoryVisibility.Private None (context "leak") ("ghp_" + String('a', 36)) "ds_engagement" []

    Assert.Equal<string list>([ "SIGNAL.STORAGE.OPERATION_REFUSED" ], leaked |> codes)
