/// The canonical spelling of answer primitives and selector presets inside a
/// template's canonical form (`TemplateCanonical`). Names are part of the
/// hash: a spelling is never changed, only added.
module Echelon.Signal.Engine.PrimitiveCanonical

open System.Text.Json
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors

let private semanticName =
    function
    | Agreement -> "agreement"
    | Frequency -> "frequency"
    | Importance -> "importance"
    | Satisfaction -> "satisfaction"
    | Confidence -> "confidence"
    | Quality -> "quality"
    | Difficulty -> "difficulty"
    | Effort -> "effort"
    | Likelihood -> "likelihood"
    | Maturity -> "maturity"
    | Severity -> "severity"
    | Priority -> "priority"
    | Probability -> "probability"
    | Familiarity -> "familiarity"

let presetName =
    function
    | YesNo -> "yes-no"
    | Likert3 -> "likert-3"
    | Likert5 -> "likert-5"
    | Likert7 -> "likert-7"
    | Agreement5 -> "agreement-5"
    | Frequency5 -> "frequency-5"
    | Quality5 -> "quality-5"
    | Confidence5 -> "confidence-5"
    | Satisfaction5 -> "satisfaction-5"
    | Maturity5 -> "maturity-5"
    | NumericRating -> "numeric-rating"
    | SingleSelect -> "single-select"
    | ForcedChoice -> "forced-choice"
    | YesNoNA -> "yes-no-na"
    | YesNoDontKnow -> "yes-no-dont-know"
    | YesNoInProgress -> "yes-no-in-progress"
    | TrueFalse -> "true-false"
    | AgreeDisagree -> "agree-disagree"
    | BinaryToggle -> "binary-toggle"
    | BinaryButtons -> "binary-buttons"
    | ThreeWayChoice -> "three-way-choice"
    | GenericOrdinal -> "generic-ordinal"
    | Likert4 -> "likert-4"
    | Likert6 -> "likert-6"
    | Likert10 -> "likert-10"
    | Likert11 -> "likert-11"
    | SemanticScale kind -> $"semantic-{semanticName kind}"
    | SemanticDifferential _ -> "semantic-differential"
    | Nps0To10 -> "nps-0-10"
    | StarRating -> "star-rating"
    | IconRating -> "icon-rating"
    | Slider -> "slider"
    | RangeSlider -> "range-slider"
    | NumericStepper -> "numeric-stepper"
    | RadioList -> "radio-list"
    | Dropdown -> "dropdown"
    | SearchableSelect -> "searchable-select"
    | SegmentedControl -> "segmented-control"
    | ChoiceButtons -> "choice-buttons"
    | ChoiceCards -> "choice-cards"
    | ImageChoiceSingle -> "image-choice-single"
    | CheckboxList -> "checkbox-list"
    | MultiSelectDropdown -> "multi-select-dropdown"
    | SearchableMultiSelect -> "searchable-multi-select"
    | MultiSelectChips -> "multi-select-chips"
    | MultiSelectButtons -> "multi-select-buttons"
    | MultiSelectCards -> "multi-select-cards"
    | ImageChoiceMulti -> "image-choice-multi"
    | RankingList -> "ranking"
    | ConstantSum -> "constant-sum"
    | Pairwise -> "pairwise"
    | BestWorstSet -> "best-worst"
    | CascadingSelect -> "cascading-select"
    | TreeMultiSelect -> "tree-multi-select"

/// Members a preset adds beyond its name (semantic differential endpoints).
let writePresetDetail (w: Utf8JsonWriter) (preset: SelectorPreset) =
    match preset with
    | SemanticDifferential(left, right) ->
        w.WriteString("left", left)
        w.WriteString("right", right)
    | _ -> ()

let private writeOptions (w: Utf8JsonWriter) (name: string) (options: ChoiceOption list) =
    w.WriteStartArray name

    for o in options do
        w.WriteStartObject()
        w.WriteString("id", o.Id)
        w.WriteString("label", o.Label)
        o.Score |> Option.iter (fun s -> w.WriteNumber("score", s))
        w.WriteEndObject()

    w.WriteEndArray()

let private writeNodes (w: Utf8JsonWriter) (nodes: TreeNode list) =
    w.WriteStartArray "nodes"

    for n in nodes do
        w.WriteStartObject()
        w.WriteString("id", n.Option.Id)
        w.WriteString("label", n.Option.Label)
        n.Option.Score |> Option.iter (fun s -> w.WriteNumber("score", s))
        n.Parent |> Option.iter (fun p -> w.WriteString("parent", p))
        w.WriteEndObject()

    w.WriteEndArray()

let private writeSelection (w: Utf8JsonWriter) (rule: SelectionRule) =
    match rule with
    | AnyCount -> w.WriteString("selection", "any")
    | Exactly n ->
        w.WriteString("selection", "exactly")
        w.WriteNumber("count", n)
    | AtLeast n ->
        w.WriteString("selection", "at-least")
        w.WriteNumber("count", n)
    | AtMost n ->
        w.WriteString("selection", "at-most")
        w.WriteNumber("count", n)
    | Between(lo, hi) ->
        w.WriteString("selection", "between")
        w.WriteNumber("minimum", lo)
        w.WriteNumber("maximum", hi)

let private writeBounded (w: Utf8JsonWriter) (b: BoundedSpec) =
    w.WriteNumber("minimum", b.Minimum)
    w.WriteNumber("maximum", b.Maximum)
    w.WriteNumber("step", b.Step)
    w.WriteNumber("decimals", b.Decimals)

/// The "answer" member of a question.
let writeAnswer (w: Utf8JsonWriter) (answer: AnswerDefinition) =
    w.WriteStartObject "answer"
    let kind (name: string) = w.WriteString("kind", name)

    match answer with
    | Boolean -> kind "boolean"
    | Ordinal points ->
        kind "ordinal"
        w.WriteNumber("points", points)
    | SingleChoice options ->
        kind "single-choice"
        writeOptions w "options" options
    | MultiChoice m ->
        kind "multi-choice"
        writeOptions w "options" m.Options
        writeSelection w m.Selection

        if not m.Exclusive.IsEmpty then
            w.WriteStartArray "exclusive"
            m.Exclusive |> List.iter w.WriteStringValue
            w.WriteEndArray()
            w.WriteString("whenExclusive", (match m.WhenExclusive with RejectCombination -> "reject" | ClearOthers -> "clear-others"))
    | BoundedNumber b ->
        kind "bounded-number"
        writeBounded w b
    | BoundedRange b ->
        kind "bounded-range"
        writeBounded w b
    | Ranking r ->
        kind "ranking"
        writeOptions w "options" r.Options
        r.Positions |> Option.iter (fun p -> w.WriteNumber("positions", p))
    | Allocation a ->
        kind "allocation"
        writeOptions w "options" a.Options
        w.WriteNumber("total", a.Total)
        w.WriteNumber("step", a.Step)
        w.WriteNumber("itemMinimum", a.ItemMinimum)
        w.WriteNumber("itemMaximum", a.ItemMaximum)
    | BestWorst options ->
        kind "best-worst"
        writeOptions w "options" options
    | Hierarchical(nodes, terminalOnly) ->
        kind "hierarchical"
        writeNodes w nodes
        w.WriteBoolean("terminalOnly", terminalOnly)
    | HierarchicalMulti(nodes, rule, selection) ->
        kind "hierarchical-multi"
        writeNodes w nodes

        w.WriteString(
            "parentRule",
            match rule with
            | ParentImpliesDescendants -> "implies"
            | ParentForbidsDescendants -> "forbids"
            | ParentIndependent -> "independent"
        )

        writeSelection w selection

    w.WriteEndObject()
