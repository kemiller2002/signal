/// The console's validation, presentation, scoring, translation and
/// accessibility areas (WI-0091 slice 3, AUT-001 §5, AUT-002).
module Echelon.Signal.Tests.AreaAuthoringTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.LocalizationModel
open Echelon.Signal.Admin
open Echelon.Signal.Admin.AreaAuthoring

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

/// Q1 five-point, Q2 yes/no, Q3 choice shown when Q2 is yes, ... (SurveyPageTests.mixedEditor).
let private editor = SurveyPageTests.mixedEditor
let private area change (e: Authoring.Editor) = Authoring.apply (Authoring.Area change) e
let private content (e: Authoring.Editor) = e.Draft.Content
let private refused change e = (area change e |> Result.toOption).IsNone

let private numberQuestion =
    questions editor.Draft.Content |> List.map snd |> List.find (fun q -> match q.Answer with Primitives.BoundedNumber _ -> true | _ -> false)

[<Fact>]
let ``validation rules are authored, described and removed`` () =
    let authored =
        editor
        |> area (AddRangeCheck(numberQuestion.Id, "1", "10", "Give a number from 1 to 10."))
        |> Result.bind (area (AddRequiredWhen("q3", "Q2", "Yes", "Say which one.")))
        |> Result.bind (area (AddAnsweredBetween("Q1, Q2", "1", "2", "Answer at least one.")))
        |> ok

    let rules = (content authored).Rules.Validation
    Assert.Equal<string list>([ "V1"; "V2"; "V3" ], rules |> List.map _.Id)
    Assert.Equal(AllowedRange(numberQuestion.Id, 1.0, 10.0), rules.Head.Check)
    Assert.Equal(AnsweredBetween([ "Q1"; "Q2" ], 1, 2), rules[2].Check)
    Assert.Contains("must be from 1 to 10", describeValidation (content authored) rules.Head)
    Assert.True((Authoring.report authored).Passes, $"%A{(Authoring.report authored).Blockers}")
    Assert.Empty((area (RemoveValidation "V2") authored |> ok |> content).Rules.Validation |> List.filter (fun v -> v.Id = "V2"))

    Assert.True(refused (AddRangeCheck("Q1", "x", "4", "m")) editor)
    Assert.True(refused (AddRangeCheck("Q1", "5", "1", "m")) editor)
    Assert.True(refused (AddRangeCheck("Q1", "1", "4", " ")) editor)
    Assert.True(refused (AddRangeCheck("nope", "1", "4", "m")) editor)
    Assert.True(refused (AddAnsweredBetween("Q1", "0", "1", "m")) editor)
    Assert.True(refused (AddAnsweredBetween("Q1, Q2", "1", "3", "m")) editor)
    Assert.True(refused (RemoveValidation "V9") editor)

[<Fact>]
let ``presentation, a survey time limit and a question bank are authored`` () =
    let authored =
        editor
        |> area (SetItemsPerPage "2")
        |> Result.bind (area (SetShowNumbers false))
        |> Result.bind (area (SetProgress "page"))
        |> Result.bind (area (SetAllowBack false))
        |> Result.bind (area (SetReviewBeforeSubmit true))
        |> Result.bind (area (AddSurveyTimer("20", "5")))
        |> Result.bind (area (AddBank("Q1, Q2", "1")))
        |> ok

    let p = (content authored).Presentation
    Assert.Equal(Some 2, p.ItemsPerPage)
    Assert.False p.ShowQuestionNumbers
    Assert.Equal(PageProgress, p.Progress)
    Assert.False p.AllowBackNavigation
    Assert.True p.ReviewBeforeSubmit
    Assert.Equal(TimeSpan.FromMinutes 20.0, p.Timers.Head.Duration)
    Assert.Equal<TimeSpan list>([ TimeSpan.FromMinutes 5.0 ], p.Timers.Head.Warnings)
    Assert.Equal("timer-1: 20 minutes for the whole survey, warning at 5 min left", describeTimer p.Timers.Head)
    Assert.Equal<Banking.Bank list>([ ({ Id = "bank-1"; Pool = [ "Q1"; "Q2" ]; Select = 1 }: Banking.Bank) ], p.Banks)

    Assert.Equal(None, (area (SetItemsPerPage "") authored |> ok |> content).Presentation.ItemsPerPage)
    Assert.True(refused (AddSurveyTimer("10", "")) authored) // one survey timer
    Assert.True(refused (AddSurveyTimer("10", "10")) editor)
    Assert.True(refused (AddBank("Q1, Q2", "2")) editor)
    Assert.True(refused (SetProgress "sideways") editor)
    Assert.True(refused (SetItemsPerPage "0") editor)
    Assert.Empty((area (RemoveTimer "timer-1") authored |> ok |> content).Presentation.Timers)
    Assert.Empty((area (RemoveBank "bank-1") authored |> ok |> content).Presentation.Banks)

[<Fact>]
let ``a section's scoring is chosen explicitly`` () =
    let s1 = (content editor).Sections.Head.Id
    let summed = area (SetSectionScoring(s1, "sum")) editor |> ok
    Assert.Equal(Scoring.RawSum, (content summed).Sections.Head.Scoring.Value.Scorer.Aggregate)
    Assert.Equal("sum", aggregateOf (content summed).Sections.Head)
    let unscored = area (SetSectionScoring(s1, "none")) summed |> ok
    Assert.Equal(None, (content unscored).Sections.Head.Scoring)
    let rescored = area (SetSectionScoring(s1, "median")) unscored |> ok
    Assert.Equal(Scoring.Median, (content rescored).Sections.Head.Scoring.Value.Scorer.Aggregate)
    Assert.True((Authoring.report rescored).Passes, $"%A{(Authoring.report rescored).Blockers}")
    Assert.True(refused (SetSectionScoring(s1, "average")) editor)
    Assert.True(refused (SetSectionScoring("S9", "mean")) editor)

[<Fact>]
let ``translations and accessibility text are authored and checked like any other`` () =
    let authored =
        editor
        |> area (SetTranslation("fr", "question/Q1/prompt", "Combien de fois ?"))
        |> Result.bind (area (SetTranslation("fr", "title", "Enquête")))
        |> Result.bind (area (SetAccessibility("q1", "Frequency", "Five steps")))
        |> Result.bind (area (SetTranslation("fr", "question/Q1/accessible-name", "Fréquence")))
        |> ok

    let c = content authored
    Assert.Equal("Combien de fois ?", c.Localizations.Head.Texts[QuestionPrompt "Q1"])
    Assert.Equal(Some "Frequency", (tryQuestion c "Q1").Value.Selector.Accessibility.Name)
    Assert.Contains(("fr", "title", "Enquête"), describeTranslations c)
    Assert.Contains(Template.UsesLocalization, Template.supportedCapabilities)
    Assert.True((Authoring.report authored).Passes, $"%A{(Authoring.report authored).Blockers}")

    // Clearing the last text of a locale removes the locale.
    let cleared =
        authored
        |> area (SetTranslation("fr", "question/Q1/prompt", ""))
        |> Result.bind (area (SetTranslation("fr", "title", "")))
        |> Result.bind (area (SetTranslation("fr", "question/Q1/accessible-name", "")))
        |> ok

    Assert.Empty((content cleared).Localizations)

    Assert.True(refused (SetTranslation("French", "title", "x")) editor)
    Assert.True(refused (SetTranslation("fr", "question/Q1", "x")) editor)
    // A key the template lacks is accepted by the form and blocked by validation.
    let unknown = area (SetTranslation("fr", "question/Q99/prompt", "x")) editor |> ok
    Assert.Contains("LOCALE-REFERENCE", (Authoring.report unknown).Blockers |> List.map _.Code)

[<Fact>]
let ``the form keeps what is typed, sends one edit per action and clears it after`` () =
    let typed =
        [ "validationKind", "range"; "validationQuestion", "Q1"; "validationMinimum", "1"; "validationMaximum", "3"; "validationMessage", "m" ]
        |> List.fold (fun form (name, value) -> fst (update name None value form)) emptyForm

    let _, edit = update "addValidation" None "" typed
    Assert.Equal(Some(AddRangeCheck("Q1", "1", "3", "m")), edit)
    Assert.Equal("", (cleared edit.Value typed).ValidationQuestion)
    Assert.Equal(Some(SetShowNumbers true), snd (update "showNumbers" None "true" emptyForm))
    Assert.Equal(Some(SetShowNumbers false), snd (update "showNumbers" None "" emptyForm))
    Assert.Equal(Some(SetTranslation("fr", "title", "")), snd (update "removeTranslation" (Some "fr title") "" emptyForm))
    Assert.Equal(Some(SetSectionScoring("S1", "sum")), snd (update "sectionScoring" (Some "S1") "sum" emptyForm))
    Assert.True(Set.isSubset events Authoring.events)

[<Fact>]
let ``through the console page each area is authored`` () =
    let _, _, page = AdminPageTests.signedIn ()
    page.Event("navigate", key = "/assessments")
    page.Event("newDraftSurvey", value = "AREAS")
    page.Event("newDraftTitle", value = "Areas")
    page.Event("startDraft")
    page.Event("newSectionTitle", value = "Delivery")
    page.Event("addSection")
    page.Event("newQuestionKind", value = "frequency5")
    page.Event("newQuestionPrompt", value = "How often does the team ship?")
    page.Event("addQuestion", key = "S1")
    page.Event("newQuestionKind", value = "number")
    page.Event("newQuestionDetails", value = "0-20")
    page.Event("newQuestionPrompt", value = "How many releases?")
    page.Event("addQuestion", key = "S1")

    page.Event("validationQuestion", value = "Q2")
    page.Event("validationMinimum", value = "1")
    page.Event("validationMaximum", value = "12")
    page.Event("validationMessage", value = "From 1 to 12 releases.")
    page.Event("addValidation")
    page.Event("itemsPerPage", value = "1")
    page.Event("progressMode", value = "page")
    page.Event("timerMinutes", value = "15")
    page.Event("addTimer")
    page.Event("sectionScoring", key = "S1", value = "median")
    page.Event("translationLocale", value = "fr")
    page.Event("translationKey", value = "question/Q1/prompt")
    page.Event("translationText", value = "À quelle fréquence ?")
    page.Event("setTranslation")
    page.Event("accessibilityQuestion", value = "Q1")
    page.Event("accessibilityName", value = "Shipping frequency")
    page.Event("setAccessibility")

    Assert.False(page.Flag "hasAuthoringProblem", page.ViewText)
    Assert.Contains("must be from 1 to 12", (page.Items "draftValidation").Head["label"].GetValue<string>())
    Assert.Equal("1", page.Text "itemsPerPage")
    Assert.Contains("15 minutes", (page.Items "draftTimers").Head["label"].GetValue<string>())
    Assert.True((page.Items "draftScoring").Head["isMedian"].GetValue<bool>())
    Assert.Equal("fr · question/Q1/prompt: À quelle fréquence ?", (page.Items "draftTranslations").Head["label"].GetValue<string>())
    Assert.Contains("Shipping frequency", (page.Items "draftAccessibility").Head["label"].GetValue<string>())
    Assert.Equal("", page.Text "translationText")
