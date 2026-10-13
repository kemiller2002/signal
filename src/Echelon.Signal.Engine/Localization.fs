/// Checking template translations and selector accessibility text before
/// publication (CAN-001 §§2, 7; ACR-006 §§17-18; DF-SIGNAL-2026-0020).
/// Every translated text must name something the template has, so a
/// translation can never add meaning: a key for an unknown question,
/// option, label index or result label is a blocker.
///
/// Pure.
module Echelon.Signal.Engine.Localization

open System
open System.Text.RegularExpressions
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.LocalizationModel
open Echelon.Signal.Engine.Findings

/// A BCP 47 language tag in its usual shape: a 2-3 letter language, then
/// subtags of 2-8 letters or digits ("fr", "fr-FR", "zh-Hant-TW").
let private localePattern = Regex(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)

let isLocale (tag: string) = localePattern.IsMatch tag

/// The labels each interpretation can produce.
let private resultLabels (i: Interpretation) =
    match i.Kind with
    | Bands bands -> bands |> List.map _.Label
    | PassFail _ -> [ "pass"; "fail" ]
    | Stages(baseline, stages) -> baseline :: (stages |> List.map _.Label)

/// Whether the template has the text a key translates.
let private exists (content: Content) (key: TextKey) =
    let question id = questions content |> List.tryFind (fun (_, q) -> q.Id = id) |> Option.map snd
    let section id = content.Sections |> List.tryFind (fun s -> s.Id = id)
    let recommendation id = content.Rules.Recommendations |> List.exists (fun r -> r.Id = id)

    match key with
    | SurveyTitle -> true
    | SurveyShortTitle -> content.Metadata.ShortTitle.IsSome
    | SurveyDescription -> content.Metadata.Description.IsSome
    | SurveyInstructions -> content.Metadata.Instructions.IsSome
    | SectionTitle id -> (section id).IsSome
    | SectionDescription id -> section id |> Option.exists (fun s -> s.Description.IsSome)
    | QuestionPrompt id -> (question id).IsSome
    | QuestionHelp id -> question id |> Option.exists (fun q -> q.HelpText.IsSome)
    | AnswerLabel(id, index) -> question id |> Option.exists (fun q -> index >= 0 && index < q.Selector.Labels.Length)
    | OptionLabel(id, option) -> question id |> Option.exists (fun q -> options q.Answer |> List.exists (fun o -> o.Id = option))
    | AccessibleName id -> question id |> Option.exists (fun q -> q.Selector.Accessibility.Name.IsSome)
    | AccessibleDescription id -> question id |> Option.exists (fun q -> q.Selector.Accessibility.Description.IsSome)
    | RecommendationTitle id
    | RecommendationDescription id -> recommendation id
    | ResultLabel(id, label) ->
        content.Results.Interpretations |> List.exists (fun i -> i.Id = id && List.contains label (resultLabels i))

let check (content: Content) : Finding list =
    let block = finding LocalizationCategory Blocker
    let blank (text: string) = String.IsNullOrWhiteSpace text

    [ for id in duplicates (content.Localizations |> List.map _.Locale) do
          block "LOCALE-DUPLICATE" id $"Locale '{id}' is translated more than once."
      for l in content.Localizations do
          if not (isLocale l.Locale) then
              block "LOCALE-TAG" l.Locale $"'{l.Locale}' is not a language tag such as 'fr' or 'fr-FR'."
          if l.Texts.IsEmpty then
              block "LOCALE-EMPTY" l.Locale $"Locale '{l.Locale}' translates nothing."
          for KeyValue(key, text) in l.Texts do
              if not (exists content key) then
                  block "LOCALE-REFERENCE" l.Locale $"'{l.Locale}' translates '{keyName key}', which the template does not have."
              elif blank text then
                  block "LOCALE-BLANK" l.Locale $"'{l.Locale}' gives '{keyName key}' an empty translation."
      for _, q in questions content do
          let a = q.Selector.Accessibility
          if a.Name |> Option.exists blank || a.Description |> Option.exists blank then
              block "ACCESSIBILITY-BLANK" q.Id $"Question '{q.Id}' has empty accessibility text; leave it out instead." ]

/// Every translated text, for the privacy check.
let texts (content: Content) =
    content.Localizations |> List.collect (fun l -> l.Texts |> Map.toList |> List.map (fun (key, text) -> l.Locale, key, text))

// ---- Showing a template in a locale (WI-0091 slice 2) --------------------------------------

/// The translation for a requested locale: the same tag (any letter case),
/// else the first whose language subtag matches ("fr-CA" reads "fr-FR"
/// when there is no "fr-CA"), else none and the source text shows.
let pick (locale: string option) (content: Content) : Localization option =
    let language (tag: string) = tag.Split('-').[0].ToLowerInvariant()

    locale
    |> Option.bind (fun wanted ->
        content.Localizations
        |> List.tryFind (fun l -> String.Equals(l.Locale, wanted, StringComparison.OrdinalIgnoreCase))
        |> Option.orElse (content.Localizations |> List.tryFind (fun l -> language l.Locale = language wanted)))

/// The template as a respondent in `locale` reads it: every translated
/// text in place of its source, nothing else changed. For display only:
/// evaluation, scoring and the encoding always use the published content,
/// which this never replaces.
let display (locale: string option) (content: Content) : Content =
    match pick locale content with
    | None -> content
    | Some l ->
        let t key fallback = l.Texts.TryFind key |> Option.defaultValue fallback
        let tOpt key fallback = fallback |> Option.map (t key)

        let option (qid: string) (o: ChoiceOption) = { o with Label = t (OptionLabel(qid, o.Id)) o.Label }
        let node qid (n: TreeNode) = { n with Option = option qid n.Option }

        let answer qid (a: AnswerDefinition) =
            match a with
            | SingleChoice os -> SingleChoice(List.map (option qid) os)
            | MultiChoice m -> MultiChoice { m with Options = List.map (option qid) m.Options }
            | Ranking r -> Ranking { r with Options = List.map (option qid) r.Options }
            | Allocation x -> Allocation { x with Options = List.map (option qid) x.Options }
            | BestWorst os -> BestWorst(List.map (option qid) os)
            | Hierarchical(nodes, terminal) -> Hierarchical(List.map (node qid) nodes, terminal)
            | HierarchicalMulti(nodes, rule, selection) -> HierarchicalMulti(List.map (node qid) nodes, rule, selection)
            | Boolean
            | Ordinal _
            | BoundedNumber _
            | BoundedRange _ -> a

        let question (q: Question) =
            { q with
                Prompt = t (QuestionPrompt q.Id) q.Prompt
                HelpText = tOpt (QuestionHelp q.Id) q.HelpText
                Answer = answer q.Id q.Answer
                Selector =
                    { q.Selector with
                        Labels = q.Selector.Labels |> List.mapi (fun i label -> t (AnswerLabel(q.Id, i)) label)
                        Accessibility =
                            { Name = tOpt (AccessibleName q.Id) q.Selector.Accessibility.Name
                              Description = tOpt (AccessibleDescription q.Id) q.Selector.Accessibility.Description } } }

        let m = content.Metadata

        { content with
            Metadata =
                { m with
                    Title = t SurveyTitle m.Title
                    ShortTitle = tOpt SurveyShortTitle m.ShortTitle
                    Description = tOpt SurveyDescription m.Description
                    Instructions = tOpt SurveyInstructions m.Instructions }
            Sections =
                content.Sections
                |> List.map (fun s ->
                    { s with
                        Title = t (SectionTitle s.Id) s.Title
                        Description = tOpt (SectionDescription s.Id) s.Description
                        Questions = List.map question s.Questions })
            Rules =
                { content.Rules with
                    Recommendations =
                        content.Rules.Recommendations
                        |> List.map (fun r ->
                            { r with
                                Title = t (RecommendationTitle r.Id) r.Title
                                Description = t (RecommendationDescription r.Id) r.Description }) } }
