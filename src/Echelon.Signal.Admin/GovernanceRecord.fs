/// Audit records and the dataset's lifecycle state as Arca records
/// (WI-0076, ADM-030, ADM-045).
///
/// - **Audit** (`signal.audit`, immutable): one record per audited event,
///   written in the same commit as the change it describes, so a change and
///   its audit record land together or not at all. Its id is derived from
///   the record's hash and the operation's idempotency key, so a retried
///   operation writes the same record once. Reading it re-validates every
///   field through `Audit.create`, so a stored record that names a person is
///   refused like a new one.
/// - **Lifecycle** (`signal.dataset-lifecycle`, mutable): the dataset's
///   ADM-045 state. A dataset with no record is active.
///
/// Pure.
module Echelon.Signal.Admin.GovernanceRecord

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Arca
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec

let private typeOf (name: string) =
    match RecordType.create name with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

let auditType = typeOf "signal.audit"
let lifecycleType = typeOf "signal.dataset-lifecycle"

let private schemaOf recordType = { Type = recordType; OldestReadable = 1; Current = 1 }

let private idFor (text: string) =
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant().Substring(0, 32)

let private pathOf (recordType: RecordType) (id: string) : Result<RelativePath, Problem> =
    match RecordId.create id with
    | Ok recordId ->
        { Type = recordType; Partition = []; Id = recordId }
        |> Layout.recordPath
        |> Result.mapError (LocationError.describe >> InvalidDataLocation)
    | Error text -> Error(UnstorableRecord(text, "not a record id"))

let folderOf (recordType: RecordType) : RelativePath =
    match RelativePath.parse (String.concat "/" [ Layout.RecordsFolder; RecordType.value recordType ]) with
    | Ok found -> found
    | Error error -> invalidOp ("internal: " + LocationError.describe error)

let private encodeWith (recordType: RecordType) (mutability: Mutability) (id: string) (body: Json) : Result<string, Problem> =
    match RecordId.create id with
    | Ok recordId ->
        { Id = recordId; Type = recordType; SchemaVersion = 1; Mutability = mutability; Body = body }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(id, "the record is too large"))
    | Error text -> Error(UnstorableRecord(text, "not a record id"))

// ---- Audit -------------------------------------------------------------------------------------

let private events =
    [ Audit.StorageConfigured
      Audit.StorageVerified
      Audit.GroupCreated
      Audit.GroupConfigurationVersioned
      Audit.SubmissionAccepted
      Audit.SubmissionRejected
      Audit.DuplicateRejected
      Audit.UnknownWriteObserved
      Audit.WriteReconciled
      Audit.AggregateRebuilt
      Audit.IndexRebuilt
      Audit.ReportDefinitionPublished
      Audit.ReportSnapshotCreated
      Audit.ExportCreated
      Audit.MigrationStarted
      Audit.MigrationVerified
      Audit.DestinationActivated
      Audit.LifecycleChanged
      Audit.ReproducibilityLimited ]

[<NoComparison>]
type StoredAudit =
    { DatasetId: string
      /// The operation's idempotency key, which with the hash names the record.
      Operation: string
      Record: Audit.Record }

let auditId (operation: string) (record: Audit.Record) = idFor (Audit.hashOf record + "\n" + operation)

let auditPath operation record = pathOf auditType (auditId operation record)

let private pairsJson (items: (string * string) list) =
    Json.Array(items |> List.map (fun (k, v) -> Json.objectOf [ "role", Json.String k; "value", Json.String v ]))

let private pairsOf name value =
    list name (fun item -> both (text "role" item) (text "value" item)) value

let private optionalText = Option.map Json.String >> Option.defaultValue Json.Null

let private optionalTextOf name value : Decoded<string option> =
    field name value
    |> Result.bind (function
        | Json.Null -> Ok None
        | Json.String s -> Ok(Some s)
        | _ -> Error $"'{name}' is not text")

let encodeAudit (datasetId: string) (operation: string) (r: Audit.Record) : Result<string, Problem> =
    Json.objectOf
        [ "datasetId", Json.String datasetId
          "operation", Json.String operation
          "event", Json.String(Audit.eventName r.Event)
          "ids", pairsJson r.Ids
          "hashes", pairsJson r.Hashes
          "schemaVersion", Json.Number(decimal r.SchemaVersion)
          "reasons", textArray r.Reasons
          "before", optionalText r.Before
          "after", optionalText r.After
          "clock",
          (match r.ClockEvidence with
           | Some at -> Json.String(at.ToString("O", CultureInfo.InvariantCulture))
           | None -> Json.Null) ]
    |> encodeWith auditType Mutability.Immutable (auditId operation r)

let auditOfBody (value: Json) : Decoded<StoredAudit> =
    let event =
        text "event" value
        |> Result.bind (fun name ->
            match events |> List.tryFind (fun e -> Audit.eventName e = name) with
            | Some e -> Ok e
            | None -> Error $"'{name}' is not an audit event")

    let clock =
        optionalTextOf "clock" value
        |> Result.bind (function
            | None -> Ok None
            | Some t ->
                match DateTimeOffset.TryParseExact(t, "O", CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, parsed -> Ok(Some parsed)
                | _ -> Error "'clock' is not a timestamp")

    closed [ "after"; "before"; "clock"; "datasetId"; "event"; "hashes"; "ids"; "operation"; "reasons"; "schemaVersion" ] value
    |> Result.bind (fun () ->
        both (both (text "datasetId" value) (text "operation" value)) (both event (both (pairsOf "ids" value) (both (pairsOf "hashes" value) (both (integer "schemaVersion" value) (both (texts "reasons" value) (both (optionalTextOf "before" value) (both (optionalTextOf "after" value) clock))))))))
    |> Result.bind (fun ((datasetId, operation), (e, (ids, (hashes, (schema, (reasons, (before, (after, at)))))))) ->
        match Audit.create e ids hashes schema reasons before after at with
        | Ok record -> Ok { DatasetId = datasetId; Operation = operation; Record = record }
        | Error problems -> Error $"the audit record holds what audit may not: %A{problems}")

let auditReader: Loading.RecordReader<StoredAudit> =
    { Type = auditType
      Schema = schemaOf auditType
      MaxBytes = Record.DefaultMaxBytes
      Decode = auditOfBody
      IdOf = fun stored -> auditId stored.Operation stored.Record
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }

// ---- The dataset's lifecycle -------------------------------------------------------------------

[<Literal>]
let LifecycleId = "dataset"

let lifecyclePath () = pathOf lifecycleType LifecycleId

let private states =
    [ Retention.Active
      Retention.Archived
      Retention.RetainedForAudit
      Retention.Superseded
      Retention.Retired
      Retention.DeletedFromActiveState
      Retention.ProviderDeletionRequested
      Retention.ProviderDeletionVerified
      Retention.ProviderDeletionNotProvable ]

[<NoComparison>]
type StoredLifecycle = { DatasetId: string; State: Retention.State }

let encodeLifecycle (datasetId: string) (state: Retention.State) : Result<string, Problem> =
    Json.objectOf [ "datasetId", Json.String datasetId; "state", Json.String(Retention.stateId state) ]
    |> encodeWith lifecycleType Mutability.Mutable LifecycleId

let lifecycleOfBody (value: Json) : Decoded<StoredLifecycle> =
    closed [ "datasetId"; "state" ] value
    |> Result.bind (fun () -> both (text "datasetId" value) (text "state" value))
    |> Result.bind (fun (datasetId, state) ->
        match states |> List.tryFind (fun s -> Retention.stateId s = state) with
        | Some s -> Ok { DatasetId = datasetId; State = s }
        | None -> Error $"'{state}' is not a lifecycle state")

let lifecycleReader: Loading.RecordReader<StoredLifecycle> =
    { Type = lifecycleType
      Schema = schemaOf lifecycleType
      MaxBytes = Record.DefaultMaxBytes
      Decode = lifecycleOfBody
      IdOf = fun _ -> LifecycleId
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }

// ---- Writing audit records with the change they describe ---------------------------------------

/// The operation with its audit records added, in the same commit.
let audited (datasetId: string) (records: Audit.Record list) (operation: Operation) : Result<Operation, Problem list> =
    let key = IdempotencyKey.value operation.Metadata.IdempotencyKey

    records
    |> List.map (fun r ->
        match auditPath key r, encodeAudit datasetId key r with
        | Ok path, Ok content -> Ok(Change.Create(path, content))
        | Error p, _
        | _, Error p -> Error p)
    |> List.fold (fun acc next -> match acc, next with Ok xs, Ok x -> Ok(xs @ [ x ]) | Error e, _ | _, Error e -> Error e) (Ok [])
    |> Result.mapError List.singleton
    |> Result.bind (fun creates ->
        Operation.create operation.Namespace operation.Metadata (operation.Changes @ creates)
        |> Result.mapError (fun error -> [ OperationRefused $"%A{error}" ]))

/// An audit record the system makes from its own ids, hashes and codes.
let record event ids hashes reasons before after clock : Result<Audit.Record, Problem> =
    Audit.create event ids hashes 1 reasons before after clock
    |> Result.mapError (fun problems -> UnstorableRecord("audit", $"not PII-free: %A{problems}"))

/// `audited` for records that may themselves have been refused.
let auditedWith (datasetId: string) (records: Result<Audit.Record, Problem> list) (operation: Operation) : Result<Operation, Problem list> =
    match records |> List.choose (function Error p -> Some p | Ok _ -> None) with
    | [] -> audited datasetId (records |> List.choose Result.toOption) operation
    | problems -> Error problems

/// A reason code from one of Signal's own outcome codes ("rejected:bad-hash" becomes "REJECTED-BAD-HASH").
let codeOf (text: string) =
    let upper = text.ToUpperInvariant() |> String.map (fun c -> if Char.IsAsciiLetterOrDigit c then c else '-')
    let parts = upper.Split('-', StringSplitOptions.RemoveEmptyEntries)
    if parts.Length = 0 then "UNSPECIFIED" else String.Join("-", parts)

/// The audit records for one import chunk: the artifacts accepted, rejected
/// and found already imported, by content hash only.
let importRecords (group: string) (outcomes: (string * string) list) (clock: DateTimeOffset option) =
    let byKind kind = outcomes |> List.filter (fun (_, outcome) -> kind outcome) |> List.map fst

    let make event hashes reasons =
        if List.isEmpty hashes then None
        else Some(record event [ "group", group ] (hashes |> List.map (fun h -> "artifact", h)) reasons None None clock)

    [ make Audit.SubmissionAccepted (byKind ((=) "accepted")) []
      make Audit.DuplicateRejected (byKind ((=) "duplicate")) []
      make
          Audit.SubmissionRejected
          (byKind (fun o -> o <> "accepted" && o <> "duplicate"))
          (outcomes |> List.filter (fun (_, o) -> o <> "accepted" && o <> "duplicate") |> List.map (snd >> codeOf) |> List.distinct |> List.sort) ]
    |> List.choose id

/// An operation whose only change is its audit record, for an event that
/// writes nothing else (an export) or whose change was Arca's own commit.
let auditOnly (ns: Namespace) (context: Storage.OperationContext) (summary: string) (datasetId: string) (made: Result<Audit.Record, Problem>) : Result<Operation, Problem list> =
    let key = IdempotencyKey.value context.IdempotencyKey

    made
    |> Result.bind (fun r ->
        match auditPath key r, encodeAudit datasetId key r with
        | Ok path, Ok content -> Ok(Change.Create(path, content))
        | Error p, _
        | _, Error p -> Error p)
    |> Result.mapError List.singleton
    |> Result.bind (fun change -> Storage.operation ns context summary [ change ])
