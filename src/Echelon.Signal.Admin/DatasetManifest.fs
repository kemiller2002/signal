/// Signal's storage manifest (ADM-005): one record in each dataset's folder,
/// `records/signal.storage-manifest/<dataset>.json`, written when the dataset
/// is set up. It says which dataset the folder holds and which versions of
/// Signal's formats its contents use, so a loader can refuse what it cannot
/// read before anything mutable is loaded.
///
/// Arca's own manifest (`arca-manifest.json`, beside it) already records the
/// storage schema, the provider contract, the location and any migration in
/// progress; Signal checks that one first (`Storage.openNamespace`). This
/// record adds what only Signal knows. It holds no personal data: no login,
/// name or e-mail, only ids and version numbers.
///
/// Pure.
module Echelon.Signal.Admin.DatasetManifest

open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec

/// The versions of Signal's formats a dataset's contents use.
type FormatVersions =
    { /// Signal's own folder layout inside the dataset.
      StorageLayout: int
      /// The canonical form of templates and results (`Canonical`).
      Canonicalization: int
      /// The shape of a respondent's result (`SurveyResult`).
      ResultSchema: int
      /// The shape of a group's result (`GroupResult`).
      GroupResultSchema: int
      /// The template schema (`Template`).
      TemplateSchema: int
      /// Administrator state records (rosters, imports, groups).
      AdminStateSchema: int
      /// Report definitions and snapshots.
      ReportDefinitionSchema: int
      /// Visualization and dashboard definitions.
      VisualizationSchema: int
      /// Response encodings the dataset's submissions may use.
      SupportedEncodings: int list }

/// The versions this Signal writes and reads.
let current =
    { StorageLayout = 1
      Canonicalization = Canonical.CanonicalFormVersion
      ResultSchema = SurveyResult.ResultShapeVersion
      GroupResultSchema = GroupResult.GroupResultVersion
      TemplateSchema = Template.SchemaVersion
      AdminStateSchema = 1
      ReportDefinitionSchema = 1
      VisualizationSchema = 1
      SupportedEncodings = [ UrlState.ResponseEncodingVersion ] }

/// A dataset's storage manifest.
type StorageManifest =
    { DatasetId: string
      /// The dataset folder's path in its repository, as Arca renders it.
      RootNamespace: string
      Versions: FormatVersions
      /// The Signal version that set the dataset up, for diagnosis only.
      CreatedFromApplicationVersion: string }

/// The record type of the storage manifest.
let recordType =
    match RecordType.create "signal.storage-manifest" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

/// The manifest record's schema versions this Signal reads and writes.
let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

let private key (datasetId: string) : Result<RecordKey, Problem> =
    match RecordId.create datasetId with
    | Ok id ->
        Ok
            { Type = recordType
              Partition = []
              Id = id }
    | Error _ -> Error(InvalidDatasetId datasetId)

/// The manifest's path inside its dataset's folder.
let path (datasetId: string) : Result<RelativePath, Problem> =
    key datasetId
    |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

let private versionsBody (versions: FormatVersions) =
    let number (value: int) = Json.Number(decimal value)

    Json.objectOf
        [ "storageLayout", number versions.StorageLayout
          "canonicalization", number versions.Canonicalization
          "resultSchema", number versions.ResultSchema
          "groupResultSchema", number versions.GroupResultSchema
          "templateSchema", number versions.TemplateSchema
          "adminStateSchema", number versions.AdminStateSchema
          "reportDefinitionSchema", number versions.ReportDefinitionSchema
          "visualizationSchema", number versions.VisualizationSchema
          "supportedEncodings", Json.Array(versions.SupportedEncodings |> List.map number) ]

/// The manifest's record body.
let body (manifest: StorageManifest) =
    Json.objectOf
        [ "datasetId", Json.String manifest.DatasetId
          "rootNamespace", Json.String manifest.RootNamespace
          "versions", versionsBody manifest.Versions
          "createdFromApplicationVersion", Json.String manifest.CreatedFromApplicationVersion ]

/// The manifest's canonical stored text.
let encode (manifest: StorageManifest) : Result<string, Problem> =
    key manifest.DatasetId
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Mutable
          Body = body manifest }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(manifest.DatasetId, "the storage manifest is too large")))

let private versionsOf (value: Json) : Decoded<FormatVersions> =
    closed
        [ "adminStateSchema"
          "canonicalization"
          "groupResultSchema"
          "reportDefinitionSchema"
          "resultSchema"
          "storageLayout"
          "supportedEncodings"
          "templateSchema"
          "visualizationSchema" ]
        value
    |> Result.bind (fun () ->
        let encodings =
            list
                "supportedEncodings"
                (function
                | Json.Number n when n = System.Math.Floor n && n >= 1m && n <= 255m -> Ok(int n)
                | _ -> Error "'supportedEncodings' holds something other than an encoding version")
                value

        match
            integer "storageLayout" value,
            integer "canonicalization" value,
            integer "resultSchema" value,
            integer "groupResultSchema" value,
            integer "templateSchema" value,
            integer "adminStateSchema" value,
            integer "reportDefinitionSchema" value,
            integer "visualizationSchema" value,
            encodings
        with
        | Ok layout, Ok canonical, Ok result, Ok group, Ok template, Ok admin, Ok report, Ok visual, Ok encodings ->
            Ok
                { StorageLayout = layout
                  Canonicalization = canonical
                  ResultSchema = result
                  GroupResultSchema = group
                  TemplateSchema = template
                  AdminStateSchema = admin
                  ReportDefinitionSchema = report
                  VisualizationSchema = visual
                  SupportedEncodings = encodings }
        | Error e, _, _, _, _, _, _, _, _
        | _, Error e, _, _, _, _, _, _, _
        | _, _, Error e, _, _, _, _, _, _
        | _, _, _, Error e, _, _, _, _, _
        | _, _, _, _, Error e, _, _, _, _
        | _, _, _, _, _, Error e, _, _, _
        | _, _, _, _, _, _, Error e, _, _
        | _, _, _, _, _, _, _, Error e, _
        | _, _, _, _, _, _, _, _, Error e -> Error e)

/// A manifest from its record body.
let ofBody (value: Json) : Decoded<StorageManifest> =
    closed [ "createdFromApplicationVersion"; "datasetId"; "rootNamespace"; "versions" ] value
    |> Result.bind (fun () ->
        match
            text "datasetId" value,
            text "rootNamespace" value,
            field "versions" value |> Result.bind versionsOf,
            text "createdFromApplicationVersion" value
        with
        | Ok datasetId, Ok root, Ok versions, Ok application ->
            Ok
                { DatasetId = datasetId
                  RootNamespace = root
                  Versions = versions
                  CreatedFromApplicationVersion = application }
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

/// Every reason this Signal cannot use a dataset whose manifest says
/// `stored`: a format newer than it reads (an unsupported future version
/// fails explicitly, ADM-005) or one it no longer reads, and response
/// encodings it does not know.
let incompatibilities (supported: FormatVersions) (stored: FormatVersions) =
    let check name (mine: int) (theirs: int) =
        if theirs > mine then [ $"{name} {theirs} is newer than {mine}" ]
        elif theirs < 1 then [ $"{name} {theirs} is not a version" ]
        else []

    check "the storage layout" supported.StorageLayout stored.StorageLayout
    @ check "the canonical form" supported.Canonicalization stored.Canonicalization
    @ check "the result schema" supported.ResultSchema stored.ResultSchema
    @ check "the group result schema" supported.GroupResultSchema stored.GroupResultSchema
    @ check "the template schema" supported.TemplateSchema stored.TemplateSchema
    @ check "the administrator state schema" supported.AdminStateSchema stored.AdminStateSchema
    @ check "the report definition schema" supported.ReportDefinitionSchema stored.ReportDefinitionSchema
    @ check "the visualization schema" supported.VisualizationSchema stored.VisualizationSchema
    @ (stored.SupportedEncodings
       |> List.filter (fun version -> not (List.contains version supported.SupportedEncodings))
       |> List.map (fun version -> $"response encoding {version} is not one this Signal reads"))

/// The dataset's manifest, read and checked before anything else in the
/// dataset is loaded (ADM-005, ADM-046): a valid record of the right type, at
/// a schema this Signal reads, for this dataset and this folder, with
/// formats this Signal reads.
let read (supported: FormatVersions) (datasetId: string) (root: string) (stored: ReadOutcome) : Result<StorageManifest, Problem> =
    match stored with
    | ReadOutcome.Absent -> Error(DatasetNotInitialized datasetId)
    | ReadOutcome.Found found ->
        let where = RelativePath.render found.Path

        key datasetId
        |> Result.bind (fun expected ->
            Integrity.validate expected schema Record.DefaultMaxBytes found
            |> Result.mapError (fun failure -> InvalidStoredRecord(where, describeIntegrity failure))
            |> Result.bind (fun valid -> ofBody valid.Record.Body |> Result.mapError (fun detail -> InvalidStoredRecord(where, detail))))
        |> Result.bind (fun manifest ->
            if manifest.DatasetId <> datasetId then
                Error(ManifestUnusable(datasetId, $"the manifest is for dataset '{manifest.DatasetId}'"))
            elif manifest.RootNamespace <> root then
                Error(ManifestUnusable(datasetId, $"the manifest describes '{manifest.RootNamespace}', not '{root}'"))
            else
                match incompatibilities supported manifest.Versions with
                | [] -> Ok manifest
                | problem :: _ -> Error(ManifestUnusable(datasetId, problem)))
