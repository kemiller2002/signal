/// Selector presets (CAN-001 §7, SCS-010, SCS-013, SCS-018): presentation
/// for an answer primitive, never scoring. Many presets share a primitive;
/// semantic presets compile to explicit ordered labels and carry no hidden
/// scoring meaning.
///
/// The handoff contract (SCS-018) maps each preset to a web-component family
/// and variant. Components render engine-projected state and emit typed
/// events; they never own answers, scores, applicability or completion.
module Echelon.Signal.Engine.Selectors

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives

type SemanticKind =
    | Agreement
    | Frequency
    | Importance
    | Satisfaction
    | Confidence
    | Quality
    | Difficulty
    | Effort
    | Likelihood
    | Maturity
    | Severity
    | Priority
    | Probability
    | Familiarity

type SelectorPreset =
    // The WI-0042 presets keep their names (and canonical spellings).
    | YesNo
    | Likert3
    | Likert5
    | Likert7
    | Agreement5
    | Frequency5
    | Quality5
    | Confidence5
    | Satisfaction5
    | Maturity5
    | NumericRating
    | SingleSelect
    | ForcedChoice
    // Binary and three-state (SCS-010).
    | YesNoNA
    | YesNoDontKnow
    | YesNoInProgress
    | TrueFalse
    | AgreeDisagree
    | BinaryToggle
    | BinaryButtons
    | ThreeWayChoice
    // Generic ordinal, Likert and semantic scales.
    | GenericOrdinal
    | Likert4
    | Likert6
    | Likert10
    | Likert11
    | SemanticScale of SemanticKind
    | SemanticDifferential of left: string * right: string
    // Numeric and rating.
    | Nps0To10
    | StarRating
    | IconRating
    | Slider
    | RangeSlider
    | NumericStepper
    // Single-choice presentations.
    | RadioList
    | Dropdown
    | SearchableSelect
    | SegmentedControl
    | ChoiceButtons
    | ChoiceCards
    | ImageChoiceSingle
    // Multi-choice presentations.
    | CheckboxList
    | MultiSelectDropdown
    | SearchableMultiSelect
    | MultiSelectChips
    | MultiSelectButtons
    | MultiSelectCards
    | ImageChoiceMulti
    // Structured selectors (SCS-013).
    | RankingList
    | ConstantSum
    | Pairwise
    | BestWorstSet
    | CascadingSelect
    | TreeMultiSelect

type Selector =
    { Preset: SelectorPreset
      /// One label per stored value, in value order, for binary and ordinal
      /// selectors. Empty for option-based selectors, whose options carry
      /// their own labels, and for numeric scales.
      Labels: string list }

/// Whether a preset presents an answer primitive with these special states.
let fits (preset: SelectorPreset) (answer: AnswerDefinition) (special: SpecialState list) =
    match preset, answer with
    | (YesNo | TrueFalse | AgreeDisagree | BinaryToggle | BinaryButtons), Boolean -> true
    | YesNoNA, Boolean -> List.contains NotApplicable special
    | YesNoDontKnow, Boolean -> List.contains DontKnow special
    | YesNoInProgress, Ordinal 3 -> true
    | Likert3, Ordinal 3
    | Likert4, Ordinal 4
    | Likert6, Ordinal 6
    | Likert7, Ordinal 7
    | Likert10, Ordinal 10
    | Likert11, Ordinal 11
    | Nps0To10, Ordinal 11 -> true
    | (Likert5 | Agreement5 | Frequency5 | Quality5 | Confidence5 | Satisfaction5 | Maturity5), Ordinal 5 -> true
    | (GenericOrdinal | NumericRating | StarRating | IconRating | SemanticScale _ | SemanticDifferential _), Ordinal _ -> true
    | (Slider | NumericStepper), BoundedNumber _ -> true
    | RangeSlider, BoundedRange _ -> true
    | (SingleSelect | ForcedChoice | RadioList | Dropdown | SearchableSelect | SegmentedControl | ChoiceButtons | ChoiceCards | ImageChoiceSingle),
      SingleChoice _ -> true
    | ThreeWayChoice, SingleChoice os -> os.Length = 3
    | Pairwise, SingleChoice os -> os.Length = 2
    | (CheckboxList | MultiSelectDropdown | SearchableMultiSelect | MultiSelectChips | MultiSelectButtons | MultiSelectCards | ImageChoiceMulti),
      MultiChoice _ -> true
    | RankingList, Ranking _
    | ConstantSum, Allocation _
    | BestWorstSet, BestWorst _
    | CascadingSelect, Hierarchical _
    | TreeMultiSelect, HierarchicalMulti _ -> true
    | _ -> false

/// How many labels a selector must carry: one per value for binary and
/// ordinal answers; none otherwise (options and numeric scales label
/// themselves).
let labelsRequired (answer: AnswerDefinition) =
    match answer with
    | Boolean -> 2
    | Ordinal points -> points
    | _ -> 0

/// The SCS-018 component family and variant that renders a preset.
let componentOf (preset: SelectorPreset) : string * string =
    match preset with
    | YesNo
    | YesNoNA
    | YesNoDontKnow
    | TrueFalse
    | AgreeDisagree -> "signal-binary-choice", "radio"
    | BinaryToggle -> "signal-binary-choice", "toggle"
    | BinaryButtons -> "signal-binary-choice", "buttons"
    | Likert3
    | Likert4
    | Likert5
    | Likert6
    | Likert7
    | Likert10
    | Likert11
    | Agreement5
    | Frequency5
    | Quality5
    | Confidence5
    | Satisfaction5
    | Maturity5
    | GenericOrdinal
    | YesNoInProgress
    | SemanticScale _ -> "signal-ordinal-scale", "radio"
    | SemanticDifferential _ -> "signal-semantic-differential", "radio"
    | NumericRating
    | Nps0To10 -> "signal-numeric-rating", "buttons"
    | StarRating -> "signal-star-rating", "stars"
    | IconRating -> "signal-icon-rating", "icons"
    | Slider -> "signal-slider", "slider"
    | RangeSlider -> "signal-range-slider", "slider"
    | NumericStepper -> "signal-numeric-stepper", "stepper"
    | SingleSelect
    | ForcedChoice
    | RadioList
    | ThreeWayChoice -> "signal-single-choice", "radio list"
    | Dropdown -> "signal-single-choice", "dropdown"
    | SearchableSelect -> "signal-single-choice", "searchable select"
    | SegmentedControl -> "signal-single-choice", "segmented control"
    | ChoiceButtons -> "signal-single-choice", "buttons"
    | ChoiceCards -> "signal-single-choice", "cards"
    | ImageChoiceSingle -> "signal-single-choice", "image choices"
    | CheckboxList -> "signal-multi-choice", "checkbox list"
    | MultiSelectDropdown -> "signal-multi-choice", "multi-select dropdown"
    | SearchableMultiSelect -> "signal-multi-choice", "searchable multi-select"
    | MultiSelectChips -> "signal-multi-choice", "chips"
    | MultiSelectButtons -> "signal-multi-choice", "buttons"
    | MultiSelectCards -> "signal-multi-choice", "cards"
    | ImageChoiceMulti -> "signal-multi-choice", "image choices"
    | RankingList -> "signal-ranking", "move up/down"
    | ConstantSum -> "signal-allocation", "steppers"
    | Pairwise -> "signal-pairwise", "two choices"
    | BestWorstSet -> "signal-best-worst", "best and worst"
    | CascadingSelect -> "signal-hierarchical-select", "cascading"
    | TreeMultiSelect -> "signal-hierarchical-multi-select", "tree"
