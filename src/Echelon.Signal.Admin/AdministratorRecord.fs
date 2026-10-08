/// One administrator's membership of a dataset's roster as an Arca record:
/// `records/signal.administrator/<principal>.json` in the dataset's folder,
/// mutable under its revision. The roster is the set of these records; a
/// removed administrator's record is deleted.
///
/// Pure.
module Echelon.Signal.Admin.AdministratorRecord

open System.Text
open Arca
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec
open Echelon.Signal.Admin.Access

/// The record type of a membership.
let recordType =
    match RecordType.create "signal.administrator" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

/// The schema versions this Signal reads and writes.
let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

/// A principal id as a record id: ASCII letters, digits and '-' are kept
/// (not leading '-'); every other character becomes `_` and its UTF-8 bytes
/// in hex, so distinct principals have distinct ids. An id that would start
/// with `_` is prefixed with `p` (a raw `_` is always encoded, so this cannot
/// collide). `github:583231` is `github_3a583231`.
let idOf (principalId: string) =
    let encoded =
        principalId
        |> Seq.mapi (fun index c ->
            if System.Char.IsAsciiLetterOrDigit c || (c = '-' && index > 0) then
                string c
            else
                Encoding.UTF8.GetBytes(string c) |> Array.map (fun b -> $"_{b:x2}") |> String.concat "")
        |> String.concat ""

    if encoded.StartsWith "_" then "p" + encoded else encoded

let private key (principalId: string) : Result<RecordKey, Problem> =
    match RecordId.create (idOf principalId) with
    | Ok id ->
        Ok
            { Type = recordType
              Partition = []
              Id = id }
    | Error _ -> Error(UnstorableRecord(principalId, "the principal id is too long to store"))

/// The membership's path inside its dataset's folder.
let path (principalId: string) : Result<RelativePath, Problem> =
    key principalId |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The folder of the dataset's administrators.
let folder: RelativePath =
    match RelativePath.parse (String.concat "/" [ Layout.RecordsFolder; RecordType.value recordType ]) with
    | Ok found -> found
    | Error error -> invalidOp ("internal: " + LocationError.describe error)

/// The membership's record body. It holds the principal id and a display
/// name for the roster screen; no e-mail or other personal data.
let body (datasetId: string) (membership: Membership) =
    Json.objectOf
        [ "datasetId", Json.String datasetId
          "principalId", Json.String membership.Principal.PrincipalId
          "kind", Json.String(kindName membership.Principal.Kind)
          "displayName", Json.String membership.Principal.DisplayName
          "capabilities", textArray (allCapabilities |> List.filter membership.Capabilities.Contains |> List.map capabilityName)
          "revision", Json.Number(decimal membership.Revision) ]

/// The membership's canonical stored text.
let encode (datasetId: string) (membership: Membership) : Result<string, Problem> =
    key membership.Principal.PrincipalId
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Mutable
          Body = body datasetId membership }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(membership.Principal.PrincipalId, "the membership record is too large")))

/// A stored membership: its dataset and the membership.
type StoredMembership = { DatasetId: string; Membership: Membership }

/// A membership from its record body. A capability or kind this version does
/// not know is refused, never dropped: a roster read must be the roster stored.
let ofBody (value: Json) : Decoded<StoredMembership> =
    closed [ "capabilities"; "datasetId"; "displayName"; "kind"; "principalId"; "revision" ] value
    |> Result.bind (fun () ->
        match
            text "datasetId" value,
            text "principalId" value,
            text "kind" value,
            text "displayName" value,
            texts "capabilities" value,
            integer "revision" value
        with
        | Ok datasetId, Ok principalId, Ok kind, Ok displayName, Ok capabilities, Ok revision ->
            match kindOf kind, capabilities |> List.map capabilityOf with
            | None, _ -> Error $"'{kind}' is not a kind of principal"
            | Some _, found when found |> List.exists Option.isNone -> Error "a capability is not one this version knows"
            | Some _, _ when revision < 1 -> Error "'revision' must be at least 1"
            | Some kind, found ->
                let held = found |> List.choose id |> Set.ofList

                if held |> Set.exists (permitsKind kind >> not) then
                    Error "a person-only capability is held by a principal that is not a person"
                else
                    Ok
                        { DatasetId = datasetId
                          Membership =
                            { Principal =
                                { PrincipalId = principalId
                                  Kind = kind
                                  DisplayName = displayName }
                              Capabilities = held
                              Revision = revision } }
        | Error e, _, _, _, _, _
        | _, Error e, _, _, _, _
        | _, _, Error e, _, _, _
        | _, _, _, Error e, _, _
        | _, _, _, _, Error e, _
        | _, _, _, _, _, Error e -> Error e)

/// How `Loading` reads memberships.
let reader: Loading.RecordReader<StoredMembership> =
    { Type = recordType
      Schema = schema
      MaxBytes = Record.DefaultMaxBytes
      Decode = ofBody
      IdOf = fun stored -> idOf stored.Membership.Principal.PrincipalId
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }
