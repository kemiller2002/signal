/// Template localization (CAN-001 §2, ACR-006 §17, DF-SIGNAL-2026-0020):
/// translated presentation text keyed by what it translates, never by
/// position in a rendering. A translation changes only text a respondent
/// reads: question, section, option and answer-label wording, instructions,
/// recommendation and result labels, and selector accessibility text. It
/// never changes identity, option order, cardinality, scoring or the
/// encoding.
///
/// Pure.
module Echelon.Signal.Engine.LocalizationModel

/// What a translated text replaces. Ids are the template's stable ids.
type TextKey =
    | SurveyTitle
    | SurveyShortTitle
    | SurveyDescription
    | SurveyInstructions
    | SectionTitle of sectionId: string
    | SectionDescription of sectionId: string
    | QuestionPrompt of questionId: string
    | QuestionHelp of questionId: string
    /// A selector label by its value index (binary and ordinal selectors).
    | AnswerLabel of questionId: string * index: int
    | OptionLabel of questionId: string * optionId: string
    | AccessibleName of questionId: string
    | AccessibleDescription of questionId: string
    | RecommendationTitle of recommendationId: string
    | RecommendationDescription of recommendationId: string
    /// A result label (a band, pass/fail or stage label) by the label the
    /// template gives it.
    | ResultLabel of interpretationId: string * label: string

/// One locale's translations. `Locale` is a BCP 47 tag ("fr-FR").
type Localization =
    { Locale: string
      Texts: Map<TextKey, string> }

/// The canonical spelling of a key (`question/q1/prompt`).
let keyName (key: TextKey) =
    match key with
    | SurveyTitle -> "title"
    | SurveyShortTitle -> "short-title"
    | SurveyDescription -> "description"
    | SurveyInstructions -> "instructions"
    | SectionTitle id -> $"section/{id}/title"
    | SectionDescription id -> $"section/{id}/description"
    | QuestionPrompt id -> $"question/{id}/prompt"
    | QuestionHelp id -> $"question/{id}/help"
    | AnswerLabel(id, index) -> $"question/{id}/label/{index}"
    | OptionLabel(id, option) -> $"question/{id}/option/{option}"
    | AccessibleName id -> $"question/{id}/accessible-name"
    | AccessibleDescription id -> $"question/{id}/accessible-description"
    | RecommendationTitle id -> $"recommendation/{id}/title"
    | RecommendationDescription id -> $"recommendation/{id}/description"
    | ResultLabel(id, label) -> $"result/{id}/{label}"

/// The key a canonical spelling names; None for any other text. Ids never
/// contain '/', so only a result label may, and it is the remainder.
let keyOf (name: string) : TextKey option =
    match name.Split('/', 3) |> List.ofArray with
    | [ "title" ] -> Some SurveyTitle
    | [ "short-title" ] -> Some SurveyShortTitle
    | [ "description" ] -> Some SurveyDescription
    | [ "instructions" ] -> Some SurveyInstructions
    | [ "section"; id; "title" ] -> Some(SectionTitle id)
    | [ "section"; id; "description" ] -> Some(SectionDescription id)
    | [ "question"; id; "prompt" ] -> Some(QuestionPrompt id)
    | [ "question"; id; "help" ] -> Some(QuestionHelp id)
    | [ "question"; id; "accessible-name" ] -> Some(AccessibleName id)
    | [ "question"; id; "accessible-description" ] -> Some(AccessibleDescription id)
    | [ "question"; id; rest ] ->
        match rest.Split('/', 2) |> List.ofArray with
        | [ "label"; index ] ->
            match System.Int32.TryParse(index, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture) with
            | true, i when string i = index -> Some(AnswerLabel(id, i))
            | _ -> None
        | [ "option"; option ] when not (option.Contains '/') -> Some(OptionLabel(id, option))
        | _ -> None
    | [ "recommendation"; id; "title" ] -> Some(RecommendationTitle id)
    | [ "recommendation"; id; "description" ] -> Some(RecommendationDescription id)
    | [ "result"; id; label ] when label <> "" -> Some(ResultLabel(id, label))
    | _ -> None

/// The texts in canonical order: by spelling, ordinal.
let orderedTexts (texts: Map<TextKey, string>) =
    texts |> Map.toList |> List.sortWith (fun (a, _) (b, _) -> System.String.CompareOrdinal(keyName a, keyName b))
