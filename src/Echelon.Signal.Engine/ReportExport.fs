/// Report exports, snapshots and the V1 report families (RPT-004 §§42-44,
/// RPT-005 §55, RPT-006 §§62-67, SRPP-009..SRPP-013).
///
/// The structured JSON export is deterministic for the same ReportData, so a
/// snapshot hash proves which result state, definition, locale and renderer
/// profile produced a formal report. PDF is one renderer over this data,
/// never the canonical model.
module Echelon.Signal.Engine.ReportExport

open System
open System.Buffers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Echelon.Signal.Engine.ReportModel
open Echelon.Signal.Engine.Import

[<Literal>]
let ExportVersion = 1

let private valueText =
    function
    | Shown v -> v.ToString("R", Globalization.CultureInfo.InvariantCulture)
    | NotAvailable -> "not-available"
    | NotApplicableValue -> "not-applicable"
    | InsufficientResponses(h, n) -> $"insufficient {h}/{n}"
    | NotComparable reason -> $"not-comparable: {reason}"
    | SuppressedValue(h, n) -> $"suppressed {h}/{n}"

let private writeValue (w: Utf8JsonWriter) (name: string) (v: Value) =
    match v with
    | Shown x -> w.WriteNumber(name, x)
    | other -> w.WriteString(name, valueText other)

/// The versioned structured export (RPT-004 §43). Deterministic: no clock,
/// fixed member order, only what the definition included.
let json (report: ReportData) : string =
    let buffer = ArrayBufferWriter<byte>()

    (
        use w = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        w.WriteStartObject()
        w.WriteNumber("export", ExportVersion)
        w.WriteString("definition", fst report.Definition)
        w.WriteNumber("definitionVersion", snd report.Definition)
        w.WriteString("audience", sprintf "%A" report.Audience)

        w.WriteString(
            "status",
            match report.Status with
            | Complete -> "complete"
            | Partial _ -> "partial"
            | InsufficientData -> "insufficient-data"
        )

        w.WriteString("title", report.Title)
        w.WriteStartObject "counts"
        w.WriteNumber("expected", report.Counts.Expected)
        w.WriteNumber("accepted", report.Counts.Accepted)
        w.WriteNumber("missing", report.Counts.Missing)
        w.WriteNumber("completionPercent", report.Counts.CompletionPercent)
        w.WriteEndObject()
        writeValue w "overall" report.Overall
        w.WriteStartArray "sections"

        for s in report.Sections do
            w.WriteStartObject()
            w.WriteString("id", s.Section)
            w.WriteString("title", s.Title)
            writeValue w "mean" s.Mean
            writeValue w "median" s.Median
            writeValue w "minimum" s.Minimum
            writeValue w "maximum" s.Maximum
            w.WriteNumber("scored", s.Scored)
            w.WriteNumber("unscored", s.Unscored)
            w.WriteEndObject()

        w.WriteEndArray()

        let strings (name: string) (xs: string list) =
            w.WriteStartArray name
            xs |> List.iter w.WriteStringValue
            w.WriteEndArray()

        strings "strengths" report.Strengths
        strings "weaknesses" report.Weaknesses
        w.WriteStartArray "recommendations"

        for r in report.Recommendations do
            w.WriteStartObject()
            w.WriteString("id", r.Id)
            w.WriteString("title", r.Title)
            w.WriteString("priority", r.Priority)
            w.WriteNumber("frequency", r.Frequency)
            w.WriteEndObject()

        w.WriteEndArray()
        w.WriteStartObject "coverage"
        w.WriteNumber("applicable", report.Coverage.Applicable)
        w.WriteNumber("answered", report.Coverage.Answered)
        w.WriteNumber("special", report.Coverage.Special)
        w.WriteNumber("unanswered", report.Coverage.Unanswered)
        w.WriteEndObject()
        strings "warnings" (report.Warnings |> List.map warningCode)
        w.WriteStartArray "comparisons"

        for c in report.Comparisons do
            w.WriteStartObject()
            w.WriteString("label", c.Input.Label)
            w.WriteString("baseline", c.Input.Baseline)
            w.WriteString("metric", c.Input.Metric)
            w.WriteBoolean("comparable", c.Input.Comparable)
            writeValue w "delta" c.Delta
            w.WriteEndObject()

        w.WriteEndArray()
        strings "methodology" report.Methodology

        report.Audit
        |> Option.iter (fun a ->
            w.WriteStartObject "audit"
            w.WriteString("surveyId", a.SurveyId)
            w.WriteString("templateVersion", a.TemplateVersion)
            w.WriteString("templateHash", a.TemplateHash)
            w.WriteString("groupResultHash", a.GroupResultHash)
            w.WriteNumber("groupResultVersion", a.GroupResultVersion)
            w.WriteEndObject())

        w.WriteEndObject()
    )

    Encoding.UTF8.GetString(buffer.WrittenSpan)

let private csvField (text: string) =
    if text.IndexOfAny([| ','; '"'; '\n' |]) >= 0 then "\"" + text.Replace("\"", "\"\"") + "\"" else text

/// Section summary as CSV (RPT-004 §44): tabular data only.
let sectionsCsv (report: ReportData) : string =
    let header = "section,title,mean,median,minimum,maximum,scored,unscored"

    let rows =
        report.Sections
        |> List.map (fun s ->
            [ s.Section; s.Title; valueText s.Mean; valueText s.Median; valueText s.Minimum; valueText s.Maximum; string s.Scored; string s.Unscored ]
            |> List.map csvField
            |> String.concat ",")

    String.concat "\n" (header :: rows) + "\n"

/// A formal report's identity (RPT-005 §55, SRPP-010): the hash covers the
/// export, the locale and the renderer profile (Folio package and profile
/// version); the generation time is recorded but not hashed.
type Snapshot =
    { DefinitionId: string
      DefinitionVersion: int
      GroupResultHash: string
      Locale: string
      RendererProfile: string
      GeneratedAt: DateTimeOffset
      Hash: string }

let snapshot (report: ReportData) (groupResultHash: string) (locale: string) (rendererProfile: string) : Snapshot =
    let text = String.concat "\n" [ "report-snapshot/1"; json report; groupResultHash; locale; rendererProfile ]

    { DefinitionId = fst report.Definition
      DefinitionVersion = snd report.Definition
      GroupResultHash = groupResultHash
      Locale = locale
      RendererProfile = rendererProfile
      GeneratedAt = report.GeneratedAt
      Hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant() }

// ---------------------------------------------------------------------------
// The V1 report families (RPT-006 §§62-67).
// ---------------------------------------------------------------------------

let private definition id audience blocks detail =
    { Id = id
      Version = 1
      Audience = audience
      Blocks = blocks
      Detail = detail
      MinimumGroupSize = 5
      Decimals = 1
      HighlightCount = 3 }

let administratorGroup =
    definition
        "administrator-group"
        AdministratorAudience
        [ Header; ResponseCounts; Summary; OverallResult; SectionResults; Recommendations; CoverageAndConfidence; Methodology ]
        StandardDetail

let executiveSummary =
    definition
        "executive-summary"
        ExecutiveAudience
        [ Header; Summary; OverallResult; Strengths; Weaknesses; Recommendations; CoverageAndConfidence; Comparisons ]
        SummaryDetail

let detailedSection =
    definition
        "detailed-section"
        AdministratorAudience
        [ Header; SectionResults; GroupDistributions; Recommendations; CoverageAndConfidence ]
        DetailedDetail

let anonymousAggregate =
    definition
        "anonymous-aggregate"
        AdministratorAudience
        [ Header; ResponseCounts; OverallResult; SectionResults; GroupDistributions; Recommendations; CoverageAndConfidence ]
        StandardDetail

let identifiedDetail =
    definition
        "identified-detail"
        AdministratorAudience
        [ Header; OverallResult; SectionResults; Recommendations; CoverageAndConfidence; RespondentDetail ]
        DetailedDetail

let audit =
    definition
        "audit"
        TechnicalAudience
        [ Header; ResponseCounts; OverallResult; SectionResults; GroupDistributions; Methodology; AuditMetadata ]
        AuditDetail

let families = [ administratorGroup; executiveSummary; detailedSection; anonymousAggregate; identifiedDetail; audit ]

/// The families a group may expose: those whose privacy conditions hold for
/// its identity mode (SRPP-013).
let availableFamilies (mode: IdentityMode) (groupMinimum: int) =
    families |> List.filter (fun d -> (Report.check d mode groupMinimum).IsEmpty)
