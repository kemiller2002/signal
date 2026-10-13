/// What the authoring screens add to the administrator page (WI-0073): the
/// catalog's actions (start, derive, edit, hide) and the draft editor with its
/// validation report and publication preview, exactly as publication judges.
///
/// Pure.
module Echelon.Signal.Admin.AuthoringView

open Echelon.Signal.Engine
open Echelon.Signal.Engine.View
open Echelon.Signal.Admin.Routes
open Echelon.Signal.Admin.AdminApp

let private text (value: string) = Value(Text value)
let private flag (value: bool) = Value(Flag value)

let private severity (f: Findings.Finding) =
    match f.Severity with
    | Findings.Blocker -> "Blocker"
    | Findings.Warning -> "Warning"

let private kindLabel (q: Template.Question) =
    match q.Answer with
    | Primitives.Ordinal 5 -> "five-point, scored"
    | Primitives.Boolean -> "yes or no"
    | Primitives.SingleChoice os -> $"one of {os.Length}"
    | Primitives.MultiChoice m -> $"any of {m.Options.Length}"
    | Primitives.BoundedNumber b -> $"a number {b.Minimum}-{b.Maximum}"
    | Primitives.Ranking r -> $"an order of {r.Options.Length}"
    | Primitives.Allocation a -> $"{a.Total} points shared by {a.Options.Length}"
    | Primitives.BestWorst os -> $"best and least of {os.Length}"
    | other -> (sprintf "%A" other).Split(' ')[0]

let project (model: Model) : (string * ViewValue) list =
    let can = capabilities model
    let survey = match model.Place.View with Ok(Draft s) -> Some s | _ -> None
    let editor = survey |> Option.bind (fun s -> Authoring.current model.Templates s model.Authoring)
    let report = editor |> Option.map Authoring.report
    let sections = editor |> Option.map (fun e -> e.Draft.Content.Sections) |> Option.defaultValue []

    let preview =
        editor
        |> Option.map (fun e ->
            match Authoring.preview model.Templates.Catalog e with
            | Ok summary -> $"Publishing makes {summary.SurveyId} version {summary.NewVersion}: {summary.SectionCount} section(s), {summary.QuestionCount} question(s), template hash {summary.TemplateHash}."
            | Error refusal -> $"Publication would be refused: %A{refusal}")
        |> Option.defaultValue ""

    [ "viewDraft", flag survey.IsSome
      "canEditDrafts", flag (can.Contains AdminState.CanEditDrafts)
      "canPublishTemplates", flag (can.Contains AdminState.CanPublishTemplates)
      "newDraftSurvey", text model.Authoring.NewSurvey
      "newDraftTitle", text model.Authoring.NewTitle
      "hasAuthoringProblem", flag model.Authoring.Problem.IsSome
      "authoringProblem", text (model.Authoring.Problem |> Option.defaultValue "")
      "templateActions",
      Items(
          model.Templates.Published
          |> List.map (fun r ->
              [ "key", Text r.Hash
                "label", Text $"{r.Title} ({r.SurveyId} {r.Version})"
                "canHide", Flag(not r.Hidden && can.Contains AdminState.CanPublishTemplates)
                "canDerive", Flag(not (model.Templates.DraftsById.ContainsKey r.SurveyId) && can.Contains AdminState.CanEditDrafts) ])
      )
      "draftLinks", Items(model.Templates.Drafts |> List.map (fun (s, title) -> [ "key", Text s; "label", Text $"{title} ({s})"; "href", Text(href (Draft s)) ]))
      "hasDraft", flag editor.IsSome
      "hasNoDraft", flag (survey.IsSome && editor.IsNone)
      "draftSurvey", text (survey |> Option.defaultValue "")
      "draftHeading", text (editor |> Option.map (fun e -> $"{e.Draft.Content.Metadata.Title} ({e.Draft.SurveyId}, draft)") |> Option.defaultValue "")
      "draftTitle", text (editor |> Option.map (fun e -> e.Draft.Content.Metadata.Title) |> Option.defaultValue "")
      "draftDescription", text (editor |> Option.bind (fun e -> e.Draft.Content.Metadata.Description) |> Option.defaultValue "")
      "draftUnsaved", flag (editor |> Option.exists _.Unsaved)
      "draftParent", text (editor |> Option.bind (fun e -> e.Draft.Parent) |> Option.map (fun p -> $"Derived from version {p.Version}") |> Option.defaultValue "New survey")
      "newSectionTitle", text model.Authoring.NewSection
      "newQuestionPrompt", text model.Authoring.NewQuestion
      "questionKinds",
      Items(
          QuestionKinds.kinds
          |> List.map (fun (key, _, label, _) -> [ "value", Text key; "label", Text label; "selected", Flag(key = model.Authoring.NewQuestionKind) ])
      )
      "newQuestionDetails", text model.Authoring.NewQuestionDetails
      "questionDetailsHint",
      text (
          QuestionKinds.kinds
          |> List.tryFind (fun (key, _, _, _) -> key = model.Authoring.NewQuestionKind)
          |> Option.map (fun (_, _, _, hint) -> if hint = "" then "No details needed." else $"Details: {hint}.")
          |> Option.defaultValue ""
      )
      "ruleQuestion", text model.Authoring.RuleQuestion
      "ruleWhen", text model.Authoring.RuleWhen
      "ruleAnswer", text model.Authoring.RuleAnswer
      "draftRules",
      Items(
          editor
          |> Option.map (fun e -> e.Draft.Content.Rules.Flow |> List.map (fun r -> [ "id", Text r.Id; "label", Text(QuestionKinds.describeRule e.Draft.Content r) ]))
          |> Option.defaultValue []
      )
      "draftSections",
      Items(
          sections
          |> List.map (fun s ->
              [ "id", Text s.Id
                "title", Text s.Title
                "questionCount", Text(string s.Questions.Length)
                "requiredForCompletion",
                Flag(editor |> Option.exists (fun e -> e.Draft.Content.Rules.Completion.RequiredSections |> List.contains s.Id)) ])
      )
      // Facts, recommendations and completion (RuleAuthoring, DF-SIGNAL-2026-0011).
      "draftFacts",
      Items(
          editor
          |> Option.map (fun e -> e.Draft.Content.Rules.Facts |> List.map (fun f -> [ "id", Text f.Id; "label", Text(RuleAuthoring.describeFact e.Draft.Content f) ]))
          |> Option.defaultValue []
      )
      "draftRecommendations",
      Items(
          editor
          |> Option.map (fun e ->
              e.Draft.Content.Rules.Recommendations
              |> List.map (fun r -> [ "id", Text r.Id; "label", Text(RuleAuthoring.describeRecommendation e.Draft.Content r) ]))
          |> Option.defaultValue []
      )
      "draftCompletion", text (editor |> Option.map (fun e -> RuleAuthoring.describeCompletion e.Draft.Content) |> Option.defaultValue "")
      "minimumAnswered",
      text (editor |> Option.bind (fun e -> e.Draft.Content.Rules.Completion.MinimumAnsweredPercent) |> Option.map (fun p -> string (int p)) |> Option.defaultValue "")
      "factWhen", text model.Authoring.Rules.FactWhen
      "factAnswer", text model.Authoring.Rules.FactAnswer
      "recommendationWhen", text model.Authoring.Rules.RecommendationWhen
      "recommendationAnswer", text model.Authoring.Rules.RecommendationAnswer
      "recommendationTitle", text model.Authoring.Rules.RecommendationTitle
      "recommendationText", text model.Authoring.Rules.RecommendationText
      "recommendationPriorities",
      Items(
          RuleAuthoring.priorities
          |> List.map (fun (key, _, label) -> [ "value", Text key; "label", Text label; "selected", Flag(key = model.Authoring.Rules.RecommendationPriority) ])
      )
      "draftQuestions",
      Items(
          sections
          |> List.collect (fun s -> s.Questions |> List.map (fun q -> [ "id", Text q.Id; "section", Text s.Id; "prompt", Text q.Prompt; "kind", Text(kindLabel q) ]))
      )
      "draftFixtures", Items(editor |> Option.map (fun e -> e.Draft.Fixtures |> List.map (fun f -> [ "id", Text f.Id; "name", Text f.Name ])) |> Option.defaultValue [])
      "draftFindings",
      Items(
          report
          |> Option.map (fun r -> r.Findings |> List.mapi (fun i f -> [ "key", Text $"{i}"; "severity", Text(severity f); "code", Text f.Code; "message", Text f.Message ]))
          |> Option.defaultValue []
      )
      "draftPasses", flag (report |> Option.exists _.Passes)
      "draftHasWarnings", flag (report |> Option.exists (fun r -> not r.Warnings.IsEmpty))
      "acknowledgeWarnings", flag (editor |> Option.exists _.AcknowledgeWarnings)
      // Review before publication (AUT-006 §§64-65).
      "draftReview", text (survey |> Option.bind (fun s -> model.Templates.Reviews |> Map.tryFind s) |> Option.defaultValue "Not saved yet")
      "canRequestReview", flag (can.Contains AdminState.CanEditDrafts)
      "canApproveReview", flag (can.Contains AdminState.CanReviewTemplates)
      "draftPreview", text preview ]
