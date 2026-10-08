/// Where Signal keeps its administrator data (SIG-DATALOC-001..005, ADM-004,
/// ADM-005, ADM-057): the deployment's configured location, the namespace
/// Signal owns inside it, one folder per dataset in the dataset's storage
/// profile, setting those folders up and opening them, and the refusal to
/// start production data in a public repository.
///
/// Layout, under the configured base path of the deployment's own profile:
///
/// ```
/// <base>/signal/arca-manifest.json                        Signal's namespace
/// <base>/signal/datasets/<dataset>/arca-manifest.json     one dataset
/// <base>/signal/datasets/<dataset>/records/signal.storage-manifest/<dataset>.json
/// <base>/signal/datasets/<dataset>/records/<type>/...     its records
/// ```
///
/// A dataset in another profile has the same folder under that profile's
/// base path, in that profile's repository. Signal reads and writes nothing
/// outside these folders and never assumes it owns the repository root
/// (SIG-DATALOC-002, SIG-DATALOC-003). Folders are named by immutable ids,
/// never by labels, so relabelling moves nothing. Nothing is encrypted at
/// rest (SIG-DATALOC-005).
///
/// Pure: this module builds Arca operations and reads Arca objects; an Arca
/// provider (in memory for tests, GitHub in the application) carries them out.
module Echelon.Signal.Admin.Storage

open System
open Arca
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Deployment

/// Signal's application id, which is also the folder it owns under the
/// configured base path. It is the application's name, not a location.
let application =
    match AppId.create "signal" with
    | Ok id -> id
    | Error error -> invalidOp ("internal: Signal's application id is invalid: " + LocationError.describe error)

/// Signal's storage binding for a deployment: its own profile's location.
let binding (config: DeploymentConfig) : Result<ApplicationBinding, Problem> =
    match primaryProfile config with
    | None -> Error(MissingConfiguration "profiles")
    | Some _ when String.IsNullOrWhiteSpace config.EnvironmentName -> Error(MissingConfiguration "environmentName")
    | Some primary ->
        dataLocation primary.Location
        |> Result.map (fun location ->
            { Application = application
              Environment =
                { Kind = config.Environment
                  Name = config.EnvironmentName }
              Location = location })

/// Signal's own namespace: `<base path>/signal`.
let applicationNamespace (binding: ApplicationBinding) : Result<Namespace, Problem> =
    Namespace.ofApplication binding |> Result.mapError (LocationError.describe >> InvalidDataLocation)

/// A dataset's folder: `<base path>/signal/datasets/<dataset>` in the
/// dataset's profile. A dataset the deployment does not configure has none.
let datasetNamespace (config: DeploymentConfig) (binding: ApplicationBinding) (datasetId: string) : Result<Namespace, Problem> =
    match Deployment.dataset config datasetId, DatasetId.create datasetId with
    | None, _ -> Error(UnknownDataset datasetId)
    | Some _, Error _ -> Error(InvalidDatasetId datasetId)
    | Some found, Ok dataset ->
        let own =
            match primaryProfile config, Deployment.profile config found.Profile with
            | Some primary, Some chosen when primary.Id = chosen.Id -> Ok None
            | _, Some chosen -> dataLocation chosen.Location |> Result.map Some
            | _, None -> Error(UnknownDataset datasetId)

        own
        |> Result.bind (fun location ->
            Namespace.ofDataset binding dataset location |> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// Whether production data may be initialized in a repository of this
/// visibility (ARCA-LOC-008): only a production deployment into a public
/// repository is refused, unless someone has decided otherwise and said why.
let permitsInitialization
    (environment: EnvironmentKind)
    (visibility: RepositoryVisibility)
    (overrideDecision: PublicProductionOverride option)
    : Result<unit, Problem> =
    Visibility.permitsInitialization environment visibility overrideDecision
    |> Result.mapError (function
        | VisibilityRefusal.PublicProductionRepository -> PublicProductionRepository
        | VisibilityRefusal.OverrideWithoutReason -> OverrideWithoutReason)

// ---- Opening ------------------------------------------------------------------------

let private place (location: DataLocation) =
    $"{location.Repository} ({BranchName.value location.Branch}, '{RelativePath.render location.BasePath}')"

let private describeProblem =
    function
    | ManifestProblem.WrongApplication(found, expected) -> $"the folder belongs to '{found}', not '{expected}'"
    | ManifestProblem.WrongScope -> "the folder's manifest describes a different scope"
    | ManifestProblem.UnsupportedStorageSchema(found, supported) -> $"storage schema {found} is not {supported}"
    | ManifestProblem.UnsupportedProviderContract(found, supported) -> $"provider contract {found} is newer than {supported}"
    | ManifestProblem.Relocated relocation ->
        $"the data lives at {place relocation.Recorded}, not the configured {place relocation.Configured}; moving it is a migration"
    | ManifestProblem.MigrationInProgress state -> $"migration {state.MigrationId} is in progress"
    | ManifestProblem.Retired migrationId -> $"the data was moved by migration {migrationId} and this copy retired"

/// The folder's path, for messages.
let root (ns: Namespace) = RelativePath.render ns.Root

/// A namespace's Arca manifest, checked against the namespace Signal is
/// configured for before anything in it is read or written. A folder with
/// no manifest is not initialized; one recorded at another location must be
/// migrated, never re-pointed by editing configuration (ADM-057); one in
/// the middle of a migration is not mistaken for an active store (ADM-005).
let openNamespace (ns: Namespace) (stored: ReadOutcome) : Result<Manifest, Problem list> =
    match stored with
    | ReadOutcome.Absent -> Error [ NamespaceNotInitialized(root ns) ]
    | ReadOutcome.Found found ->
        Manifest.decode found.Content
        |> Result.mapError (fun error -> [ InvalidStoredRecord(RelativePath.render found.Path, Codec.describeDecode error) ])
        |> Result.bind (fun manifest ->
            match Manifest.check ns manifest with
            | [] -> Ok manifest
            | problems -> Error(problems |> List.map (fun problem -> NamespaceUnusable(root ns, describeProblem problem))))

// ---- Operations ---------------------------------------------------------------------

/// Who is changing storage, and the operation's identifiers. Supplied by the
/// caller: this module reads no clock and draws no randomness.
type OperationContext =
    { Actor: Actor
      ProviderIdentity: string option
      CorrelationId: CorrelationId
      IdempotencyKey: IdempotencyKey
      At: DateTimeOffset }

/// The Arca metadata of an operation made in this context.
let metadata (context: OperationContext) (summary: string) : OperationMetadata =
    { Summary = summary
      Actor = context.Actor
      ProviderIdentity = context.ProviderIdentity
      ExecutionId = None
      CorrelationId = context.CorrelationId
      IdempotencyKey = context.IdempotencyKey }

/// One Arca operation in `ns`: the changes become one commit carrying the
/// context's actor, correlation and idempotency key, or the reason Arca
/// refuses to send it (a credential in the content among them, ADM-004).
let operation (ns: Namespace) (context: OperationContext) (summary: string) (changes: Change list) : Result<Operation, Problem list> =
    Operation.create ns (metadata context summary) changes
    |> Result.mapError (fun error ->
        [ OperationRefused(
              match error with
              | OperationError.NoChanges -> "nothing to write"
              | OperationError.DuplicatePath path -> $"'{path}' is written twice"
              | OperationError.InvalidPath error -> LocationError.describe error
              | OperationError.InvalidSummary _ -> "the summary is not one short line"
              | OperationError.CredentialInContent field -> $"'{field}' looks like a credential"
              | OperationError.InvalidMetadata field -> $"'{field}' is not a single-line identifier"
          ) ])

let private arcaManifest (ns: Namespace) (context: OperationContext) =
    { Scope =
        match ns.Dataset with
        | Some dataset -> ManifestScope.Dataset dataset
        | None -> ManifestScope.Application
      Application = ns.Application
      StorageSchema = Manifest.StorageSchema
      ProviderContract = StorageContract.Version
      RecordSchemas =
        match ns.Dataset with
        | Some _ -> Map.ofList [ RecordType.value DatasetManifest.recordType, DatasetManifest.schema.Current ]
        | None -> Map.empty
      CreatedBy = context.Actor
      CreatedAt = context.At
      Location = ns.Location
      Migration = None }

let private manifestPath () =
    Layout.manifestPath |> Result.mapError (LocationError.describe >> InvalidDataLocation >> List.singleton)

/// Sets up Signal's own namespace: one commit creating its Arca manifest. A
/// folder already set up is a conflict, never overwritten.
let initializeApplication
    (binding: ApplicationBinding)
    (visibility: RepositoryVisibility)
    (overrideDecision: PublicProductionOverride option)
    (context: OperationContext)
    : Result<Operation, Problem list> =
    permitsInitialization binding.Environment.Kind visibility overrideDecision
    |> Result.mapError List.singleton
    |> Result.bind (fun () -> applicationNamespace binding |> Result.mapError List.singleton)
    |> Result.bind (fun ns ->
        manifestPath ()
        |> Result.bind (fun path -> operation ns context "initialize Signal storage" [ Change.Create(path, Manifest.encode (arcaManifest ns context)) ]))

/// The storage manifest a new dataset starts with.
let storageManifest (ns: Namespace) (datasetId: string) (applicationVersion: string) : DatasetManifest.StorageManifest =
    { DatasetId = datasetId
      RootNamespace = root ns
      Versions = DatasetManifest.current
      CreatedFromApplicationVersion = applicationVersion }

/// Sets up a dataset's folder: one commit creating its Arca manifest and its
/// Signal storage manifest, and any further records the caller adds (its
/// first administrators). `visibility` is that of the repository the
/// dataset's profile points at. Nothing is overwritten: a dataset already
/// set up is a conflict.
let initializeDataset
    (config: DeploymentConfig)
    (binding: ApplicationBinding)
    (visibility: RepositoryVisibility)
    (overrideDecision: PublicProductionOverride option)
    (context: OperationContext)
    (applicationVersion: string)
    (datasetId: string)
    (records: Change list)
    : Result<Operation, Problem list> =
    permitsInitialization binding.Environment.Kind visibility overrideDecision
    |> Result.mapError List.singleton
    |> Result.bind (fun () -> datasetNamespace config binding datasetId |> Result.mapError List.singleton)
    |> Result.bind (fun ns ->
        let manifest = storageManifest ns datasetId applicationVersion

        match DatasetManifest.encode manifest, DatasetManifest.path datasetId with
        | Ok content, Ok path ->
            manifestPath ()
            |> Result.bind (fun arcaPath ->
                operation
                    ns
                    context
                    $"initialize dataset {datasetId}"
                    ([ Change.Create(arcaPath, Manifest.encode (arcaManifest ns context)); Change.Create(path, content) ] @ records))
        | Error problem, _
        | _, Error problem -> Error [ problem ])
