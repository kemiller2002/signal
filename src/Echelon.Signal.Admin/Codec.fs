/// Small, total readers over Arca's `Json` values, for configuration and for
/// stored records. Everything read is untrusted (ADM-046): every read names
/// the field and says what was wrong, and objects are closed, so a field this
/// version does not define is refused rather than ignored.
///
/// Pure.
module Echelon.Signal.Admin.Codec

open System
open Arca

/// A decoded value, or one sentence saying why the input is not one.
type Decoded<'a> = Result<'a, string>

let field (name: string) (value: Json) : Decoded<Json> =
    match Json.field name value with
    | Some found -> Ok found
    | None -> Error $"'{name}' is missing"

let text name value : Decoded<string> =
    field name value
    |> Result.bind (function
        | Json.String found -> Ok found
        | _ -> Error $"'{name}' is not text")

let integer name value : Decoded<int> =
    field name value
    |> Result.bind (function
        | Json.Number number when number = Math.Floor number && number >= decimal Int32.MinValue && number <= decimal Int32.MaxValue ->
            Ok(int number)
        | _ -> Error $"'{name}' is not a whole number")

let flag name value : Decoded<bool> =
    field name value
    |> Result.bind (function
        | Json.Bool found -> Ok found
        | _ -> Error $"'{name}' is not true or false")

/// Ok when the value is an object whose every key is one of `names`.
let closed (names: string list) (value: Json) : Decoded<unit> =
    match value with
    | Json.Object members ->
        match members |> List.tryFind (fun (key, _) -> not (List.contains key names)) with
        | Some(key, _) -> Error $"'{key}' is not a field this version reads"
        | None -> Ok()
    | _ -> Error "expected an object"

/// Every element decoded, or the first failure.
let traverse (decode: 'a -> Decoded<'b>) (items: 'a list) : Decoded<'b list> =
    List.foldBack
        (fun item state ->
            match decode item, state with
            | Ok decoded, Ok rest -> Ok(decoded :: rest)
            | Error error, _ -> Error error
            | Ok _, Error error -> Error error)
        items
        (Ok [])

let list name (decode: Json -> Decoded<'b>) value : Decoded<'b list> =
    field name value
    |> Result.bind (function
        | Json.Array items -> traverse decode items
        | _ -> Error $"'{name}' is not a list")

let texts name value : Decoded<string list> =
    list
        name
        (function
        | Json.String found -> Ok found
        | _ -> Error $"'{name}' holds something other than text")
        value

/// An optional field: None when absent.
let optional name (decode: Json -> Decoded<'b>) value : Decoded<'b option> =
    match Json.field name value with
    | None -> Ok None
    | Some found -> decode found |> Result.map Some

/// A list of text values for an `Array` of strings.
let textArray (items: string list) = Json.Array(items |> List.map Json.String)

/// The first error of two decodes, or both values.
let both (first: Decoded<'a>) (second: Decoded<'b>) : Decoded<'a * 'b> =
    match first, second with
    | Ok a, Ok b -> Ok(a, b)
    | Error e, _
    | _, Error e -> Error e

/// Why stored bytes are not a record, as one phrase.
let describeDecode =
    function
    | DecodeError.InvalidJson error -> JsonError.describe error
    | DecodeError.NotCanonical -> "the stored text is not canonical"
    | DecodeError.MissingField name -> $"'{name}' is missing"
    | DecodeError.InvalidField(name, detail) -> $"'{name}': {detail}"
    | DecodeError.UnknownField name -> $"'{name}' is not a field this version reads"
    | DecodeError.UnsupportedFormat(found, supported) -> $"format {found} is newer than {supported}"
    | DecodeError.TooLarge(bytes, limit) -> $"{bytes} bytes is over the {limit}-byte limit"

/// Why a stored object is not the record its path names, as one phrase.
let describeIntegrity =
    function
    | IntegrityFailure.Invalid error -> describeDecode error
    | IntegrityFailure.IdentityMismatch(expected, found) -> $"the record says it is '{found}' but its path names '{expected}'"
    | IntegrityFailure.TypeMismatch(expected, found) -> $"a {found} record where a {expected} belongs"
    | IntegrityFailure.UnsupportedSchema access ->
        match access with
        | SchemaAccess.UnsupportedFuture(found, newest) -> $"schema version {found} is newer than {newest}"
        | SchemaAccess.UnsupportedPast(found, oldest) -> $"schema version {found} is older than {oldest}"
        | SchemaAccess.ReadWrite
        | SchemaAccess.ReadOnly -> "the schema version is not readable"
    | IntegrityFailure.HashMismatch _ -> "the record changed since it was read"
    | IntegrityFailure.ImmutableChanged _ -> "an immutable record changed"
