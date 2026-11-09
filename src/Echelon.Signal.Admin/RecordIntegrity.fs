/// The content hash every stored record is checked against on read (SEC-01;
/// ADM-025, ADM-046).
///
/// Arca proves that a stored object is a canonical record of the type, id and
/// schema its path names (`Integrity.validate`), but a record whose body was
/// edited by hand and stored canonically again passes every one of those
/// checks. So Signal records, inside each body it writes, a `bodyHash`: the
/// `sha256:` (Arca's canonical `Json.contentHash`) of the whole record
/// envelope as Arca encodes it (`Record.toJson`: format, id, type, schema
/// version, mutability) with the body as sealed. A record is read only when it
/// still hashes to the value recorded with it:
///
/// - `encode` seals a record and encodes it; it is the only way Signal writes
///   a record.
/// - `validate` and `decode` check the envelope with Arca, then the record
///   against its hash, and give back the body without the hash, so domain
///   readers see exactly what was sealed. A record without the hash is
///   refused: removing it is not a way around the check.
///
/// No other module encodes, decodes or validates an Arca record directly
/// (`ArchitectureTests`). Records written before this check carry no hash and
/// are refused; no production dataset existed when it was introduced.
///
/// What this detects: an edit made outside Signal to a record's body or
/// envelope (its mutability, schema version, id or type) that does not also
/// recompute the hash, such as a manual edit in the repository or corruption
/// that leaves canonical JSON.
///
/// What it does not detect (residual risks, SECURITY-REVIEW SEC-01):
///
/// - **Rollback.** An earlier version of a mutable record that Signal itself
///   sealed still verifies. Restoring it (for example a `git revert` of a
///   capability removal) is not detected here; it needs an anchor outside the
///   record, such as Arca's commit history (`Integrity.externalEdits`) or a
///   hash ledger.
/// - **A record copied whole to another place** of the same id and type (for
///   example another dataset's folder) verifies; the dataset and path checks in
///   `Loading` refuse what they can identify.
/// - **Deliberate forgery.** The hash is unkeyed: someone who edits a record
///   and recomputes the SHA-256 is not detected. Authenticity against a holder
///   of write access needs a keyed signature, which is not in scope.
///
/// Pure.
module Echelon.Signal.Admin.RecordIntegrity

open Arca

/// The body member that holds the record's hash.
[<Literal>]
let HashMember = "bodyHash"

/// Why a stored record cannot be trusted.
type Failure =
    /// The body carries no hash, or a hash that is not text.
    | Unsealed
    /// The record no longer hashes to the value recorded with it.
    | Altered of recorded: string * actual: string

/// The failure as one phrase. It names hashes only, never body content.
let describe =
    function
    | Unsealed -> $"it carries no '{HashMember}', so its content cannot be checked"
    | Altered(recorded, actual) -> $"it hashes to {actual}, not the {recorded} recorded with it: it was changed outside Signal"

/// The hash recorded with a record: of its whole envelope with the body as
/// sealed, so neither the body nor the envelope can change without it.
let private hashOf (record: Record) = Json.contentHash (Record.toJson record)

/// The record with its hash recorded in its body. Every body Signal stores is
/// a JSON object without that member; anything else is a programming defect.
let seal (record: Record) : Record =
    match record.Body with
    | Json.Object members when members |> List.exists (fst >> (=) HashMember) ->
        invalidArg (nameof record) $"internal: a record body already has a '{HashMember}' member"
    | Json.Object members -> { record with Body = Json.objectOf ((HashMember, Json.String(hashOf record)) :: members) }
    | _ -> invalidArg (nameof record) "internal: a record body is not an object"

/// The record with its hash removed from its body, when it still hashes to
/// the value recorded with it.
let verify (record: Record) : Result<Record, Failure> =
    match record.Body with
    | Json.Object members ->
        match members |> List.partition (fst >> (=) HashMember) with
        | [ _, Json.String recorded ], rest ->
            let content = { record with Body = Json.objectOf rest }

            match hashOf content with
            | actual when actual = recorded -> Ok content
            | actual -> Error(Altered(recorded, actual))
        | _ -> Error Unsealed
    | _ -> Error Unsealed

/// The canonical text to store: the record, sealed.
let encode (maxBytes: int64) (record: Record) = Record.encode maxBytes (seal record)

/// Why stored bytes are not a record Signal may use.
type ReadFailure =
    /// Not a valid canonical record of the expected type, id and schema.
    | Invalid of detail: string
    /// A valid record that fails its hash.
    | Untrusted of Failure

/// The failure as one phrase.
let describeRead =
    function
    | Invalid detail -> detail
    | Untrusted failure -> describe failure

/// A stored record proven usable.
[<NoComparison>]
type Verified =
    { /// The record as stored: its `ContentHash` is that of the stored bytes,
      /// so later changes and erasures are checked against what was read.
      Stored: ValidatedRecord
      /// The record's body without its hash, as it was sealed.
      Body: Json }

/// A stored object validated as the record `key` names (Arca), then verified
/// against its hash.
let validate (key: RecordKey) (schema: SchemaSupport) (maxBytes: int64) (stored: StoredObject) : Result<Verified, ReadFailure> =
    Integrity.validate key schema maxBytes stored
    |> Result.mapError (Codec.describeIntegrity >> Invalid)
    |> Result.bind (fun valid ->
        verify valid.Record
        |> Result.mapError Untrusted
        |> Result.map (fun record -> { Stored = valid; Body = record.Body }))

/// A record from stored text, verified and returned without its hash.
let decode (maxBytes: int64) (text: string) : Result<Record, ReadFailure> =
    Record.decode maxBytes text
    |> Result.mapError (Codec.describeDecode >> Invalid)
    |> Result.bind (verify >> Result.mapError Untrusted)

/// The content hash Arca reports for these stored bytes once read
/// (`ValidatedRecord.ContentHash`), when they are a record.
let storedHash (text: string) : string option =
    Record.decode Record.DefaultMaxBytes text |> Result.toOption |> Option.map Record.contentHash

/// The failure as Signal's typed storage problem for the record at `path`.
let problem (path: string) : ReadFailure -> Problems.Problem =
    function
    | Invalid detail -> Problems.InvalidStoredRecord(path, detail)
    | Untrusted failure -> Problems.AlteredRecord(path, describe failure)
