/// Saved report definitions and formal report snapshots as Arca records
/// (WI-0050, ADM-010, ADM-021, ADM-022, ADM-063).
///
/// - **Definition** (`signal.report-definition`, mutable): one per
///   definition id, holding every version with its pins and whether it was
///   used. Reading it refuses a record whose versions are not 1, 2, ... in
///   order.
/// - **Snapshot** (`signal.report-snapshot`, immutable): the snapshot with
///   its canonical report data. Reading it refuses a snapshot whose data does
///   not hash to its recorded hash or whose id does not match its contents.
///
/// Pure.
module Echelon.Signal.Admin.ReportRecord

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Arca
open Echelon.Signal.Engine.ReportModel
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec
open Echelon.Signal.Admin.ReportLibrary

let private typeOf (name: string) =
    match RecordType.create name with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

let definitionType = typeOf "signal.report-definition"
let snapshotType = typeOf "signal.report-snapshot"

let private schemaOf recordType = { Type = recordType; OldestReadable = 1; Current = 1 }

let private idFor (text: string) =
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant().Substring(0, 32)

let definitionId (id: string) = idFor ("report-definition:" + id)
let snapshotRecordId (s: Snapshot) = idFor s.SnapshotId

let private pathOf (recordType: RecordType) (id: string) : Result<RelativePath, Problem> =
    match RecordId.create id with
    | Ok recordId ->
        { Type = recordType; Partition = []; Id = recordId }
        |> Layout.recordPath
        |> Result.mapError (LocationError.describe >> InvalidDataLocation)
    | Error text -> Error(UnstorableRecord(text, "not a record id"))

let definitionPath id = pathOf definitionType (definitionId id)
let snapshotPath s = pathOf snapshotType (snapshotRecordId s)

let folderOf (recordType: RecordType) : RelativePath =
    match RelativePath.parse (String.concat "/" [ Layout.RecordsFolder; RecordType.value recordType ]) with
    | Ok found -> found
    | Error error -> invalidOp ("internal: " + LocationError.describe error)

let private encodeWith (recordType: RecordType) (mutability: Mutability) (id: string) (body: Json) : Result<string, Problem> =
    match RecordId.create id with
    | Ok recordId ->
        { Id = recordId; Type = recordType; SchemaVersion = 1; Mutability = mutability; Body = body }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(id, "the report record is too large"))
    | Error text -> Error(UnstorableRecord(text, "not a record id"))

// ---- Spellings ---------------------------------------------------------------------------------

let private audiences =
    [ AdministratorAudience, "administrator"
      ExecutiveAudience, "executive"
      RespondentAudience, "respondent"
      FacilitatorAudience, "facilitator"
      GroupReviewerAudience, "group-reviewer"
      TechnicalAudience, "technical" ]

let private blocks =
    [ Header, "header"
      Summary, "summary"
      ResponseCounts, "response-counts"
      OverallResult, "overall-result"
      SectionResults, "section-results"
      GroupDistributions, "group-distributions"
      Strengths, "strengths"
      Weaknesses, "weaknesses"
      Recommendations, "recommendations"
      CoverageAndConfidence, "coverage-and-confidence"
      Comparisons, "comparisons"
      RespondentDetail, "respondent-detail"
      RoleBreakdown, "role-breakdown"
      Methodology, "methodology"
      AuditMetadata, "audit-metadata" ]

let private details =
    [ SummaryDetail, "summary"
      StandardDetail, "standard"
      DetailedDetail, "detailed"
      AuditDetail, "audit" ]

let private spell table value = table |> List.find (fst >> (=) value) |> snd

let private named table kind (spelling: string) =
    match table |> List.tryFind (snd >> (=) spelling) with
    | Some(value, _) -> Ok value
    | None -> Error $"'{spelling}' is not a known {kind}"

let private whole (n: int) = Json.Number(decimal n)

// ---- Definitions -------------------------------------------------------------------------------

[<NoComparison>]
type StoredDefinitions =
    { DatasetId: string
      Id: string
      Versions: Entry list }

let private templateRefJson (t: TemplateRef) =
    Json.objectOf [ "surveyId", Json.String t.SurveyId; "version", Json.String t.Version; "hash", Json.String t.Hash ]

let private templateRefOf (value: Json) : Decoded<TemplateRef> =
    closed [ "hash"; "surveyId"; "version" ] value
    |> Result.bind (fun () -> both (text "surveyId" value) (both (text "version" value) (text "hash" value)))
    |> Result.map (fun (s, (v, h)) -> { SurveyId = s; Version = v; Hash = h })

let private pinsJson (p: Pins) =
    Json.objectOf
        [ "template",
          (match p.Template with
           | ExactTemplate t -> Json.objectOf [ "exact", templateRefJson t ]
           | LatestTemplate s -> Json.objectOf [ "latest", Json.String s ])
          "visualizationSpec", whole p.VisualizationSpec
          "resultSchema", whole p.ResultSchema
          "aggregateSchema", whole p.AggregateSchema
          "exportSchema", whole p.ExportSchema ]

let private pinsOf (value: Json) : Decoded<Pins> =
    let template =
        field "template" value
        |> Result.bind (fun t ->
            match field "exact" t, text "latest" t with
            | Ok exact, _ -> templateRefOf exact |> Result.map ExactTemplate
            | _, Ok surveyId -> Ok(LatestTemplate surveyId)
            | _ -> Error "'template' is neither exact nor latest")

    closed [ "aggregateSchema"; "exportSchema"; "resultSchema"; "template"; "visualizationSpec" ] value
    |> Result.bind (fun () -> both template (both (integer "visualizationSpec" value) (both (integer "resultSchema" value) (both (integer "aggregateSchema" value) (integer "exportSchema" value)))))
    |> Result.map (fun (t, (v, (r, (a, e)))) -> { Template = t; VisualizationSpec = v; ResultSchema = r; AggregateSchema = a; ExportSchema = e })

/// One definition version as JSON (also a configuration package item, WI-0075).
let entryJson (e: Entry) =
    let d = e.Definition

    Json.objectOf
        [ "version", whole d.Version
          "audience", Json.String(spell audiences d.Audience)
          "blocks", textArray (d.Blocks |> List.map (spell blocks))
          "detail", Json.String(spell details d.Detail)
          "minimumGroupSize", whole d.MinimumGroupSize
          "decimals", whole d.Decimals
          "highlightCount", whole d.HighlightCount
          "pins", pinsJson e.Pins
          "used", Json.Bool e.Used ]

let entryOf (id: string) (value: Json) : Decoded<Entry> =
    let audience = text "audience" value |> Result.bind (named audiences "audience")
    let blockList = texts "blocks" value |> Result.bind (traverse (named blocks "block"))
    let detail = text "detail" value |> Result.bind (named details "detail level")
    let numbers = both (integer "version" value) (both (integer "minimumGroupSize" value) (both (integer "decimals" value) (integer "highlightCount" value)))
    let rest = both (field "pins" value |> Result.bind pinsOf) (flag "used" value)

    closed [ "audience"; "blocks"; "decimals"; "detail"; "highlightCount"; "minimumGroupSize"; "pins"; "used"; "version" ] value
    |> Result.bind (fun () -> both audience (both blockList (both detail (both numbers rest))))
    |> Result.map (fun (a, (b, (d, ((version, (minimum, (decimals, highlights))), (pins, used))))) ->
        { Definition =
            { Id = id
              Version = version
              Audience = a
              Blocks = b
              Detail = d
              MinimumGroupSize = minimum
              Decimals = decimals
              HighlightCount = highlights }
          Pins = pins
          Used = used })

let encodeDefinitions (datasetId: string) (id: string) (entries: Entry list) : Result<string, Problem> =
    Json.objectOf [ "datasetId", Json.String datasetId; "id", Json.String id; "versions", Json.Array(entries |> List.map entryJson) ]
    |> encodeWith definitionType Mutability.Mutable (definitionId id)

let definitionsOfBody (value: Json) : Decoded<StoredDefinitions> =
    closed [ "datasetId"; "id"; "versions" ] value
    |> Result.bind (fun () -> both (text "datasetId" value) (text "id" value))
    |> Result.bind (fun (datasetId, id) ->
        list "versions" (entryOf id) value
        |> Result.bind (fun entries ->
            if entries |> List.mapi (fun i e -> e.Definition.Version = i + 1) |> List.forall (fun inOrder -> inOrder) then
                Ok { DatasetId = datasetId; Id = id; Versions = entries }
            else
                Error "the definition's versions are not 1, 2, ... in order"))

let definitionReader: Loading.RecordReader<StoredDefinitions> =
    { Type = definitionType
      Schema = schemaOf definitionType
      MaxBytes = Record.DefaultMaxBytes
      Decode = definitionsOfBody
      IdOf = fun stored -> definitionId stored.Id
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }

// ---- Snapshots ---------------------------------------------------------------------------------

[<NoComparison>]
type StoredSnapshot = { DatasetId: string; Snapshot: Snapshot }

let encodeSnapshot (datasetId: string) (s: Snapshot) : Result<string, Problem> =
    Json.objectOf
        [ "datasetId", Json.String datasetId
          "snapshotId", Json.String s.SnapshotId
          "groupId", Json.String s.GroupId
          "groupResultHash", Json.String s.GroupResultHash
          "reportStateHash", Json.String s.ReportStateHash
          "definitionId", Json.String s.DefinitionId
          "definitionVersion", whole s.DefinitionVersion
          "template", templateRefJson s.Template
          "visualizationSpec", whole s.VisualizationSpec
          "resultSchema", whole s.ResultSchema
          "aggregateSchema", whole s.AggregateSchema
          "exportSchema", whole s.ExportSchema
          "comparisons", textArray s.ComparisonReferences
          "locale", Json.String s.Locale
          "accepted", whole s.Accepted
          "reportDataHash", Json.String s.CanonicalReportDataHash
          "reportData", Json.String s.ReportData
          "sectionsCsv", Json.String s.SectionsCsv
          "generatedAt",
          (match s.GeneratedAtEvidence with
           | Some at -> Json.String(at.ToString("O", CultureInfo.InvariantCulture))
           | None -> Json.Null)
          "snapshotSchema", whole s.SnapshotSchemaVersion ]
    |> encodeWith snapshotType Mutability.Immutable (snapshotRecordId s)

let snapshotOfBody (value: Json) : Decoded<StoredSnapshot> =
    let generatedAt =
        field "generatedAt" value
        |> Result.bind (function
            | Json.Null -> Ok None
            | Json.String t ->
                match DateTimeOffset.TryParseExact(t, "O", CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, parsed -> Ok(Some parsed)
                | _ -> Error "'generatedAt' is not a timestamp"
            | _ -> Error "'generatedAt' is not a timestamp")

    let identity = both (text "datasetId" value) (both (text "snapshotId" value) (both (text "groupId" value) (both (text "groupResultHash" value) (text "reportStateHash" value))))
    let definition = both (text "definitionId" value) (integer "definitionVersion" value)
    let template = field "template" value |> Result.bind templateRefOf
    let schemas = both (integer "visualizationSpec" value) (both (integer "resultSchema" value) (both (integer "aggregateSchema" value) (both (integer "exportSchema" value) (integer "snapshotSchema" value))))
    let data = both (texts "comparisons" value) (both (text "locale" value) (both (integer "accepted" value) (both (text "reportDataHash" value) (both (text "reportData" value) (both (text "sectionsCsv" value) generatedAt)))))

    closed
        [ "accepted"; "aggregateSchema"; "comparisons"; "datasetId"; "definitionId"; "definitionVersion"; "exportSchema"; "generatedAt"; "groupId"; "groupResultHash"
          "locale"; "reportData"; "reportDataHash"; "reportStateHash"; "resultSchema"; "sectionsCsv"; "snapshotId"; "snapshotSchema"; "template"; "visualizationSpec" ]
        value
    |> Result.bind (fun () -> both identity (both definition (both template (both schemas data))))
    |> Result.bind (fun ((datasetId, (snapshotId, (groupId, (resultHash, stateHash)))), ((defId, defVersion), (t, ((viz, (result, (aggregate, (exportSchema, snapshotSchema)))), (comparisons, (locale, (accepted, (dataHash, (reportData, (csv, at)))))))))) ->
        let s =
            { SnapshotId = snapshotId
              GroupId = groupId
              GroupResultHash = resultHash
              ReportStateHash = stateHash
              DefinitionId = defId
              DefinitionVersion = defVersion
              Template = t
              VisualizationSpec = viz
              ResultSchema = result
              AggregateSchema = aggregate
              ExportSchema = exportSchema
              ComparisonReferences = comparisons
              Locale = locale
              Accepted = accepted
              CanonicalReportDataHash = dataHash
              ReportData = reportData
              SectionsCsv = csv
              GeneratedAtEvidence = at
              SnapshotSchemaVersion = snapshotSchema }

        if intact s then Ok { DatasetId = datasetId; Snapshot = s }
        else Error "the snapshot's report data or id does not match what it records")

let snapshotReader: Loading.RecordReader<StoredSnapshot> =
    { Type = snapshotType
      Schema = schemaOf snapshotType
      MaxBytes = Record.DefaultMaxBytes
      Decode = snapshotOfBody
      IdOf = fun stored -> snapshotRecordId stored.Snapshot
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }
