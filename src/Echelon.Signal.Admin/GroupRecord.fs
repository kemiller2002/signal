/// A survey group's configuration as an Arca record (ADM-007, ADM-010,
/// ARP-002): `records/signal.group/<group>.json` in the dataset's folder,
/// mutable under its revision, so concurrent incompatible edits are never
/// silently merged (ADM-009). The template is referenced by its exact
/// content hash; the template itself is resolved from the catalog, never
/// copied into the group.
///
/// Pure.
module Echelon.Signal.Admin.GroupRecord

open System
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec

/// Whether the canonical submission is kept with its result (ARP-003 §39-40).
/// Raw URLs are never kept either way.
type SubmissionRetention =
    /// Privacy-oriented, the default: only the derived result and its hash.
    | NoneAfterImport
    /// Audit and full reproducibility: the canonical submission payload too.
    | RetainCanonicalSubmission

/// A group's stored configuration.
[<NoComparison>]
type GroupConfig =
    { Group: OpaqueId
      Mode: IdentityMode
      ExpectedCount: int
      SurveyIdentifier: string
      TemplateVersion: string
      /// The exact immutable template, by its canonical hash.
      TemplateHash: string
      MinimumReportableCount: int
      Retention: SubmissionRetention
      /// Optimistic-concurrency revision, from 1.
      Revision: int }

/// The group id as a folder and record id: lower-case hex of its bytes.
let groupKey (group: OpaqueId) = Convert.ToHexString(group.Bytes).ToLowerInvariant()

let recordType =
    match RecordType.create "signal.group" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

let private key (group: OpaqueId) : Result<RecordKey, Problem> =
    match RecordId.create (groupKey group) with
    | Ok id ->
        Ok
            { Type = recordType
              Partition = []
              Id = id }
    | Error text -> Error(UnstorableRecord(text, "not a record id"))

let path (group: OpaqueId) : Result<RelativePath, Problem> =
    key group |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

let folder: RelativePath =
    match RelativePath.parse (String.concat "/" [ Layout.RecordsFolder; RecordType.value recordType ]) with
    | Ok found -> found
    | Error error -> invalidOp ("internal: " + LocationError.describe error)

let private modeName =
    function
    | IdentifiedGroup -> "identified"
    | AnonymousGroup -> "anonymous"

let private retentionName =
    function
    | NoneAfterImport -> "none-after-import"
    | RetainCanonicalSubmission -> "retain-canonical-submission"

let body (datasetId: string) (config: GroupConfig) =
    Json.objectOf
        [ "datasetId", Json.String datasetId
          "group", Json.String(string config.Group)
          "mode", Json.String(modeName config.Mode)
          "expectedCount", Json.Number(decimal config.ExpectedCount)
          "surveyIdentifier", Json.String config.SurveyIdentifier
          "templateVersion", Json.String config.TemplateVersion
          "templateHash", Json.String config.TemplateHash
          "minimumReportableCount", Json.Number(decimal config.MinimumReportableCount)
          "retention", Json.String(retentionName config.Retention)
          "revision", Json.Number(decimal config.Revision) ]

let encode (datasetId: string) (config: GroupConfig) : Result<string, Problem> =
    key config.Group
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Mutable
          Body = body datasetId config }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(groupKey config.Group, "the group record is too large")))

/// A stored group: its dataset and configuration.
[<NoComparison>]
type StoredGroup = { DatasetId: string; Config: GroupConfig }

let ofBody (value: Json) : Decoded<StoredGroup> =
    closed
        [ "datasetId"
          "expectedCount"
          "group"
          "minimumReportableCount"
          "mode"
          "retention"
          "revision"
          "surveyIdentifier"
          "templateHash"
          "templateVersion" ]
        value
    |> Result.bind (fun () ->
        let group =
            text "group" value
            |> Result.bind (fun t -> OpaqueId.tryParse t |> Option.map Ok |> Option.defaultValue (Error "'group' is not a group id"))

        let mode =
            text "mode" value
            |> Result.bind (function
                | "identified" -> Ok IdentifiedGroup
                | "anonymous" -> Ok AnonymousGroup
                | other -> Error $"'{other}' is not a group mode")

        let retention =
            text "retention" value
            |> Result.bind (function
                | "none-after-import" -> Ok NoneAfterImport
                | "retain-canonical-submission" -> Ok RetainCanonicalSubmission
                | other -> Error $"'{other}' is not a retention policy")

        match
            text "datasetId" value,
            group,
            mode,
            integer "expectedCount" value,
            both (text "surveyIdentifier" value) (text "templateVersion" value),
            both (text "templateHash" value) (integer "minimumReportableCount" value),
            retention,
            integer "revision" value
        with
        | Ok datasetId, Ok group, Ok mode, Ok expected, Ok(survey, version), Ok(hash, minimum), Ok retention, Ok revision ->
            if expected < 1 || minimum < 1 || revision < 1 then
                Error "'expectedCount', 'minimumReportableCount' and 'revision' must be at least 1"
            else
                Ok
                    { DatasetId = datasetId
                      Config =
                        { Group = group
                          Mode = mode
                          ExpectedCount = expected
                          SurveyIdentifier = survey
                          TemplateVersion = version
                          TemplateHash = hash
                          MinimumReportableCount = minimum
                          Retention = retention
                          Revision = revision } }
        | Error e, _, _, _, _, _, _, _
        | _, Error e, _, _, _, _, _, _
        | _, _, Error e, _, _, _, _, _
        | _, _, _, Error e, _, _, _, _
        | _, _, _, _, Error e, _, _, _
        | _, _, _, _, _, Error e, _, _
        | _, _, _, _, _, _, Error e, _
        | _, _, _, _, _, _, _, Error e -> Error e)

let reader: Loading.RecordReader<StoredGroup> =
    { Type = recordType
      Schema = schema
      MaxBytes = Record.DefaultMaxBytes
      Decode = ofBody
      IdOf = fun stored -> groupKey stored.Config.Group
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }

/// Resolves an exact template by its canonical hash; None when the catalog
/// does not hold it.
type TemplateResolver = string -> Assessment.Assessment option

/// The import definition for a stored group, or why imports are blocked:
/// the exact template it names is not available (ADM-008).
let definition (resolve: TemplateResolver) (config: GroupConfig) : Result<GroupDefinition, string> =
    match resolve config.TemplateHash with
    | Some template when Canonical.templateHash template = config.TemplateHash ->
        Ok
            { Group = config.Group
              Mode = config.Mode
              ExpectedCount = config.ExpectedCount
              Template = template }
    | Some _ -> Error $"the catalog's template does not hash to {config.TemplateHash}"
    | None -> Error $"template {config.SurveyIdentifier} {config.TemplateVersion} ({config.TemplateHash}) is not in the catalog"

/// The disclosure policy a group's result is computed under.
let policy (config: GroupConfig) : Aggregation.Policy =
    { MinimumReportableCount = config.MinimumReportableCount }
