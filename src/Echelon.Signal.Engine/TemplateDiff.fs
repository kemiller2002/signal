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
    | OptionScoresChanged of questionId: string
    | SpecialStatesChanged of questionId: string
    | SelectorChanged of questionId: string
    | FlowChanged
    | FactsChanged
    | ValidationRulesChanged
    | CompletionChanged
    | RecommendationsChanged
    | OverallScoringChanged
    | InterpretationsChanged
    | DisplayChanged
    | ItemKeysChanged

/// Whether a change can alter scores, alter how stored answers are read, or
/// only how the survey looks.
type Impact =
    { Scoring: bool
      Encoding: bool }

let impact (change: Change) =
    match change with
    | MetadataChanged
    | PresentationChanged
    | RuntimeChanged
    | SectionTextChanged _
    | SectionPresentationChanged _
    | SelectorChanged _ -> { Scoring = false; Encoding = false }
    | QuestionWordingChanged _
    | SectionRequirementChanged _
    | QuestionRequirementChanged _
    | CompatibilityChanged -> { Scoring = false; Encoding = false }
    | SectionScoringChanged _
    | OptionScoresChanged _
    | FlowChanged
    | FactsChanged
    | OverallScoringChanged
    | ItemKeysChanged -> { Scoring = true; Encoding = false }
    | InterpretationsChanged
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
      if before.Results.Interpretations <> after.Results.Interpretations then InterpretationsChanged
      if before.Results.Display <> after.Results.Display then DisplayChanged
      if before.Results.ItemKeys <> after.Results.ItemKeys then ItemKeysChanged
      if before.Presentation <> after.Presentation then PresentationChanged
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

          match a.Answer, b.Answer with
          | SingleChoice x, SingleChoice y ->
              if (x |> List.map _.Id) <> (y |> List.map _.Id) then OptionsChanged id
              if (x |> List.map (fun o -> o.Id, o.Score) |> Map.ofList) <> (y |> List.map (fun o -> o.Id, o.Score) |> Map.ofList) then
                  OptionScoresChanged id
          | x, y when x <> y -> AnswerChanged id
          | _ -> () ]

type Comparability =
    | Comparable
    | ComparableWithCaution of reasons: string list
    | NotComparable of reasons: string list

/// Whether results of two versions can be compared (AUT-005 §48): a change
/// to what is scored or how is not comparable; a wording or requirement
/// change is comparable with caution; presentation alone is comparable.
let comparability (changes: Change list) =
    let notComparable =
        changes
        |> List.choose (function
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
            | FactsChanged -> Some "derived facts changed"
            | OverallScoringChanged -> Some "overall scoring changed"
            | ItemKeysChanged -> Some "item keys changed"
            | _ -> None)

    let caution =
        changes
        |> List.choose (function
            | QuestionWordingChanged id -> Some $"wording of '{id}' changed"
            | QuestionRequirementChanged id -> Some $"requirement of '{id}' changed"
            | SectionRequirementChanged id -> Some $"requirement of section '{id}' changed"
            | OptionsChanged id -> Some $"options of '{id}' reordered or relabelled"
            | ValidationRulesChanged -> Some "validation rules changed"
            | CompletionChanged -> Some "completion policy changed"
            | RecommendationsChanged -> Some "recommendations changed"
            | InterpretationsChanged -> Some "interpretations changed"
            | _ -> None)

    match notComparable, caution with
    | [], [] -> Comparable
    | [], reasons -> ComparableWithCaution reasons
    | reasons, _ -> NotComparable reasons
