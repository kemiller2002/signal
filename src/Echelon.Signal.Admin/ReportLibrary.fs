/// Saved report definitions, formal report snapshots and exports (WI-0050,
/// ADM-021, ADM-022, ADM-023, ADM-025, ADM-063).
///
/// - **Definitions are versioned** (ADM-021). Saving a definition whose
///   latest version has not been used replaces that version; saving one whose
///   latest version was used for a formal snapshot creates the next version,
///   so a used version never changes. A definition holds presentation only:
///   it has no field that could change scoring, applicability, duplicates,
///   completion or aggregation.
/// - **Dependencies are pinned** (ADM-063): the template exactly, or "latest"
///   while drafting; the visualization, result, aggregate and export schema
///   versions exactly. Taking a snapshot resolves "latest" to the exact
///   published template, and a pin that cannot be resolved is refused, never
///   substituted.
/// - **A snapshot is immutable** (ADM-022): it records the exact result state,
///   definition version, resolved dependencies, locale, comparison references
///   and the hash of the canonical report data, and carries that report data,
///   so opening it never reads current group state. Time is clock evidence
///   kept outside every hash.
/// - **Exports** (ADM-023, ADM-025) are files derived from the same ReportData
///   as the screen, with non-PII lineage. Nothing here carries a storage
///   location or a credential, so neither can reach an export.
///
/// Pure.
module Echelon.Signal.Admin.ReportLibrary

open System
open System.Security.Cryptography
open System.Text
open Echelon.Signal.Engine
open Echelon.Signal.Engine.ReportModel
open Echelon.Signal.Engine.Import

[<Literal>]
let SnapshotSchemaVersion = 1

/// An exact published template.
type TemplateRef =
    { SurveyId: string
      Version: string
      Hash: string }

/// The template a definition reports on (ADM-063).
type TemplatePin =
    | ExactTemplate of TemplateRef
    /// A drafting convenience only: resolved to an exact template when a
    /// snapshot is taken.
    | LatestTemplate of surveyId: string

/// A definition's external semantic dependencies (ADM-063).
type Pins =
    { Template: TemplatePin
      VisualizationSpec: int
      ResultSchema: int
      AggregateSchema: int
      ExportSchema: int }

/// The schema versions this build reads and writes, with a template pin.
let currentPins (template: TemplatePin) =
    { Template = template
      VisualizationSpec = Visualization.SpecVersion
      ResultSchema = GroupResult.GroupResultVersion
      AggregateSchema = ReportState.StateVersion
      ExportSchema = ReportExport.ExportVersion }

/// One version of a saved definition.
type Entry =
    { Definition: Definition
      Pins: Pins
      /// Used for a formal snapshot: this version never changes again.
      Used: bool }

/// Every saved definition, by id, versions ascending.
type Library = Map<string, Entry list>

let empty: Library = Map.empty

let versions (library: Library) (id: string) = library |> Map.tryFind id |> Option.defaultValue []

let latest (library: Library) (id: string) = versions library id |> List.tryLast

let resolve (library: Library) (id: string) (version: int) =
    versions library id |> List.tryFind (fun e -> e.Definition.Version = version)

type LibraryProblem =
    | InvalidDefinition of Report.DefinitionProblem list
    | InvalidId of string
    | UnknownDefinition of id: string * version: int

let private validId (id: string) =
    id.Length > 0 && id.Length <= 64 && id |> Seq.forall (fun c -> Char.IsAsciiLetterLower c || Char.IsAsciiDigit c || c = '-')

/// Saves a definition (its version is assigned here): a new id starts at 1,
/// an unused latest version is replaced, a used one gets a successor.
let save (library: Library) (definition: Definition) (pins: Pins) : Result<Library * Entry, LibraryProblem> =
    // Identified mode and no minimum: only the definition's own structure is checked.
    match Report.check definition IdentifiedGroup 0 with
    | _ :: _ as problems -> Error(InvalidDefinition problems)
    | [] when not (validId definition.Id) -> Error(InvalidId definition.Id)
    | [] ->
        let existing = versions library definition.Id

        let kept, version =
            match List.tryLast existing with
            | None -> [], 1
            | Some last when last.Used -> existing, last.Definition.Version + 1
            | Some last -> List.take (existing.Length - 1) existing, last.Definition.Version

        let entry = { Definition = { definition with Version = version }; Pins = pins; Used = false }
        Ok(library |> Map.add definition.Id (kept @ [ entry ]), entry)

/// Marks a version used (by a formal snapshot).
let markUsed (library: Library) (id: string) (version: int) : Result<Library, LibraryProblem> =
    match resolve library id version with
    | None -> Error(UnknownDefinition(id, version))
    | Some _ ->
        let used = versions library id |> List.map (fun e -> if e.Definition.Version = version then { e with Used = true } else e)
        Ok(library |> Map.add id used)

// ---- Snapshots ---------------------------------------------------------------------------------

/// An immutable formal report snapshot (ADM-022).
type Snapshot =
    { SnapshotId: string
      GroupId: string
      GroupResultHash: string
      ReportStateHash: string
      DefinitionId: string
      DefinitionVersion: int
      Template: TemplateRef
      VisualizationSpec: int
      ResultSchema: int
      AggregateSchema: int
      ExportSchema: int
      ComparisonReferences: string list
      Locale: string
      /// Accepted responses the report covers (the release ledger's count).
      Accepted: int
      CanonicalReportDataHash: string
      /// The canonical report data (the structured export).
      ReportData: string
      /// Clock evidence, outside every hash.
      GeneratedAtEvidence: DateTimeOffset option
      SnapshotSchemaVersion: int }

/// What a snapshot is taken from: the group's exact state and its report.
type Source =
    { GroupId: string
      GroupResultHash: string
      ReportStateHash: string
      /// The exact template the group's results were scored with.
      GroupTemplate: TemplateRef
      Report: ReportData
      Locale: string
      ComparisonReferences: string list
      /// The group's identity mode and minimum: anonymous snapshots go through the release ledger.
      Mode: IdentityMode
      MinimumReportable: int
      Clock: DateTimeOffset option }

type SnapshotProblem =
    | DefinitionMissing of id: string * version: int
    | ReportFromAnotherDefinition of id: string * version: int
    | TemplateUnresolved of surveyId: string
    /// The pinned template is not the one the group was scored with.
    | TemplateMismatch of pinned: TemplateRef * group: TemplateRef
    | SchemaUnsupported of name: string * pinned: int * supported: int
    /// With an earlier snapshot of the group, it would single out the
    /// responses between them (ARX-009: repeated snapshots).
    | WouldRevealDifference of earlier: string * difference: int

let private sha256 (text: string) =
    "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

let private resolveTemplate (catalog: Publication.Catalog) (pin: TemplatePin) =
    let refOf (t: Publication.Published) = { SurveyId = t.SurveyId; Version = t.Version; Hash = t.Hash }

    match pin with
    | ExactTemplate exact ->
        match Publication.resolveHash catalog exact.Hash with
        | Some t when refOf t = exact -> Ok exact
        | _ -> Error(TemplateUnresolved exact.SurveyId)
    | LatestTemplate surveyId ->
        match Publication.versionsOf catalog surveyId |> List.tryLast with
        | Some t -> Ok(refOf t)
        | None -> Error(TemplateUnresolved surveyId)

let private schemas (pins: Pins) =
    [ "visualization", pins.VisualizationSpec, Visualization.SpecVersion
      "result", pins.ResultSchema, GroupResult.GroupResultVersion
      "aggregate", pins.AggregateSchema, ReportState.StateVersion
      "export", pins.ExportSchema, ReportExport.ExportVersion ]

/// The text a snapshot's id is the hash of: everything but the clock.
let private identityText (s: Snapshot) =
    String.concat
        "\n"
        [ $"report-snapshot/{s.SnapshotSchemaVersion}"
          s.GroupId
          s.GroupResultHash
          s.ReportStateHash
          s.DefinitionId
          string s.DefinitionVersion
          s.Template.SurveyId
          s.Template.Version
          s.Template.Hash
          string s.VisualizationSpec
          string s.ResultSchema
          string s.AggregateSchema
          string s.ExportSchema
          String.concat "," s.ComparisonReferences
          s.Locale
          string s.Accepted
          s.CanonicalReportDataHash ]

let snapshotIdOf (s: Snapshot) = "snap-" + (sha256 (identityText s)).Substring(7, 32)

/// Whether a new snapshot of the group would differ from an earlier one by
/// fewer responses than the minimum (anonymous groups only).
let private ledger (earlier: Snapshot list) (source: Source) =
    let view id count : Disclosure.View = { Id = id; Filters = Set.empty; Count = count }
    let views = earlier |> List.filter (fun s -> s.GroupId = source.GroupId) |> List.map (fun s -> view s.SnapshotId s.Accepted)
    let policy = Disclosure.forGroup source.MinimumReportable

    match fst (Disclosure.release policy source.Mode views (view "new" source.Report.Counts.Accepted)) with
    | Disclosure.Withhold(Disclosure.Differencing(earlier, difference)) -> Error(WouldRevealDifference(earlier, difference))
    | _ -> Ok()

/// Takes a formal snapshot with the definition version it names, resolving
/// every pin exactly; the definition version becomes used. `earlier` are the
/// snapshots already taken, for the release ledger.
let take (library: Library) (catalog: Publication.Catalog) (earlier: Snapshot list) (source: Source) : Result<Snapshot * Library, SnapshotProblem> =
    let id, version = source.Report.Definition

    match resolve library id version with
    | None -> Error(DefinitionMissing(id, version))
    | Some entry when entry.Definition.Audience <> source.Report.Audience -> Error(ReportFromAnotherDefinition(id, version))
    | Some entry ->
        match schemas entry.Pins |> List.tryFind (fun (_, pinned, supported) -> pinned <> supported) with
        | Some(name, pinned, supported) -> Error(SchemaUnsupported(name, pinned, supported))
        | None ->
            ledger earlier source
            |> Result.bind (fun () -> resolveTemplate catalog entry.Pins.Template)
            |> Result.bind (fun template ->
                if template <> source.GroupTemplate then Error(TemplateMismatch(template, source.GroupTemplate))
                else Ok template)
            |> Result.map (fun template ->
                let data = ReportExport.json source.Report

                let snapshot =
                    { SnapshotId = ""
                      GroupId = source.GroupId
                      GroupResultHash = source.GroupResultHash
                      ReportStateHash = source.ReportStateHash
                      DefinitionId = id
                      DefinitionVersion = version
                      Template = template
                      VisualizationSpec = entry.Pins.VisualizationSpec
                      ResultSchema = entry.Pins.ResultSchema
                      AggregateSchema = entry.Pins.AggregateSchema
                      ExportSchema = entry.Pins.ExportSchema
                      ComparisonReferences = source.ComparisonReferences |> List.distinct |> List.sort
                      Locale = source.Locale
                      Accepted = source.Report.Counts.Accepted
                      CanonicalReportDataHash = sha256 data
                      ReportData = data
                      GeneratedAtEvidence = source.Clock
                      SnapshotSchemaVersion = SnapshotSchemaVersion }

                let used = markUsed library id version |> Result.defaultValue library
                { snapshot with SnapshotId = snapshotIdOf snapshot }, used)

/// Whether a stored snapshot is what it claims: its data hashes to its
/// recorded hash and its id to its contents.
let intact (s: Snapshot) =
    sha256 s.ReportData = s.CanonicalReportDataHash && snapshotIdOf s = s.SnapshotId

/// A dependency a historical snapshot names that is not available now.
type Missing =
    | MissingDefinition of id: string * version: int
    | MissingTemplate of TemplateRef
    | DataAltered

/// Opening a historical snapshot (ADM-022): its own report data, or an
/// explicit unresolved state; never current group state instead.
type Opened =
    | Resolved of reportData: string
    | Unresolved of Missing list

let openSnapshot (library: Library) (catalog: Publication.Catalog) (s: Snapshot) : Opened =
    let missing =
        [ if not (intact s) then DataAltered
          if (resolve library s.DefinitionId s.DefinitionVersion).IsNone then MissingDefinition(s.DefinitionId, s.DefinitionVersion)
          match Publication.resolveHash catalog s.Template.Hash with
          | Some t when t.SurveyId = s.Template.SurveyId && t.Version = s.Template.Version -> ()
          | _ -> MissingTemplate s.Template ]

    if missing.IsEmpty then Resolved s.ReportData else Unresolved missing

// ---- Exports -----------------------------------------------------------------------------------

/// Non-PII lineage on every export (ADM-023): survey and group, schema
/// versions, definition version, the snapshot and its data hash, comparisons.
let lineage (s: Snapshot) =
    Arca.Json.objectOf
        [ "surveyId", Arca.Json.String s.Template.SurveyId
          "templateVersion", Arca.Json.String s.Template.Version
          "templateHash", Arca.Json.String s.Template.Hash
          "groupId", Arca.Json.String s.GroupId
          "groupResultHash", Arca.Json.String s.GroupResultHash
          "resultSchema", Arca.Json.Number(decimal s.ResultSchema)
          "definitionId", Arca.Json.String s.DefinitionId
          "definitionVersion", Arca.Json.Number(decimal s.DefinitionVersion)
          "snapshotId", Arca.Json.String s.SnapshotId
          "reportDataHash", Arca.Json.String s.CanonicalReportDataHash
          "comparisons", Arca.Json.Array(s.ComparisonReferences |> List.map Arca.Json.String) ]
    |> Arca.Json.canonicalText

type ExportProblem = ReportIsNotTheSnapshot

/// The export files for a snapshot, from the ReportData it was taken from:
/// the structured JSON (canonical), the sections CSV (a legal tabular
/// projection, never the canonical form) and the lineage on its own.
let export (s: Snapshot) (report: ReportData) : Result<(string * string) list, ExportProblem> =
    let data = ReportExport.json report

    if sha256 data <> s.CanonicalReportDataHash then
        Error ReportIsNotTheSnapshot
    else
        let line = lineage s

        Ok
            [ "report.json", "{\"lineage\":" + line + ",\"report\":" + data + "}"
              "sections.csv", ReportExport.sectionsCsv report
              "lineage.json", line ]
