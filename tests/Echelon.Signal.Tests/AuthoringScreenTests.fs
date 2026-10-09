/// The authoring screens (WI-0073): a draft started, edited, validated,
/// saved, published and hidden through the page, with the same judgement as
/// publication; and a template authored here starts groups.
module Echelon.Signal.Tests.AuthoringScreenTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Authoring
open Echelon.Signal.Tests.AdminPageTests

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private authored =
    Authoring.start "PULSE" "Team pulse" |> ok
    |> apply (AddSection "Focus") |> Result.bind (apply (AddQuestion("S1", "We finish what we start before starting more.", QuestionKinds.Frequency5, "")))
    |> Result.bind (apply (AddQuestion("S1", "Interruptions are rare during focused work.", QuestionKinds.Frequency5, "")))
    |> Result.bind (apply AddMidpointFixture)
    |> ok

[<Fact>]
let ``an authored draft validates as publication will, and has the group pipeline's shape`` () =
    Assert.True((report authored).Passes, $"%A{(report authored).Blockers}")
    Assert.Equal<string list>([ "Q1"; "Q2" ], authored.Draft.Content.Sections.Head.Questions |> List.map _.Id)
    let published, _ = Publication.publish Validation.defaultPolicy Publication.emptyCatalog (acknowledged { authored with AcknowledgeWarnings = true }) DateTimeOffset.UnixEpoch "github:1" authored.Draft |> ok
    Assert.True(TemplateListing.assessmentOf published |> Result.isOk)

    Assert.True(Authoring.start "pulse" "Team pulse" |> Result.isError)
    Assert.True(Authoring.start "PULSE" " " |> Result.isError)
    Assert.True(apply (AddQuestion("S9", "Where?", QuestionKinds.Frequency5, "")) authored |> Result.isError)
    Assert.True(apply (AddQuestion("S1", "  ", QuestionKinds.Frequency5, "")) authored |> Result.isError)
    Assert.Equal<string list>([ "Q2" ], (apply (RemoveQuestion "Q1") authored |> ok).Draft.Content.Sections.Head.Questions |> List.map _.Id)

[<Fact>]
let ``screen events start, refuse duplicates, derive and hide by the catalog`` () =
    let listing = TemplateListing.empty
    let screen, commands = update listing None "newDraftSurvey" None "pulse" emptyScreen
    Assert.Equal("PULSE", screen.NewSurvey)
    let screen, _ = update listing None "newDraftTitle" None "Team pulse" screen
    let screen, commands = update listing None "startDraft" None "" screen
    Assert.Equal<Command list>([ OpenDraft "PULSE" ], commands)
    Assert.True(screen.Editor.IsSome)

    let published, catalog = Publication.publish Validation.defaultPolicy Publication.emptyCatalog (acknowledged { authored with AcknowledgeWarnings = true }) DateTimeOffset.UnixEpoch "github:1" authored.Draft |> ok
    let listing = TemplateListing.ofCatalog catalog []
    let refused, _ = update listing None "startDraft" None "" { emptyScreen with NewSurvey = "PULSE"; NewTitle = "Again" }
    Assert.StartsWith("'PULSE' already exists", refused.Problem.Value)

    let derived, commands = update listing None "deriveDraft" (Some published.Hash) "" emptyScreen
    Assert.Equal<Command list>([ OpenDraft "PULSE" ], commands)
    Assert.Equal(Some "1", derived.Editor |> Option.bind (fun e -> e.Draft.Parent) |> Option.map _.Version)

    let _, hide = update listing None "hideVersion" (Some published.Hash) "" emptyScreen
    Assert.Equal<Command list>([ HideVersion("PULSE", "1") ], hide)

[<Fact>]
let ``through the page a survey is authored, saved, published, offered for groups and hidden`` () =
    let _, _, page = signedIn ()
    page.Event("navigate", key = "/assessments")
    Assert.True(page.Flag "canEditDrafts")
    page.Event("newDraftSurvey", value = "PULSE")
    page.Event("newDraftTitle", value = "Team pulse")
    page.Event("startDraft")
    Assert.True(page.Flag "viewDraft", page.ViewText)
    Assert.True(page.Flag "hasDraft")
    Assert.False(page.Flag "draftPasses")

    page.Event("newSectionTitle", value = "Focus")
    page.Event("addSection")
    page.Event("newQuestionPrompt", value = "We finish what we start before starting more.")
    page.Event("addQuestion", key = "S1")
    page.Event("addFixture")
    Assert.True(page.Flag "draftPasses", page.ViewText)
    Assert.True(page.Flag "draftUnsaved")

    page.Event("saveDraft")
    Assert.False(page.Flag "draftUnsaved", page.ViewText)
    Assert.Contains(page.Items "draftLinks", fun i -> i["key"].GetValue<string>() = "PULSE")

    // Publication needs a review of the stored draft as it is (AUT-006 §§64-65).
    page.Event("acknowledgeWarnings", value = "true")
    page.Event("publishDraft")
    Assert.Contains("has not been reviewed", page.Text "notice")
    Assert.Equal("Not submitted for review", page.Text "draftReview")
    page.Event("requestReview")
    Assert.Equal("Ready for review", page.Text "draftReview")
    page.Event("approveReview")
    Assert.Equal("Reviewed", page.Text "draftReview")
    page.Event("acknowledgeWarnings", value = "true")
    page.Event("publishDraft")
    Assert.Contains("PULSE version 1 is published", page.Text "notice")
    Assert.Contains(page.Items "catalog", fun i -> i["label"].GetValue<string>() = "Team pulse (PULSE 1)")

    page.Event("navigate", key = "/assessments")
    let action = page.Items "templateActions" |> List.find (fun i -> i["label"].GetValue<string>().Contains "PULSE")
    let hash = action["key"].GetValue<string>()
    page.Event("hideVersion", key = hash)
    Assert.Contains("hidden from new groups", page.Text "notice")
    Assert.DoesNotContain(page.Items "catalog", fun i -> i["label"].GetValue<string>().Contains "PULSE")
