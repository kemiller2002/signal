/// Authoring facts, recommendations and completion in the console (WI-0081,
/// AST-005, ALG-004, SCS-017, AUT-002, DF-SIGNAL-2026-0011).
module Echelon.Signal.Tests.RuleAuthoringTests

open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Admin

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

/// Q1 five-point, Q2 yes/no, Q3 choice shown when Q2 is yes, ... (SurveyPageTests.mixedEditor).
let private editor = SurveyPageTests.mixedEditor
let private rule change (e: Authoring.Editor) = Authoring.apply (Authoring.Rule change) e

let private authored =
    editor
    |> rule (RuleAuthoring.AddFact("Q2", "Yes"))
    |> Result.bind (rule (RuleAuthoring.AddRecommendation("F1", "", "important", "Keep shipping", "Write down what made shipping possible.")))
    |> Result.bind (rule (RuleAuthoring.AddRecommendation("q1", "Never or almost never", "critical", "Finish work", "Limit work in progress.")))
    |> Result.bind (rule (RuleAuthoring.SetMinimumAnswered "50"))
    |> Result.bind (rule (RuleAuthoring.SetSectionRequiredForCompletion("S1", true)))
    |> ok

let private content = authored.Draft.Content

[<Fact>]
let ``facts, recommendations and completion are authored from plain conditions`` () =
    Assert.Equal<Fact list>([ { Id = "F1"; Expr = BooleanFact(AnswerIs("Q2", Responses.Flag true)) } ], content.Rules.Facts)
    Assert.Equal<string list>([ "R1"; "R2" ], content.Rules.Recommendations |> List.map _.Id)
    Assert.Equal(FactTrue "F1", content.Rules.Recommendations.Head.When)
    Assert.Equal(Some 50.0, content.Rules.Completion.MinimumAnsweredPercent)
    Assert.Equal<string list>([ "S1" ], content.Rules.Completion.RequiredSections)
    // The template declares what its rules use, and still publishes.
    Assert.Contains(UsesDerivedFacts, content.Compatibility.Capabilities)
    Assert.Contains(UsesRecommendations, content.Compatibility.Capabilities)
    let report = Authoring.report authored
    Assert.True(report.Passes, $"%A{report.Blockers}")
    Assert.Equal("F1: true when Q2 is 'Yes'", RuleAuthoring.describeFact content content.Rules.Facts.Head)
    Assert.Equal("R1 (Important): \"Keep shipping\" when F1 is true", RuleAuthoring.describeRecommendation content content.Rules.Recommendations.Head)
    Assert.Contains("At least 50% of applicable questions", RuleAuthoring.describeCompletion content)

[<Fact>]
let ``authored recommendations fire on the answers they name, most urgent first`` () =
    let answer id label =
        let q = (tryQuestion content id).Value
        id, GenericSession.choices q |> List.find (fun (_, l) -> l = label) |> fst

    let answers = Map.ofList [ answer "Q1" "Never or almost never"; answer "Q2" "Yes" ]
    let evaluation = Rules.evaluate content answers
    Assert.Equal<string list>([ "R2"; "R1" ], evaluation.Recommendations |> List.map _.Id)
    Assert.Empty((Rules.evaluate content (Map.ofList [ answer "Q2" "No" ])).Recommendations)

[<Fact>]
let ``what an author gets wrong is refused with the reason, and nothing changes`` () =
    let refused change = (rule change authored |> Result.toOption).IsNone
    Assert.True(refused (RuleAuthoring.AddFact("Q9", "Yes")))
    Assert.True(refused (RuleAuthoring.AddFact("Q2", "Perhaps")))
    Assert.True(refused (RuleAuthoring.AddFact("Q4", "Pairing")))
    Assert.True(refused (RuleAuthoring.AddFact("F1", "No")))
    Assert.True(refused (RuleAuthoring.AddRecommendation("Q2", "Yes", "urgent", "T", "D")))
    Assert.True(refused (RuleAuthoring.AddRecommendation("Q2", "Yes", "suggested", " ", "D")))
    Assert.True(refused (RuleAuthoring.SetMinimumAnswered "150"))
    Assert.True(refused (RuleAuthoring.SetSectionRequiredForCompletion("S9", true)))
    Assert.True(refused (RuleAuthoring.RemoveFact "F9"))
    Assert.Equal(None, (rule (RuleAuthoring.SetMinimumAnswered "") authored |> ok).Draft.Content.Rules.Completion.MinimumAnsweredPercent)

[<Fact>]
let ``removing a question removes the facts and recommendations that depend on it`` () =
    let removed = Authoring.apply (Authoring.RemoveQuestion "Q2") authored |> ok
    Assert.Empty(removed.Draft.Content.Rules.Facts)
    // R1 depended on F1, which depended on Q2; R2 stays.
    Assert.Equal<string list>([ "R2" ], removed.Draft.Content.Rules.Recommendations |> List.map _.Id)

[<Fact>]
let ``the console's form adds a recommendation and then clears`` () =
    let screen = { Authoring.emptyScreen with Editor = Some editor }
    let listing = TemplateListing.empty
    let step (screen: Authoring.Screen) (name, key, value) = Authoring.update listing (Some "MIXED") name key value screen |> fst

    let after =
        [ "recommendationWhen", None, "Q2"
          "recommendationAnswer", None, "No"
          "recommendationPriority", None, "important"
          "recommendationTitle", None, "Ship something"
          "recommendationText", None, "Agree one small release."
          "addRecommendation", None, "" ]
        |> List.fold step screen

    Assert.Equal(None, after.Problem)
    Assert.Equal("Ship something", after.Editor.Value.Draft.Content.Rules.Recommendations.Head.Title)
    Assert.Equal("", after.Rules.RecommendationTitle)
    Assert.True(Authoring.events.Contains "addRecommendation")

[<Fact>]
let ``through the console page a fact, a recommendation and completion are authored`` () =
    let _, _, page = AdminPageTests.signedIn ()
    page.Event("navigate", key = "/assessments")
    page.Event("newDraftSurvey", value = "RULES")
    page.Event("newDraftTitle", value = "Rules")
    page.Event("startDraft")
    page.Event("newSectionTitle", value = "Delivery")
    page.Event("addSection")
    page.Event("newQuestionKind", value = "yesno")
    page.Event("newQuestionPrompt", value = "Did the team ship this month?")
    page.Event("addQuestion", key = "S1")

    page.Event("factWhen", value = "Q1")
    page.Event("factAnswer", value = "Yes")
    page.Event("addFact")
    page.Event("recommendationWhen", value = "F1")
    page.Event("recommendationPriority", value = "important")
    page.Event("recommendationTitle", value = "Keep shipping")
    page.Event("recommendationText", value = "Write down what made shipping possible.")
    page.Event("addRecommendation")
    page.Event("minimumAnswered", value = "80")
    page.Event("sectionRequired", key = "S1", value = "true")

    Assert.False(page.Flag "hasAuthoringProblem", page.ViewText)
    Assert.Equal("F1: true when Q1 is 'Yes'", (page.Items "draftFacts").Head["label"].GetValue<string>())
    Assert.Contains("Keep shipping", (page.Items "draftRecommendations").Head["label"].GetValue<string>())
    Assert.Equal("80", page.Text "minimumAnswered")
    Assert.Contains("At least 80% of applicable questions", page.Text "draftCompletion")
    Assert.True((page.Items "draftSections").Head["requiredForCompletion"].GetValue<bool>())
    Assert.Equal("", page.Text "recommendationTitle")
