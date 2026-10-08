/// The canonical form and hash of a generic published template (VER-002,
/// CAN-005 §29, AUT-004 §43, AUT-006 §70).
///
/// Compact JSON with a fixed member order, no whitespace, arrays in template
/// order and the default ASCII-safe escaper, so one template is one byte
/// sequence on every platform. Identity (survey identifier and version) and
/// every interpretive member are written; provenance (lineage, publisher,
/// time) is not, so it can vary without changing the hash.
///
/// Extension rule: an optional member that is absent (None) and a list that
/// is empty are not written. A later schema addition that is optional or a
/// list therefore leaves the hash of every template that does not use it
/// unchanged.
///
/// This form is distinct from `Canonical` (the SDRA `Assessment` form
/// version 1), which stays frozen; the two are told apart by their first
/// member.
module Echelon.Signal.Engine.TemplateCanonical

open System
open System.Buffers
open System.Security.Cryptography
open System.Text.Json
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Layout

/// Identifies this canonical form inside the bytes.
[<Literal>]
let Form = "signal-template/1"

[<Literal>]
let ReferenceLength = 8

let private optionalString (w: Utf8JsonWriter) (name: string) (value: string option) =
    value |> Option.iter (fun v -> w.WriteString(name, v))

let private stringList (w: Utf8JsonWriter) (name: string) (values: string list) =
    if not values.IsEmpty then
        w.WriteStartArray name
        values |> List.iter w.WriteStringValue
        w.WriteEndArray()

let private numberList (w: Utf8JsonWriter) (name: string) (values: float list) =
    w.WriteStartArray name
    values |> List.iter w.WriteNumberValue
    w.WriteEndArray()

let specialName = RuleCanonical.specialName

let presetName = PrimitiveCanonical.presetName

let capabilityName =
    function
    | UsesBranching -> "branching"
    | UsesDerivedFacts -> "derived-facts"
    | UsesCustomScoring -> "custom-scoring"
    | UsesRanking -> "ranking"
    | UsesAllocation -> "allocation"
    | UsesGroupScoring -> "group-scoring"
    | UsesConditionalSections -> "conditional-sections"
    | UsesRecommendations -> "recommendations"
    | UsesAdvancedValidation -> "advanced-validation"
    | UsesLocalization -> "localization"

let private progressName =
    function
    | NoProgress -> "none"
    | QuestionProgress -> "question"
    | PageProgress -> "page"
    | SectionProgress -> "section"

let private resultModeName =
    function
    | Immediate -> "immediate"
    | Hidden -> "hidden"
    | Deferred -> "deferred"
    | External -> "external"

/// A catalog scorer, written as tagged data (the scorer's declared
/// semantics, not its implementation).
let writeScorer (w: Utf8JsonWriter) (scorer: Scoring.Scorer) =
    w.WriteStartObject()

    w.WriteStartObject "scale"

    match scorer.Scale with
    | Scoring.Direct -> w.WriteString("kind", "direct")
    | Scoring.Reverse(lo, hi) ->
        w.WriteString("kind", "reverse")
        w.WriteNumber("minimum", lo)
        w.WriteNumber("maximum", hi)
    | Scoring.Mapped map ->
        w.WriteString("kind", "mapped")
        w.WriteStartArray "map"

        for KeyValue(k, v) in map do
            w.WriteStartArray()
            w.WriteNumberValue k
            w.WriteNumberValue v
            w.WriteEndArray()

        w.WriteEndArray()

    w.WriteEndObject()

    w.WriteStartObject "aggregate"

    let kind (name: string) = w.WriteString("kind", name)

    match scorer.Aggregate with
    | Scoring.RawSum -> kind "raw-sum"
    | Scoring.Mean -> kind "mean"
    | Scoring.Median -> kind "median"
    | Scoring.Minimum -> kind "minimum"
    | Scoring.Maximum -> kind "maximum"
    | Scoring.CountAnswered -> kind "count-answered"
    | Scoring.CountAtLeast t ->
        kind "count-at-least"
        w.WriteNumber("threshold", t)
    | Scoring.WeightedSum ws ->
        kind "weighted-sum"
        numberList w "weights" ws
    | Scoring.WeightedMean ws ->
        kind "weighted-mean"
        numberList w "weights" ws
    | Scoring.PercentageOfMaximum m ->
        kind "percentage-of-maximum"
        w.WriteNumber("itemMaximum", m)
    | Scoring.PercentageOfRange(lo, hi) ->
        kind "percentage-of-range"
        w.WriteNumber("minimum", lo)
        w.WriteNumber("maximum", hi)
    | Scoring.TrimmedMean t ->
        kind "trimmed-mean"
        w.WriteNumber("trim", t)
    | Scoring.CappedSum c ->
        kind "capped-sum"
        w.WriteNumber("cap", c)
    | Scoring.TopN n ->
        kind "top-n"
        w.WriteNumber("n", n)
    | Scoring.BottomN n ->
        kind "bottom-n"
        w.WriteNumber("n", n)
    | Scoring.TopKBox(k, m) ->
        kind "top-k-box"
        w.WriteNumber("k", k)
        w.WriteNumber("scaleMaximum", m)
    | Scoring.BottomKBox(k, m) ->
        kind "bottom-k-box"
        w.WriteNumber("k", k)
        w.WriteNumber("scaleMaximum", m)
    | Scoring.WeightedTopKBox(ws, m) ->
        kind "weighted-top-k-box"
        numberList w "weights" ws
        w.WriteNumber("scaleMaximum", m)
    | Scoring.FavorableRate c ->
        kind "favorable-rate"
        w.WriteNumber("atLeast", c)
    | Scoring.UnfavorableRate c ->
        kind "unfavorable-rate"
        w.WriteNumber("atMost", c)
    | Scoring.NetFavorable(f, u) ->
        kind "net-favorable"
        w.WriteNumber("favorableAtLeast", f)
        w.WriteNumber("unfavorableAtMost", u)
    | Scoring.NetPromoterScore -> kind "net-promoter-score"

    w.WriteEndObject()

    w.WriteStartArray "transforms"

    for t in scorer.Transforms do
        w.WriteStartObject()

        match t with
        | Scoring.LinearTransform(m, o) ->
            w.WriteString("kind", "linear")
            w.WriteNumber("multiplier", m)
            w.WriteNumber("offset", o)
        | Scoring.LinearNormalize(a, b, c, d) ->
            w.WriteString("kind", "normalize")
            w.WriteNumber("fromMinimum", a)
            w.WriteNumber("fromMaximum", b)
            w.WriteNumber("toMinimum", c)
            w.WriteNumber("toMaximum", d)
        | Scoring.Clamp(lo, hi) ->
            w.WriteString("kind", "clamp")
            w.WriteNumber("minimum", lo)
            w.WriteNumber("maximum", hi)
        | Scoring.Floor -> w.WriteString("kind", "floor")
        | Scoring.Ceiling -> w.WriteString("kind", "ceiling")

        w.WriteEndObject()

    w.WriteEndArray()

    w.WriteStartObject "missing"
    w.WriteNumber("minimumObservations", scorer.Missing.MinimumObservations)

    match scorer.Missing.Special with
    | Scoring.Exclude -> w.WriteString("special", "exclude")
    | Scoring.Substitute v ->
        w.WriteString("special", "substitute")
        w.WriteNumber("value", v)

    w.WriteEndObject()
    w.WriteNumber("decimals", scorer.Decimals)
    w.WriteString("rounding", "half-away-from-zero")
    w.WriteEndObject()

let private writeQuestion (w: Utf8JsonWriter) (q: Question) =
    w.WriteStartObject()
    w.WriteString("id", q.Id)
    w.WriteString("prompt", q.Prompt)
    optionalString w "help" q.HelpText
    PrimitiveCanonical.writeAnswer w q.Answer
    w.WriteStartObject "selector"
    w.WriteString("preset", presetName q.Selector.Preset)
    PrimitiveCanonical.writePresetDetail w q.Selector.Preset
    stringList w "labels" q.Selector.Labels
    w.WriteEndObject()
    stringList w "special" (q.SpecialStates |> List.map specialName)
    w.WriteBoolean("required", q.Required)
    stringList w "tags" q.Tags
    w.WriteEndObject()

let private writeSection (w: Utf8JsonWriter) (s: Section) =
    w.WriteStartObject()
    w.WriteString("id", s.Id)
    w.WriteString("title", s.Title)
    optionalString w "description" s.Description
    w.WriteBoolean("required", s.Required)
    w.WriteStartObject "presentation"
    s.Presentation.ItemsPerPage |> Option.iter (fun n -> w.WriteNumber("itemsPerPage", n))
    w.WriteBoolean("startOnNewPage", s.Presentation.StartOnNewPage)
    stringList w "pageBreaksBefore" s.Presentation.PageBreaksBefore
    w.WriteEndObject()

    s.Scoring
    |> Option.iter (fun scoring ->
        w.WritePropertyName "scoring"
        w.WriteStartObject()
        w.WritePropertyName "scorer"
        writeScorer w scoring.Scorer
        stringList w "questions" scoring.Questions
        w.WriteEndObject())

    w.WriteStartArray "questions"
    s.Questions |> List.iter (writeQuestion w)
    w.WriteEndArray()
    w.WriteEndObject()

/// The canonical UTF-8 bytes of a template at an identity.
let bytes (surveyId: string) (version: string) (content: Content) : byte[] =
    let buffer = ArrayBufferWriter<byte>()

    (
        use w = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        w.WriteStartObject()
        w.WriteString("form", Form)
        w.WriteString("surveyId", surveyId)
        w.WriteString("version", version)

        w.WriteStartObject "compatibility"
        w.WriteNumber("schema", content.Compatibility.SchemaVersion)
        w.WriteNumber("minimumEngine", content.Compatibility.MinimumEngineVersion)
        w.WriteNumber("responseEncoding", content.Compatibility.ResponseEncodingVersion)
        stringList w "capabilities" (content.Compatibility.Capabilities |> List.map capabilityName |> List.sort)
        w.WriteEndObject()

        w.WriteStartObject "metadata"
        w.WriteString("title", content.Metadata.Title)
        optionalString w "shortTitle" content.Metadata.ShortTitle
        optionalString w "description" content.Metadata.Description
        optionalString w "instructions" content.Metadata.Instructions
        stringList w "tags" content.Metadata.Tags
        w.WriteEndObject()

        let p = content.Presentation
        w.WriteStartObject "presentation"
        p.ItemsPerPage |> Option.iter (fun n -> w.WriteNumber("itemsPerPage", n))
        w.WriteBoolean("showQuestionNumbers", p.ShowQuestionNumbers)
        w.WriteString("progress", progressName p.Progress)
        w.WriteBoolean("allowBackNavigation", p.AllowBackNavigation)
        w.WriteBoolean("reviewBeforeSubmit", p.ReviewBeforeSubmit)
        w.WriteBoolean("sectionStartsOnNewPage", p.SectionStartsOnNewPage)
        w.WriteString("randomization", "none")

        // Written only when not the default (extension rule).
        if p.Revisit <> fullRevisit then
            w.WriteStartObject "revisit"
            w.WriteBoolean("lockPreviousQuestions", (p.Revisit.Questions = LockPreviousQuestionsAfterAdvance))
            w.WriteBoolean("lockPreviousSections", (p.Revisit.Sections = LockPreviousSectionsAfterExit))
            w.WriteEndObject()
        w.WriteEndObject()

        let r = content.Runtime
        w.WriteStartObject "runtime"
        w.WriteBoolean("allowResume", r.AllowResume)
        w.WriteBoolean("allowChangesAfterCompletion", r.AllowChangesAfterCompletion)
        w.WriteBoolean("showResults", r.ShowResults)
        w.WriteString("resultMode", resultModeName r.ResultMode)
        w.WriteEndObject()

        w.WriteStartArray "sections"
        content.Sections |> List.iter (writeSection w)
        w.WriteEndArray()

        // Written only when present (extension rule), so a template without
        // rules keeps the hash it had before rules existed.
        if content.Rules <> noRules then
            w.WritePropertyName "rules"
            RuleCanonical.write w content.Rules

        if content.Results <> ResultModel.noResults then
            w.WritePropertyName "results"
            ResultCanonical.write w content.Results

        w.WriteEndObject()
    )

    buffer.WrittenSpan.ToArray()

let private hex (data: byte[]) = "sha256:" + Convert.ToHexString(data).ToLowerInvariant()

let hashBytes surveyId version content = SHA256.HashData(bytes surveyId version content: byte[])

/// `sha256:<lowercase hex>`: the TemplateHash.
let templateHash surveyId version content = hex (hashBytes surveyId version content)

/// The compact reference a URL carries: the first `ReferenceLength` bytes.
let reference surveyId version content : byte[] =
    (hashBytes surveyId version content)[.. ReferenceLength - 1]

/// A fingerprint of the encoding layout alone (AUT-004 §43): two templates
/// with the same fingerprint encode answers identically.
let layoutFingerprint (content: Content) =
    let text =
        layout content
        |> List.map (fun slot -> $"{slot.QuestionId}:{slot.States}:{slot.Bits}")
        |> String.concat "\n"

    hex (SHA256.HashData(Text.Encoding.UTF8.GetBytes($"encoding/{EncodingVersion}\n{text}")))
