/// Semantic diff between two template versions, the impact of each change
/// on scoring and encoding, and whether results stay comparable
/// (AUT-005 §§44-48).
module Echelon.Signal.Engine.TemplateDiff

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template

type Change =
    | MetadataChanged
    | PresentationChanged
    /// Timers changed (DF-SIGNAL-2026-0008): how long, not what is scored.
    | TimersChanged
    /// Banks changed: which questions an instance answers, so scores can move.
    | BanksChanged
    | RuntimeChanged
    | CompatibilityChanged
    | SectionAdded of sectionId: string
    | SectionRemoved of sectionId: string
    | SectionsReordered
    | SectionTextChanged of sectionId: string
    | SectionRequirementChanged of sectionId: string
    | SectionPresentationChanged of sectionId: string
    | SectionScoringChanged of sectionId: string
    | QuestionAdded of questionId: string
    | QuestionRemoved of questionId: string
    | QuestionsReordered
    | QuestionMovedSection of questionId: string
    | QuestionWordingChanged of questionId: string
    | QuestionRequirementChanged of questionId: string
    | AnswerChanged of questionId: string
    | OptionsChanged of questionId: string
    /// An option's label changed, its id and place unchanged: the text a
    /// respondent answers, like a wording change (WI-0097).
    | OptionsRelabelled of questionId: string
    | OptionScoresChanged of questionId: string
    | SpecialStatesChanged of questionId: string
    | SelectorChanged of questionId: string
    | FlowChanged
    | FactsChanged
    | ValidationRulesChanged
    | CompletionChanged
    | RecommendationsChanged
    | OverallScoringChanged
    /// A threshold, band bound, pass/fail direction, stage condition or
    /// target changed, or an interpretation was added or removed: the same
    /// score can be read differently (AUT-005 §§45, 48; DF-SIGNAL-2026-0019).
    | InterpretationThresholdsChanged
    /// Only labels or the order of interpretations changed.
    | InterpretationLabelsChanged
    | DisplayChanged
    | ItemKeysChanged
    /// One translated text changed, was added or was removed in one locale.
    /// It is classified exactly as the source-text change for that key
    /// (`sourceEquivalent`), scoped to the locale: respondents in that
    /// locale answer the translation (DF-SIGNAL-2026-0020).
    | LocalizationChanged of locale: string * key: LocalizationModel.TextKey

/// Whether a change can alter scores, alter how stored answers are read, or
/// only how the survey looks.
type Impact =
    { Scoring: bool
      Encoding: bool }

/// The source-text change a translated text stands for: a translated
/// prompt is a wording change, a translated title a metadata change.
let sourceEquivalent (key: LocalizationModel.TextKey) : Change =
    match key with
    | LocalizationModel.SurveyTitle
    | LocalizationModel.SurveyShortTitle
    | LocalizationModel.SurveyDescription
    | LocalizationModel.SurveyInstructions -> MetadataChanged
    | LocalizationModel.SectionTitle id
    | LocalizationModel.SectionDescription id -> SectionTextChanged id
    | LocalizationModel.QuestionPrompt id
    | LocalizationModel.QuestionHelp id -> QuestionWordingChanged id
    | LocalizationModel.AnswerLabel(id, _)
    | LocalizationModel.AccessibleName id
    | LocalizationModel.AccessibleDescription id -> SelectorChanged id
    | LocalizationModel.OptionLabel(id, _) -> OptionsRelabelled id
    | LocalizationModel.RecommendationTitle _
    | LocalizationModel.RecommendationDescription _ -> RecommendationsChanged
    | LocalizationModel.ResultLabel _ -> InterpretationLabelsChanged

let rec impact (change: Change) =
    match change with
    // A translation never changes the encoding: only its scoring impact
    // follows the source text's rule.
    | LocalizationChanged(_, key) -> { impact (sourceEquivalent key) with Encoding = false }
    | MetadataChanged
    | PresentationChanged
    | TimersChanged
    | RuntimeChanged
    | SectionTextChanged _
    | SectionPresentationChanged _
    | SelectorChanged _ -> { Scoring = false; Encoding = false }
    | QuestionWordingChanged _
    | OptionsRelabelled _
    | SectionRequirementChanged _
    | QuestionRequirementChanged _
    | CompatibilityChanged -> { Scoring = false; Encoding = false }
    | SectionScoringChanged _
    | OptionScoresChanged _
    | FlowChanged
    | BanksChanged
    | FactsChanged
    | OverallScoringChanged
    | ItemKeysChanged -> { Scoring = true; Encoding = false }
    | InterpretationThresholdsChanged -> { Scoring = true; Encoding = false }
    | InterpretationLabelsChanged
    | DisplayChanged -> { Scoring = false; Encoding = false }
    | ValidationRulesChanged
    | CompletionChanged
    | RecommendationsChanged -> { Scoring = false; Encoding = false }
    | SectionAdded _
    | SectionRemoved _
    | QuestionAdded _
    | QuestionRemoved _
    | QuestionMovedSection _
    | AnswerChanged _
    | SpecialStatesChanged _ -> { Scoring = true; Encoding = true }
    | SectionsReordered
    | QuestionsReordered
    | OptionsChanged _ -> { Scoring = false; Encoding = true }

/// What an interpretation means without its labels: the numbers or
/// conditions that decide each outcome.
type private Meaning =
    | BandBounds of (float * float) list
    | Threshold of float * ResultModel.Direction
    | StageConditions of RuleModel.Condition list

/// Each interpretation's target and meaning, keyed by id so that reordering
/// alone is not a change of meaning.
let private meaning (interpretations: ResultModel.Interpretation list) =
    interpretations
    |> List.map (fun i ->
        let rule =
            match i.Kind with
            | ResultModel.Bands bands -> BandBounds(bands |> List.map (fun b -> b.From, b.To))
            | ResultModel.PassFail(threshold, direction) -> Threshold(threshold, direction)
            | ResultModel.Stages(_, stages) -> StageConditions(stages |> List.map _.Requires)

        i.Id, (i.Target, rule))
    |> Map.ofList

/// An answer definition with every option's label and score blanked and
/// options in id order: what is left is its structure (selection rules,
/// totals, positions, tree parents), which only an AnswerChanged may alter.
/// Only the options whose ids `keep` holds are kept, so two definitions
/// can be compared on the options they share.
let private structureOf (keep: string -> bool) (answer: AnswerDefinition) =
    let blank (o: ChoiceOption) = { o with Label = ""; Score = None }
    let blankAll = List.filter (fun (o: ChoiceOption) -> keep o.Id) >> List.map blank >> List.sortBy (fun o -> o.Id)

    let blankTree =
        List.filter (fun (n: TreeNode) -> keep n.Option.Id)
        >> List.map (fun n -> { n with Option = blank n.Option })
        >> List.sortBy (fun n -> n.Option.Id)

    match answer with
    | SingleChoice os -> SingleChoice(blankAll os)
    | MultiChoice m -> MultiChoice { m with Options = blankAll m.Options }
    | Ranking r -> Ranking { r with Options = blankAll r.Options }
    | Allocation a -> Allocation { a with Options = blankAll a.Options }
    | BestWorst os -> BestWorst(blankAll os)
    | Hierarchical(nodes, terminal) -> Hierarchical(blankTree nodes, terminal)
    | HierarchicalMulti(nodes, rule, selection) -> HierarchicalMulti(blankTree nodes, rule, selection)
    | Boolean
    | Ordinal _
    | BoundedNumber _
    | BoundedRange _ -> answer

/// How one question's answer definition changed (WI-0097). Every primitive
/// with options follows one rule: ids added, removed or reordered is
/// OptionsChanged, a label is OptionsRelabelled, a score is
/// OptionScoresChanged; anything else (another primitive, selection rules,
/// totals, positions, tree shape, a bound) is AnswerChanged.
let private answerChanges (id: string) (a: AnswerDefinition) (b: AnswerDefinition) =
    let ids d = options d |> List.map (fun o -> o.Id)
    let shared = Set.intersect (Set.ofList (ids a)) (Set.ofList (ids b)) |> Set.toList
    let on (field: ChoiceOption -> 'v) d = options d |> List.filter (fun o -> List.contains o.Id shared) |> List.map (fun o -> o.Id, field o) |> Map.ofList
    let all _ = true
    let sameKind = (structureOf all a).GetType() = (structureOf all b).GetType()
    let common id = List.contains id shared

    [ if a = b then
          ()
      elif not sameKind || (options a).IsEmpty then
          AnswerChanged id
      else
          if ids a <> ids b then OptionsChanged id
          if on _.Label a <> on _.Label b then OptionsRelabelled id
          if on _.Score a <> on _.Score b then OptionScoresChanged id
          // On the options both have, any remaining difference is structure
          // (selection rules, totals, positions, parents). Exclusive ids
          // and tree parents naming a removed option count as structure.
          if structureOf common a <> structureOf common b then AnswerChanged id ]

let diff (before: Content) (after: Content) : Change list =
    let beforeSections = before.Sections |> List.map (fun s -> s.Id, s) |> Map.ofList
    let afterSections = after.Sections |> List.map (fun s -> s.Id, s) |> Map.ofList
    let beforeQuestions = questions before |> List.map (fun (s, q) -> q.Id, (s, q)) |> Map.ofList
    let afterQuestions = questions after |> List.map (fun (s, q) -> q.Id, (s, q)) |> Map.ofList

    let common (a: Map<string, 'a>) (b: Map<string, 'b>) =
        a |> Map.toList |> List.map fst |> List.filter b.ContainsKey

    let order ids (keep: string -> bool) = ids |> List.filter keep

    let beforeQuestionOrder = questions before |> List.map (fun (_, q) -> q.Id)
    let afterQuestionOrder = questions after |> List.map (fun (_, q) -> q.Id)

    [ if before.Metadata <> after.Metadata then MetadataChanged
      if before.Rules.Flow <> after.Rules.Flow then FlowChanged
      if before.Rules.Facts <> after.Rules.Facts then FactsChanged
      if before.Rules.Validation <> after.Rules.Validation then ValidationRulesChanged
      if before.Rules.Completion <> after.Rules.Completion then CompletionChanged
      if before.Rules.Recommendations <> after.Rules.Recommendations then RecommendationsChanged
      if before.Results.Overall <> after.Results.Overall then OverallScoringChanged
      if meaning before.Results.Interpretations <> meaning after.Results.Interpretations then
          InterpretationThresholdsChanged
      elif before.Results.Interpretations <> after.Results.Interpretations then
          InterpretationLabelsChanged
      if before.Results.Display <> after.Results.Display then DisplayChanged
      if before.Results.ItemKeys <> after.Results.ItemKeys then ItemKeysChanged
      // One change per text that differs, per locale; a locale added or
      // removed changes every text it has.
      let texts (c: Content) =
          c.Localizations
          |> List.collect (fun l -> l.Texts |> Map.toList |> List.map (fun (key, text) -> (l.Locale, key), text))
          |> Map.ofList

      let a, b = texts before, texts after

      for locale, key in Set.union (a.Keys |> Set.ofSeq) (b.Keys |> Set.ofSeq) do
          if a.TryFind(locale, key) <> b.TryFind(locale, key) then LocalizationChanged(locale, key)
      let layoutOf (p: Presentation) = { p with Timers = []; Banks = [] }
      if layoutOf before.Presentation <> layoutOf after.Presentation then PresentationChanged
      if before.Presentation.Timers <> after.Presentation.Timers then TimersChanged
      if before.Presentation.Banks <> after.Presentation.Banks then BanksChanged
      if before.Runtime <> after.Runtime then RuntimeChanged
      if before.Compatibility <> after.Compatibility then CompatibilityChanged

      for s in before.Sections do
          if not (afterSections.ContainsKey s.Id) then SectionRemoved s.Id
      for s in after.Sections do
          if not (beforeSections.ContainsKey s.Id) then SectionAdded s.Id

      let shared = common beforeSections afterSections

      if order (before.Sections |> List.map _.Id) (fun id -> List.contains id shared)
         <> order (after.Sections |> List.map _.Id) (fun id -> List.contains id shared) then
          SectionsReordered

      for id in shared do
          let a, b = beforeSections[id], afterSections[id]
          if a.Title <> b.Title || a.Description <> b.Description then SectionTextChanged id
          if a.Required <> b.Required then SectionRequirementChanged id
          if a.Presentation <> b.Presentation then SectionPresentationChanged id
          if a.Scoring <> b.Scoring then SectionScoringChanged id

      for id in beforeQuestionOrder do
          if not (afterQuestions.ContainsKey id) then QuestionRemoved id
      for id in afterQuestionOrder do
          if not (beforeQuestions.ContainsKey id) then QuestionAdded id

      let sharedQuestions = common beforeQuestions afterQuestions

      if order beforeQuestionOrder (fun id -> List.contains id sharedQuestions)
         <> order afterQuestionOrder (fun id -> List.contains id sharedQuestions) then
          QuestionsReordered

      for id in sharedQuestions do
          let (sa, a), (sb, b) = beforeQuestions[id], afterQuestions[id]
          if sa.Id <> sb.Id then QuestionMovedSection id
          if a.Prompt <> b.Prompt || a.HelpText <> b.HelpText then QuestionWordingChanged id
          if a.Required <> b.Required then QuestionRequirementChanged id
          if a.Selector <> b.Selector then SelectorChanged id
          if a.SpecialStates <> b.SpecialStates then SpecialStatesChanged id

          yield! answerChanges id a.Answer b.Answer ]

type Comparability =
    | Comparable
    | ComparableWithCaution of reasons: string list
    | NotComparable of reasons: string list

/// Whether results of two versions can be compared (AUT-005 §48): a change
/// to what is scored or how is not comparable; a wording or requirement
/// change is comparable with caution; presentation alone is comparable.
let comparability (changes: Change list) =
    let notComparableReason =
        function
        | SectionScoringChanged id -> Some $"scoring of '{id}' changed"
        | OptionScoresChanged id -> Some $"option scores of '{id}' changed"
        | AnswerChanged id -> Some $"answer definition of '{id}' changed"
        | SpecialStatesChanged id -> Some $"special states of '{id}' changed"
        | QuestionAdded id -> Some $"question '{id}' added"
        | QuestionRemoved id -> Some $"question '{id}' removed"
        | QuestionMovedSection id -> Some $"question '{id}' moved section"
        | SectionAdded id -> Some $"section '{id}' added"
        | SectionRemoved id -> Some $"section '{id}' removed"
        | FlowChanged -> Some "flow changed which questions apply"
        | BanksChanged -> Some "question banks changed which questions an instance answers"
        | FactsChanged -> Some "derived facts changed"
        | OverallScoringChanged -> Some "overall scoring changed"
        | InterpretationThresholdsChanged -> Some "interpretation thresholds changed"
        | ItemKeysChanged -> Some "item keys changed"
        | _ -> None

    let cautionReason =
        function
        | QuestionWordingChanged id -> Some $"wording of '{id}' changed"
        | QuestionRequirementChanged id -> Some $"requirement of '{id}' changed"
        | SectionRequirementChanged id -> Some $"requirement of section '{id}' changed"
        | OptionsChanged id -> Some $"options of '{id}' added, removed or reordered"
        | OptionsRelabelled id -> Some $"option labels of '{id}' changed"
        | ValidationRulesChanged -> Some "validation rules changed"
        | CompletionChanged -> Some "completion policy changed"
        | RecommendationsChanged -> Some "recommendations changed"
        | InterpretationLabelsChanged -> Some "interpretation labels changed"
        | _ -> None

    // A translated text takes its source text's reason, scoped to its locale.
    let reasons (reason: Change -> string option) =
        changes
        |> List.choose (function
            | LocalizationChanged(locale, key) -> reason (sourceEquivalent key) |> Option.map (fun r -> $"{r} in {locale}")
            | change -> reason change)

    let notComparable = reasons notComparableReason
    let caution = reasons cautionReason

    match notComparable, caution with
    | [], [] -> Comparable
    | [], reasons -> ComparableWithCaution reasons
    | reasons, _ -> NotComparable reasons
