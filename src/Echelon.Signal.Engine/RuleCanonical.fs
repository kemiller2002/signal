/// The canonical form of a template's rules: tagged JSON in declaration
/// order, written inside the template's canonical form (`TemplateCanonical`)
/// only when the template has rules.
module Echelon.Signal.Engine.RuleCanonical

open System.Text.Json
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel

let specialName =
    function
    | DontKnow -> "dont-know"
    | NotObserved -> "not-observed"
    | NotApplicable -> "not-applicable"
    | Declined -> "declined"

let private comparisonName =
    function
    | Equal -> "eq"
    | NotEqual -> "ne"
    | Greater -> "gt"
    | GreaterOrEqual -> "ge"
    | Less -> "lt"
    | LessOrEqual -> "le"

let private priorityName =
    function
    | Informational -> "informational"
    | Suggested -> "suggested"
    | Important -> "important"
    | Critical -> "critical"

let private value (w: Utf8JsonWriter) (v: AnswerValue) =
    w.WriteStartObject()

    match v with
    | Flag b -> w.WriteBoolean("flag", b)
    | Point p -> w.WriteNumber("point", p)
    | Choice id -> w.WriteString("choice", id)
    | Choices ids ->
        w.WriteStartArray "choices"
        ids |> Set.iter w.WriteStringValue
        w.WriteEndArray()
    | Tick t -> w.WriteNumber("tick", t)
    | TickRange(lo, hi) ->
        w.WriteNumber("low", lo)
        w.WriteNumber("high", hi)
    | Order ids ->
        w.WriteStartArray "order"
        ids |> List.iter w.WriteStringValue
        w.WriteEndArray()
    | Allocated steps ->
        w.WriteStartArray "allocated"

        for KeyValue(id, n) in steps do
            w.WriteStartArray()
            w.WriteStringValue id
            w.WriteNumberValue n
            w.WriteEndArray()

        w.WriteEndArray()
    | BestWorstPick(b, worst) ->
        w.WriteString("best", b)
        w.WriteString("worst", worst)

    w.WriteEndObject()

let private number (w: Utf8JsonWriter) (n: NumberExpr) =
    w.WriteStartObject()

    match n with
    | Constant v -> w.WriteNumber("constant", v)
    | AnswerNumber id -> w.WriteString("answer", id)
    | SectionScore id -> w.WriteString("sectionScore", id)
    | NumberFact id -> w.WriteString("fact", id)

    w.WriteEndObject()

let rec condition (w: Utf8JsonWriter) (c: Condition) =
    w.WriteStartObject()

    let list (name: string) (cs: Condition list) =
        w.WriteStartArray name
        cs |> List.iter (condition w)
        w.WriteEndArray()

    match c with
    | Always -> w.WriteBoolean("always", true)
    | Answered id -> w.WriteString("answered", id)
    | AnswerIs(id, v) ->
        w.WriteString("answerIs", id)
        w.WritePropertyName "value"
        value w v
    | AnswerIn(id, vs) ->
        w.WriteString("answerIn", id)
        w.WriteStartArray "values"
        vs |> List.iter (value w)
        w.WriteEndArray()
    | IsSpecial(id, s) ->
        w.WriteString("isSpecial", id)
        w.WriteString("state", specialName s)
    | Compare(a, op, b) ->
        w.WriteString("compare", comparisonName op)
        w.WritePropertyName "left"
        number w a
        w.WritePropertyName "right"
        number w b
    | FactTrue id -> w.WriteString("factTrue", id)
    | CategoryIs(id, label) ->
        w.WriteString("categoryIs", id)
        w.WriteString("category", label)
    | All cs -> list "all" cs
    | Any cs -> list "any" cs
    | Not inner ->
        w.WritePropertyName "not"
        condition w inner

    w.WriteEndObject()

let private action (w: Utf8JsonWriter) (a: FlowAction) =
    w.WriteStartObject()

    match a with
    | ShowQuestion q -> w.WriteString("showQuestion", q)
    | HideQuestion q -> w.WriteString("hideQuestion", q)
    | ShowSection s -> w.WriteString("showSection", s)
    | HideSection s -> w.WriteString("hideSection", s)
    | SkipToQuestion(a, b) ->
        w.WriteString("skipFrom", a)
        w.WriteString("toQuestion", b)
    | SkipToSection(a, s) ->
        w.WriteString("skipFrom", a)
        w.WriteString("toSection", s)
    | Terminate(a, reason) ->
        w.WriteString("terminateAfter", a)
        w.WriteString("reason", reason)

    w.WriteEndObject()

let private array (w: Utf8JsonWriter) (name: string) (items: 'a list) (each: 'a -> unit) =
    if not items.IsEmpty then
        w.WriteStartArray name
        items |> List.iter each
        w.WriteEndArray()

let write (w: Utf8JsonWriter) (rules: RuleSet) =
    w.WriteStartObject()

    array w "facts" rules.Facts (fun f ->
        w.WriteStartObject()
        w.WriteString("id", f.Id)

        match f.Expr with
        | BooleanFact c ->
            w.WritePropertyName "boolean"
            condition w c
        | NumberFactOf n ->
            w.WritePropertyName "number"
            number w n
        | CategoryFact(cases, otherwise) ->
            w.WriteStartArray "category"

            for c, label in cases do
                w.WriteStartObject()
                w.WritePropertyName "when"
                condition w c
                w.WriteString("label", label)
                w.WriteEndObject()

            w.WriteEndArray()
            otherwise |> Option.iter (fun o -> w.WriteString("otherwise", o))

        w.WriteEndObject())

    array w "flow" rules.Flow (fun r ->
        w.WriteStartObject()
        w.WriteString("id", r.Id)
        w.WritePropertyName "when"
        condition w r.When
        w.WritePropertyName "then"
        action w r.Then
        w.WriteEndObject())

    array w "validation" rules.Validation (fun r ->
        w.WriteStartObject()
        w.WriteString("id", r.Id)

        match r.Check with
        | RequiredWhen(q, c) ->
            w.WriteString("requiredWhen", q)
            w.WritePropertyName "when"
            condition w c
        | Prohibited c ->
            w.WritePropertyName "prohibited"
            condition w c
        | AllowedRange(q, lo, hi) ->
            w.WriteString("range", q)
            w.WriteNumber("minimum", lo)
            w.WriteNumber("maximum", hi)
        | AnsweredBetween(ids, lo, hi) ->
            w.WriteStartArray "answeredBetween"
            ids |> List.iter w.WriteStringValue
            w.WriteEndArray()
            w.WriteNumber("minimum", lo)
            w.WriteNumber("maximum", hi)
        | DistinctAnswers ids ->
            w.WriteStartArray "distinct"
            ids |> List.iter w.WriteStringValue
            w.WriteEndArray()

        w.WriteString("message", r.Message)
        w.WriteEndObject())

    let c = rules.Completion
    w.WriteStartObject "completion"
    c.MinimumAnsweredPercent |> Option.iter (fun p -> w.WriteNumber("minimumAnsweredPercent", p))
    w.WriteBoolean("specialCountsAsAnswered", c.SpecialCountsAsAnswered)

    if not c.RequiredSections.IsEmpty then
        w.WriteStartArray "requiredSections"
        c.RequiredSections |> List.iter w.WriteStringValue
        w.WriteEndArray()

    c.Condition
    |> Option.iter (fun cond ->
        w.WritePropertyName "condition"
        condition w cond)

    w.WriteEndObject()

    array w "recommendations" rules.Recommendations (fun r ->
        w.WriteStartObject()
        w.WriteString("id", r.Id)
        w.WritePropertyName "when"
        condition w r.When

        match r.Kind with
        | Recommendation -> w.WriteString("kind", "recommendation")
        | Action required ->
            w.WriteString("kind", "action")
            w.WriteBoolean("required", required)

        w.WriteString("priority", priorityName r.Priority)
        w.WriteString("category", r.Category)
        w.WriteString("title", r.Title)
        w.WriteString("description", r.Description)
        r.RelatedSection |> Option.iter (fun s -> w.WriteString("relatedSection", s))
        r.RelatedQuestion |> Option.iter (fun q -> w.WriteString("relatedQuestion", q))
        w.WriteEndObject())

    w.WriteEndObject()
