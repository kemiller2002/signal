/// The administrator state system (ADM-002, ADM-033): explicit states,
/// capabilities derived from authoritative F# state, and obligations for
/// unresolved work. The page shows what this module says; it never decides
/// a capability itself (ADM-035).
///
/// Pure.
module Echelon.Signal.Admin.AdminState

open Echelon.Signal.Admin.Access

/// Where storage stands.
type StorageState =
    /// The deployment configures no storage.
    | Unconfigured
    | InvalidConfiguration of reason: string
    /// Configured and verified with a credential that may write.
    | StorageConfigured
    /// Readable, but changes are unavailable now; the reason says why and
    /// whether the view may be stale.
    | DegradedReadOnly of reason: string

/// Where one group stands, from its lifecycle and what is stored.
type GroupPhase =
    | GroupReady
    | Importing
    | GroupPartial
    | GroupComplete
    | Reconciling
    | GroupClosedIncomplete
    | GroupFinalized
    | GroupFinalizedIncomplete
    | GroupSealed
    | GroupSuperseded

let phaseName =
    function
    | GroupReady -> "GroupReady"
    | Importing -> "Importing"
    | GroupPartial -> "GroupPartial"
    | GroupComplete -> "GroupComplete"
    | Reconciling -> "Reconciling"
    | GroupClosedIncomplete -> "ClosedIncomplete"
    | GroupFinalized -> "Finalized"
    | GroupFinalizedIncomplete -> "FinalizedIncomplete"
    | GroupSealed -> "Sealed"
    | GroupSuperseded -> "Superseded"

/// What is known about a group for its phase.
[<NoComparison>]
type GroupFacts =
    { Status: GroupLifecycle.Status
      Accepted: int
      Expected: int
      /// A batch is running (not yet finished).
      BatchRunning: bool
      UnreconciledBatches: int }

let phase (facts: GroupFacts) =
    match facts.Status with
    | GroupLifecycle.Collecting when facts.UnreconciledBatches > 0 -> Reconciling
    | GroupLifecycle.Collecting when facts.BatchRunning -> Importing
    | GroupLifecycle.Collecting when facts.Accepted = 0 -> GroupReady
    | GroupLifecycle.Collecting when facts.Accepted < facts.Expected -> GroupPartial
    | GroupLifecycle.Collecting -> GroupComplete
    | GroupLifecycle.ClosedIncomplete -> GroupClosedIncomplete
    | GroupLifecycle.Finalized -> GroupFinalized
    | GroupLifecycle.FinalizedIncomplete -> GroupFinalizedIncomplete
    | GroupLifecycle.Sealed -> GroupSealed
    | GroupLifecycle.Superseded _ -> GroupSuperseded

/// What the administrator may do now (ADM-002).
type AdminCapability =
    | CanReadStore
    | CanWriteStore
    | CanConfigureStorage
    | CanCreateGroup
    | CanImportSubmission
    | CanImportBatch
    | CanViewPartialReport
    | CanCloseGroup
    | CanReopenGroup
    | CanFinalizeGroup
    | CanSealGroup
    | CanBuildReport
    | CanCreateSnapshot
    | CanExport
    | CanMigrateStorage
    | CanRepairIndex
    | CanReconcile
    | CanManageAdministrators
    /// Author drafts (EditDrafts) and publish or hide versions (PublishTemplates), WI-0073.
    | CanEditDrafts
    | CanPublishTemplates
    /// Review a draft before publication (ReviewTemplates, AUT-006 §64).
    | CanReviewTemplates

let capabilityName (capability: AdminCapability) = $"%A{capability}"

/// The capabilities for a storage state, the person's usable capabilities in
/// the dataset (`Credential.usable`), and the open group's facts, if any.
let capabilities (storage: StorageState) (usable: Set<Capability>) (group: GroupFacts option) : Set<AdminCapability> =
    let reading =
        match storage with
        | StorageConfigured
        | DegradedReadOnly _ -> true
        | Unconfigured
        | InvalidConfiguration _ -> false

    let writing = storage = StorageConfigured
    let holds capability = usable.Contains capability
    let status = group |> Option.map _.Status

    let collecting = status = Some GroupLifecycle.Collecting
    let closedIncomplete = status = Some GroupLifecycle.ClosedIncomplete
    let finalized = status = Some GroupLifecycle.Finalized || status = Some GroupLifecycle.FinalizedIncomplete

    set
        [ if reading then CanReadStore
          if writing then CanWriteStore
          if writing && holds ManageStorage then
              CanConfigureStorage
              CanMigrateStorage
              CanRepairIndex
          if writing && holds ManageGroups then CanCreateGroup
          if writing && holds ImportSubmissions && collecting then
              CanImportSubmission
              CanImportBatch
          if writing && holds ImportSubmissions && (group |> Option.exists (fun g -> g.UnreconciledBatches > 0)) then CanReconcile
          if reading && holds ViewResults then CanViewPartialReport
          if writing && holds ManageGroups && collecting then CanCloseGroup
          if writing && holds ManageGroups && closedIncomplete then CanReopenGroup
          if writing && holds SealAndFinalize && (collecting || closedIncomplete) then CanFinalizeGroup
          if writing && holds SealAndFinalize && finalized then CanSealGroup
          if writing && holds BuildReports then
              CanBuildReport
              CanCreateSnapshot
          if reading && holds ExportData then CanExport
          if writing && holds ManageAdministrators then CanManageAdministrators
          if writing && holds EditDrafts then CanEditDrafts
          if writing && holds PublishTemplates then CanPublishTemplates
          if writing && holds ReviewTemplates then CanReviewTemplates ]

/// Unresolved work (ADM-002), named for the overview (ADM-031).
type Obligation =
    | ConfigureDurableStore
    | ResolveTemplate of reason: string
    | ReconcileUnknownWrite of batches: int
    | ResolveConcurrentModification of what: string
    | RepairCorruptRecords of problems: int
    | RebuildDerivedProjection
    | ResolveUnsupportedStoreCapability of capability: string
    | ResolveIncompatibleSurveyVersions of reason: string
    | ResolvePrivacyThresholdViolation

let obligationCode (obligation: Obligation) =
    match obligation with
    | ConfigureDurableStore -> "ConfigureDurableStore"
    | ResolveTemplate _ -> "ResolveTemplate"
    | ReconcileUnknownWrite _ -> "ReconcileUnknownWrite"
    | ResolveConcurrentModification _ -> "ResolveConcurrentModification"
    | RepairCorruptRecords _ -> "RepairCorruptRecords"
    | RebuildDerivedProjection -> "RebuildDerivedProjection"
    | ResolveUnsupportedStoreCapability _ -> "ResolveUnsupportedStoreCapability"
    | ResolveIncompatibleSurveyVersions _ -> "ResolveIncompatibleSurveyVersions"
    | ResolvePrivacyThresholdViolation -> "ResolvePrivacyThresholdViolation"

/// One sentence for the overview; the code is in the technical detail.
let describeObligation (obligation: Obligation) =
    match obligation with
    | ConfigureDurableStore -> "Configure where this deployment keeps its data."
    | ResolveTemplate reason -> $"A group's template cannot be resolved: {reason}."
    | ReconcileUnknownWrite batches -> $"{batches} import(s) need reconciling before anything is sent again."
    | ResolveConcurrentModification what -> $"{what} changed elsewhere and needs a decision."
    | RepairCorruptRecords problems -> $"{problems} stored record(s) cannot be used and need repair."
    | RebuildDerivedProjection -> "The derived index is out of date; rebuild it."
    | ResolveUnsupportedStoreCapability capability -> $"The store cannot offer {capability}."
    | ResolveIncompatibleSurveyVersions reason -> $"Survey versions are incompatible: {reason}."
    | ResolvePrivacyThresholdViolation -> "A privacy threshold is not a valid policy."

/// The obligations a group's finalization evidence leaves.
let ofGroup (obligations: GroupLifecycle.Obligation list) =
    obligations
    |> List.map (function
        | GroupLifecycle.CompletionNotSettled -> ResolveConcurrentModification "The group's completion"
        | GroupLifecycle.ReconcileUnknownImport batches -> ReconcileUnknownWrite batches
        | GroupLifecycle.RepairStoredRecords problems -> RepairCorruptRecords problems
        | GroupLifecycle.RebuildDerivedIndex -> RebuildDerivedProjection
        | GroupLifecycle.ResolvePrivacyThreshold -> ResolvePrivacyThresholdViolation)
