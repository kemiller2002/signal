/// Reading a template's canonical form back (WI-0057), part 1: decoding
/// combinators, the canonical spellings read back to values, answer values,
/// conditions, scorers and answer definitions. The inverse of
/// `TemplateCanonical`, `RuleCanonical` and `PrimitiveCanonical`: decoding
/// then re-encoding gives the same bytes, so a stored template keeps its hash.
///
/// Pure and total: a document that is not a canonical template is an error
/// value naming what is wrong, never an exception. Names are inverted from
/// explicit case lists, not reflection, so the engine stays trimmable.
module Echelon.Signal.Engine.TemplateDecodeCore

open System
open System.Text.Json
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors

type Decoded<'a> = Result<'a, string>

// ---- Combinators -----------------------------------------------------------------------------

let private isObject (e: JsonElement) = e.ValueKind = JsonValueKind.Object

let has (name: string) (e: JsonElement) =
    isObject e && fst (e.TryGetProperty name)

let field (name: string) (e: JsonElement) : Decoded<JsonElement> =
    match isObject e, (if isObject e then e.TryGetProperty name else (false, Unchecked.defaultof<_>)) with
    | true, (true, v) -> Ok v
    | _ -> Error $"'{name}' is missing"

let private typed (kind: JsonValueKind) (what: string) (read: JsonElement -> 'a) (name: string) (e: JsonElement) : Decoded<'a> =
    field name e |> Result.bind (fun v -> if v.ValueKind = kind then Ok(read v) else Error $"'{name}' is not {what}")

let text = typed JsonValueKind.String "text" (fun v -> v.GetString() |> string)

let number = typed JsonValueKind.Number "a number" (fun v -> v.GetDouble())

let integer (name: string) (e: JsonElement) : Decoded<int> =
    field name e
    |> Result.bind (fun v ->
        match v.ValueKind, (if v.ValueKind = JsonValueKind.Number then v.TryGetInt32() else (false, 0)) with
        | JsonValueKind.Number, (true, n) -> Ok n
        | _ -> Error $"'{name}' is not a whole number")

let flag (name: string) (e: JsonElement) : Decoded<bool> =
    field name e
    |> Result.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.True -> Ok true
        | JsonValueKind.False -> Ok false
        | _ -> Error $"'{name}' is not true or false")

/// A member that the canonical form writes more than once under one name,
/// told apart by its value kind (a single item key writes "correct" as the
/// option list, then as the points; the hash fixes that spelling).
let fieldOfKind (name: string) (kind: JsonValueKind) (e: JsonElement) : Decoded<JsonElement> =
    if not (isObject e) then
        Error $"'{name}' is missing"
    else
        e.EnumerateObject()
        |> Seq.tryFind (fun p -> p.Name = name && p.Value.ValueKind = kind)
        |> Option.map (fun p -> Ok p.Value)
        |> Option.defaultValue (Error $"'{name}' is missing")

/// An optional member: absent is None.
let optional (read: string -> JsonElement -> Decoded<'a>) (name: string) (e: JsonElement) : Decoded<'a option> =
    if has name e then read name e |> Result.map Some else Ok None

/// A list in order, failing on the first bad item.
let traverse (f: 'a -> Decoded<'b>) (items: 'a list) : Decoded<'b list> =
    List.foldBack (fun item acc -> acc |> Result.bind (fun rest -> f item |> Result.map (fun x -> x :: rest))) items (Ok [])

/// An array member's elements; an absent member is empty (the extension rule).
let elements (name: string) (e: JsonElement) : Decoded<JsonElement list> =
    if not (has name e) then
        Ok []
    else
        field name e
        |> Result.bind (fun v -> if v.ValueKind = JsonValueKind.Array then Ok(v.EnumerateArray() |> List.ofSeq) else Error $"'{name}' is not a list")

let array (read: JsonElement -> Decoded<'a>) (name: string) (e: JsonElement) = elements name e |> Result.bind (traverse read)

let strings = array (fun v -> if v.ValueKind = JsonValueKind.String then Ok(string (v.GetString())) else Error "a list holds a non-text value")

let numbers = array (fun v -> if v.ValueKind = JsonValueKind.Number then Ok(v.GetDouble()) else Error "a list holds a non-number")

/// Pairs written as two-element arrays: [key, value].
let pairs (key: JsonElement -> Decoded<'k>) (value: JsonElement -> Decoded<'v>) =
    array (fun v ->
        match v.ValueKind, (if v.ValueKind = JsonValueKind.Array then v.GetArrayLength() else 0) with
        | JsonValueKind.Array, 2 -> key v[0] |> Result.bind (fun k -> value v[1] |> Result.map (fun x -> k, x))
        | _ -> Error "a pair is not a two-element list")

let textValue (v: JsonElement) = if v.ValueKind = JsonValueKind.String then Ok(string (v.GetString())) else Error "not text"
let numberValue (v: JsonElement) = if v.ValueKind = JsonValueKind.Number then Ok(v.GetDouble()) else Error "not a number"

let intValue (v: JsonElement) =
    match v.ValueKind, (if v.ValueKind = JsonValueKind.Number then v.TryGetInt32() else (false, 0)) with
    | JsonValueKind.Number, (true, n) -> Ok n
    | _ -> Error "not a whole number"

/// The value a canonical spelling names, from an explicit list of cases.
let named (cases: ('a * string) list) (what: string) (spelling: string) : Decoded<'a> =
    match cases |> List.tryFind (fun (_, s) -> s = spelling) with
    | Some(value, _) -> Ok value
    | None -> Error $"'{spelling}' is not a known {what}"

let spelled (cases: ('a * string) list) (what: string) (name: string) (e: JsonElement) =
    text name e |> Result.bind (named cases what)

let map2 f (a: Decoded<'a>) (b: Decoded<'b>) = a |> Result.bind (fun x -> b |> Result.map (f x))
let map3 f a b (c: Decoded<'c>) = map2 (fun x y -> x, y) a b |> Result.bind (fun (x, y) -> c |> Result.map (f x y))
let map4 f a b c (d: Decoded<'d>) = map3 (fun x y z -> x, y, z) a b c |> Result.bind (fun (x, y, z) -> d |> Result.map (f x y z))

/// The first case whose distinguishing member is present decides the shape.
let byMember (what: string) (cases: (string * (JsonElement -> Decoded<'a>)) list) (e: JsonElement) : Decoded<'a> =
    match cases |> List.tryFind (fun (name, _) -> has name e) with
    | Some(_, read) -> read e
    | None -> Error $"not a known {what}"

// ---- Spellings -----------------------------------------------------------------------------

let specialStates = [ DontKnow, "dont-know"; NotObserved, "not-observed"; NotApplicable, "not-applicable"; Declined, "declined" ]

let private semanticKinds =
    [ Agreement; Frequency; Importance; Satisfaction; Confidence; Quality; Difficulty; Effort; Likelihood; Maturity; Severity; Priority; Probability; Familiarity ]

/// Every preset without arguments; semantic differentials carry endpoints.
let presets =
    [ YesNo; Likert3; Likert5; Likert7; Agreement5; Frequency5; Quality5; Confidence5; Satisfaction5; Maturity5; NumericRating; SingleSelect
      ForcedChoice; YesNoNA; YesNoDontKnow; YesNoInProgress; TrueFalse; AgreeDisagree; BinaryToggle; BinaryButtons; ThreeWayChoice; GenericOrdinal
      Likert4; Likert6; Likert10; Likert11; Nps0To10; StarRating; IconRating; Slider; RangeSlider; NumericStepper; RadioList; Dropdown
      SearchableSelect; SegmentedControl; ChoiceButtons; ChoiceCards; ImageChoiceSingle; CheckboxList; MultiSelectDropdown; SearchableMultiSelect
      MultiSelectChips; MultiSelectButtons; MultiSelectCards; ImageChoiceMulti; RankingList; ConstantSum; Pairwise; BestWorstSet; CascadingSelect
      TreeMultiSelect ]
    @ (semanticKinds |> List.map SemanticScale)
    |> List.map (fun p -> p, PrimitiveCanonical.presetName p)

let private comparisons = [ Equal, "eq"; NotEqual, "ne"; Greater, "gt"; GreaterOrEqual, "ge"; Less, "lt"; LessOrEqual, "le" ]

// ---- Answer values, numbers and conditions (RuleCanonical) ------------------------------------

let answerValue (v: JsonElement) : Decoded<AnswerValue> =
    byMember
        "answer value"
        [ "flag", flag "flag" >> Result.map Flag
          "point", integer "point" >> Result.map Point
          "choice", text "choice" >> Result.map Choice
          "choices", strings "choices" >> Result.map (Set.ofList >> Choices)
          "tick", integer "tick" >> Result.map Tick
          "low", (fun v -> map2 (fun a b -> TickRange(a, b)) (integer "low" v) (integer "high" v))
          "order", strings "order" >> Result.map Order
          "allocated", pairs textValue intValue "allocated" >> Result.map (Map.ofList >> Allocated)
          "best", (fun v -> map2 (fun a b -> BestWorstPick(a, b)) (text "best" v) (text "worst" v)) ]
        v

let numberExpr (v: JsonElement) : Decoded<NumberExpr> =
    byMember
        "number"
        [ "constant", number "constant" >> Result.map Constant
          "answer", text "answer" >> Result.map AnswerNumber
          "sectionScore", text "sectionScore" >> Result.map SectionScore
          "fact", text "fact" >> Result.map NumberFact ]
        v

let rec condition (v: JsonElement) : Decoded<Condition> =
    let conditions name = array condition name v

    byMember
        "condition"
        [ "always", (fun _ -> Ok Always)
          "answered", text "answered" >> Result.map Answered
          "answerIs", (fun v -> map2 (fun q x -> AnswerIs(q, x)) (text "answerIs" v) (field "value" v |> Result.bind answerValue))
          "answerIn", (fun v -> map2 (fun q xs -> AnswerIn(q, xs)) (text "answerIn" v) (array answerValue "values" v))
          "isSpecial", (fun v -> map2 (fun q s -> IsSpecial(q, s)) (text "isSpecial" v) (spelled specialStates "special state" "state" v))
          "compare",
          (fun v ->
              map3
                  (fun op a b -> Compare(a, op, b))
                  (spelled comparisons "comparison" "compare" v)
                  (field "left" v |> Result.bind numberExpr)
                  (field "right" v |> Result.bind numberExpr))
          "factTrue", text "factTrue" >> Result.map FactTrue
          "categoryIs", (fun v -> map2 (fun f c -> CategoryIs(f, c)) (text "categoryIs" v) (text "category" v))
          "all", (fun _ -> conditions "all" |> Result.map All)
          "any", (fun _ -> conditions "any" |> Result.map Any)
          "not", (fun v -> field "not" v |> Result.bind condition |> Result.map Not) ]
        v

// ---- Scorers (TemplateCanonical.writeScorer) -----------------------------------------------------

let private scale (v: JsonElement) : Decoded<Scoring.ItemScale> =
    text "kind" v
    |> Result.bind (function
        | "direct" -> Ok Scoring.Direct
        | "reverse" -> map2 (fun a b -> Scoring.Reverse(a, b)) (number "minimum" v) (number "maximum" v)
        | "mapped" -> pairs numberValue numberValue "map" v |> Result.map (Map.ofList >> Scoring.Mapped)
        | other -> Error $"'{other}' is not a known item scale")

let private aggregate (v: JsonElement) : Decoded<Scoring.Aggregate> =
    let n name = number name v
    let i name = integer name v

    text "kind" v
    |> Result.bind (function
        | "raw-sum" -> Ok Scoring.RawSum
        | "mean" -> Ok Scoring.Mean
        | "median" -> Ok Scoring.Median
        | "minimum" -> Ok Scoring.Minimum
        | "maximum" -> Ok Scoring.Maximum
        | "count-answered" -> Ok Scoring.CountAnswered
        | "count-at-least" -> n "threshold" |> Result.map Scoring.CountAtLeast
        | "weighted-sum" -> numbers "weights" v |> Result.map Scoring.WeightedSum
        | "weighted-mean" -> numbers "weights" v |> Result.map Scoring.WeightedMean
        | "percentage-of-maximum" -> n "itemMaximum" |> Result.map Scoring.PercentageOfMaximum
        | "percentage-of-range" -> map2 (fun a b -> Scoring.PercentageOfRange(a, b)) (n "minimum") (n "maximum")
        | "trimmed-mean" -> i "trim" |> Result.map Scoring.TrimmedMean
        | "capped-sum" -> n "cap" |> Result.map Scoring.CappedSum
        | "top-n" -> i "n" |> Result.map Scoring.TopN
        | "bottom-n" -> i "n" |> Result.map Scoring.BottomN
        | "top-k-box" -> map2 (fun k m -> Scoring.TopKBox(k, m)) (i "k") (i "scaleMaximum")
        | "bottom-k-box" -> map2 (fun k m -> Scoring.BottomKBox(k, m)) (i "k") (i "scaleMaximum")
        | "weighted-top-k-box" -> map2 (fun ws m -> Scoring.WeightedTopKBox(ws, m)) (numbers "weights" v) (i "scaleMaximum")
        | "favorable-rate" -> n "atLeast" |> Result.map Scoring.FavorableRate
        | "unfavorable-rate" -> n "atMost" |> Result.map Scoring.UnfavorableRate
        | "net-favorable" -> map2 (fun a b -> Scoring.NetFavorable(a, b)) (n "favorableAtLeast") (n "unfavorableAtMost")
        | "net-promoter-score" -> Ok Scoring.NetPromoterScore
        | other -> Error $"'{other}' is not a known aggregate")

let private transform (v: JsonElement) : Decoded<Scoring.Transform> =
    let n name = number name v

    text "kind" v
    |> Result.bind (function
        | "linear" -> map2 (fun a b -> Scoring.LinearTransform(a, b)) (n "multiplier") (n "offset")
        | "normalize" -> map4 (fun a b c d -> Scoring.LinearNormalize(a, b, c, d)) (n "fromMinimum") (n "fromMaximum") (n "toMinimum") (n "toMaximum")
        | "clamp" -> map2 (fun a b -> Scoring.Clamp(a, b)) (n "minimum") (n "maximum")
        | "floor" -> Ok Scoring.Floor
        | "ceiling" -> Ok Scoring.Ceiling
        | other -> Error $"'{other}' is not a known transform")

let scorer (v: JsonElement) : Decoded<Scoring.Scorer> =
    let missing =
        field "missing" v
        |> Result.bind (fun m ->
            let special =
                text "special" m
                |> Result.bind (function
                    | "exclude" -> Ok Scoring.Exclude
                    | "substitute" -> number "value" m |> Result.map Scoring.Substitute
                    | other -> Error $"'{other}' is not a known special-answer policy")

            map2 (fun minimum special -> ({ MinimumObservations = minimum; Special = special }: Scoring.MissingPolicy)) (integer "minimumObservations" m) special)

    map4
        (fun scale aggregate transforms (missing, decimals) ->
            ({ Scale = scale; Aggregate = aggregate; Transforms = transforms; Missing = missing; Decimals = decimals }: Scoring.Scorer))
        (field "scale" v |> Result.bind scale)
        (field "aggregate" v |> Result.bind aggregate)
        (array transform "transforms" v)
        (map2 (fun m d -> m, d) missing (integer "decimals" v))

// ---- Answer definitions and selectors (PrimitiveCanonical) ----------------------------------------

let private option (v: JsonElement) : Decoded<ChoiceOption> =
    map3 (fun id label score -> { Id = id; Label = label; Score = score }) (text "id" v) (text "label" v) (optional number "score" v)

let private node (v: JsonElement) : Decoded<TreeNode> =
    map2 (fun o parent -> { Option = o; Parent = parent }) (option v) (optional text "parent" v)

let private selection (v: JsonElement) : Decoded<SelectionRule> =
    text "selection" v
    |> Result.bind (function
        | "any" -> Ok AnyCount
        | "exactly" -> integer "count" v |> Result.map Exactly
        | "at-least" -> integer "count" v |> Result.map AtLeast
        | "at-most" -> integer "count" v |> Result.map AtMost
        | "between" -> map2 (fun a b -> Between(a, b)) (integer "minimum" v) (integer "maximum" v)
        | other -> Error $"'{other}' is not a known selection rule")

let private bounded (v: JsonElement) : Decoded<BoundedSpec> =
    map4 (fun a b c d -> { Minimum = a; Maximum = b; Step = c; Decimals = d }) (number "minimum" v) (number "maximum" v) (number "step" v) (integer "decimals" v)

let answerDefinition (v: JsonElement) : Decoded<AnswerDefinition> =
    let options = array option "options" v

    text "kind" v
    |> Result.bind (function
        | "boolean" -> Ok Boolean
        | "ordinal" -> integer "points" v |> Result.map Ordinal
        | "single-choice" -> options |> Result.map SingleChoice
        | "multi-choice" ->
            let policy =
                if has "whenExclusive" v then
                    spelled [ RejectCombination, "reject"; ClearOthers, "clear-others" ] "exclusive policy" "whenExclusive" v
                else
                    Ok RejectCombination

            map4 (fun o s x p -> MultiChoice { Options = o; Selection = s; Exclusive = x; WhenExclusive = p }) options (selection v) (strings "exclusive" v) policy
        | "bounded-number" -> bounded v |> Result.map BoundedNumber
        | "bounded-range" -> bounded v |> Result.map BoundedRange
        | "ranking" -> map2 (fun o p -> Ranking { Options = o; Positions = p }) options (optional integer "positions" v)
        | "allocation" ->
            map4
                (fun o (total, step) a b -> Allocation { Options = o; Total = total; Step = step; ItemMinimum = a; ItemMaximum = b })
                options
                (map2 (fun t s -> t, s) (integer "total" v) (number "step" v))
                (integer "itemMinimum" v)
                (integer "itemMaximum" v)
        | "best-worst" -> options |> Result.map BestWorst
        | "hierarchical" -> map2 (fun n t -> Hierarchical(n, t)) (array node "nodes" v) (flag "terminalOnly" v)
        | "hierarchical-multi" ->
            map3
                (fun n r s -> HierarchicalMulti(n, r, s))
                (array node "nodes" v)
                (spelled [ ParentImpliesDescendants, "implies"; ParentForbidsDescendants, "forbids"; ParentIndependent, "independent" ] "parent rule" "parentRule" v)
                (selection v)
        | other -> Error $"'{other}' is not a known answer kind")

let selector (v: JsonElement) : Decoded<Selector> =
    let preset =
        text "preset" v
        |> Result.bind (function
            | "semantic-differential" -> map2 (fun l r -> SemanticDifferential(l, r)) (text "left" v) (text "right" v)
            | spelling -> named presets "selector preset" spelling)

    map2 (fun p labels -> { Preset = p; Labels = labels }) preset (strings "labels" v)
