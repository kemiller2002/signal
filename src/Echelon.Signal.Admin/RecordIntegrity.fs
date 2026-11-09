/// The content hash every stored record is checked against on read (SEC-01;
/// ADM-025, ADM-046).
///
/// Arca proves that a stored object is a canonical record of the type, id and
/// schema its path names (`Integrity.validate`), but a record whose body was
/// edited by hand and stored canonically again passes every one of those
/// checks. So Signal records, inside each body it writes, the `sha256:` hash
/// of the rest of that body (Arca's canonical `Json.contentHash`), and reads
/// a record only when its body still hashes to the value recorded with it:
///
/// - `encode` seals a record's body and encodes it; it is the only way Signal
///   writes a record.
/// - `validate` and `decode` check the envelope, then the body against its
///   hash, and give back the body without the hash, so domain readers see
///   exactly what was sealed. A record without the hash is refused: removing
///   it is not a way around the check.
///
/// No other module encodes, decodes or validates an Arca record directly
/// (`ArchitectureTests`). Records written before this check carry no hash and
/// are refused; no production dataset existed when it was introduced.
///
/// What this does and does not prove: it detects any edit made outside Signal
/// that does not also recompute the hash, which covers manual edits,
/// truncation-preserving corruption and stale or mixed copies. It is not
/// authentication: someone who deliberately recomputes the SHA-256 of the
/// edited body is not detected, as with any unkeyed hash. Authenticity
/// against a holder of write access needs a keyed signature, which is not in
/// scope here.
///
/// Pure.
module Echelon.Signal.Admin.RecordIntegrity

open Arca

/// The body member that holds the hash of the rest of the body.
[<Literal>]
let HashMember = "bodyHash"

/// Why a stored body cannot be trusted.
type Failure =
    /// The body carries no hash, or a hash that is not text.
    | Unsealed
    /// The body no longer hashes to the value recorded with it.
    | Altered of recorded: string * actual: string

/// The failure as one phrase. It names hashes only, never body content.
let describe =
    function
    | Unsealed -> $"it carries no '{HashMember}', so its content cannot be checked"
    | Altered(recorded, actual) -> $"its content hashes to {actual}, not the {recorded} recorded with it: it was changed outside Signal"

/// The body with the hash of its content recorded in it. Every body Signal
/// stores is a JSON object; anything else is a programming defect.
let seal (body: Json) : Json =
    match body with
    | Json.Object members when members |> List.exists (fst >> (=) HashMember) ->
        invalidArg (nameof body) $"internal: a record body already has a '{HashMember}' member"
    | Json.Object members -> Json.objectOf ((HashMember, Json.String(Json.contentHash body)) :: members)
    | _ -> invalidArg (nameof body) "internal: a record body is not an object"

/// The body without its hash, when the rest of it still hashes to the value
/// recorded with it.
let verify (body: Json) : Result<Json, Failure> =
    match body with
    | Json.Object members ->
        match members |> List.partition (fst >> (=) HashMember) with
        | [ _, Json.String recorded ], rest ->
            let content = Json.objectOf rest

            match Json.contentHash content with
            | actual when actual = recorded -> Ok content
            | actual -> Error(Altered(recorded, actual))
        | _ -> Error Unsealed
    | _ -> Error Unsealed

let private withBody (record: Record) (body: Json) = { record with Body = body }

/// The canonical text to store: the record with its body sealed.
let encode (maxBytes: int64) (record: Record) =
    Record.encode maxBytes (withBody record (seal record.Body))

/// Why stored bytes are not a record Signal may use.
type ReadFailure =
    /// Not a valid canonical record of the expected type, id and schema.
    | Invalid of detail: string
    /// A valid record whose body fails its hash.
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

/// A stored object validated as the record `key` names (Arca), then its body
/// verified against its hash.
let validate (key: RecordKey) (schema: SchemaSupport) (maxBytes: int64) (stored: StoredObject) : Result<Verified, ReadFailure> =
    Integrity.validate key schema maxBytes stored
    |> Result.mapError (Codec.describeIntegrity >> Invalid)
    |> Result.bind (fun valid ->
        verify valid.Record.Body
        |> Result.mapError Untrusted
        |> Result.map (fun body -> { Stored = valid; Body = body }))

/// A record from stored text, its body verified and returned without its hash.
let decode (maxBytes: int64) (text: string) : Result<Record, ReadFailure> =
    Record.decode maxBytes text
    |> Result.mapError (Codec.describeDecode >> Invalid)
    |> Result.bind (fun record -> verify record.Body |> Result.mapError Untrusted |> Result.map (withBody record))

/// The content hash Arca reports for these stored bytes once read
/// (`ValidatedRecord.ContentHash`), when they are a record.
let storedHash (text: string) : string option =
    Record.decode Record.DefaultMaxBytes text |> Result.toOption |> Option.map Record.contentHash

/// The failure as Signal's typed storage problem for the record at `path`.
let problem (path: string) : ReadFailure -> Problems.Problem =
    function
    | Invalid detail -> Problems.InvalidStoredRecord(path, detail)
    | Untrusted failure -> Problems.AlteredRecord(path, describe failure)
