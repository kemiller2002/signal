/// What the validation, presentation, scoring and translation areas add to
/// the draft editor (WI-0091 slice 3, `AreaAuthoring`).
///
/// Pure.
module Echelon.Signal.Admin.AreaAuthoringView

open Echelon.Signal.Engine
open Echelon.Signal.Engine.View
open Echelon.Signal.Engine.Template

let private text (value: string) = Value(Text value)
let private flag (value: bool) = Value(Flag value)

let private choices (options: (string * string) list) (selected: string) =
    Items(options |> List.map (fun (value, label) -> [ "value", Text value; "label", Text label; "selected", Flag(value = selected) ]))

let project (content: Content option) (form: AreaAuthoring.Form) : (string * ViewValue) list =
    let p = content |> Option.map _.Presentation |> Option.defaultValue defaultPresentation
    let progress = AreaAuthoring.progressModes |> List.tryFind (fun (_, m, _) -> m = p.Progress) |> Option.map (fun (k, _, _) -> k) |> Option.defaultValue "question"
    let listed f = content |> Option.map f |> Option.defaultValue []

    [ // Validation rules.
      "draftValidation",
      Items(listed (fun c -> c.Rules.Validation |> List.map (fun v -> [ "id", Text v.Id; "label", Text(AreaAuthoring.describeValidation c v) ])))
      "validationKinds", choices AreaAuthoring.validationKinds form.ValidationKind
      "validationIsRange", flag (form.ValidationKind = "range")
      "validationIsRequiredWhen", flag (form.ValidationKind = "required-when")
      "validationIsAnsweredBetween", flag (form.ValidationKind = "answered-between")
      "validationQuestion", text form.ValidationQuestion
      "validationQuestions", text form.ValidationQuestions
      "validationMinimum", text form.ValidationMinimum
      "validationMaximum", text form.ValidationMaximum
      "validationWhen", text form.ValidationWhen
      "validationAnswer", text form.ValidationAnswer
      "validationMessage", text form.ValidationMessage
      // Presentation.
      "itemsPerPage", text (p.ItemsPerPage |> Option.map string |> Option.defaultValue "")
      "showNumbers", flag p.ShowQuestionNumbers
      "progressModes", choices (AreaAuthoring.progressModes |> List.map (fun (k, _, l) -> k, l)) progress
      "allowBack", flag p.AllowBackNavigation
      "reviewBeforeSubmit", flag p.ReviewBeforeSubmit
      "draftTimers", Items(p.Timers |> List.map (fun t -> [ "id", Text t.Id; "label", Text(AreaAuthoring.describeTimer t) ]))
      "timerMinutes", text form.TimerMinutes
      "timerWarning", text form.TimerWarning
      "timerQuestion", text form.TimerQuestion
      "timerHelp", text AreaAuthoring.timerHelp
      "draftBanks", Items(p.Banks |> List.map (fun b -> [ "id", Text b.Id; "label", Text(AreaAuthoring.describeBank b) ]))
      "bankQuestions", text form.BankQuestions
      "bankSelect", text form.BankSelect
      // Scoring: one choice per section.
      "draftScoring",
      Items(
          listed (fun c ->
              c.Sections
              |> List.map (fun s ->
                  let current = AreaAuthoring.aggregateOf s

                  // A row holds no nested list, so each option's
                  // selected state is its own field (`isMean`, ...).
                  [ "id", Text s.Id; "label", Text $"{s.Id} {s.Title}"; "aggregate", Text current ]
                  @ (AreaAuthoring.aggregates |> List.map (fun (k, _, _) -> "is" + string (System.Char.ToUpperInvariant k[0]) + k.Substring 1, Flag(k = current)))))
      )
      // Translations and accessibility text.
      "draftTranslations",
      Items(
          listed (fun c ->
              AreaAuthoring.describeTranslations c
              |> List.map (fun (locale, key, value) -> [ "id", Text $"{locale} {key}"; "label", Text $"{locale} · {key}: {value}" ]))
      )
      "translationLocale", text form.TranslationLocale
      "translationKey", text form.TranslationKey
      "translationText", text form.TranslationText
      "draftAccessibility",
      Items(
          listed (fun c ->
              questions c
              |> List.choose (fun (_, q) ->
                  let a = q.Selector.Accessibility

                  if a = Selectors.noAccessibility then None
                  else Some [ "id", Text q.Id; "label", Text $"""{q.Id}: {defaultArg a.Name "(prompt)"} / {defaultArg a.Description "(no description)"}""" ]))
      )
      "accessibilityQuestion", text form.AccessibilityQuestion
      "accessibilityName", text form.AccessibilityName
      "accessibilityDescription", text form.AccessibilityDescription ]
