/// The administrator store: opening a dataset and changing it through Arca,
/// following the pattern Chrona established (WI-0040).
///
/// - **Opening** resolves what the signed-in credential can do at the
///   dataset's repository (its identity, visibility, whether it may write),
///   sets up Signal's namespace and the dataset when they are new and the
///   person is one of the dataset's configured administrators (by GitHub
///   numeric id), refuses production data in a public repository, verifies
///   both manifests before reading anything else, loads the roster as
///   untrusted input, and records the change token it was read at. The
///   dataset is writable only when all of that is clean and the credential
///   may write directly.
/// - **Changing**: one command is one Arca operation, one commit, every
///   change conditioned on the revision last read. When the data moved, the
///   roster is reloaded and Signal's rules decide the command again; if it
///   still conflicts the person is told. An unknown outcome is reconciled
///   before anything is sent again.
/// - **Offline**: there is no offline queue (ADM-070). When the provider
///   cannot be reached the store reports it; the caller keeps showing
///   verified data read-only and withholds every mutation
///   (`Credential.usable`).
///
/// The rules are the domain's (`Storage`, `Loading`, `RosterStore`,
/// `Credential`); this module only sequences them over Arca's
/// provider-neutral `StorageProvider`: Arca's GitHub adapter in the
/// application, its in-memory provider in tests.
module Echelon.Signal.Application.Store

open System
open Arca
open Arca.GitHub
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Application.Flow

/// What the store needs from where the data lives.
[<NoComparison; NoEquality>]
type Backend =
    { /// The provider serving a location.
      Provider: DataLocation -> StorageProvider
      /// What the signed-in credential can do at a location.
      Resolve: DataLocation -> Async<Result<CapabilitySnapshot, Credential.ResolveProblem>> }

/// Arca's GitHub adapter over a host (requests, waits and Fides' tokens).
let gitHub (host: Host) : Backend =
    { Provider = fun location -> GitHubStorage.provider host (GitHubConfig.create location)
      Resolve =
        fun location ->
            async {
                match! Conversation.run host (Arca.GitHub.Identity.resolve (GitHubConfig.create location)) with
                | Ok snapshot -> return Ok snapshot
                | Error(ResolveError.CredentialUnavailable unavailable) -> return Error(Credential.NoUsableToken unavailable)
                | Error ResolveError.CredentialRejected -> return Error Credential.Rejected
                | Error(ResolveError.RepositoryNotFound repository) -> return Error(Credential.RepositoryNotVisible repository)
                | Error(ResolveError.Call _) -> return Error(Credential.Unreachable "the provider could not be reached")
            } }

/// A dataset as opened.
[<NoComparison; NoEquality>]
type Opened =
    { DatasetId: string
      Namespace: Namespace
      Provider: StorageProvider
      Verified: Loading.VerifiedDataset
      Roster: RosterStore.StoredRoster
      /// The repository state the data was read at.
      Token: ChangeToken
      /// The repository the dataset is pinned to.
      RepositoryId: string
      Verification: ProviderContract.ProfileVerification
      Credential: Credential.CredentialState
      /// Present only when changes are allowed.
      Grant: Loading.WriteGrant option
      /// The dataset's lifecycle state (ADM-045); only an active dataset
      /// grants changes.
      Lifecycle: Retention.State
      LifecycleRevision: Revision option
      /// The grant a lifecycle transition uses: everything but the lifecycle
      /// state, so an archived dataset can be made active again.
      LifecycleGrant: Loading.WriteGrant option
      /// Why changes are not allowed, when they are not.
      ReadOnlyReasons: Problem list }

/// Why a dataset did not open.
type OpenFailure =
    | Misconfigured of Problem
    /// Not set up, and this person is not one of its configured administrators.
    | NeedsBootstrapAdministrator of datasetId: string
    | CredentialNotUsable of Credential.CredentialState
    | Refused of Problem list
    /// The provider could not be reached; nothing verified is available.
    | Unreachable of ProviderContract.FailureMeaning
    | StorageFailed of ProviderContract.FailureMeaning

/// Who is acting, with the operation identifiers and clock the caller supplies.
[<NoComparison; NoEquality>]
type Actor =
    { Principal: Access.Principal
      SignIn: Credential.SignIn
      /// A fresh operation context for each operation (new idempotency key).
      NewContext: unit -> Storage.OperationContext }

let private failed (now: DateTimeOffset) (failure: StorageFailure) =
    let meaning = ProviderContract.meaning now failure

    match failure with
    | StorageFailure.ProviderFailed(_, true, _) -> Unreachable meaning
    | _ -> StorageFailed meaning

let private call (now: DateTimeOffset) (work: Async<Result<'a, StorageFailure>>) : AsyncResult<'a, OpenFailure> = work |> mapError (failed now)

/// Commits an operation that sets something up, as part of opening.
let private setUp (now: DateTimeOffset) (provider: StorageProvider) (operation: Result<Operation, Problem list>) =
    asyncResult {
        let! found = operation |> Result.mapError Refused |> lift
        let! _ = call now (provider.Commit found)
        return ()
    }

/// Reads every record file in a folder and loads it as untrusted input.
let private loadFolder (now: DateTimeOffset) (provider: StorageProvider) (ns: Namespace) (verified: Loading.VerifiedDataset) reader folder =
    asyncResult {
        let! listing = call now (provider.List ns folder)
        let! objects = Loading.recordFiles listing |> List.map (fun path -> call now (provider.Read ns path)) |> sequence

        let found =
            objects
            |> List.choose (function
                | ReadOutcome.Found stored -> Some stored
                | ReadOutcome.Absent -> None)

        return Loading.load verified reader folder listing found
    }

let private loadRoster now provider ns verified =
    asyncResult {
        let! loaded = loadFolder now provider ns verified AdministratorRecord.reader AdministratorRecord.folder
        return RosterStore.ofLoaded verified.Id loaded
    }

let private principalActor (actor: Actor) = actor.Principal.PrincipalId

/// Opens a dataset for an administrator. `pinned` is the repository id the
/// dataset was opened at before, if any (ADM-056: a credential that resolves
/// the configured location to another repository needs an administrator's
/// confirmation, never a silent switch).
let openDataset
    (backend: Backend)
    (config: Deployment.DeploymentConfig)
    (actor: Actor)
    (pinned: string option)
    (applicationVersion: string)
    (datasetId: string)
    (now: DateTimeOffset)
    : AsyncResult<Opened, OpenFailure> =
    asyncResult {
        let! binding = Storage.binding config |> Result.mapError Misconfigured |> lift
        let! dataset = Deployment.dataset config datasetId |> Option.map Ok |> Option.defaultValue (Error(Misconfigured(UnknownDataset datasetId))) |> lift
        let! appNs = Storage.applicationNamespace binding |> Result.mapError Misconfigured |> lift
        let! ns = Storage.datasetNamespace config binding datasetId |> Result.mapError Misconfigured |> lift
        let! manifestAt = Layout.manifestPath |> Result.mapError (LocationError.describe >> InvalidDataLocation >> Misconfigured) |> lift

        // What the credential can do at the dataset's repository.
        let! resolved = attempt (backend.Resolve ns.Location)
        let credential = Credential.assess actor.SignIn pinned (Some resolved)

        let! snapshot =
            lift <|
            match credential, resolved with
            | Credential.CredentialUnverifiable reason, _ ->
                Error(Unreachable(ProviderContract.meaning now (StorageFailure.ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", true, reason))))
            | state, Ok found when Credential.permitsReading state -> Ok found
            | state, _ -> Error(CredentialNotUsable state)

        let provider = backend.Provider ns.Location
        let appProvider = backend.Provider binding.Location
        let bootstrap = Deployment.isBootstrapAdministrator dataset (principalActor actor)
        let canSetUp = bootstrap && credential = Credential.CredentialValidReadWrite

        // Signal's own namespace.
        let! appManifest = call now (appProvider.Read appNs manifestAt)

        do!
            match appManifest with
            | ReadOutcome.Absent when canSetUp ->
                setUp now appProvider (Storage.initializeApplication binding snapshot.Visibility None (actor.NewContext()))
            | ReadOutcome.Absent -> async.Return(Error(NeedsBootstrapAdministrator datasetId))
            | found -> async.Return(Storage.openNamespace appNs found |> Result.map ignore |> Result.mapError Refused)

        // The dataset: both manifests, set up together with its first administrator.
        let! manifestPath = DatasetManifest.path datasetId |> Result.mapError Misconfigured |> lift
        let! arcaManifest = call now (provider.Read ns manifestAt)

        do!
            match arcaManifest with
            | ReadOutcome.Absent when canSetUp ->
                setUp
                    now
                    provider
                    (RosterStore.bootstrapRecords datasetId actor.Principal
                     |> Result.bind (Storage.initializeDataset config binding snapshot.Visibility None (actor.NewContext()) applicationVersion datasetId)
                     |> Result.bind (
                         GovernanceRecord.auditedWith
                             datasetId
                             [ GovernanceRecord.record Audit.StorageConfigured [] [] [ "DATASET-INITIALIZED" ] None (Some(Retention.stateId Retention.Active)) (Some now) ]
                     ))
            | ReadOutcome.Absent -> async.Return(Error(NeedsBootstrapAdministrator datasetId))
            | _ -> async.Return(Ok())

        let! arcaManifest = call now (provider.Read ns manifestAt)
        let! storageManifest = call now (provider.Read ns manifestPath)
        let! verified = Loading.verify DatasetManifest.current ns datasetId arcaManifest storageManifest |> Result.mapError Refused |> lift
        let! roster = loadRoster now provider ns verified
        let! lifecycle = loadFolder now provider ns verified GovernanceRecord.lifecycleReader (GovernanceRecord.folderOf GovernanceRecord.lifecycleType)
        let stored = lifecycle.Records |> Map.tryFind GovernanceRecord.LifecycleId
        let state = stored |> Option.map _.Value.State |> Option.defaultValue Retention.Active
        let! token = call now (provider.ChangeToken ns)

        let verification = ProviderContract.verify provider.Capabilities (Ok snapshot)

        let grant =
            match credential with
            | Credential.CredentialValidReadWrite ->
                Loading.writable verified roster.Problems provider.Capabilities (ProviderContract.writeMode snapshot)
            | other -> Error [ WritesUnavailable(Credential.stateCode other) ]

        return
            { DatasetId = datasetId
              Namespace = ns
              Provider = provider
              Verified = verified
              Roster = roster
              Token = token
              RepositoryId = snapshot.RepositoryId
              Verification = verification
              Credential = credential
              Grant = if Retention.writable state && lifecycle.Problems.IsEmpty then grant |> Result.toOption else None
              Lifecycle = state
              LifecycleRevision = stored |> Option.map _.Revision
              LifecycleGrant = grant |> Result.toOption
              ReadOnlyReasons =
                [ if not (Retention.writable state) then DatasetNotActive(Retention.stateId state)
                  match grant with
                  | Ok _ -> ()
                  | Error reasons -> yield! reasons ]
                @ (lifecycle.Problems) }
    }

// ---- Changing the roster -------------------------------------------------------------

/// Why a roster change did not land.
type ChangeFailure =
    /// The dataset is read-only now, for these reasons.
    | ReadOnly of Problem list
    /// Signal's rules refuse the command.
    | RefusedByRules of Access.AccessRefusal list
    | Unstorable of Problem list
    /// Decided again against reloaded data and still in conflict.
    | ConflictPersists
    /// The provider still cannot say whether it landed; nothing is resent.
    | OutcomeStillUnknown of PendingReconciliation
    /// The provider could not be reached; nothing was queued.
    | Offline of ProviderContract.FailureMeaning
    | Failed of ProviderContract.FailureMeaning

let private changeFailed (now: DateTimeOffset) (failure: StorageFailure) =
    match failure with
    | StorageFailure.ProviderFailed(_, true, _) -> Offline(ProviderContract.meaning now failure)
    | _ -> Failed(ProviderContract.meaning now failure)

/// Changes a dataset's roster: one command, one commit, conditioned on the
/// revisions last read and on the repository's change token, because the
/// roster's rule (someone must remain who can manage it) spans records: two
/// administrators removing each other touch different records, and only the
/// token shows that the roster moved underneath. Returns the dataset as it
/// stands after the change.
let changeRoster (actor: Actor) (command: Access.RosterCommand) (now: DateTimeOffset) (opened: Opened) : AsyncResult<Opened, ChangeFailure> =
    let decide (grant: Loading.WriteGrant) (roster: RosterStore.StoredRoster) (token: ChangeToken) =
        RosterStore.command grant opened.Namespace (actor.NewContext()) (principalActor actor) command roster
        |> Result.map (fun (after, operation) -> after, Operation.requireChangeToken token operation)
        |> Result.mapError (function
            | RosterStore.Refused refusals -> RefusedByRules refusals
            | RosterStore.Unstorable problems -> Unstorable problems)

    let landed (after: Access.Roster) (roster: RosterStore.StoredRoster) (receipt: CommitReceipt) =
        { opened with
            Roster = RosterStore.committed after receipt roster
            Token = receipt.ChangeToken }

    let reload () =
        asyncResult {
            let! roster = loadRoster now opened.Provider opened.Namespace opened.Verified
            let! token = call now (opened.Provider.ChangeToken opened.Namespace)
            return roster, token
        }
        |> mapError (fun _ -> ConflictPersists)

    let rec send (round: int) (grant: Loading.WriteGrant) (roster: RosterStore.StoredRoster) (token: ChangeToken) =
        asyncResult {
            let! after, operation = decide grant roster token |> lift
            let! result = attempt (opened.Provider.Commit operation)

            match result with
            | Ok receipt -> return landed after roster receipt
            | Error(StorageFailure.Conflicted _)
            | Error(StorageFailure.StaleChangeToken _) when round = 0 ->
                // Reload and let Signal's rules decide again.
                let! fresh, freshToken = reload ()
                return! send 1 grant fresh freshToken
            | Error(StorageFailure.Conflicted _)
            | Error(StorageFailure.StaleChangeToken _) -> return! lift (Error ConflictPersists)
            | Error(StorageFailure.OutcomeUnknown pending) ->
                // Never resent blindly: find out whether it landed first.
                let! reconciled = attempt (opened.Provider.Reconcile opened.Namespace pending)

                match reconciled with
                | Ok(ReconcileOutcome.Landed receipt) -> return landed after roster receipt
                | Ok ReconcileOutcome.NotLanded when round = 0 ->
                    let! fresh, freshToken = reload ()
                    return! send 1 grant fresh freshToken
                | Ok ReconcileOutcome.NotLanded -> return! lift (Error ConflictPersists)
                | Ok(ReconcileOutcome.StillUnknown still) -> return! lift (Error(OutcomeStillUnknown still))
                | Error failure -> return! lift (Error(changeFailed now failure))
            | Error failure -> return! lift (Error(changeFailed now failure))
        }

    match opened.Grant with
    | None -> lift (Error(ReadOnly opened.ReadOnlyReasons))
    | Some grant -> send 0 grant opened.Roster opened.Token

/// Reloads a dataset's roster at the provider's current state, for another
/// tab's notice or after reconnecting (ADM-070, ADM-072): the provider's
/// evidence, not the notice, decides.
let refresh (now: DateTimeOffset) (opened: Opened) : AsyncResult<Opened, ChangeFailure> =
    asyncResult {
        let! roster = loadRoster now opened.Provider opened.Namespace opened.Verified |> mapError (fun _ -> ConflictPersists)
        let! token = opened.Provider.ChangeToken opened.Namespace |> mapError (changeFailed now)

        return
            { opened with
                Roster = roster
                Token = token }
    }
