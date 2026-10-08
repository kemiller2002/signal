/// Signal's storage-provider contract on Arca (WI-0039): ADM-003 capability
/// knowledge, ADM-006 a domain free of GitHub concepts, ADM-026 failure
/// meanings and no automatic retry, ADM-044 Arca's conformance suite,
/// ADM-046 staged untrusted loading, ADM-058 growth warnings, ADM-073 write
/// modes, and AER-002/AER-032 GitHub failure classes through Aegis.
module Echelon.Signal.Tests.ProviderTests

open System
open Xunit
open Aegis
open Arca
open Arca.GitHub
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Problems

/// Arca's change token, named before ProviderContract's `ChangeToken` capability shadows it.
let private token (text: string) = ChangeToken text

open Echelon.Signal.Admin.ProviderContract
open Echelon.Signal.Application

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private configText =
    """{"environment":"production","environmentName":"production",
"identity":{"exchange":"https://fides.test","application":"signal-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://signal.test/admin/"},
"profiles":[{"id":"primary","label":"Survey data","provider":"github","location":{"owner":"acme","repository":"signal-data","branch":"main","basePath":"prod"}}],
"datasets":[{"id":"ds_engagement","label":"Engagement","profile":"primary","administrators":["583231"]}]}"""

let private config = Deployment.parse configText |> ok
let private binding = Storage.binding config |> ok
let private ns = Storage.datasetNamespace config binding "ds_engagement" |> ok
let private at = DateTimeOffset(2026, 10, 8, 14, 30, 0, TimeSpan.Zero)

// ---- ADM-003: explicit, versioned capability knowledge --------------------------------

[<Fact>]
let ``every ADM-003 capability has explicit knowledge, and none is simulated`` () =
    let github = describe Arca.GitHub.Provider.capabilities
    Assert.Equal(22, github.Length)

    let unavailable =
        github |> List.choose (fun (capability, knowledge) -> match knowledge with Unavailable _ -> Some capability | _ -> None)

    Assert.Equal<StorageCapability list>(
        [ BatchRead; ServerSideQuery; ServerSideAggregate; Transactions; StrongDelete; Compression; EncryptionAtRestClaim; MaxBatchSize; StreamingRead; StreamingWrite ],
        unavailable
    )

    // GitHub declares at-rest encryption unavailable, and says why.
    match knowledge Arca.GitHub.Provider.capabilities EncryptionAtRestClaim with
    | Unavailable reason -> Assert.Contains("deferred", reason)
    | other -> failwith $"%A{other}"

    // What the contract offers but a declaration cannot prove is marked so, with evidence.
    Assert.True(match knowledge Arca.GitHub.Provider.capabilities RateLimitObservation with Unverified _ -> true | _ -> false)
    Assert.Equal(Available 1, knowledge Arca.GitHub.Provider.capabilities ConditionalWrite)
    Assert.Empty(missing writeNeeds Arca.GitHub.Provider.capabilities)

[<Fact>]
let ``the GitHub provider and the in-memory provider state the same knowledge`` () =
    Assert.Equal<(StorageCapability * Knowledge) list>(describe Arca.GitHub.Provider.capabilities, describe InMemory.capabilities)

[<Fact>]
let ``a provider without conditional writes cannot be written, and says so`` () =
    let readOnly =
        { InMemory.capabilities with
            States = InMemory.capabilities.States |> Map.add Capability.ConditionalWrite (CapabilityState.Unavailable "read-only replica") }

    Assert.Equal(Unavailable "read-only replica", knowledge readOnly WriteObject)
    Assert.Equal(Unavailable "read-only replica", knowledge readOnly DeleteObject)
    Assert.Equal<StorageCapability list>([ ConditionalWrite ], missing writeNeeds readOnly |> List.map _.Capability)
    Assert.Empty(missing readNeeds readOnly)

    // A capability nobody declared is unavailable, never assumed.
    let bare = { InMemory.capabilities with States = Map.empty }
    Assert.Equal<StorageCapability list>(writeNeeds, missing writeNeeds bare |> List.map _.Capability)

// ---- ADM-006: the survey domain knows nothing of GitHub -----------------------------------

[<Fact>]
let ``the survey domain names no repository, branch, commit or path concept`` () =
    let engine = typeof<Echelon.Signal.Engine.Session.Session>.Assembly

    Assert.DoesNotContain(engine.GetReferencedAssemblies(), fun name -> name.Name.StartsWith "Arca" || name.Name.Contains "GitHub")

    let names =
        engine.GetTypes()
        |> Array.collect (fun t -> Array.append [| t.Name |] (t.GetProperties() |> Array.map _.Name))

    // Whole words of a name: survey "branching" or a "Commitment" dimension are survey concepts.
    let words (name: string) =
        Text.RegularExpressions.Regex.Split(name, "(?<!^)(?=[A-Z])") |> Array.map (fun w -> w.Trim('`', '1', '2', '@'))

    let storageWords = set [ "GitHub"; "Repository"; "Branch"; "Commit"; "Sha" ]
    let offending = names |> Array.filter (fun name -> words name |> Array.exists storageWords.Contains)
    Assert.Empty(offending)

// ---- ADM-073: direct writes or an explicit refusal ------------------------------------------

let private snapshot canRead canWrite archived branch =
    { Identity =
        { Provider = "github"
          Subject = "583231"
          Login = Some "octocat"
          Kind = IdentityKind.User }
      RepositoryId = "R_1"
      Repository = RepositoryRef.create "acme" "signal-data" |> ok
      Visibility = RepositoryVisibility.Private
      CanRead = canRead
      CanWrite = canWrite
      Archived = archived
      Branch = branch }

[<Fact>]
let ``the write mode distinguishes every reason a direct write is impossible`` () =
    let mode a b c d = snapshot a b c d |> writeMode |> writeModeCode

    Assert.Equal("DirectWriteAvailable", mode true true false BranchAccess.Writable)
    Assert.Equal("ReadOnlyByPermission", mode true false false BranchAccess.Writable)
    Assert.Equal("ProtectedBranchRequiresReview", mode true true false (BranchAccess.NotWritable [ "pull request required" ]))
    Assert.Equal("BranchMissing", mode true true false BranchAccess.Missing)
    Assert.Equal("RepositoryArchived", mode true true true BranchAccess.Writable)
    Assert.Equal("ProviderPolicyUnknown", mode false false false BranchAccess.Writable)

[<Fact>]
let ``a profile's verification is the provider's evidence, and only a clean one permits writes`` () =
    let github = Arca.GitHub.Provider.capabilities
    let writable = verify github (Ok(snapshot true true false BranchAccess.Writable))

    Assert.Equal(Verified("R_1", DirectWriteAvailable, []), writable)
    Assert.True(permitsWrites writable)
    Assert.False(permitsWrites NotVerified)
    Assert.False(permitsWrites (verify github (Ok(snapshot true false false BranchAccess.Writable))))
    Assert.False(permitsWrites (verify { github with States = Map.empty } (Ok(snapshot true true false BranchAccess.Writable))))
    Assert.Equal(VerificationFailed "Your GitHub account cannot see acme/signal-data.", verify github (Error "Your GitHub account cannot see acme/signal-data."))

// ---- ADM-026: failures mean something, and nothing spins --------------------------------------

[<Fact>]
let ``a rate limit is retried only after the provider's evidence, never automatically`` () =
    let retryOf failure = (meaning at failure).Retry

    Assert.Equal(NotBefore(at.AddSeconds 60.0), retryOf (StorageFailure.RateLimited(Some(TimeSpan.FromSeconds 60.0), None)))
    Assert.Equal(NotBefore(DateTimeOffset.FromUnixTimeSeconds 1_800_000_000L), retryOf (StorageFailure.RateLimited(None, Some 1_800_000_000L)))
    Assert.Equal(WhenProviderAllows, retryOf (StorageFailure.RateLimited(None, None)))
    Assert.Equal("SIGNAL.STORAGE.RATE_LIMITED", (meaning at (StorageFailure.RateLimited(None, None))).Code)

[<Fact>]
let ``conflicts reload, unknown outcomes reconcile, refusals stop`` () =
    let pending =
        { IdempotencyKey = IdempotencyKey.create "op-provider-0001" |> ok
          Base = token "abc"
          Candidate = None
          Revisions = Map.empty }

    let unknown = meaning at (StorageFailure.OutcomeUnknown pending)
    Assert.Equal(AfterReconciling, unknown.Retry)
    Assert.False(unknown.NothingWritten)

    Assert.Equal(AfterReloading, (meaning at (StorageFailure.Conflicted [])).Retry)
    Assert.Equal(AfterReloading, (meaning at (StorageFailure.StaleChangeToken(token "a", token "b"))).Retry)

    let protectedBranch = meaning at (StorageFailure.Refused WriteRefusal.BranchProtected)
    Assert.Equal(("SIGNAL.STORAGE.BRANCH_PROTECTED", Never), (protectedBranch.Code, protectedBranch.Retry))
    Assert.Equal("SIGNAL.STORAGE.OBJECT_TOO_LARGE", (meaning at (StorageFailure.ObjectTooLarge("records/x.json", 2_000_000L, 1_048_576L))).Code)
    Assert.Equal(WhenThePersonChooses, (meaning at (StorageFailure.ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", true, "offline"))).Retry)

// ---- AER-002 / AER-032: GitHub failure classes through Aegis ---------------------------------

let private response status (headers: (string * string) list) = HttpOutcome.Response(status, Map.ofList headers, "")

let private classified outcome =
    StorageFaults.classify "acme/signal-data" outcome |> Option.map StorageFaults.present

[<Fact>]
let ``every GitHub failure class translates deterministically through Aegis`` () =
    let code outcome = classified outcome |> Option.map _.Code

    // authentication, authorization
    Assert.Equal(Some "AEGIS.GITHUB.AUTHENTICATION_FAILED", code (response 401 []))
    Assert.Equal(Some "AEGIS.GITHUB.AUTHENTICATION_FAILED", code (response 403 []))
    // not found
    Assert.Equal(Some "AEGIS.GITHUB.REPOSITORY_NOT_FOUND", code (response 404 []))
    // rate limiting, primary and secondary, whatever the status
    Assert.Equal(Some "AEGIS.GITHUB.RATE_LIMITED", code (response 403 [ "x-ratelimit-remaining", "0" ]))
    Assert.Equal(Some "AEGIS.GITHUB.RATE_LIMITED", code (response 429 [ "retry-after", "30" ]))
    // timeout and network failure
    Assert.Equal(Some "AEGIS.NETWORK.TIMEOUT", code (response 504 []))
    Assert.Equal(Some "AEGIS.NETWORK.UNAVAILABLE", code (HttpOutcome.Failed HttpFailure.Network))
    // transient provider failure
    Assert.Equal(Some "AEGIS.NETWORK.UNAVAILABLE", code (response 502 []))
    Assert.Equal(Some "AEGIS.NETWORK.UNAVAILABLE", code (response 503 []))
    // malformed or unexpected response
    Assert.Equal(Some "AEGIS.GITHUB.INVALID_RESPONSE", code (HttpOutcome.Failed HttpFailure.InvalidResponse))
    Assert.Equal(Some "AEGIS.GITHUB.INVALID_RESPONSE", code (response 500 []))
    // an uncertain write outcome is not a fault: it is reconciled (ADM-009)
    Assert.Equal(None, code (HttpOutcome.OutcomeUnknown UnknownReason.ConnectionLost))
    Assert.Equal(None, code (response 200 []))

[<Fact>]
let ``Signal retries nothing on its own, whatever Aegis suggests`` () =
    let presented = classified (response 429 [ "retry-after", "30" ]) |> Option.get

    Assert.Equal(RecoveryPolicy.Retry(3, Backoff.Fixed(TimeSpan.FromSeconds 30.0)), presented.Recovery)
    Assert.False(presented.AutomaticRetry)
    Assert.Equal(Some(RecoveryPolicy.Reauthenticate), classified (response 401 []) |> Option.map _.Recovery)

[<Fact>]
let ``a GitHub fault is deterministic and carries no token, URL or content`` () =
    let identity = FaultId "F-1", Aegis.CorrelationId "C-1", at
    let failure = StorageFaults.classify "acme/signal-data" (response 401 []) |> Option.get
    let first = StorageFaults.fault identity (Some "1.0") "commit" "acme/signal-data" failure
    let second = StorageFaults.fault identity (Some "1.0") "commit" "acme/signal-data" failure

    Assert.Equal(first, second)
    Assert.Equal(FaultCode "AEGIS.GITHUB.AUTHENTICATION_FAILED", first.Code)
    Assert.Equal<string list>([ "repository" ], first.Context |> Map.keys |> List.ofSeq)
    Assert.DoesNotContain("https://", first.UserMessage)
    Assert.DoesNotContain("token", first.UserMessage.ToLowerInvariant())

// ---- ADM-044: the conformance suite -----------------------------------------------------------

[<Fact>]
let ``Arca's provider conformance suite passes in Signal's dataset namespace`` () =
    let results = Conformance.run (fun () -> Conformance.inMemory ns) |> Async.RunSynchronously

    Assert.True(results.Length >= 10, $"{results.Length} cases")

    let failures =
        results
        |> List.choose (fun result ->
            match result.Outcome with
            | ConformanceOutcome.Passed -> None
            | outcome -> Some $"{result.Case} ({result.Requirement}): %A{outcome}")

    Assert.True(failures.IsEmpty, String.concat "\n" failures)

// ---- ADM-058: growth warnings ------------------------------------------------------------------

[<Fact>]
let ``growth warns before GitHub becomes unreliable, and says when it cannot tell`` () =
    let measure objects bytes complete =
        { Objects = objects
          Bytes = bytes
          Folders = 10
          Complete = complete }

    Assert.Empty(Growth.assess Growth.defaultPolicy (measure 100 1_000_000L true))

    Assert.Equal<Growth.GrowthWarning list>(
        [ Growth.Approaching(Growth.StoredBytes, 600_000_000L, 500_000_000L); Growth.Exceeded(Growth.ObjectCount, 120_000L, 100_000L) ],
        Growth.assess Growth.defaultPolicy (measure 120_000 600_000_000L true)
    )

    match Growth.assess Growth.defaultPolicy (measure 100 1_000L false) with
    | [ Growth.Unmeasured _ ] -> ()
    | other -> failwith $"%A{other}"

    // A deployment's own evidence replaces the defaults.
    let strict = { Growth.defaultPolicy with Thresholds = Map.ofList [ Growth.ObjectCount, { Warn = 50L; Critical = 80L } ] }
    Assert.Equal<Growth.GrowthWarning list>([ Growth.Exceeded(Growth.ObjectCount, 100L, 80L) ], Growth.assess strict (measure 100 1_000L true))
