/// Submission intake: quarantine, batches and promotion (ADM-008, ADM-009,
/// ADM-060, ADM-061, ADM-067).
///
/// - Every artifact enters **quarantine**: it is parsed, matched to the
///   group's exact template, verified, decoded, validated and scored, but
///   nothing about it is authoritative. A quarantined artifact contributes
///   to no result, count or report.
/// - A **batch** has a stable id from its group and the set of its artifact
///   hashes, so the same artifacts in any order are the same batch. Each item
///   is accepted or rejected on its own; the plan is order-independent: when
///   two different artifacts in a batch claim one identity, the one with the
///   lower submission hash is accepted and the other is a duplicate, whatever
///   order they arrived in.
/// - **Promotion** turns accepted items into immutable contribution records
///   (`ResultRecord`), each a `Create` at its identity's path: the explicit
///   transition from quarantine to authoritative. Rejected artifacts leave
///   only their hash and reason code.
/// - The batch's record states each item's outcome, so a resumed batch skips
///   what was decided and never counts it twice; replaying it is idempotent.
///
/// Pure.
module Echelon.Signal.Admin.Intake

open System
open System.Security.Cryptography
open System.Text
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec
open Echelon.Signal.Admin.ResultRecord

/// One artifact as pasted or read: its text and hash.
type Artifact = { Text: string; Hash: string }

let artifact (text: string) = { Text = text; Hash = artifactHash text }

/// What happened to one item of a batch.
type ItemOutcome =
    | AcceptedItem of identityKey: string * submissionHash: string
    /// The same artifact was already accepted: nothing changes (idempotent).
    | DuplicateItem of identityKey: string
    | RejectedItem of code: string
    /// The exact template is not in the catalog; reprocess once it is.
    | BlockedMissingTemplate of reason: string
    /// The artifact uses an encoding this Signal cannot read.
    | BlockedUnsupportedEncoding of version: int
    /// Its commit's outcome is unknown; reconciled before anything is resent.
    | ReconciliationRequired

let itemCode =
    function
    | AcceptedItem _ -> "accepted"
    | DuplicateItem _ -> "duplicate"
    | RejectedItem code -> code
    | BlockedMissingTemplate _ -> "blocked:missing-template"
    | BlockedUnsupportedEncoding version -> $"blocked:unsupported-encoding:{version}"
    | ReconciliationRequired -> "reconciliation-required"

/// The quarantine verdict on one artifact against a group.
[<NoComparison>]
type Quarantined =
    { Artifact: Artifact
      Outcome: ImportOutcome
      /// The checks it passed, as evidence for promotion.
      Evidence: string list }

let private evidenceFor =
    function
    | Accepted _ -> [ "parsed"; "template-resolved"; "integrity-verified"; "decoded"; "validated"; "scored"; "group-rules" ]
    | AlreadyImported _ -> [ "parsed"; "template-resolved"; "integrity-verified"; "decoded"; "validated"; "already-accepted" ]
    | Rejected _ -> []

/// Quarantines artifacts against a group: each is evaluated against the
/// accepted identities and nothing is written. Collisions inside the batch
/// are settled by the lower submission hash, so the order is irrelevant.
let quarantine (today: System.DateOnly) (definition: GroupDefinition) (accepted: string -> string option) (artifacts: Artifact list) : Quarantined list =
    let evaluated =
        artifacts
        |> List.distinctBy _.Hash
        |> List.sortBy _.Hash
        |> List.map (fun item -> item, GenericImport.evaluateOn today definition accepted item.Text)

    let winners =
        evaluated
        |> List.choose (fun (_, outcome) ->
            match outcome with
            | Accepted result -> Some(result.Identity.Key, result.SubmissionHash)
            | _ -> None)
        |> List.groupBy fst
        |> List.map (fun (key, hashes) -> key, hashes |> List.map snd |> List.min)
        |> Map.ofList

    evaluated
    |> List.map (fun (item, outcome) ->
        let settled =
            match outcome with
            | Accepted result when winners[result.Identity.Key] = result.SubmissionHash -> outcome
            | Accepted result -> Rejected(DuplicateInstance winners[result.Identity.Key])
            | other -> other

        { Artifact = item
          Outcome = settled
          Evidence = evidenceFor settled })

/// What a quarantine verdict means for the batch.
let outcomeOf (quarantined: Quarantined) =
    match quarantined.Outcome with
    | Accepted result -> AcceptedItem(result.Identity.Key, result.SubmissionHash)
    | AlreadyImported identity -> DuplicateItem identity.Key
    | Rejected(Unreadable(UnsupportedVersion version)) -> BlockedUnsupportedEncoding version
    | Rejected _ as rejected -> RejectedItem(outcomeCode rejected)

// ---- Promotion -------------------------------------------------------------------------

/// The contribution an accepted artifact becomes, with how it entered.
/// Anything not accepted is never promoted.
let promote (group: OpaqueId) (retention: GroupRecord.SubmissionRetention) (origin: ImportOrigin) (batchId: string) (quarantined: Quarantined) : Contribution option =
    match quarantined.Outcome with
    | Accepted result ->
        Some
            { Group = group
              Result = result
              Provenance =
                { Origin = origin
                  BatchId = batchId
                  ArtifactHash = quarantined.Artifact.Hash
                  EncodingVersion = ResponseEncodingVersion }
              Submission =
                match retention with
                | GroupRecord.RetainCanonicalSubmission -> payloadOf quarantined.Artifact.Text
                | GroupRecord.NoneAfterImport -> None }
    | AlreadyImported _
    | Rejected _ -> None

/// What a retained submission says when it is read again (URLC-005).
type Reconstruction =
    /// The URL artifact and the exact template reproduce the stored result.
    | Reproduced
    /// Only the derived result was kept (NoneAfterImport).
    | NotRetained
    /// The artifact no longer reproduces the stored result: the record or
    /// the template is not what it was.
    | Differs of reason: string

/// Re-reads a retained submission against the group's exact template and
/// compares it with the stored result (URLC-005's core invariant: a valid
/// submission plus its exact published template reconstruct, validate and
/// score the complete response).
let reconstruct (definition: GroupDefinition) (contribution: Contribution) : Reconstruction =
    match contribution.Submission with
    | None -> NotRetained
    | Some payload ->
        match GenericImport.evaluate definition (fun _ -> None) payload with
        | Accepted result when result.SubmissionHash <> contribution.Result.SubmissionHash -> Differs "the submission hash differs"
        | Accepted result when result.Identity.Key <> contribution.Result.Identity.Key -> Differs "the identity differs"
        | Accepted result when result.Dimensions <> contribution.Result.Dimensions -> Differs "the scores differ"
        | Accepted _ -> Reproduced
        | AlreadyImported _ -> Differs "unexpected duplicate"
        | Rejected error -> Differs $"the artifact is rejected: %A{error}"

// ---- Batches -------------------------------------------------------------------------------

/// A batch's status (ADM-060).
type BatchStatus =
    | Pending
    | Running
    | Partial
    | Complete
    | CompleteWithRejections
    | NeedsReconciliation
    | CancelledBeforeProcessingRemainder

let statusName =
    function
    | Pending -> "Pending"
    | Running -> "Running"
    | Partial -> "Partial"
    | Complete -> "Complete"
    | CompleteWithRejections -> "CompleteWithRejections"
    | NeedsReconciliation -> "ReconciliationRequired"
    | CancelledBeforeProcessingRemainder -> "CancelledBeforeProcessingRemainder"

/// A batch: its stable id, and each artifact's outcome once decided.
[<NoComparison>]
type Batch =
    { BatchId: string
      Group: OpaqueId
      Origin: ImportOrigin
      /// By artifact hash, in hash order.
      Items: (string * ItemOutcome option) list
      Cancelled: bool
      /// The stored batch record's revision, from 1.
      Revision: int }

/// The batch id for a group and a set of artifacts: the same artifacts in
/// any order give the same id.
let batchId (group: OpaqueId) (artifacts: Artifact list) =
    let material = String.concat "\n" (GroupRecord.groupKey group :: (artifacts |> List.map _.Hash |> List.distinct |> List.sort))
    "batch-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes material)).ToLowerInvariant().Substring(0, 32)

/// A new batch: every item pending.
let start (group: OpaqueId) (origin: ImportOrigin) (artifacts: Artifact list) =
    { BatchId = batchId group artifacts
      Group = group
      Origin = origin
      Items = artifacts |> List.map _.Hash |> List.distinct |> List.sort |> List.map (fun hash -> hash, None)
      Cancelled = false
      Revision = 1 }

/// The items still to process: undecided, blocked (reprocessable once the
/// block is resolved) or awaiting reconciliation.
let remaining (batch: Batch) =
    batch.Items
    |> List.filter (fun (_, outcome) ->
        match outcome with
        | None
        | Some(BlockedMissingTemplate _)
        | Some(BlockedUnsupportedEncoding _)
        | Some ReconciliationRequired -> true
        | Some _ -> false)
    |> List.map fst

/// Records outcomes for items of the batch.
let record (outcomes: (string * ItemOutcome) list) (batch: Batch) =
    let decided = Map.ofList outcomes

    { batch with
        Items = batch.Items |> List.map (fun (hash, outcome) -> hash, (decided.TryFind hash |> Option.orElse outcome)) }

/// Cancels the rest of a batch. What was already committed stays: accepted
/// contributions are independent and are never rolled back (ADM-060).
let cancel (batch: Batch) = { batch with Cancelled = true }

/// Counts and status (ADM-060).
type BatchSummary =
    { BatchId: string
      InputArtifactCount: int
      ProcessedCount: int
      AcceptedCount: int
      RejectedCount: int
      DuplicateCount: int
      ReconciliationRequiredCount: int
      BlockedCount: int
      RemainingCount: int
      Status: BatchStatus }

let summarize (batch: Batch) : BatchSummary =
    let count predicate = batch.Items |> List.filter (snd >> predicate) |> List.length
    let is f = function Some outcome -> f outcome | None -> false
    let accepted = count (is (function AcceptedItem _ -> true | _ -> false))
    let rejected = count (is (function RejectedItem _ -> true | _ -> false))
    let duplicate = count (is (function DuplicateItem _ -> true | _ -> false))
    let reconcile = count (is (function ReconciliationRequired -> true | _ -> false))
    let blocked = count (is (function BlockedMissingTemplate _ | BlockedUnsupportedEncoding _ -> true | _ -> false))
    let pending = count Option.isNone
    let processed = batch.Items.Length - pending

    { BatchId = batch.BatchId
      InputArtifactCount = batch.Items.Length
      ProcessedCount = processed
      AcceptedCount = accepted
      RejectedCount = rejected
      DuplicateCount = duplicate
      ReconciliationRequiredCount = reconcile
      BlockedCount = blocked
      RemainingCount = pending + blocked + reconcile
      Status =
        if reconcile > 0 then NeedsReconciliation
        elif pending > 0 && batch.Cancelled then CancelledBeforeProcessingRemainder
        elif processed = 0 then Pending
        elif pending > 0 then Running
        elif blocked > 0 then Partial
        elif rejected > 0 then CompleteWithRejections
        else Complete }

// ---- The batch record ---------------------------------------------------------------------

let recordType =
    match RecordType.create "signal.import-batch" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

let path (batch: Batch) : Result<RelativePath, Problem> =
    match RecordId.create batch.BatchId with
    | Ok id ->
        Layout.recordPath
            { Type = recordType
              Partition = []
              Id = id }
        |> Result.mapError (LocationError.describe >> InvalidDataLocation)
    | Error text -> Error(UnstorableRecord(text, "not a record id"))

/// The batch's record: hashes and outcome codes only, no artifact text and no person.
let encode (datasetId: string) (batch: Batch) : Result<string, Problem> =
    let text' = Option.map Json.String >> Option.defaultValue Json.Null

    let item (hash: string, outcome: ItemOutcome option) =
        let identity, submission =
            match outcome with
            | Some(AcceptedItem(identity, submission)) -> Some identity, Some submission
            | Some(DuplicateItem identity) -> Some identity, None
            | _ -> None, None

        Json.objectOf
            [ "artifactHash", Json.String hash
              "outcome", text' (outcome |> Option.map itemCode)
              "identity", text' identity
              "submissionHash", text' submission ]

    match RecordId.create batch.BatchId with
    | Error text -> Error(UnstorableRecord(text, "not a record id"))
    | Ok id ->
        { Id = id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Mutable
          Body =
            Json.objectOf
                [ "datasetId", Json.String datasetId
                  "batchId", Json.String batch.BatchId
                  "group", Json.String(string batch.Group)
                  "origin", Json.String(originName batch.Origin)
                  "items", Json.Array(batch.Items |> List.map item)
                  "cancelled", Json.Bool batch.Cancelled
                  "status", Json.String(statusName (summarize batch).Status)
                  "revision", Json.Number(decimal batch.Revision) ] }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(batch.BatchId, "the batch record is too large"))

/// The Arca changes that store a batch step: a `Create` for each accepted
/// contribution (its identity's path, so a concurrent different artifact for
/// the same identity is a conflict) and the batch record (created, or
/// updated at its revision).
let changes (datasetId: string) (contributions: Contribution list) (batch: Batch) (storedRevision: Revision option) : Result<Change list, Problem list> =
    let creates =
        contributions
        |> List.map (fun contribution -> ResultRecord.encode datasetId contribution |> Result.map Change.Create)

    let batchChange =
        match path batch, encode datasetId batch with
        | Ok target, Ok content ->
            Ok(
                match storedRevision with
                | Some revision -> Change.Update(target, content, revision)
                | None -> Change.Create(target, content)
            )
        | Error problem, _
        | _, Error problem -> Error problem

    match (creates @ [ batchChange ]) |> List.choose (function Error p -> Some p | Ok _ -> None) with
    | [] -> Ok((creates @ [ batchChange ]) |> List.choose Result.toOption)
    | problems -> Error problems

let private optionalText name value : Decoded<string option> =
    field name value
    |> Result.bind (function
        | Json.Null -> Ok None
        | Json.String found -> Ok(Some found)
        | _ -> Error $"'{name}' is not text or null")

let private outcomeOf' (code: string option) (identity: string option) (submission: string option) : Decoded<ItemOutcome option> =
    match code, identity, submission with
    | None, _, _ -> Ok None
    | Some "accepted", Some identity, Some submission -> Ok(Some(AcceptedItem(identity, submission)))
    | Some "accepted", _, _ -> Error "an accepted item names no identity or submission"
    | Some "duplicate", Some identity, _ -> Ok(Some(DuplicateItem identity))
    | Some "duplicate", None, _ -> Error "a duplicate item names no identity"
    | Some "blocked:missing-template", _, _ -> Ok(Some(BlockedMissingTemplate "the template was not in the catalog"))
    | Some "reconciliation-required", _, _ -> Ok(Some ReconciliationRequired)
    | Some code, _, _ when code.StartsWith "blocked:unsupported-encoding:" ->
        match Int32.TryParse(code.Substring "blocked:unsupported-encoding:".Length) with
        | true, version -> Ok(Some(BlockedUnsupportedEncoding version))
        | _ -> Error $"'{code}' is not an outcome"
    | Some code, _, _ when code.StartsWith "rejected:" -> Ok(Some(RejectedItem code))
    | Some code, _, _ -> Error $"'{code}' is not an outcome"

/// A stored batch: its dataset and the batch.
[<NoComparison>]
type StoredBatch = { DatasetId: string; Batch: Batch }

let ofBody (value: Json) : Decoded<StoredBatch> =
    closed [ "batchId"; "cancelled"; "datasetId"; "group"; "items"; "origin"; "revision"; "status" ] value
    |> Result.bind (fun () ->
        let item (entry: Json) =
            closed [ "artifactHash"; "identity"; "outcome"; "submissionHash" ] entry
            |> Result.bind (fun () ->
                match text "artifactHash" entry, optionalText "outcome" entry, optionalText "identity" entry, optionalText "submissionHash" entry with
                | Ok hash, Ok code, Ok identity, Ok submission -> outcomeOf' code identity submission |> Result.map (fun outcome -> hash, outcome)
                | Error e, _, _, _
                | _, Error e, _, _
                | _, _, Error e, _
                | _, _, _, Error e -> Error e)

        let group =
            text "group" value
            |> Result.bind (fun t -> OpaqueId.tryParse t |> Option.map Ok |> Option.defaultValue (Error "'group' is not a group id"))

        let origin =
            text "origin" value
            |> Result.bind (fun name ->
                [ PastedUrl; MultiPaste; ImportedTextFile; ImportedCanonicalArtifact; RestoredBackup; ProviderMigration; SyntheticSandbox ]
                |> List.tryFind (fun o -> originName o = name)
                |> Option.map Ok
                |> Option.defaultValue (Error $"'{name}' is not an import origin"))

        match
            both (text "datasetId" value) (text "batchId" value),
            group,
            origin,
            list "items" item value,
            both (flag "cancelled" value) (integer "revision" value)
        with
        | Ok(dataset, id), Ok group, Ok origin, Ok items, Ok(cancelled, revision) ->
            if items |> List.map fst <> (items |> List.map fst |> List.distinct |> List.sort) then
                Error "'items' are not distinct and in hash order"
            else
                Ok
                    { DatasetId = dataset
                      Batch =
                        { BatchId = id
                          Group = group
                          Origin = origin
                          Items = items
                          Cancelled = cancelled
                          Revision = revision } }
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e)
