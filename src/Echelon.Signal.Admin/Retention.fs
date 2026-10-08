/// Data lifecycle, retention and deletion (WI-0051, ADM-045), and what a
/// deletion does to reproducibility (ADM-064).
///
/// - **States** are the nine ADM-045 names, with legal transitions only.
///   Every transition makes a PII-free audit record.
/// - **Retention** is configured per artifact class.
/// - **Deletion claims follow the provider.** Removing a file from GitHub's
///   current tree leaves it in Git history, so a GitHub deletion ends as
///   `ProviderDeletionNotProvable`, and the only claim that may say
///   "permanently erased" is one the provider verified.
/// - **A retired dataset is read-only.**
/// - **Reproducibility** (ADM-064) is derived from what is still held, never
///   asserted: deleting accepted results leaves aggregate reproduction at
///   most, and the limitation is recorded as an audit record.
///
/// Pure.
module Echelon.Signal.Admin.Retention

open System

type State =
    | Active
    | Archived
    | RetainedForAudit
    | Superseded
    | Retired
    | DeletedFromActiveState
    | ProviderDeletionRequested
    | ProviderDeletionVerified
    | ProviderDeletionNotProvable

let stateId (state: State) =
    "state-"
    + (match state with
       | Active -> "active"
       | Archived -> "archived"
       | RetainedForAudit -> "retained-for-audit"
       | Superseded -> "superseded"
       | Retired -> "retired"
       | DeletedFromActiveState -> "deleted-from-active-state"
       | ProviderDeletionRequested -> "provider-deletion-requested"
       | ProviderDeletionVerified -> "provider-deletion-verified"
       | ProviderDeletionNotProvable -> "provider-deletion-not-provable")

/// The artifact classes retention is configured by.
type ArtifactClass =
    | AcceptedResult
    | GroupResult
    | ReportState
    | DerivedIndex
    | ReportDefinition
    | ReportSnapshot
    | ComparisonData
    | AuditRecord
    | SyntheticArtifact
    | BackupBundle

type Retention =
    | KeepIndefinitely
    | KeepForDays of int
    | RemoveWhenSuperseded

/// Retention by class; a class without an entry is kept.
type Policy = Map<ArtifactClass, Retention>

let defaultPolicy: Policy =
    Map
        [ DerivedIndex, RemoveWhenSuperseded
          SyntheticArtifact, KeepForDays 30
          BackupBundle, KeepForDays 365 ]

/// Whether an artifact of this class and state is due for removal.
let due (policy: Policy) (artifact: ArtifactClass) (state: State) (age: TimeSpan) =
    match policy |> Map.tryFind artifact |> Option.defaultValue KeepIndefinitely, state with
    | _, RetainedForAudit -> false
    | KeepIndefinitely, _ -> false
    | KeepForDays days, (Active | Archived | Superseded | Retired) -> age > TimeSpan.FromDays(float days)
    | RemoveWhenSuperseded, Superseded -> true
    | _ -> false

let private legal =
    Map
        [ Active, set [ Archived; RetainedForAudit; Superseded; Retired; DeletedFromActiveState ]
          Archived, set [ Active; RetainedForAudit; Retired; DeletedFromActiveState ]
          Superseded, set [ Archived; RetainedForAudit; Retired; DeletedFromActiveState ]
          RetainedForAudit, set [ Retired; DeletedFromActiveState ]
          Retired, set [ RetainedForAudit; DeletedFromActiveState ]
          DeletedFromActiveState, set [ ProviderDeletionRequested ]
          ProviderDeletionRequested, set [ ProviderDeletionVerified; ProviderDeletionNotProvable ] ]

type TransitionProblem =
    | IllegalTransition of from: State * target: State
    | NotAudited of Audit.Problem list

/// A transition and its audit record.
let transition (artifactId: string) (clock: DateTimeOffset option) (from: State) (target: State) : Result<State * Audit.Record, TransitionProblem> =
    if not (legal |> Map.tryFind from |> Option.exists (Set.contains target)) then
        Error(IllegalTransition(from, target))
    else
        Audit.create Audit.LifecycleChanged [ "artifact", artifactId ] [] 1 [ "LIFECYCLE-TRANSITION" ] (Some(stateId from)) (Some(stateId target)) clock
        |> Result.map (fun record -> target, record)
        |> Result.mapError NotAudited

/// A dataset accepts writes only while active; retired is read-only.
let writable (dataset: State) = dataset = Active

// ---- Deletion and what may be claimed ----------------------------------------------------------

/// What the storage provider can establish about erasure.
type Provider =
    /// Git history keeps prior versions of a removed file (GitHub).
    | HistoryRetaining
    /// The provider can prove irreversible erasure.
    | ProvableErasure

/// The provider's answer to a deletion request.
let providerOutcome (provider: Provider) (proved: bool) =
    match provider with
    | ProvableErasure when proved -> ProviderDeletionVerified
    | _ -> ProviderDeletionNotProvable

/// How far a removal has gone, separately at each level ADM-045 names.
type Removal =
    { FromApplicationState: bool
      FromRepositoryTree: bool
      ProviderHistory: State option }

/// What the page may say about a removal.
type Claim =
    | Present
    | RemovedFromApplicationState
    /// Gone from the current tree; the provider's history may still hold it.
    | RemovedFromCurrentTree
    | PermanentlyErased

let claim (removal: Removal) =
    match removal with
    | { ProviderHistory = Some ProviderDeletionVerified; FromRepositoryTree = true } -> PermanentlyErased
    | { FromRepositoryTree = true } -> RemovedFromCurrentTree
    | { FromApplicationState = true } -> RemovedFromApplicationState
    | _ -> Present

// ---- Reproducibility versus deletion (ADM-064) --------------------------------------------------

/// The artifact classes still held for a group.
type Holdings = Set<ArtifactClass>

type Reconstruction =
    | FullReconstruction
    /// Contribution-level reconstruction is no longer possible.
    | AggregateOnly
    /// Issued snapshots stand as immutable history; their source set is retired.
    | HistoricalSnapshotsOnly
    | NotReproducible

let reconstruction (held: Holdings) =
    if held.Contains AcceptedResult then FullReconstruction
    elif held.Contains GroupResult || held.Contains ReportState then AggregateOnly
    elif held.Contains ReportSnapshot then HistoricalSnapshotsOnly
    else NotReproducible

let reconstructionId (r: Reconstruction) =
    "state-"
    + (match r with
       | FullReconstruction -> "full-reconstruction"
       | AggregateOnly -> "aggregate-only"
       | HistoricalSnapshotsOnly -> "historical-snapshots-only"
       | NotReproducible -> "not-reproducible")

/// What a deletion would stop working.
type Break =
    | GroupAggregateRebuild
    | ReportSnapshotRebuild
    | AuditVerification
    | ComparisonVerification
    | MigrationVerification

let breakCode (b: Break) =
    match b with
    | GroupAggregateRebuild -> "BREAKS-GROUP-AGGREGATE-REBUILD"
    | ReportSnapshotRebuild -> "BREAKS-REPORT-SNAPSHOT-REBUILD"
    | AuditVerification -> "BREAKS-AUDIT-VERIFICATION"
    | ComparisonVerification -> "BREAKS-COMPARISON-VERIFICATION"
    | MigrationVerification -> "BREAKS-MIGRATION-VERIFICATION"

/// The verifications a deletion would break, given what is held now.
let breaks (held: Holdings) (deleting: Set<ArtifactClass>) : Break list =
    let after = Set.difference held deleting
    let lost artifact = held.Contains artifact && not (after.Contains artifact)

    [ if lost AcceptedResult then
          GroupAggregateRebuild
          MigrationVerification
      if lost AcceptedResult || (not (after.Contains AcceptedResult) && (lost GroupResult || lost ReportState)) then
          if held.Contains ReportSnapshot then ReportSnapshotRebuild
      if lost AuditRecord then AuditVerification
      if lost ComparisonData || (lost AcceptedResult && held.Contains ComparisonData) then ComparisonVerification ]
    |> List.distinct

/// The durable, PII-free evidence of a deletion that limits reproducibility.
type Limitation =
    { Before: Reconstruction
      After: Reconstruction
      Breaks: Break list
      /// No further lineage can be expanded from the deleted sources.
      LineageClosed: bool
      Evidence: Audit.Record }

/// Applies a deletion the policy requires, recording any loss of
/// reproducibility; None when nothing is lost.
let delete (groupId: string) (clock: DateTimeOffset option) (held: Holdings) (deleting: Set<ArtifactClass>) : Result<Holdings * Limitation option, Audit.Problem list> =
    let after = Set.difference held deleting
    let before, now = reconstruction held, reconstruction after
    let broken = breaks held deleting

    if before = now && broken.IsEmpty then
        Ok(after, None)
    else
        Audit.create Audit.ReproducibilityLimited [ "group", groupId ] [] 1 (broken |> List.map breakCode) (Some(reconstructionId before)) (Some(reconstructionId now)) clock
        |> Result.map (fun evidence ->
            after,
            Some
                { Before = before
                  After = now
                  Breaks = broken
                  LineageClosed = deleting.Contains AcceptedResult
                  Evidence = evidence })
