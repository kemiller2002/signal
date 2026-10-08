/// One accepted contribution as an immutable Arca record (ADM-004, ADM-009,
/// ADM-010, ADM-067, ARP-003 §39-40):
/// `records/signal.result/<group>/<shard>/<identity>.json`.
///
/// - The record id is the SHA-256 of the contribution's identity key
///   (`instance:<id>` or `anonymous:<id>`), and the shard is its first two
///   hex digits, so a large group spreads over 256 folders deterministically.
/// - Writing one is a `Create`: the path is the identity, so two different
///   artifacts for the same identity meet at the same path and the second is
///   a conflict Signal decides on (a duplicate), never an overwrite; two
///   different identities never touch the same path, so independent
///   additions commute.
/// - It is immutable: Arca refuses to change it, and a changed copy is
///   detected on load.
/// - Its provenance says how it entered (origin, batch, artifact hash,
///   encoding version), never who (ADM-067). The canonical submission is kept
///   only under `RetainCanonicalSubmission`; a raw URL never is.
///
/// Scores are stored as round-trip text, so a result read back is exactly
/// the result computed.
///
/// Pure.
module Echelon.Signal.Admin.ResultRecord

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec

/// How an artifact entered (ADM-067). Never who.
type ImportOrigin =
    | PastedUrl
    | MultiPaste
    | ImportedTextFile
    | ImportedCanonicalArtifact
    | RestoredBackup
    | ProviderMigration
    | SyntheticSandbox

let originName =
    function
    | PastedUrl -> "PastedUrl"
    | MultiPaste -> "MultiPaste"
    | ImportedTextFile -> "ImportedTextFile"
    | ImportedCanonicalArtifact -> "ImportedCanonicalArtifact"
    | RestoredBackup -> "RestoredBackup"
    | ProviderMigration -> "ProviderMigration"
    | SyntheticSandbox -> "SyntheticSandbox"

let private origins = [ PastedUrl; MultiPaste; ImportedTextFile; ImportedCanonicalArtifact; RestoredBackup; ProviderMigration; SyntheticSandbox ]

/// Operational lineage of one accepted contribution.
type Provenance =
    { Origin: ImportOrigin
      BatchId: string
      /// SHA-256 of the artifact as pasted or read.
      ArtifactHash: string
      EncodingVersion: int }

/// One accepted contribution as stored.
[<NoComparison>]
type Contribution =
    { Group: OpaqueId
      Result: SurveyResult
      Provenance: Provenance
      /// The canonical submission payload, only when the group retains it.
      Submission: string option }

let recordType =
    match RecordType.create "signal.result" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

let private hex (bytes: byte[]) = Convert.ToHexString(bytes).ToLowerInvariant()

/// The record id of an identity key: its SHA-256 in hex.
let idOf (identityKey: string) = hex (SHA256.HashData(Encoding.UTF8.GetBytes identityKey))

/// The SHA-256 of an artifact's text, as provenance states it.
let artifactHash (text: string) = "sha256:" + hex (SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim())))

let private segment text =
    match Segment.create text with
    | Ok found -> found
    | Error error -> invalidOp ("internal: " + LocationError.describe error)

let private key (group: OpaqueId) (identityKey: string) : RecordKey =
    let id = idOf identityKey

    { Type = recordType
      Partition = [ segment (GroupRecord.groupKey group); segment (id.Substring(0, 2)) ]
      Id =
        match RecordId.create id with
        | Ok found -> found
        | Error text -> invalidOp ("internal: not a record id: " + text) }

/// Where a group's contribution for an identity lives.
let path (group: OpaqueId) (identityKey: string) : Result<RelativePath, Problem> =
    Layout.recordPath (key group identityKey) |> Result.mapError (LocationError.describe >> InvalidDataLocation)

/// The folder holding a group's contributions (its shard folders).
let groupFolder (group: OpaqueId) : RelativePath =
    match RelativePath.parse (String.concat "/" [ Layout.RecordsFolder; RecordType.value recordType; GroupRecord.groupKey group ]) with
    | Ok found -> found
    | Error error -> invalidOp ("internal: " + LocationError.describe error)

let private number (value: float) = value.ToString("R", CultureInfo.InvariantCulture)

let private dimensionBody ((dimension: Dimension), (result: DimensionResult)) =
    match result with
    | Scored(score, numeric, items) ->
        Json.objectOf
            [ "id", Json.String dimension.Id
              "scored", Json.Bool true
              "score", Json.String(number score)
              "numeric", Json.Number(decimal numeric)
              "items", Json.Number(decimal items) ]
    | Unscored(numeric, items, minimum) ->
        Json.objectOf
            [ "id", Json.String dimension.Id
              "scored", Json.Bool false
              "numeric", Json.Number(decimal numeric)
              "items", Json.Number(decimal items)
              "minimum", Json.Number(decimal minimum) ]

let body (datasetId: string) (contribution: Contribution) =
    let result = contribution.Result

    Json.objectOf
        [ "datasetId", Json.String datasetId
          "group", Json.String(string contribution.Group)
          "identity", Json.String result.Identity.Key
          "surveyIdentifier", Json.String result.SurveyIdentifier
          "templateVersion", Json.String result.TemplateVersion
          "templateHash", Json.String result.TemplateHash
          "submissionHash", Json.String result.SubmissionHash
          "dimensions", Json.Array(result.Dimensions |> List.map dimensionBody)
          "answeredCount", Json.Number(decimal result.AnsweredCount)
          "nonNumericCount", Json.Number(decimal result.NonNumericCount)
          "provenance",
          Json.objectOf
              [ "origin", Json.String(originName contribution.Provenance.Origin)
                "batchId", Json.String contribution.Provenance.BatchId
                "artifactHash", Json.String contribution.Provenance.ArtifactHash
                "encodingVersion", Json.Number(decimal contribution.Provenance.EncodingVersion) ]
          "submission",
          match contribution.Submission with
          | Some payload -> Json.String payload
          | None -> Json.Null ]

/// The contribution's path and canonical stored text.
let encode (datasetId: string) (contribution: Contribution) : Result<RelativePath * string, Problem> =
    let recordKey = key contribution.Group contribution.Result.Identity.Key

    path contribution.Group contribution.Result.Identity.Key
    |> Result.bind (fun target ->
        { Id = recordKey.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Immutable
          Body = body datasetId contribution }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.map (fun text -> target, text)
        |> Result.mapError (fun _ -> UnstorableRecord(contribution.Result.Identity.Key, "the result record is too large")))

let private identityOf (text: string) =
    let parse (prefix: string) make =
        if text.StartsWith prefix then
            OpaqueId.tryParse (text.Substring prefix.Length) |> Option.map make
        else
            None

    match parse "instance:" Instance, parse "anonymous:" AnonymousSubmission with
    | Some found, _
    | _, Some found -> Ok found
    | None, None -> Error $"'{text}' is not a submission identity"

let private dimensionOf (template: Assessment) (value: Json) : Decoded<Dimension * DimensionResult> =
    match text "id" value, flag "scored" value with
    | Ok id, Ok scored ->
        match template.Dimensions |> List.tryFind (fun d -> d.Id = id) with
        | None -> Error $"'{id}' is not a dimension of the template"
        | Some dimension ->
            if scored then
                closed [ "id"; "items"; "numeric"; "score"; "scored" ] value
                |> Result.bind (fun () ->
                    match text "score" value, integer "numeric" value, integer "items" value with
                    | Ok score, Ok numeric, Ok items ->
                        match Double.TryParse(score, NumberStyles.Float, CultureInfo.InvariantCulture) with
                        | true, parsed when Double.IsFinite parsed && number parsed = score -> Ok(dimension, Scored(parsed, numeric, items))
                        | _ -> Error "'score' is not a round-trip number"
                    | Error e, _, _
                    | _, Error e, _
                    | _, _, Error e -> Error e)
            else
                closed [ "id"; "items"; "minimum"; "numeric"; "scored" ] value
                |> Result.bind (fun () ->
                    match integer "numeric" value, integer "items" value, integer "minimum" value with
                    | Ok numeric, Ok items, Ok minimum -> Ok(dimension, Unscored(numeric, items, minimum))
                    | Error e, _, _
                    | _, Error e, _
                    | _, _, Error e -> Error e)
    | Error e, _
    | _, Error e -> Error e

let private provenanceOf (value: Json) : Decoded<Provenance> =
    closed [ "artifactHash"; "batchId"; "encodingVersion"; "origin" ] value
    |> Result.bind (fun () ->
        match text "origin" value, text "batchId" value, text "artifactHash" value, integer "encodingVersion" value with
        | Ok origin, Ok batch, Ok artifact, Ok encoding ->
            match origins |> List.tryFind (fun o -> originName o = origin) with
            | Some found ->
                Ok
                    { Origin = found
                      BatchId = batch
                      ArtifactHash = artifact
                      EncodingVersion = encoding }
            | None -> Error $"'{origin}' is not an import origin"
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

/// A stored contribution, decoded against the group's template: a dimension
/// the template does not have is refused.
[<NoComparison>]
type StoredContribution = { DatasetId: string; Contribution: Contribution }

let ofBody (template: Assessment) (value: Json) : Decoded<StoredContribution> =
    closed
        [ "answeredCount"
          "datasetId"
          "dimensions"
          "group"
          "identity"
          "nonNumericCount"
          "provenance"
          "submission"
          "submissionHash"
          "surveyIdentifier"
          "templateHash"
          "templateVersion" ]
        value
    |> Result.bind (fun () ->
        let group =
            text "group" value
            |> Result.bind (fun t -> OpaqueId.tryParse t |> Option.map Ok |> Option.defaultValue (Error "'group' is not a group id"))

        let submission =
            field "submission" value
            |> Result.bind (function
                | Json.Null -> Ok None
                | Json.String payload -> Ok(Some payload)
                | _ -> Error "'submission' is not text or null")

        match
            both (text "datasetId" value) group,
            text "identity" value |> Result.bind identityOf,
            both (text "surveyIdentifier" value) (text "templateVersion" value),
            both (text "templateHash" value) (text "submissionHash" value),
            list "dimensions" (dimensionOf template) value,
            both (integer "answeredCount" value) (integer "nonNumericCount" value),
            field "provenance" value |> Result.bind provenanceOf,
            submission
        with
        | Ok(dataset, group), Ok identity, Ok(survey, version), Ok(templateHash, submissionHash), Ok dimensions, Ok(answered, nonNumeric), Ok provenance, Ok submission ->
            Ok
                { DatasetId = dataset
                  Contribution =
                    { Group = group
                      Result =
                        { Identity = identity
                          SurveyIdentifier = survey
                          TemplateVersion = version
                          TemplateHash = templateHash
                          SubmissionHash = submissionHash
                          Dimensions = dimensions
                          AnsweredCount = answered
                          NonNumericCount = nonNumeric }
                      Provenance = provenance
                      Submission = submission } }
        | Error e, _, _, _, _, _, _, _
        | _, Error e, _, _, _, _, _, _
        | _, _, Error e, _, _, _, _, _
        | _, _, _, Error e, _, _, _, _
        | _, _, _, _, Error e, _, _, _
        | _, _, _, _, _, Error e, _, _
        | _, _, _, _, _, _, Error e, _
        | _, _, _, _, _, _, _, Error e -> Error e)

/// How `Loading` reads a group's contributions.
let reader (template: Assessment) : Loading.RecordReader<StoredContribution> =
    { Type = recordType
      Schema = schema
      MaxBytes = Record.DefaultMaxBytes
      Decode = ofBody template
      IdOf = fun stored -> idOf stored.Contribution.Result.Identity.Key
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }
