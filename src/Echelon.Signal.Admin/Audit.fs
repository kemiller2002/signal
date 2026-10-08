/// Audit without PII (WI-0051, ADM-030, ADM-045, ARX-009 telemetry): an
/// audit record says what the system did, in typed fields that cannot hold a
/// person's identity or answer content.
///
/// - The event is one of a closed set; there is no free-form text field.
/// - Identifiers are opaque (one of Signal's id prefixes and an id, or an
///   OpaqueId) and hashes are content hashes; both are checked when the
///   record is made, so an e-mail address, a name with spaces, a URL, a
///   login-prefixed or token-shaped value is refused.
/// - Reason codes are UPPER-KEBAB constants.
/// - Time is explicit clock evidence, kept outside the record's hash.
///
/// Pure.
module Echelon.Signal.Admin.Audit

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

/// The events ADM-030 names, and the lifecycle transitions of ADM-045.
type Event =
    | StorageConfigured
    | StorageVerified
    | GroupCreated
    | GroupConfigurationVersioned
    | SubmissionAccepted
    | SubmissionRejected
    | DuplicateRejected
    | UnknownWriteObserved
    | WriteReconciled
    | AggregateRebuilt
    | IndexRebuilt
    | ReportDefinitionPublished
    | ReportSnapshotCreated
    | ExportCreated
    | MigrationStarted
    | MigrationVerified
    | DestinationActivated
    | LifecycleChanged
    | ReproducibilityLimited

let eventName (event: Event) = $"%A{event}"

/// One audit record.
type Record =
    { Event: Event
      /// Opaque domain identifiers by role, such as "group" or "snapshot".
      Ids: (string * string) list
      Hashes: (string * string) list
      SchemaVersion: int
      Reasons: string list
      Before: string option
      After: string option
      ClockEvidence: DateTimeOffset option }

/// Why a record was refused.
type Problem =
    | NotOpaque of role: string
    | NotAHash of role: string
    | NotAReasonCode of string
    | NotAStateId of string

let private opaque = Regex(@"^[a-z][a-z0-9]{0,15}[-_:][a-z0-9][a-z0-9-]{2,80}$", RegexOptions.CultureInvariant)
let private hash = Regex(@"^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)
let private reason = Regex(@"^[A-Z][A-Z0-9]*(-[A-Z0-9]+)*$", RegexOptions.CultureInvariant)

/// The prefixes Signal's own opaque ids use. Anything else, such as a login
/// that happens to contain a hyphen, is not an id this record accepts.
let prefixes = [ "ds"; "snap"; "rs"; "def"; "idx"; "agg"; "exp"; "op"; "mig"; "dest"; "state"; "pol"; "pkg" ]

/// A prefixed id ("snap-...", "rs-..."), or a 16-byte OpaqueId in its 22-character spelling.
let private isOpaque (value: string) =
    let prefixed =
        opaque.IsMatch value
        && prefixes |> List.exists (fun p -> value.StartsWith(p + "-") || value.StartsWith(p + "_") || value.StartsWith(p + ":"))

    let opaqueId =
        value.Length = 22
        && (Echelon.Signal.Engine.UrlState.tryFromBase64Url value |> Option.exists (fun bytes -> bytes.Length = Echelon.Signal.Engine.UrlState.IdLength))

    prefixed || opaqueId

let private isState (value: string) = isOpaque value || hash.IsMatch value

/// Makes a record, refusing any field that is not opaque, a hash or a code.
let create
    (event: Event)
    (ids: (string * string) list)
    (hashes: (string * string) list)
    (schemaVersion: int)
    (reasons: string list)
    (before: string option)
    (after: string option)
    (clock: DateTimeOffset option)
    : Result<Record, Problem list> =
    let problems =
        [ for role, id in ids do
              if not (isOpaque id) then NotOpaque role
          for role, h in hashes do
              if not (hash.IsMatch h) then NotAHash role
          for r in reasons do
              if not (reason.IsMatch r) then NotAReasonCode r
          for s in List.choose (fun state -> state) [ before; after ] do
              if not (isState s) then NotAStateId s ]

    if problems.IsEmpty then
        Ok
            { Event = event
              Ids = ids
              Hashes = hashes
              SchemaVersion = schemaVersion
              Reasons = reasons
              Before = before
              After = after
              ClockEvidence = clock }
    else
        Error problems

/// The record's canonical text (without the clock) and its hash.
let canonical (r: Record) =
    let pairs (items: (string * string) list) = items |> List.sort |> List.map (fun (k, v) -> k + "=" + v) |> String.concat ","

    String.concat
        "\n"
        [ "audit/1"
          eventName r.Event
          pairs r.Ids
          pairs r.Hashes
          string r.SchemaVersion
          String.concat "," r.Reasons
          defaultArg r.Before "-"
          defaultArg r.After "-" ]

let hashOf (r: Record) =
    "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical r))).ToLowerInvariant()
