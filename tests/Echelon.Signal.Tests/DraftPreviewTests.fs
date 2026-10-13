/// The draft previewed as a respondent would see it, inside the console
/// (WI-0081; AUT-003 §§20-22, SCS-017): the survey page's own engine runs
/// the draft, so conditional questions, pages and completion behave as they
/// will once published; the preview is a test artifact that stores and
/// sends nothing; its answers are scored live and can be kept as a fixture
/// that pins what they score now.
module Echelon.Signal.Tests.DraftPreviewTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Admin
open Echelon.Signal.Tests.AdminPageTests

let private now = DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero)

let private responding =
    function
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private rows (session: Session) =
    match (GenericSessionView.view session |> Map.ofList)["rows"] with
    | View.Items items -> items |> List.map Map.ofList
    | other -> failwith $"%A{other}"

let private questionShown (session: Session) (prompt: string) =
    rows session |> List.exists (fun row -> row["prompt"] = View.Text prompt && row["hideQuestion"] = View.Flag false)

/// The row key of a question's option, as the page sends it.
let private optionKey (session: Session) (questionId: string) (label: string) =
    let r = responding session
    let index = indexedQuestions r |> List.find (fun (_, q) -> q.Id = questionId) |> fst
    let row = rows session |> List.find (fun row -> row["group"] = View.Text $"q{index}" && row["label"] = View.Text label && row["hideOption"] = View.Flag false)
    match row["id"] with View.Text t -> t | other -> failwith $"%A{other}"

[<Fact>]
let ``the preview runs the draft as the survey page will: a test, conditional questions shown when their condition holds`` () =
    let draft = SurveyPageTests.mixedEditor.Draft
    let preview = DraftPreview.start draft now
    let r = responding preview
    Assert.True(isTest r.Binding)
    Assert.False(questionShown preview "How did it ship?")

    let answered = DraftPreview.step "previewAnswered" (Some(optionKey preview "Q2" "Yes")) "" preview
    Assert.Equal(Some(Value(Flag true)), (responding answered).Answers.TryFind "Q2")
    Assert.True(questionShown answered "How did it ship?")

    // An unknown row changes nothing.
    Assert.Equal(answered, DraftPreview.step "previewAnswered" (Some "99-9") "" answered)

[<Fact>]
let ``the preview pages through the draft as the survey page does`` () =
    let draft = { SurveyPageTests.mixedEditor.Draft with Content = { SurveyPageTests.paged with Presentation = { SurveyPageTests.paged.Presentation with Revisit = Template.fullRevisit } } }
    let preview = DraftPreview.start draft now
    let page (s: Session) = (responding s).Position.Page
    let label (s: Session) = (GenericSessionView.view s |> Map.ofList)["pageLabel"]
    Assert.Equal(0, page preview)
    Assert.Equal(View.Value(View.Text "Page 1 of 3"), label preview)

    let ids = Template.questions draft.Content |> List.map (fun (_, q) -> q.Id)
    let first id s = DraftPreview.step "previewAnswered" (Some(optionKey s id (GenericSession.choices (Template.tryQuestion draft.Content id).Value |> List.head |> snd))) "" s
    let moved = preview |> first ids[0] |> first ids[1] |> DraftPreview.step "previewNext" None ""
    Assert.Equal(1, page moved)
    Assert.Equal(View.Value(View.Text "Page 2 of 3"), label moved)
    Assert.Equal(0, page (DraftPreview.step "previewBack" None "" moved))

[<Fact>]
let ``kept preview answers become a fixture that pins what they score now, and publication still passes`` () =
    let editor = SurveyPageTests.mixedEditor
    let answers = (SurveyPageTests.mixedEditor.Draft.Fixtures.Head.Answers)
    let fixture = DraftPreview.fixtureOf editor.Draft answers
    Assert.Equal("preview-1", fixture.Id)
    Assert.Contains(Drafts.ExpectComplete true, fixture.Expect)
    Assert.Contains(fixture.Expect, fun a -> match a with Drafts.ExpectSectionScore("S1", Some _) -> true | _ -> false)

    let kept = Authoring.apply (Authoring.AddFixture fixture) editor |> Result.defaultWith failwith
    Assert.True((Authoring.report kept).Passes, $"%A{(Authoring.report kept).Blockers}")
    Assert.Equal("preview-2", (DraftPreview.fixtureOf kept.Draft answers).Id)

// ---- Through the page ------------------------------------------------------------------------------

[<Fact>]
let ``through the page the draft is previewed, scored live, kept as a fixture, and nothing is stored or sent`` () =
    let _, github, page = signedIn ()
    page.Event("navigate", key = "/assessments")
    page.Event("newDraftSurvey", value = "PULSE")
    page.Event("newDraftTitle", value = "Team pulse")
    page.Event("startDraft")
    page.Event("newSectionTitle", value = "Focus")
    page.Event("addSection")
    page.Event("newQuestionPrompt", value = "We finish what we start before starting more.")
    page.Event("addQuestion", key = "S1")
    Assert.True(page.Flag "canPreview")
    Assert.False(page.Flag "previewing")

    let commits = github.State.History.Length
    page.Event("previewStart")
    Assert.True(page.Flag "previewing", page.ViewText)
    Assert.True(page.Flag "previewIsTest")
    Assert.True(page.Flag "previewAnswering")
    let option = page.Items "previewRows" |> List.find (fun i -> i["hideOption"].GetValue<bool>() = false)
    page.Event("previewAnswered", key = option["id"].GetValue<string>())
    Assert.Equal("1 of 1 answered", page.Text "previewProgress")
    let lines = page.Items "previewExplanation" |> List.map (fun i -> i["kind"].GetValue<string>(), i["text"].GetValue<string>())
    Assert.Contains(lines, fun (kind, text) -> kind = "contribution" && text.StartsWith "Q1: ")
    Assert.Contains(lines, fun (kind, text) -> kind = "calculation" && text.StartsWith "section score: ")

    // Kept as a fixture: the draft changes (unsaved), the preview stays.
    page.Event("previewKeep")
    Assert.Contains(page.Items "draftFixtures", fun i -> i["id"].GetValue<string>() = "preview-1")
    Assert.True(page.Flag "draftUnsaved")

    // Submitting seals the preview; nothing was stored or sent.
    page.Event("previewSubmit")
    Assert.True(page.Flag "previewSubmitted", page.ViewText)
    Assert.Equal(commits, github.State.History.Length)

    // Editing the draft ends the preview, which would show the old draft.
    page.Event("newQuestionPrompt", value = "Interruptions are rare during focused work.")
    page.Event("addQuestion", key = "S1")
    Assert.False(page.Flag "previewing")
    Assert.True(page.Flag "canPreview")
