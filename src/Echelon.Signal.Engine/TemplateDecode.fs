/// Reading a template's canonical form back (WI-0057), part 2: rules,
/// results, item keys, questions, sections and the whole template, so a
/// published template stored as its canonical bytes is read back unchanged.
/// `decode (TemplateCanonical.bytes s v c)` re-encodes to the same bytes and
/// the same hash.
///
/// Pure and total.
module Echelon.Signal.Engine.TemplateDecode

open System
open System.Text.Json
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Keyed
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.TemplateDecodeCore

// ---- Rules (RuleCanonical.write) -------------------------------------------------------------

let private priorities = [ Informational, "informational"; Suggested, "suggested"; Important, "important"; Critical, "critical" ]

let private fact (v: JsonElement) : Decoded<Fact> =
    let expr =
        byMember
            "fact"
            [ "boolean", (fun v -> field "boolean" v |> Result.bind condition |> Result.map BooleanFact)
              "number", (fun v -> field "number" v |> Result.bind numberExpr |> Result.map NumberFactOf)
              "category",
              (fun v ->
                  let case (c: JsonElement) = map2 (fun w l -> w, l) (field "when" c |> Result.bind condition) (text "label" c)
                  map2 (fun cases otherwise -> CategoryFact(cases, otherwise)) (array case "category" v) (optional text "otherwise" v)) ]
            v

    map2 (fun id e -> { Id = id; Expr = e }) (text "id" v) expr

let private flowAction (v: JsonElement) : Decoded<FlowAction> =
    byMember
        "flow action"
        [ "showQuestion", text "showQuestion" >> Result.map ShowQuestion
          "hideQuestion", text "hideQuestion" >> Result.map HideQuestion
          "showSection", text "showSection" >> Result.map ShowSection
          "hideSection", text "hideSection" >> Result.map HideSection
          "toQuestion", (fun v -> map2 (fun a b -> SkipToQuestion(a, b)) (text "skipFrom" v) (text "toQuestion" v))
          "toSection", (fun v -> map2 (fun a b -> SkipToSection(a, b)) (text "skipFrom" v) (text "toSection" v))
          "terminateAfter", (fun v -> map2 (fun a r -> Terminate(a, r)) (text "terminateAfter" v) (text "reason" v)) ]
        v

let private validationRule (v: JsonElement) : Decoded<ValidationRule> =
    let check =
        byMember
            "validation check"
            [ "requiredWhen", (fun v -> map2 (fun q c -> RequiredWhen(q, c)) (text "requiredWhen" v) (field "when" v |> Result.bind condition))
              "prohibited", (fun v -> field "prohibited" v |> Result.bind condition |> Result.map Prohibited)
              "range", (fun v -> map3 (fun q a b -> AllowedRange(q, a, b)) (text "range" v) (number "minimum" v) (number "maximum" v))
              "answeredBetween", (fun v -> map3 (fun ids a b -> AnsweredBetween(ids, a, b)) (strings "answeredBetween" v) (integer "minimum" v) (integer "maximum" v))
              "distinct", (fun v -> strings "distinct" v |> Result.map DistinctAnswers) ]
            v

    map3 (fun id c m -> { Id = id; Check = c; Message = m }) (text "id" v) check (text "message" v)

let private recommendation (v: JsonElement) : Decoded<RecommendationRule> =
    let kind =
        text "kind" v
        |> Result.bind (function
            | "recommendation" -> Ok Recommendation
            | "action" -> flag "required" v |> Result.map Action
            | other -> Error $"'{other}' is not a known recommendation kind")

    map4
        (fun (id, w) (k, p) (category, title, description) (section, question) ->
            { Id = id
              When = w
              Kind = k
              Priority = p
              Category = category
              Title = title
              Description = description
              RelatedSection = section
              RelatedQuestion = question })
        (map2 (fun i w -> i, w) (text "id" v) (field "when" v |> Result.bind condition))
        (map2 (fun k p -> k, p) kind (spelled priorities "priority" "priority" v))
        (map3 (fun a b c -> a, b, c) (text "category" v) (text "title" v) (text "description" v))
        (map2 (fun s q -> s, q) (optional text "relatedSection" v) (optional text "relatedQuestion" v))

let private rules (v: JsonElement) : Decoded<RuleSet> =
    let completion =
        field "completion" v
        |> Result.bind (fun c ->
            map4
                (fun percent special sections cond -> { MinimumAnsweredPercent = percent; SpecialCountsAsAnswered = special; RequiredSections = sections; Condition = cond })
                (optional number "minimumAnsweredPercent" c)
                (flag "specialCountsAsAnswered" c)
                (strings "requiredSections" c)
                (if has "condition" c then field "condition" c |> Result.bind condition |> Result.map Some else Ok None))

    let flowRule (r: JsonElement) =
        map3 (fun id w t -> { Id = id; When = w; Then = t }) (text "id" r) (field "when" r |> Result.bind condition) (field "then" r |> Result.bind flowAction)

    map4
        (fun (facts, flow) validation completion recommendations ->
            { Facts = facts; Flow = flow; Validation = validation; Completion = completion; Recommendations = recommendations })
        (map2 (fun a b -> a, b) (array fact "facts" v) (array flowRule "flow" v))
        (array validationRule "validation" v)
        completion
        (array recommendation "recommendations" v)

// ---- Results and item keys (ResultCanonical.write, ItemKeyCanonical.write) -----------------------

let private directions = [ HigherIsBetter, "higher-is-better"; HigherIsWorse, "higher-is-worse"; Neutral, "neutral" ]

let private displays =
    [ DisplayPolicy.Hidden, "hidden"; AfterEachResponse, "after-each-response"; AfterPageAdvance, "after-page-advance"; AfterSectionComplete, "after-section-complete"; FinalOnly, "final-only" ]

let rec private expression (v: JsonElement) : Decoded<ScoreExpr> =
    let operands name = array expression name v
    let two name f = operands name |> Result.bind (function [ a; b ] -> Ok(f a b) | _ -> Error $"'{name}' does not hold two operands")
    let one name = operands name |> Result.bind (function [ a ] -> Ok a | _ -> Error $"'{name}' does not hold one operand")

    byMember
        "score expression"
        [ "num", number "num" >> Result.map Num
          "item", text "item" >> Result.map Item
          "section", text "section" >> Result.map ScoreExpr.Section
          "fact", text "fact" >> Result.map FactNumber
          "add", (fun _ -> operands "add" |> Result.map Add)
          "multiply", (fun _ -> operands "multiply" |> Result.map Multiply)
          "subtract", (fun _ -> two "subtract" (fun a b -> Subtract(a, b)))
          "divide", (fun _ -> two "divide" (fun a b -> Divide(a, b)))
          "sum", (fun _ -> operands "sum" |> Result.map Sum)
          "mean", (fun _ -> operands "mean" |> Result.map ScoreExpr.Mean)
          "min", (fun _ -> operands "min" |> Result.map Min)
          "max", (fun _ -> operands "max" |> Result.map Max)
          "bound", (fun v -> map3 (fun x lo hi -> Bound(x, lo, hi)) (one "bound") (number "minimum" v) (number "maximum" v))
          "when", (fun v -> map3 (fun c a b -> When(c, a, b)) (field "when" v |> Result.bind condition) (one "then") (one "otherwise")) ]
        v

let private compositeMethod (v: JsonElement) : Decoded<CompositeMethod> =
    text "method" v
    |> Result.bind (function
        | "balanced-mean" -> Ok BalancedMean
        | "weighted-mean" -> pairs textValue numberValue "weights" v |> Result.map CompositeMethod.WeightedMean
        | "weakest-link" -> Ok WeakestLink
        | "geometric-mean" -> Ok GeometricMean
        | "harmonic-mean" -> Ok HarmonicMean
        | "minimum-domain" -> map2 (fun t c -> MinimumDomain(t, c)) (number "threshold" v) (number "cap" v)
        | other -> Error $"'{other}' is not a known composite method")

let private overall (v: JsonElement) : Decoded<OverallScoring> =
    text "kind" v
    |> Result.bind (function
        | "composite" ->
            map4
                (fun m sections (minimum, direction) decimals -> Composite { Method = m; Sections = sections; MinimumScoredSections = minimum; Direction = direction; Decimals = decimals })
                (compositeMethod v)
                (strings "sections" v)
                (map2 (fun a b -> a, b) (integer "minimumScoredSections" v) (spelled directions "direction" "direction" v))
                (integer "decimals" v)
        | "expression" ->
            map3 (fun l e d -> Custom { LanguageVersion = l; Expression = e; Decimals = d }) (integer "language" v) (field "expression" v |> Result.bind expression) (integer "decimals" v)
        | other -> Error $"'{other}' is not a known overall scoring")

let private interpretation (v: JsonElement) : Decoded<Interpretation> =
    let target = if has "target" v then Ok OverallResult else text "section" v |> Result.map SectionResultOf
    let band (b: JsonElement) = map3 (fun l f t -> ({ Label = l; From = f; To = t }: Scoring.Band)) (text "label" b) (number "from" b) (number "to" b)
    let stage (s: JsonElement) = map2 (fun l r -> { Label = l; Requires = r }) (text "label" s) (field "requires" s |> Result.bind condition)

    let kind =
        byMember
            "interpretation"
            [ "bands", array band "bands" >> Result.map Bands
              "passAt", (fun v -> map2 (fun t d -> PassFail(t, d)) (number "passAt" v) (spelled directions "direction" "direction" v))
              "stages", (fun v -> map2 (fun b s -> Stages(b, s)) (text "baseline" v) (array stage "stages" v)) ]
            v

    map3 (fun id t k -> { Id = id; Target = t; Kind = k }) (text "id" v) target kind

let private ids name v = strings name v |> Result.map Set.ofList
let private weights name v = pairs textValue numberValue name v |> Result.map Map.ofList

let private keyKind (v: JsonElement) : Decoded<KeyKind> =
    let n name = number name v

    text "kind" v
    |> Result.bind (function
        | "single" ->
            let correctIds =
                fieldOfKind "correct" JsonValueKind.Array v
                |> Result.bind (fun list -> list.EnumerateArray() |> List.ofSeq |> traverse textValue)
                |> Result.map Set.ofList

            let correctPoints = fieldOfKind "correct" JsonValueKind.Number v |> Result.map (fun p -> p.GetDouble())
            map4 (fun c a b d -> SingleKeyed { Correct = c; PointsCorrect = a; PointsIncorrect = b; PointsBlank = d }) correctIds correctPoints (n "incorrect") (n "blank")
        | "exact-set" -> map2 (fun c p -> MultiKeyed(ExactSetMatch(c, p))) (ids "correct" v) (n "points")
        | "any-correct" -> map2 (fun c p -> MultiKeyed(AnyCorrect(c, p))) (ids "acceptable" v) (n "points")
        | "all-required" ->
            map3 (fun c p x -> MultiKeyed(AllRequired(c, p, x))) (ids "required" v) (n "points") (spelled [ IgnoreExtra, "ignore"; ExtraLosesCredit, "loses-credit" ] "extra policy" "extra" v)
        | "none-forbidden" -> map2 (fun c p -> MultiKeyed(NoneForbidden(c, p))) (ids "forbidden" v) (n "points")
        | "partial-credit" ->
            map4
                (fun c (a, b) f cap -> MultiKeyed(PartialCredit { Correct = c; PerCorrect = a; PerIncorrect = b; Floor = f; Cap = cap }))
                (ids "correct" v)
                (map2 (fun a b -> a, b) (n "perCorrect") (n "perIncorrect"))
                (optional number "floor" v)
                (optional number "cap" v)
        | "option-weighted" -> weights "weights" v |> Result.map (OptionWeighted >> MultiKeyed)
        | "count-selected" -> Ok(MultiKeyed CountSelected)
        | "rank" ->
            let rankMethod =
                text "method" v
                |> Result.bind (function
                    | "rank-points" -> Ok RankPoints
                    | "borda" -> Ok BordaCount
                    | "inverse-rank" -> Ok InverseRank
                    | "top-k" -> integer "k" v |> Result.map (fun k -> TopKRankCredit(k, false))
                    | "top-k-positional" -> integer "k" v |> Result.map (fun k -> TopKRankCredit(k, true))
                    | "position-weighted" -> numbers "points" v |> Result.map PositionWeighted
                    | other -> Error $"'{other}' is not a known rank method")

            map2 (fun item m -> RankKeyed(item, m)) (text "item" v) rankMethod
        | "allocation" ->
            text "method" v
            |> Result.bind (function
                | "direct" -> text "item" v |> Result.map (DirectAllocation >> AllocationKeyed)
                | "normalized" -> text "item" v |> Result.map (NormalizedAllocation >> AllocationKeyed)
                | "share-percent" -> text "item" v |> Result.map (AllocationSharePercent >> AllocationKeyed)
                | "weighted" -> weights "weights" v |> Result.map (WeightedAllocation >> AllocationKeyed)
                | "distance-from-target" -> weights "targets" v |> Result.map (DistanceFromTargetAllocation >> AllocationKeyed)
                | other -> Error $"'{other}' is not a known allocation method")
        | "best-worst" ->
            map2 (fun item m -> BestWorstKeyed(item, m)) (text "item" v) (spelled [ BestOnly, "best"; WorstOnly, "worst"; BestMinusWorstOnly, "best-minus-worst" ] "best-worst measure" "measure" v)
        | "range" -> spelled [ RangeWidth, "width"; RangeMidpoint, "midpoint"; RangeLow, "low"; RangeHigh, "high" ] "range measure" "measure" v |> Result.map RangeKeyed
        | other -> Error $"'{other}' is not a known item key")

let private results (v: JsonElement) : Decoded<Results> =
    let display =
        field "display" v
        |> Result.bind (fun d ->
            map4
                (fun o s i e -> { Overall = o; Sections = s; Interpretations = i; Explanation = e })
                (spelled displays "display policy" "overall" d)
                (spelled displays "display policy" "sections" d)
                (spelled displays "display policy" "interpretations" d)
                (spelled [ AuthorsOnly, "authors"; RespondentsToo, "respondents" ] "explanation visibility" "explanation" d))

    let itemKey (k: JsonElement) = map2 (fun q key -> { Question = q; Key = key }) (text "question" k) (field "key" k |> Result.bind keyKind)

    map4
        (fun o i d k -> { Overall = o; Interpretations = i; Display = d; ItemKeys = k })
        (if has "overall" v then field "overall" v |> Result.bind overall |> Result.map Some else Ok None)
        (array interpretation "interpretations" v)
        display
        (array itemKey "itemKeys" v)

// ---- Questions, sections and the template (TemplateCanonical.bytes) ------------------------------

let private capabilities =
    [ UsesBranching; UsesDerivedFacts; UsesCustomScoring; UsesRanking; UsesAllocation; UsesGroupScoring; UsesConditionalSections; UsesRecommendations; UsesAdvancedValidation; UsesLocalization ]
    |> List.map (fun c -> c, TemplateCanonical.capabilityName c)

let private question (v: JsonElement) : Decoded<Question> =
    map4
        (fun (id, prompt, help) answer selector (special, required, tags) ->
            { Id = id; Prompt = prompt; HelpText = help; Answer = answer; Selector = selector; SpecialStates = special; Required = required; Tags = tags })
        (map3 (fun a b c -> a, b, c) (text "id" v) (text "prompt" v) (optional text "help" v))
        (field "answer" v |> Result.bind answerDefinition)
        (field "selector" v |> Result.bind selector)
        (map3 (fun a b c -> a, b, c) (strings "special" v |> Result.bind (traverse (named specialStates "special state"))) (flag "required" v) (strings "tags" v))

let private section (v: JsonElement) : Decoded<Section> =
    let presentation =
        field "presentation" v
        |> Result.bind (fun p ->
            map3 (fun items start breaks -> { ItemsPerPage = items; StartOnNewPage = start; PageBreaksBefore = breaks }) (optional integer "itemsPerPage" p) (flag "startOnNewPage" p) (strings "pageBreaksBefore" p))

    let scoring =
        if has "scoring" v then
            field "scoring" v
            |> Result.bind (fun s -> map2 (fun sc qs -> Some { Scorer = sc; Questions = qs }) (field "scorer" s |> Result.bind scorer) (strings "questions" s))
        else
            Ok None

    map4
        (fun (id, title, description, required) questions presentation scoring ->
            { Id = id; Title = title; Description = description; Required = required; Questions = questions; Presentation = presentation; Scoring = scoring })
        (map4 (fun a b c d -> a, b, c, d) (text "id" v) (text "title" v) (optional text "description" v) (flag "required" v))
        (array question "questions" v)
        presentation
        scoring

let private presentation (p: JsonElement) : Decoded<Presentation> =
    let revisit =
        if has "revisit" p then
            field "revisit" p
            |> Result.bind (fun r ->
                map2
                    (fun q s ->
                        { Questions = (if q then LockPreviousQuestionsAfterAdvance else AllowPreviousQuestions)
                          Sections = (if s then LockPreviousSectionsAfterExit else AllowPreviousSections) })
                    (flag "lockPreviousQuestions" r)
                    (flag "lockPreviousSections" r))
        else
            Ok fullRevisit

    map4
        (fun (items, numbers) (progress, back) (review, newPage) revisit ->
            { ItemsPerPage = items; ShowQuestionNumbers = numbers; Progress = progress; AllowBackNavigation = back; ReviewBeforeSubmit = review; SectionStartsOnNewPage = newPage; Revisit = revisit })
        (map2 (fun a b -> a, b) (optional integer "itemsPerPage" p) (flag "showQuestionNumbers" p))
        (map2 (fun a b -> a, b) (spelled [ NoProgress, "none"; QuestionProgress, "question"; PageProgress, "page"; SectionProgress, "section" ] "progress mode" "progress" p) (flag "allowBackNavigation" p))
        (map2 (fun a b -> a, b) (flag "reviewBeforeSubmit" p) (flag "sectionStartsOnNewPage" p))
        revisit

/// A canonical template document: its survey identifier, version and content.
type DecodedTemplate = { SurveyId: string; Version: string; Content: Content }

let private content (v: JsonElement) : Decoded<Content> =
    let compatibility =
        field "compatibility" v
        |> Result.bind (fun c ->
            map4
                (fun schema engine encoding caps -> { SchemaVersion = schema; MinimumEngineVersion = engine; ResponseEncodingVersion = encoding; Capabilities = caps })
                (integer "schema" c)
                (integer "minimumEngine" c)
                (integer "responseEncoding" c)
                (strings "capabilities" c |> Result.bind (traverse (named capabilities "capability"))))

    let metadata =
        field "metadata" v
        |> Result.bind (fun m ->
            map4
                (fun title (short, description) instructions tags -> { Title = title; ShortTitle = short; Description = description; Instructions = instructions; Tags = tags })
                (text "title" m)
                (map2 (fun a b -> a, b) (optional text "shortTitle" m) (optional text "description" m))
                (optional text "instructions" m)
                (strings "tags" m))

    let runtime =
        field "runtime" v
        |> Result.bind (fun r ->
            map4
                (fun resume changes show mode -> { AllowResume = resume; AllowChangesAfterCompletion = changes; ShowResults = show; ResultMode = mode })
                (flag "allowResume" r)
                (flag "allowChangesAfterCompletion" r)
                (flag "showResults" r)
                (spelled [ Immediate, "immediate"; ResultMode.Hidden, "hidden"; Deferred, "deferred"; External, "external" ] "result mode" "resultMode" r))

    map4
        (fun (compatibility, metadata) (presentation, runtime) sections (rules, results) ->
            { Metadata = metadata; Compatibility = compatibility; Presentation = presentation; Runtime = runtime; Sections = sections; Rules = rules; Results = results })
        (map2 (fun a b -> a, b) compatibility metadata)
        (map2 (fun a b -> a, b) (field "presentation" v |> Result.bind presentation) runtime)
        (array section "sections" v)
        (map2
            (fun a b -> a, b)
            (if has "rules" v then field "rules" v |> Result.bind rules else Ok noRules)
            (if has "results" v then field "results" v |> Result.bind results else Ok noResults))

/// Reads canonical template bytes (`TemplateCanonical.bytes`).
let decode (bytes: byte[]) : Decoded<DecodedTemplate> =
    try
        use document = JsonDocument.Parse(ReadOnlyMemory bytes)
        let root = document.RootElement

        match text "form" root with
        | Ok form when form = TemplateCanonical.Form ->
            map3 (fun s v c -> { SurveyId = s; Version = v; Content = c }) (text "surveyId" root) (text "version" root) (content root)
            // Only the canonical form is read: a member this version does not
            // write (a "name" or "email" field among them, ARX-009) or any
            // other spelling is refused rather than silently dropped.
            |> Result.bind (fun decoded ->
                if TemplateCanonical.bytes decoded.SurveyId decoded.Version decoded.Content = bytes then Ok decoded
                else Error "not the canonical form of the template it decodes to (an unknown member or another spelling)")
        | Ok other -> Error $"'{other}' is not the template form {TemplateCanonical.Form}"
        | Error e -> Error e
    with :? JsonException as error ->
        Error $"not JSON: {error.Message}"
