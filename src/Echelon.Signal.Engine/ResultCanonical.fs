/// The canonical form of a template's result definitions (overall scoring,
/// interpretations, display), written inside the template's canonical form
/// only when the template declares any (extension rule).
module Echelon.Signal.Engine.ResultCanonical

open System.Text.Json
open Echelon.Signal.Engine.ResultModel

let private directionName =
    function
    | HigherIsBetter -> "higher-is-better"
    | HigherIsWorse -> "higher-is-worse"
    | Neutral -> "neutral"

let private displayName =
    function
    | Hidden -> "hidden"
    | AfterEachResponse -> "after-each-response"
    | AfterPageAdvance -> "after-page-advance"
    | AfterSectionComplete -> "after-section-complete"
    | FinalOnly -> "final-only"

let rec private expression (w: Utf8JsonWriter) (e: ScoreExpr) =
    w.WriteStartObject()

    let operands (name: string) (xs: ScoreExpr list) =
        w.WriteStartArray name
        xs |> List.iter (expression w)
        w.WriteEndArray()

    match e with
    | Num v -> w.WriteNumber("num", v)
    | Item id -> w.WriteString("item", id)
    | Section id -> w.WriteString("section", id)
    | FactNumber id -> w.WriteString("fact", id)
    | Add xs -> operands "add" xs
    | Multiply xs -> operands "multiply" xs
    | Subtract(a, b) -> operands "subtract" [ a; b ]
    | Divide(a, b) -> operands "divide" [ a; b ]
    | Sum xs -> operands "sum" xs
    | Mean xs -> operands "mean" xs
    | Min xs -> operands "min" xs
    | Max xs -> operands "max" xs
    | Bound(x, lo, hi) ->
        operands "bound" [ x ]
        w.WriteNumber("minimum", lo)
        w.WriteNumber("maximum", hi)
    | When(c, a, b) ->
        w.WritePropertyName "when"
        RuleCanonical.condition w c
        operands "then" [ a ]
        operands "otherwise" [ b ]

    w.WriteEndObject()

let private compositeMethod (w: Utf8JsonWriter) (m: CompositeMethod) =
    match m with
    | BalancedMean -> w.WriteString("method", "balanced-mean")
    | WeightedMean ws ->
        w.WriteString("method", "weighted-mean")
        w.WriteStartArray "weights"

        for id, weight in ws do
            w.WriteStartArray()
            w.WriteStringValue id
            w.WriteNumberValue weight
            w.WriteEndArray()

        w.WriteEndArray()
    | WeakestLink -> w.WriteString("method", "weakest-link")
    | GeometricMean -> w.WriteString("method", "geometric-mean")
    | HarmonicMean -> w.WriteString("method", "harmonic-mean")
    | MinimumDomain(t, cap) ->
        w.WriteString("method", "minimum-domain")
        w.WriteNumber("threshold", t)
        w.WriteNumber("cap", cap)

let write (w: Utf8JsonWriter) (results: Results) =
    w.WriteStartObject()

    results.Overall
    |> Option.iter (fun overall ->
        w.WriteStartObject "overall"

        match overall with
        | Composite spec ->
            w.WriteString("kind", "composite")
            compositeMethod w spec.Method

            if not spec.Sections.IsEmpty then
                w.WriteStartArray "sections"
                spec.Sections |> List.iter w.WriteStringValue
                w.WriteEndArray()

            w.WriteNumber("minimumScoredSections", spec.MinimumScoredSections)
            w.WriteString("direction", directionName spec.Direction)
            w.WriteNumber("decimals", spec.Decimals)
        | Custom custom ->
            w.WriteString("kind", "expression")
            w.WriteNumber("language", custom.LanguageVersion)
            w.WritePropertyName "expression"
            expression w custom.Expression
            w.WriteNumber("decimals", custom.Decimals)

        w.WriteEndObject())

    if not results.Interpretations.IsEmpty then
        w.WriteStartArray "interpretations"

        for i in results.Interpretations do
            w.WriteStartObject()
            w.WriteString("id", i.Id)

            match i.Target with
            | OverallResult -> w.WriteString("target", "overall")
            | SectionResultOf s -> w.WriteString("section", s)

            match i.Kind with
            | Bands bands ->
                w.WriteStartArray "bands"

                for b in bands do
                    w.WriteStartObject()
                    w.WriteString("label", b.Label)
                    w.WriteNumber("from", b.From)
                    w.WriteNumber("to", b.To)
                    w.WriteEndObject()

                w.WriteEndArray()
            | PassFail(threshold, direction) ->
                w.WriteNumber("passAt", threshold)
                w.WriteString("direction", directionName direction)
            | Stages(baseline, stages) ->
                w.WriteString("baseline", baseline)
                w.WriteStartArray "stages"

                for s in stages do
                    w.WriteStartObject()
                    w.WriteString("label", s.Label)
                    w.WritePropertyName "requires"
                    RuleCanonical.condition w s.Requires
                    w.WriteEndObject()

                w.WriteEndArray()

            w.WriteEndObject()

        w.WriteEndArray()

    if not results.ItemKeys.IsEmpty then
        w.WriteStartArray "itemKeys"

        for k in results.ItemKeys do
            w.WriteStartObject()
            w.WriteString("question", k.Question)
            ItemKeyCanonical.write w k.Key
            w.WriteEndObject()

        w.WriteEndArray()

    let d = results.Display
    w.WriteStartObject "display"
    w.WriteString("overall", displayName d.Overall)
    w.WriteString("sections", displayName d.Sections)
    w.WriteString("interpretations", displayName d.Interpretations)
    w.WriteString("explanation", (match d.Explanation with AuthorsOnly -> "authors" | RespondentsToo -> "respondents"))
    w.WriteEndObject()
    w.WriteEndObject()
