/// Why an administrator request is refused or what is wrong with stored data:
/// every case has a stable code (`SIGNAL.STORAGE.*`, `SIGNAL.ACCESS.*`) for
/// tests, logs and the page, and a sentence for the person. Expected outcomes
/// are values, never exceptions (AER-010 to AER-020).
///
/// Pure.
module Echelon.Signal.Admin.Problems

/// One problem.
type Problem =
    /// The deployment's configuration is not valid; the detail says which value.
    | InvalidConfiguration of detail: string
    /// A configuration value the deployment needs is absent.
    | MissingConfiguration of field: string
    /// A configured location is not a valid data location.
    | InvalidDataLocation of detail: string
    /// A dataset id is not one Signal can store under.
    | InvalidDatasetId of datasetId: string
    /// The deployment does not configure this dataset.
    | UnknownDataset of datasetId: string
    /// Production data may not be initialized in a public repository (ARCA-LOC-008).
    | PublicProductionRepository
    /// A decision to allow it anyway gives no reason.
    | OverrideWithoutReason
    /// The folder has no Arca manifest: it was never initialized.
    | NamespaceNotInitialized of root: string
    /// The folder's manifest does not match what Signal is configured for.
    | NamespaceUnusable of root: string * detail: string
    /// The dataset has no Signal storage manifest.
    | DatasetNotInitialized of datasetId: string
    /// The Signal storage manifest cannot be used (ADM-005).
    | ManifestUnusable of datasetId: string * detail: string
    /// A stored object is not a valid record of the kind its path names (ADM-046).
    | InvalidStoredRecord of path: string * detail: string
    /// Arca refused to build an operation from these changes.
    | OperationRefused of detail: string
    /// A record cannot be encoded for storage.
    | UnstorableRecord of id: string * detail: string
    /// A record is not where its id and type place it.
    | MisplacedRecord of path: string
    /// The same id at two paths: neither copy can be trusted to be the record.
    | DuplicateRecord of id: string
    /// A record belongs to another dataset (ADM-025, ADM-046).
    | WrongDataset of path: string * found: string
    /// A record refers to one that is not stored.
    | DanglingReference of from: string * target: string
    /// Records refer to each other in a cycle.
    | ReferenceCycle of ids: string list
    /// The provider returned a partial listing: what was not listed was not validated.
    | IncompleteRead of folder: string
    /// A needed storage capability is not offered (ADM-003).
    | CapabilityUnavailable of capability: string * reason: string
    /// Direct writes are not possible at the configured branch (ADM-073).
    | WritesUnavailable of mode: string

/// The problem's stable code.
let code =
    function
    | InvalidConfiguration _ -> "SIGNAL.STORAGE.INVALID_CONFIGURATION"
    | MissingConfiguration _ -> "SIGNAL.STORAGE.MISSING_CONFIGURATION"
    | InvalidDataLocation _ -> "SIGNAL.STORAGE.INVALID_LOCATION"
    | InvalidDatasetId _ -> "SIGNAL.STORAGE.INVALID_DATASET_ID"
    | UnknownDataset _ -> "SIGNAL.STORAGE.UNKNOWN_DATASET"
    | PublicProductionRepository -> "SIGNAL.STORAGE.PUBLIC_PRODUCTION_REPOSITORY"
    | OverrideWithoutReason -> "SIGNAL.STORAGE.OVERRIDE_WITHOUT_REASON"
    | NamespaceNotInitialized _ -> "SIGNAL.STORAGE.NOT_INITIALIZED"
    | NamespaceUnusable _ -> "SIGNAL.STORAGE.NAMESPACE_UNUSABLE"
    | DatasetNotInitialized _ -> "SIGNAL.STORAGE.DATASET_NOT_INITIALIZED"
    | ManifestUnusable _ -> "SIGNAL.STORAGE.MANIFEST_UNUSABLE"
    | InvalidStoredRecord _ -> "SIGNAL.STORAGE.INVALID_RECORD"
    | OperationRefused _ -> "SIGNAL.STORAGE.OPERATION_REFUSED"
    | UnstorableRecord _ -> "SIGNAL.STORAGE.UNSTORABLE_RECORD"
    | MisplacedRecord _ -> "SIGNAL.STORAGE.MISPLACED_RECORD"
    | DuplicateRecord _ -> "SIGNAL.STORAGE.DUPLICATE_RECORD"
    | WrongDataset _ -> "SIGNAL.STORAGE.WRONG_DATASET"
    | DanglingReference _ -> "SIGNAL.STORAGE.DANGLING_REFERENCE"
    | ReferenceCycle _ -> "SIGNAL.STORAGE.REFERENCE_CYCLE"
    | IncompleteRead _ -> "SIGNAL.STORAGE.INCOMPLETE_READ"
    | CapabilityUnavailable _ -> "SIGNAL.STORAGE.CAPABILITY_UNAVAILABLE"
    | WritesUnavailable _ -> "SIGNAL.STORAGE.WRITES_UNAVAILABLE"

/// The problem as one sentence for the person. It never holds a credential.
let describe =
    function
    | InvalidConfiguration detail -> $"The deployment's configuration is not valid: {detail}."
    | MissingConfiguration field -> $"The deployment's configuration has no '{field}'."
    | InvalidDataLocation detail -> $"The configured data location is not valid: {detail}."
    | InvalidDatasetId datasetId -> $"'{datasetId}' is not a dataset id Signal can store under."
    | UnknownDataset datasetId -> $"The deployment does not configure dataset '{datasetId}'."
    | PublicProductionRepository -> "Production data cannot be started in a public repository."
    | OverrideWithoutReason -> "A decision to use a public repository for production data must say why."
    | NamespaceNotInitialized root -> $"'{root}' has not been set up for Signal."
    | NamespaceUnusable(root, detail) -> $"'{root}' cannot be used: {detail}."
    | DatasetNotInitialized datasetId -> $"Dataset '{datasetId}' has not been set up."
    | ManifestUnusable(datasetId, detail) -> $"Dataset '{datasetId}' cannot be opened: {detail}."
    | InvalidStoredRecord(path, detail) -> $"'{path}' is not a valid record: {detail}."
    | OperationRefused detail -> $"The change cannot be saved: {detail}."
    | UnstorableRecord(id, detail) -> $"'{id}' cannot be stored: {detail}."
    | MisplacedRecord path -> $"'{path}' is not where its id and type place it."
    | DuplicateRecord id -> $"'{id}' is stored twice; neither copy is used."
    | WrongDataset(path, found) -> $"'{path}' belongs to dataset '{found}'."
    | DanglingReference(from, target) -> $"'{from}' refers to '{target}', which is not stored."
    | ReferenceCycle ids -> "Records refer to each other in a cycle: " + String.concat ", " ids + "."
    | IncompleteRead folder -> $"The provider listed only part of '{folder}'; the rest was not checked."
    | CapabilityUnavailable(capability, reason) -> $"The store cannot offer {capability}: {reason}."
    | WritesUnavailable mode -> $"Changes cannot be saved ({mode})."
