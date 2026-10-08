/// A group's lifecycle (ADM-007, ADM-065, ADM-066): its legal states, the
/// transitions between them, the obligations that block finalization, and
/// the record that stores it.
///
/// Completion is derived (enough accepted contributions); closing,
/// finalizing and sealing are administrator decisions, each a legal
/// transition from named states only:
///
/// ```
/// Collecting --close--> ClosedIncomplete --reopen--> Collecting
/// Collecting|ClosedIncomplete --finalize--> Finalized | FinalizedIncomplete
/// Finalized|FinalizedIncomplete --seal--> Sealed
/// any but Superseded --supersede(successor)--> Superseded
/// ```
///
/// A finalized, sealed or superseded group accepts no contribution and no
/// configuration change; a sealed one is never reopened: the way forward is
/// a successor group, which never mutates the prior one's results.
///
/// The lifecycle is its own record, `records/signal.group-lifecycle/<group>.json`,
/// mutable under its revision, beside the group's configuration.
///
/// Pure.
module Echelon.Signal.Admin.GroupLifecycle

open Arca
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec

/// Where a group stands.
[<NoComparison>]
type Status =
    /// Accepting contributions.
    | Collecting
    /// Closed before completion, by decision; it may be reopened.
    | ClosedIncomplete
    | Finalized
    | FinalizedIncomplete
    /// Finalized and sealed: read, reproduce and export only.
    | Sealed
    /// Replaced by a successor group.
    | Superseded of successor: OpaqueId

let statusName =
    function
    | Collecting -> "Collecting"
    | ClosedIncomplete -> "ClosedIncomplete"
    | Finalized -> "Finalized"
    | FinalizedIncomplete -> "FinalizedIncomplete"
    | Sealed -> "Sealed"
    | Superseded _ -> "Superseded"

/// Whether the group accepts new contributions.
let acceptsContributions =
    function
    | Collecting -> true
    | _ -> false

/// Whether the group's configuration may change.
let acceptsConfiguration =
    function
    | Collecting
    | ClosedIncomplete -> true
    | _ -> false

/// What must be settled before a group is finalized (ADM-066).
type Obligation =
    /// Neither complete nor closed incomplete.
    | CompletionNotSettled
    /// An import's outcome is still unknown.
    | ReconcileUnknownImport of batches: int
    /// Stored records could not all be used.
    | RepairStoredRecords of problems: int
    /// The derived index does not reflect the records.
    | RebuildDerivedIndex
    /// The privacy threshold is not a valid policy.
    | ResolvePrivacyThreshold

let obligationCode =
    function
    | CompletionNotSettled -> "CompletionNotSettled"
    | ReconcileUnknownImport _ -> "ReconcileUnknownWrite"
    | RepairStoredRecords _ -> "RepairCorruptRecords"
    | RebuildDerivedIndex -> "RebuildDerivedProjection"
    | ResolvePrivacyThreshold -> "ResolvePrivacyThresholdViolation"

/// What finalization checks, gathered by the caller from the store.
type Evidence =
    { Complete: bool
      UnreconciledBatches: int
      RecordProblems: int
      IndexCurrent: bool
      MinimumReportableCount: int }

/// The obligations evidence leaves open.
let obligations (status: Status) (evidence: Evidence) =
    [ if not evidence.Complete && status <> ClosedIncomplete then CompletionNotSettled
      if evidence.UnreconciledBatches > 0 then ReconcileUnknownImport evidence.UnreconciledBatches
      if evidence.RecordProblems > 0 then RepairStoredRecords evidence.RecordProblems
      if not evidence.IndexCurrent then RebuildDerivedIndex
      if evidence.MinimumReportableCount < 1 then ResolvePrivacyThreshold ]

/// A transition an administrator asks for.
[<NoComparison>]
type Transition =
    | Close
    | Reopen
    | Finalize of Evidence
    | Seal
    | Supersede of successor: OpaqueId

/// Why a transition is refused.
type Refusal =
    /// Not a legal transition from this state.
    | Illegal of from: string * transition: string
    /// Finalization is blocked by these obligations.
    | BlockedByObligations of Obligation list

let refusalCode =
    function
    | Illegal _ -> "SIGNAL.GROUP.ILLEGAL_TRANSITION"
    | BlockedByObligations _ -> "SIGNAL.GROUP.BLOCKED_BY_OBLIGATIONS"

let private transitionName =
    function
    | Close -> "close"
    | Reopen -> "reopen"
    | Finalize _ -> "finalize"
    | Seal -> "seal"
    | Supersede _ -> "supersede"

/// The next status, or why not.
let apply (transition: Transition) (status: Status) : Result<Status, Refusal> =
    let illegal () = Error(Illegal(statusName status, transitionName transition))

    match transition, status with
    | Close, Collecting -> Ok ClosedIncomplete
    | Reopen, ClosedIncomplete -> Ok Collecting
    | Finalize evidence, (Collecting | ClosedIncomplete) ->
        match obligations status evidence with
        | [] -> Ok(if evidence.Complete then Finalized else FinalizedIncomplete)
        | open' -> Error(BlockedByObligations open')
    | Seal, (Finalized | FinalizedIncomplete) -> Ok Sealed
    | Supersede successor, (Collecting | ClosedIncomplete | Finalized | FinalizedIncomplete | Sealed) -> Ok(Superseded successor)
    | _ -> illegal ()

// ---- The record ---------------------------------------------------------------------------

/// A group's stored lifecycle.
[<NoComparison>]
type Lifecycle =
    { Group: OpaqueId
      Status: Status
      /// The group this one succeeds, if any.
      Predecessor: OpaqueId option
      Revision: int }

let initial (group: OpaqueId) (predecessor: OpaqueId option) =
    { Group = group
      Status = Collecting
      Predecessor = predecessor
      Revision = 1 }

let recordType =
    match RecordType.create "signal.group-lifecycle" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

let path (group: OpaqueId) : Result<RelativePath, Problem> =
    match RecordId.create (GroupRecord.groupKey group) with
    | Ok id ->
        Layout.recordPath
            { Type = recordType
              Partition = []
              Id = id }
        |> Result.mapError (LocationError.describe >> InvalidDataLocation)
    | Error text -> Error(UnstorableRecord(text, "not a record id"))

let encode (datasetId: string) (lifecycle: Lifecycle) : Result<string, Problem> =
    match RecordId.create (GroupRecord.groupKey lifecycle.Group) with
    | Error text -> Error(UnstorableRecord(text, "not a record id"))
    | Ok id ->
        { Id = id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Mutable
          Body =
            Json.objectOf
                [ "datasetId", Json.String datasetId
                  "group", Json.String(string lifecycle.Group)
                  "status", Json.String(statusName lifecycle.Status)
                  "successor",
                  (match lifecycle.Status with
                   | Superseded successor -> Json.String(string successor)
                   | _ -> Json.Null)
                  "predecessor", (lifecycle.Predecessor |> Option.map (string >> Json.String) |> Option.defaultValue Json.Null)
                  "revision", Json.Number(decimal lifecycle.Revision) ] }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(GroupRecord.groupKey lifecycle.Group, "the lifecycle record is too large"))

let private groupOf (value: Json) : Decoded<OpaqueId option> =
    match value with
    | Json.Null -> Ok None
    | Json.String text -> OpaqueId.tryParse text |> Option.map (Some >> Ok) |> Option.defaultValue (Error "not a group id")
    | _ -> Error "not a group id or null"

[<NoComparison>]
type StoredLifecycle = { DatasetId: string; Lifecycle: Lifecycle }

let ofBody (value: Json) : Decoded<StoredLifecycle> =
    closed [ "datasetId"; "group"; "predecessor"; "revision"; "status"; "successor" ] value
    |> Result.bind (fun () ->
        match
            text "datasetId" value,
            field "group" value |> Result.bind groupOf,
            both (text "status" value) (field "successor" value |> Result.bind groupOf),
            field "predecessor" value |> Result.bind groupOf,
            integer "revision" value
        with
        | Ok dataset, Ok(Some group), Ok(status, successor), Ok predecessor, Ok revision when revision >= 1 ->
            let parsed =
                match status, successor with
                | "Collecting", None -> Ok Collecting
                | "ClosedIncomplete", None -> Ok ClosedIncomplete
                | "Finalized", None -> Ok Finalized
                | "FinalizedIncomplete", None -> Ok FinalizedIncomplete
                | "Sealed", None -> Ok Sealed
                | "Superseded", Some next -> Ok(Superseded next)
                | other, _ -> Error $"'{other}' with this successor is not a lifecycle state"

            parsed
            |> Result.map (fun status ->
                { DatasetId = dataset
                  Lifecycle =
                    { Group = group
                      Status = status
                      Predecessor = predecessor
                      Revision = revision } })
        | Ok _, Ok None, _, _, _ -> Error "'group' is missing"
        | Ok _, Ok _, Ok _, Ok _, Ok _ -> Error "'revision' must be at least 1"
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e)

let reader: Loading.RecordReader<StoredLifecycle> =
    { Type = recordType
      Schema = schema
      MaxBytes = Record.DefaultMaxBytes
      Decode = ofBody
      IdOf = fun stored -> GroupRecord.groupKey stored.Lifecycle.Group
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }
