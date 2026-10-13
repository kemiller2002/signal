/// The authoring areas the console lacked (WI-0091 slice 3, AUT-001 §5,
/// AUT-002): validation rules, presentation (paging, numbering, progress,
/// back navigation, review, a survey timer and question banks), explicit
/// section scoring, translations and selector accessibility text.
///
/// Each edit is checked here for shape (numbers, ids, a known locale key),
/// and the whole draft is then judged by publication validation, exactly as
/// the other authoring areas are. Custom scoring expressions and section or
/// page timers stay in imported templates.
///
/// Pure.
module Echelon.Signal.Admin.AreaAuthoring

open System
open Echelon.Signal.Engine
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.LocalizationModel

type Edit =
    // Validation rules (RuleModel.ValidationCheck).
    | AddRangeCheck of questionId: string * minimum: string * maximum: string * message: string
    | AddRequiredWhen of questionId: string * whenName: string * answerLabel: string * message: string
    | AddAnsweredBetween of questionIds: string * minimum: string * maximum: string * message: string
    | RemoveValidation of ruleId: string
    // Presentation.
    | SetItemsPerPage of count: string
    | SetShowNumbers of bool
    | SetProgress of mode: string
    | SetAllowBack of bool
    | SetReviewBeforeSubmit of bool
    | AddSurveyTimer of minutes: string * warnAtMinutes: string
    | RemoveTimer of timerId: string
    | AddBank of questionIds: string * select: string
    | RemoveBank of bankId: string
    // Scoring.
    | SetSectionScoring of sectionId: string * aggregate: string
    // Translations and accessibility text.
    | SetTranslation of locale: string * key: string * text: string
    | SetAccessibility of questionId: string * name: string * description: string

/// The progress modes, by page value.
let progressModes =
    [ "question", QuestionProgress, "By question"
      "page", PageProgress, "By page"
      "section", SectionProgress, "By section"
      "none", NoProgress, "No progress shown" ]

/// The section aggregates an author picks from, by page value; "none"
/// leaves the section unscored.
let aggregates =
    [ "mean", Some Scoring.Mean, "Mean"
      "median", Some Scoring.Median, "Median"
      "sum", Some Scoring.RawSum, "Sum"
      "minimum", Some Scoring.Minimum, "Lowest"
      "maximum", Some Scoring.Maximum, "Highest"
      "none", None, "Not scored" ]

let private nextId (prefix: string) (taken: string list) =
    Seq.initInfinite (fun i -> $"{prefix}{i + 1}") |> Seq.find (fun id -> not (List.contains id taken))

let private nonEmpty (what: string) (text: string) =
    if String.IsNullOrWhiteSpace text then Error $"The {what} cannot be empty." else Ok(text.Trim())

let private number (what: string) (text: string) =
    match Double.TryParse(text.Trim(), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
    | true, n when Double.IsFinite n -> Ok n
    | _ -> Error $"The {what} must be a number."

let private whole (what: string) (low: int) (text: string) =
    match Int32.TryParse(text.Trim()) with
    | true, n when n >= low -> Ok n
    | _ -> Error $"The {what} must be a whole number of at least {low}."

let private question (content: Content) (id: string) =
    let id = id.Trim().ToUpperInvariant()
    match tryQuestion content id with
    | Some _ -> Ok id
    | None -> Error $"There is no question '{id}'."

let private questionList (content: Content) (ids: string) =
    ids.Split([| ','; ' ' |], StringSplitOptions.RemoveEmptyEntries)
    |> List.ofArray
    |> List.fold (fun acc id -> acc |> Result.bind (fun xs -> question content id |> Result.map (fun x -> xs @ [ x ]))) (Ok [])
    |> Result.bind (fun xs -> if xs.Length < 2 then Error "Name at least two questions, separated by commas." else Ok(List.distinct xs))

let private withRules change (content: Content) = { content with Rules = change content.Rules }
let private withPresentation change (content: Content) = { content with Presentation = change content.Presentation }

let private addValidation (check: ValidationCheck) (message: string) (content: Content) =
    nonEmpty "message" message
    |> Result.map (fun m ->
        content
        |> withRules (fun r -> { r with Validation = r.Validation @ [ { Id = nextId "V" (r.Validation |> List.map _.Id); Check = check; Message = m } ] }))

let private optionalText (text: string) = if String.IsNullOrWhiteSpace text then None else Some(text.Trim())

/// Sets or clears one translated text; a locale left with no text goes.
let private translate (locale: string) (key: TextKey) (text: string) (content: Content) =
    let others, existing = content.Localizations |> List.partition (fun l -> l.Locale <> locale)
    let texts = existing |> List.tryHead |> Option.map _.Texts |> Option.defaultValue Map.empty
    let next = if String.IsNullOrWhiteSpace text then texts.Remove key else texts.Add(key, text.Trim())

    { content with
        Localizations =
            (if next.IsEmpty then others else others @ [ { Locale = locale; Texts = next } ])
            |> List.sortWith (fun a b -> String.CompareOrdinal(a.Locale, b.Locale)) }

/// Applies one edit to the content; it is unchanged when refused.
let apply (defaultScorer: Scoring.Scorer) (edit: Edit) (content: Content) : Result<Content, string> =
    match edit with
    | AddRangeCheck(id, low, high, message) ->
        question content id
        |> Result.bind (fun q -> number "lowest value" low |> Result.map (fun l -> q, l))
        |> Result.bind (fun (q, l) -> number "highest value" high |> Result.map (fun h -> q, l, h))
        |> Result.bind (fun (q, l, h) -> if l > h then Error "The lowest value is above the highest." else Ok(q, l, h))
        |> Result.bind (fun (q, l, h) -> addValidation (AllowedRange(q, l, h)) message content)
    | AddRequiredWhen(id, whenName, answer, message) ->
        question content id
        |> Result.bind (fun q -> RuleAuthoring.condition content whenName answer |> Result.map (fun c -> q, c))
        |> Result.bind (fun (q, c) -> addValidation (RequiredWhen(q, c)) message content)
    | AddAnsweredBetween(ids, low, high, message) ->
        questionList content ids
        |> Result.bind (fun qs -> whole "fewest answered" 0 low |> Result.map (fun l -> qs, l))
        |> Result.bind (fun (qs, l) -> whole "most answered" 0 high |> Result.map (fun h -> qs, l, h))
        |> Result.bind (fun (qs, l, h) -> if l > h || h > qs.Length then Error "The numbers answered must fit the questions named." else Ok(qs, l, h))
        |> Result.bind (fun (qs, l, h) -> addValidation (AnsweredBetween(qs, l, h)) message content)
    | RemoveValidation id when content.Rules.Validation |> List.exists (fun v -> v.Id = id) ->
        Ok(content |> withRules (fun r -> { r with Validation = r.Validation |> List.filter (fun v -> v.Id <> id) }))
    | RemoveValidation id -> Error $"There is no validation rule '{id}'."
    | SetItemsPerPage count when String.IsNullOrWhiteSpace count -> Ok(content |> withPresentation (fun p -> { p with ItemsPerPage = None }))
    | SetItemsPerPage count -> whole "questions per page" 1 count |> Result.map (fun n -> content |> withPresentation (fun p -> { p with ItemsPerPage = Some n }))
    | SetShowNumbers on -> Ok(content |> withPresentation (fun p -> { p with ShowQuestionNumbers = on }))
    | SetProgress mode ->
        match progressModes |> List.tryFind (fun (key, _, _) -> key = mode) with
        | Some(_, m, _) -> Ok(content |> withPresentation (fun p -> { p with Progress = m }))
        | None -> Error "Choose how progress is shown."
    | SetAllowBack on -> Ok(content |> withPresentation (fun p -> { p with AllowBackNavigation = on }))
    | SetReviewBeforeSubmit on -> Ok(content |> withPresentation (fun p -> { p with ReviewBeforeSubmit = on }))
    | AddSurveyTimer(minutes, warn) ->
        whole "minutes" 1 minutes
        |> Result.bind (fun m ->
            (if String.IsNullOrWhiteSpace warn then Ok [] else whole "warning minutes" 1 warn |> Result.map (fun w -> [ w ]))
            |> Result.bind (fun ws -> if ws |> List.exists (fun w -> w >= m) then Error "Warn before the time runs out." else Ok(m, ws)))
        |> Result.bind (fun (m, ws) ->
            if content.Presentation.Timers |> List.exists (fun t -> t.Scope = Timers.SurveyScope) then
                Error "The survey already has a timer; remove it first."
            else
                let timer: Timers.Timer =
                    { Id = nextId "timer-" (content.Presentation.Timers |> List.map _.Id)
                      Scope = Timers.SurveyScope
                      Duration = TimeSpan.FromMinutes(float m)
                      Warnings = ws |> List.map (fun w -> TimeSpan.FromMinutes(float w))
                      Expiry = Timers.LockOnly
                      Enforcement = Timers.Enforced }

                Ok(content |> withPresentation (fun p -> { p with Timers = p.Timers @ [ timer ] })))
    | RemoveTimer id when content.Presentation.Timers |> List.exists (fun t -> t.Id = id) ->
        Ok(content |> withPresentation (fun p -> { p with Timers = p.Timers |> List.filter (fun t -> t.Id <> id) }))
    | RemoveTimer id -> Error $"There is no timer '{id}'."
    | AddBank(ids, select) ->
        questionList content ids
        |> Result.bind (fun pool -> whole "questions each respondent answers" 1 select |> Result.map (fun n -> pool, n))
        |> Result.bind (fun (pool, n) -> if n >= pool.Length then Error "A bank selects fewer questions than it holds." else Ok(pool, n))
        |> Result.map (fun (pool, n) ->
            // Template order, as banks keep it.
            let ordered = questions content |> List.map (fun (_, q) -> q.Id) |> List.filter (fun id -> List.contains id pool)
            let bank: Banking.Bank = { Id = nextId "bank-" (content.Presentation.Banks |> List.map _.Id); Pool = ordered; Select = n }
            content |> withPresentation (fun p -> { p with Banks = p.Banks @ [ bank ] }))
    | RemoveBank id when content.Presentation.Banks |> List.exists (fun b -> b.Id = id) ->
        Ok(content |> withPresentation (fun p -> { p with Banks = p.Banks |> List.filter (fun b -> b.Id <> id) }))
    | RemoveBank id -> Error $"There is no bank '{id}'."
    | SetSectionScoring(sectionId, aggregate) ->
        match content.Sections |> List.tryFind (fun s -> s.Id = sectionId), aggregates |> List.tryFind (fun (key, _, _) -> key = aggregate) with
        | None, _ -> Error $"There is no section '{sectionId}'."
        | _, None -> Error "Choose how the section is scored."
        | Some _, Some(_, choice, _) ->
            let scored (s: Section) =
                match choice, s.Scoring with
                | None, _ -> { s with Scoring = None }
                | Some a, Some sc -> { s with Scoring = Some { sc with Scorer = { sc.Scorer with Aggregate = a } } }
                | Some a, None ->
                    let five = s.Questions |> List.filter QuestionKinds.isScored |> List.map _.Id
                    let ids = if five.Length = s.Questions.Length then [] else five
                    { s with Scoring = Some { Scorer = { defaultScorer with Aggregate = a }; Questions = ids } }

            Ok { content with Sections = content.Sections |> List.map (fun s -> if s.Id = sectionId then scored s else s) }
    | SetTranslation(locale, keyName, text) ->
        let locale = locale.Trim()

        match Localization.isLocale locale, keyOf (keyName.Trim()) with
        | false, _ -> Error $"'{locale}' is not a language tag such as 'fr' or 'fr-FR'."
        | _, None -> Error $"'{keyName}' is not a text the template can translate."
        | true, Some key -> Ok(translate locale key text content)
    | SetAccessibility(id, name, description) ->
        question content id
        |> Result.map (fun qid ->
            let a: Selectors.SelectorAccessibility = { Name = optionalText name; Description = optionalText description }

            { content with
                Sections =
                    content.Sections
                    |> List.map (fun s ->
                        { s with Questions = s.Questions |> List.map (fun q -> if q.Id = qid then { q with Selector = { q.Selector with Accessibility = a } } else q) }) })

// ---- How each area reads in the console --------------------------------------------------------

let describeValidation (content: Content) (v: ValidationRule) =
    let rule =
        match v.Check with
        | AllowedRange(q, l, h) -> $"{q} must be from {l} to {h}"
        | RequiredWhen(q, c) -> $"{q} must be answered when {QuestionKinds.describeCondition content c}"
        | AnsweredBetween(qs, l, h) -> $"""from {l} to {h} of {String.Join(", ", qs)} must be answered"""
        | DistinctAnswers qs -> $"""{String.Join(", ", qs)} must differ"""
        | Prohibited c -> $"refused when {QuestionKinds.describeCondition content c}"

    $"{v.Id}: {rule} (\"{v.Message}\")"

let describeTimer (t: Timers.Timer) =
    let warn = t.Warnings |> List.map (fun w -> $", warning at {int w.TotalMinutes} min left") |> String.concat ""
    $"{t.Id}: {int t.Duration.TotalMinutes} minutes for the whole survey{warn}"

let describeBank (b: Banking.Bank) =
    $"""{b.Id}: each respondent answers {b.Select} of {String.Join(", ", b.Pool)}"""

let aggregateOf (s: Section) =
    match s.Scoring with
    | None -> "none"
    | Some sc -> aggregates |> List.tryFind (fun (_, a, _) -> a = Some sc.Scorer.Aggregate) |> Option.map (fun (key, _, _) -> key) |> Option.defaultValue "imported"

/// Every translation as "locale · key: text".
let describeTranslations (content: Content) =
    content.Localizations
    |> List.collect (fun l -> orderedTexts l.Texts |> List.map (fun (key, text) -> l.Locale, keyName key, text))

// ---- The form the screens hold ------------------------------------------------------------------

type Form =
    { ValidationKind: string
      ValidationQuestion: string
      ValidationQuestions: string
      ValidationMinimum: string
      ValidationMaximum: string
      ValidationWhen: string
      ValidationAnswer: string
      ValidationMessage: string
      TimerMinutes: string
      TimerWarning: string
      BankQuestions: string
      BankSelect: string
      TranslationLocale: string
      TranslationKey: string
      TranslationText: string
      AccessibilityQuestion: string
      AccessibilityName: string
      AccessibilityDescription: string }

let emptyForm =
    { ValidationKind = "range"
      ValidationQuestion = ""
      ValidationQuestions = ""
      ValidationMinimum = ""
      ValidationMaximum = ""
      ValidationWhen = ""
      ValidationAnswer = ""
      ValidationMessage = ""
      TimerMinutes = ""
      TimerWarning = ""
      BankQuestions = ""
      BankSelect = ""
      TranslationLocale = ""
      TranslationKey = ""
      TranslationText = ""
      AccessibilityQuestion = ""
      AccessibilityName = ""
      AccessibilityDescription = "" }

/// The validation rule kinds, by page value.
let validationKinds =
    [ "range", "A number or point within a range"
      "required-when", "Required when another answer holds"
      "answered-between", "How many of several questions are answered" ]

/// The page events this form owns.
let events =
    set
        [ "validationKind"; "validationQuestion"; "validationQuestions"; "validationMinimum"; "validationMaximum"; "validationWhen"; "validationAnswer"
          "validationMessage"; "addValidation"; "removeValidation"
          "itemsPerPage"; "showNumbers"; "progressMode"; "allowBack"; "reviewBeforeSubmit"
          "timerMinutes"; "timerWarning"; "addTimer"; "removeTimer"; "bankQuestions"; "bankSelect"; "addBank"; "removeBank"
          "sectionScoring"
          "translationLocale"; "translationKey"; "translationText"; "setTranslation"; "removeTranslation"
          "accessibilityQuestion"; "accessibilityName"; "accessibilityDescription"; "setAccessibility" ]

let private checkedValue (value: string) = value <> "" && value <> "false"

/// One form event: the form after it, and the edit it asks for, if any.
let update (name: string) (key: string option) (value: string) (form: Form) : Form * Edit option =
    match name, key with
    | "validationKind", _ -> { form with ValidationKind = value }, None
    | "validationQuestion", _ -> { form with ValidationQuestion = value }, None
    | "validationQuestions", _ -> { form with ValidationQuestions = value }, None
    | "validationMinimum", _ -> { form with ValidationMinimum = value }, None
    | "validationMaximum", _ -> { form with ValidationMaximum = value }, None
    | "validationWhen", _ -> { form with ValidationWhen = value }, None
    | "validationAnswer", _ -> { form with ValidationAnswer = value }, None
    | "validationMessage", _ -> { form with ValidationMessage = value }, None
    | "addValidation", _ ->
        form,
        Some(
            match form.ValidationKind with
            | "required-when" -> AddRequiredWhen(form.ValidationQuestion, form.ValidationWhen, form.ValidationAnswer, form.ValidationMessage)
            | "answered-between" -> AddAnsweredBetween(form.ValidationQuestions, form.ValidationMinimum, form.ValidationMaximum, form.ValidationMessage)
            | _ -> AddRangeCheck(form.ValidationQuestion, form.ValidationMinimum, form.ValidationMaximum, form.ValidationMessage)
        )
    | "removeValidation", Some id -> form, Some(RemoveValidation id)
    | "itemsPerPage", _ -> form, Some(SetItemsPerPage value)
    | "showNumbers", _ -> form, Some(SetShowNumbers(checkedValue value))
    | "progressMode", _ -> form, Some(SetProgress value)
    | "allowBack", _ -> form, Some(SetAllowBack(checkedValue value))
    | "reviewBeforeSubmit", _ -> form, Some(SetReviewBeforeSubmit(checkedValue value))
    | "timerMinutes", _ -> { form with TimerMinutes = value }, None
    | "timerWarning", _ -> { form with TimerWarning = value }, None
    | "addTimer", _ -> form, Some(AddSurveyTimer(form.TimerMinutes, form.TimerWarning))
    | "removeTimer", Some id -> form, Some(RemoveTimer id)
    | "bankQuestions", _ -> { form with BankQuestions = value }, None
    | "bankSelect", _ -> { form with BankSelect = value }, None
    | "addBank", _ -> form, Some(AddBank(form.BankQuestions, form.BankSelect))
    | "removeBank", Some id -> form, Some(RemoveBank id)
    | "sectionScoring", Some sectionId -> form, Some(SetSectionScoring(sectionId, value))
    | "translationLocale", _ -> { form with TranslationLocale = value }, None
    | "translationKey", _ -> { form with TranslationKey = value }, None
    | "translationText", _ -> { form with TranslationText = value }, None
    | "setTranslation", _ -> form, Some(SetTranslation(form.TranslationLocale, form.TranslationKey, form.TranslationText))
    // A listed translation's key is "<locale> <text key>".
    | "removeTranslation", Some id ->
        match id.Split(' ', 2) with
        | [| locale; textKey |] -> form, Some(SetTranslation(locale, textKey, ""))
        | _ -> form, None
    | "accessibilityQuestion", _ -> { form with AccessibilityQuestion = value }, None
    | "accessibilityName", _ -> { form with AccessibilityName = value }, None
    | "accessibilityDescription", _ -> { form with AccessibilityDescription = value }, None
    | "setAccessibility", _ -> form, Some(SetAccessibility(form.AccessibilityQuestion, form.AccessibilityName, form.AccessibilityDescription))
    | _ -> form, None

/// The form after its edit was applied: an add clears what it used.
let cleared (edit: Edit) (form: Form) =
    match edit with
    | AddRangeCheck _
    | AddRequiredWhen _
    | AddAnsweredBetween _ ->
        { form with
            ValidationQuestion = ""
            ValidationQuestions = ""
            ValidationMinimum = ""
            ValidationMaximum = ""
            ValidationWhen = ""
            ValidationAnswer = ""
            ValidationMessage = "" }
    | AddSurveyTimer _ -> { form with TimerMinutes = ""; TimerWarning = "" }
    | AddBank _ -> { form with BankQuestions = ""; BankSelect = "" }
    | SetTranslation(_, _, text) when text <> "" -> { form with TranslationKey = ""; TranslationText = "" }
    | SetAccessibility _ -> { form with AccessibilityQuestion = ""; AccessibilityName = ""; AccessibilityDescription = "" }
    | _ -> form
