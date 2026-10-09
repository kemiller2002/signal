/// The authoring screens' editing (WI-0073, AUT-001..AUT-005): a draft is
/// started new or derived from a published version, edited through a closed
/// set of edits, validated and previewed exactly as publication will judge
/// it. Questions are of the kinds `QuestionKinds` offers; a section's score
/// keeps to its five-point questions, read by the catalog scorer. A question
/// may be shown only when an earlier one has a given answer.
///
/// Pure.
module Echelon.Signal.Admin.Authoring

open System.Text.RegularExpressions
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Drafts

/// The draft being edited and what the editor knows about it.
type Editor =
    { Draft: Draft
      /// Changed since it was last saved.
      Unsaved: bool
      /// The publisher accepts the validation warnings (AUT-005 §51).
      AcknowledgeWarnings: bool }

/// The edits the screens offer.
type Edit =
    | SetTitle of string
    | SetDescription of string
    | AddSection of title: string
    | RemoveSection of sectionId: string
    | AddQuestion of sectionId: string * prompt: string * kind: QuestionKinds.Kind * details: string
    | RemoveQuestion of questionId: string
    /// Show a question only when an earlier question has an answer (named by its label).
    | AddShowRule of questionId: string * whenQuestion: string * answerLabel: string
    | RemoveRule of ruleId: string
    /// A fixture where every question is answered at the midpoint.
    | AddMidpointFixture

let private surveyIdPattern = Regex(@"^[A-Z][A-Z0-9-]{1,31}$", RegexOptions.CultureInvariant)

/// Why a survey identifier is not usable, if it is not.
let surveyIdProblem (surveyId: string) =
    if surveyIdPattern.IsMatch surveyId then None
    else Some "A survey identifier is 2 to 32 capital letters, digits or hyphens, starting with a letter."

/// A new draft, or why not.
let start (surveyId: string) (title: string) : Result<Editor, string> =
    match surveyIdProblem surveyId, title.Trim() with
    | Some problem, _ -> Error problem
    | None, "" -> Error "A draft needs a title."
    | None, title -> Ok { Draft = newDraft surveyId title; Unsaved = true; AcknowledgeWarnings = false }

/// An editor over a stored or derived draft.
let editing (draft: Draft) (unsaved: bool) =
    { Draft = draft; Unsaved = unsaved; AcknowledgeWarnings = false }

/// The scorer every authored section uses: the catalog scorer with one usable answer required.
let private scorer = Assessment.dimensionScorer { Pilot.assessment with MinimumNumericAnswers = 1 }

let private nextId (prefix: string) (taken: string list) =
    Seq.initInfinite (fun i -> $"{prefix}{i + 1}") |> Seq.find (fun id -> not (List.contains id taken))

let private describe (error: EditError) =
    match error with
    | UnknownSection id -> $"There is no section '{id}'."
    | UnknownQuestionId id -> $"There is no question '{id}'."
    | DuplicateId id -> $"'{id}' is already used."
    | PositionOutOfRange p -> $"Position {p} is out of range."

let private nonEmpty (what: string) (text: string) =
    if System.String.IsNullOrWhiteSpace text then Error $"The {what} cannot be empty." else Ok(text.Trim())

/// A question's middle answer: the middle value, or a multi-choice's first option.
let private midpoint (q: Question) =
    match GenericSession.toggles q, GenericSession.choices q |> List.filter (fun (s, _) -> match s with Responses.Value _ -> true | _ -> false) with
    | (first, _) :: _, _ -> Some(Responses.Value(Responses.Choices(Set.singleton first)))
    | [], [] -> None
    | [], values -> Some(fst values[values.Length / 2])

/// Applies one edit; the draft is unchanged when it is refused.
let apply (edit: Edit) (editor: Editor) : Result<Editor, string> =
    let draft = editor.Draft
    let content = draft.Content

    let edited =
        match edit with
        | SetTitle title -> nonEmpty "title" title |> Result.map (fun t -> editContent (fun c -> { c with Metadata = { c.Metadata with Title = t } }) draft)
        | SetDescription text ->
            Ok(editContent (fun c -> { c with Metadata = { c.Metadata with Description = (if System.String.IsNullOrWhiteSpace text then None else Some(text.Trim())) } }) draft)
        | AddSection title ->
            nonEmpty "section title" title
            |> Result.bind (fun t ->
                addSection
                    { Id = nextId "S" (sectionIds content)
                      Title = t
                      Description = None
                      Required = true
                      Questions = []
                      Presentation = defaultSectionPresentation
                      Scoring = Some { Scorer = scorer; Questions = [] } }
                    draft
                |> Result.mapError describe)
        | RemoveSection id -> removeSection id draft |> Result.mapError describe
        | AddQuestion(sectionId, prompt, kind, details) ->
            nonEmpty "question" prompt
            |> Result.bind (fun p -> QuestionKinds.build (nextId "Q" (questionIds content)) p kind details)
            |> Result.bind (fun q ->
                match content.Sections |> List.tryFind (fun s -> s.Id = sectionId) with
                | None -> Error(describe (UnknownSection sectionId))
                | Some before ->
                    addQuestion sectionId q draft
                    |> Result.bind (editSection sectionId (QuestionKinds.afterAdding scorer q before))
                    |> Result.mapError describe)
        | RemoveQuestion id ->
            let mentions (rule: RuleModel.FlowRule) =
                match rule.When, rule.Then with
                | _, RuleModel.ShowQuestion q when q = id -> true
                | (RuleModel.AnswerIs(q, _) | RuleModel.IsSpecial(q, _)), _ -> q = id
                | _ -> false

            match content.Sections |> List.tryFind (fun s -> s.Questions |> List.exists (fun q -> q.Id = id)) with
            | None -> Error(describe (UnknownQuestionId id))
            | Some section ->
                removeQuestion id draft
                |> Result.bind (editSection section.Id (QuestionKinds.afterRemoving id))
                |> Result.map (editContent (fun c -> { c with Rules = { c.Rules with Flow = c.Rules.Flow |> List.filter (mentions >> not) } }))
                |> Result.mapError describe
        | AddShowRule(questionId, whenQuestion, answerLabel) ->
            QuestionKinds.showRule (nextId "show-" (content.Rules.Flow |> List.map _.Id)) content questionId whenQuestion answerLabel
            |> Result.map (fun rule -> editContent (fun c -> { c with Rules = { c.Rules with Flow = c.Rules.Flow @ [ rule ] } }) draft)
        | RemoveRule ruleId when content.Rules.Flow |> List.exists (fun r -> r.Id = ruleId) ->
            Ok(editContent (fun c -> { c with Rules = { c.Rules with Flow = c.Rules.Flow |> List.filter (fun r -> r.Id <> ruleId) } }) draft)
        | RemoveRule ruleId -> Error $"There is no rule '{ruleId}'."
        | AddMidpointFixture ->
            let id = nextId "midpoint-" (draft.Fixtures |> List.map _.Id)

            addFixture
                { Id = id
                  Name = "Every answer at the midpoint"
                  Answers = questions content |> List.choose (fun (_, q) -> midpoint q |> Option.map (fun a -> q.Id, a)) |> Map.ofList
                  Expect = [ ExpectComplete true ] }
                draft
            |> Result.mapError describe

    // The template declares what its rules use (a condition needs branching).
    let declared (d: Draft) =
        let required = RuleChecks.requiredCapabilities d.Content
        let missing = required |> Set.filter (fun c -> not (List.contains c d.Content.Compatibility.Capabilities))

        if missing.IsEmpty then d
        else editContent (fun c -> { c with Compatibility = { c.Compatibility with Capabilities = c.Compatibility.Capabilities @ Set.toList missing } }) d

    edited |> Result.map (fun d -> { editor with Draft = declared d; Unsaved = true })

/// What publication would say about the draft now.
let report (editor: Editor) = Validation.validate Validation.defaultPolicy editor.Draft

/// The warning codes the publisher accepts by acknowledging.
let acknowledged (editor: Editor) =
    if editor.AcknowledgeWarnings then (report editor).Warnings |> List.map _.Code |> Set.ofList else Set.empty

/// The publication summary, or why it would be refused (AUT-005 §55).
let preview (catalog: Publication.Catalog) (editor: Editor) = Publication.preview Validation.defaultPolicy catalog editor.Draft

// ---- The authoring screens' state -----------------------------------------------------------------

/// What the screens hold between events.
type Screen =
    { Editor: Editor option
      NewSurvey: string
      NewTitle: string
      NewSection: string
      NewQuestion: string
      NewQuestionKind: string
      NewQuestionDetails: string
      RuleQuestion: string
      RuleWhen: string
      RuleAnswer: string
      Problem: string option }

let emptyScreen =
    { Editor = None
      NewSurvey = ""
      NewTitle = ""
      NewSection = ""
      NewQuestion = ""
      NewQuestionKind = "frequency5"
      NewQuestionDetails = ""
      RuleQuestion = ""
      RuleWhen = ""
      RuleAnswer = ""
      Problem = None }

/// What the screens ask the application to do.
type Command =
    | OpenDraft of survey: string
    | SaveDraft of Draft
    | PublishDraft of Draft * acknowledged: Set<string>
    | HideVersion of survey: string * version: string
    /// Review before publication (AUT-006 §§64-65).
    | RequestReview of survey: string
    | ApproveReview of Draft

/// The page events the authoring screens own.
let events =
    set
        [ "newDraftSurvey"; "newDraftTitle"; "startDraft"; "deriveDraft"; "editDraft"; "draftTitle"; "draftDescription"
          "newSectionTitle"; "addSection"; "removeSection"; "newQuestionPrompt"; "addQuestion"; "removeQuestion"
          "newQuestionKind"; "newQuestionDetails"; "ruleQuestion"; "ruleWhen"; "ruleAnswer"; "addShowRule"; "removeRule"
          "addFixture"; "acknowledgeWarnings"; "saveDraft"; "publishDraft"; "hideVersion"; "requestReview"; "approveReview" ]

/// The editor for a survey: the one in hand, or its stored draft.
let current (listing: TemplateListing.Listing) (survey: string) (screen: Screen) =
    match screen.Editor with
    | Some editor when editor.Draft.SurveyId = survey -> Some editor
    | _ -> listing.DraftsById |> Map.tryFind survey |> Option.map (fun d -> editing d false)

let private exists (listing: TemplateListing.Listing) (survey: string) =
    listing.DraftsById.ContainsKey survey || listing.Published |> List.exists (fun r -> r.SurveyId = survey)

/// One authoring event, for the survey whose draft is in view (if any).
let update (listing: TemplateListing.Listing) (survey: string option) (name: string) (key: string option) (value: string) (screen: Screen) : Screen * Command list =
    let editor = survey |> Option.bind (fun s -> current listing s screen)
    let fail problem = { screen with Problem = Some problem }, []

    let edit change =
        match editor with
        | None -> fail "Open a draft first."
        | Some e ->
            match apply change e with
            | Ok next -> { screen with Editor = Some next; Problem = None }, []
            | Error problem -> fail problem

    match name, key with
    | "newDraftSurvey", _ -> { screen with NewSurvey = value.Trim().ToUpperInvariant() }, []
    | "newDraftTitle", _ -> { screen with NewTitle = value }, []
    | "startDraft", _ when exists listing screen.NewSurvey -> fail $"'{screen.NewSurvey}' already exists: edit its draft or derive one from a published version."
    | "startDraft", _ ->
        match start screen.NewSurvey screen.NewTitle with
        | Ok e -> { screen with Editor = Some e; Problem = None; NewSurvey = ""; NewTitle = "" }, [ OpenDraft e.Draft.SurveyId ]
        | Error problem -> fail problem
    | "deriveDraft", Some hash ->
        match Publication.resolveHash listing.Catalog hash with
        | Some published when not (listing.DraftsById.ContainsKey published.SurveyId) ->
            { screen with Editor = Some(editing (Publication.deriveDraft published) true); Problem = None }, [ OpenDraft published.SurveyId ]
        | Some published -> fail $"'{published.SurveyId}' already has a draft: edit it instead."
        | None -> fail "That version is not in the catalog."
    | "editDraft", Some s -> { screen with Problem = None }, [ OpenDraft s ]
    | "draftTitle", _ -> edit (SetTitle value)
    | "draftDescription", _ -> edit (SetDescription value)
    | "newSectionTitle", _ -> { screen with NewSection = value }, []
    | "addSection", _ ->
        let next, commands = edit (AddSection screen.NewSection)
        (if next.Problem.IsNone then { next with NewSection = "" } else next), commands
    | "removeSection", Some id -> edit (RemoveSection id)
    | "newQuestionPrompt", _ -> { screen with NewQuestion = value }, []
    | "newQuestionKind", _ when (QuestionKinds.parse value).IsSome -> { screen with NewQuestionKind = value }, []
    | "newQuestionDetails", _ -> { screen with NewQuestionDetails = value }, []
    | "addQuestion", Some section ->
        match QuestionKinds.parse screen.NewQuestionKind with
        | None -> fail "Choose what kind of question to add."
        | Some kind ->
            let next, commands = edit (AddQuestion(section, screen.NewQuestion, kind, screen.NewQuestionDetails))
            (if next.Problem.IsNone then { next with NewQuestion = ""; NewQuestionDetails = "" } else next), commands
    | "ruleQuestion", _ -> { screen with RuleQuestion = value.Trim().ToUpperInvariant() }, []
    | "ruleWhen", _ -> { screen with RuleWhen = value.Trim().ToUpperInvariant() }, []
    | "ruleAnswer", _ -> { screen with RuleAnswer = value }, []
    | "addShowRule", _ ->
        let next, commands = edit (AddShowRule(screen.RuleQuestion, screen.RuleWhen, screen.RuleAnswer))
        (if next.Problem.IsNone then { next with RuleQuestion = ""; RuleWhen = ""; RuleAnswer = "" } else next), commands
    | "removeRule", Some id -> edit (RemoveRule id)
    | "removeQuestion", Some id -> edit (RemoveQuestion id)
    | "addFixture", _ -> edit AddMidpointFixture
    | "acknowledgeWarnings", _ ->
        match editor with
        | Some e -> { screen with Editor = Some { e with AcknowledgeWarnings = (value = "true" || value = "on") } }, []
        | None -> screen, []
    | "saveDraft", _ ->
        match editor with
        | Some e -> screen, [ SaveDraft e.Draft ]
        | None -> fail "Open a draft first."
    | "publishDraft", _ ->
        match editor with
        | Some e when (report e).Passes -> screen, [ PublishDraft(e.Draft, acknowledged e) ]
        | Some _ -> fail "Publication is blocked: resolve the blockers listed under validation."
        | None -> fail "Open a draft first."
    | "requestReview", _ ->
        match editor with
        | Some e when e.Unsaved -> fail "Save the draft first: review covers the stored draft."
        | Some e -> screen, [ RequestReview e.Draft.SurveyId ]
        | None -> fail "Open a draft first."
    | "approveReview", _ ->
        match editor with
        | Some e when e.Unsaved -> fail "This draft has unsaved changes; review the stored draft."
        | Some e -> screen, [ ApproveReview e.Draft ]
        | None -> fail "Open a draft first."
    | "hideVersion", Some hash ->
        match listing.Published |> List.tryFind (fun r -> r.Hash = hash) with
        | Some r -> screen, [ HideVersion(r.SurveyId, r.Version) ]
        | None -> fail "That version is not in the catalog."
    | _ -> screen, []

/// After a save: the editor's draft is what is stored.
let saved (survey: string) (screen: Screen) =
    match screen.Editor with
    | Some e when e.Draft.SurveyId = survey -> { screen with Editor = Some { e with Unsaved = false } }
    | _ -> screen

/// After publication: the editor closes; the next edit starts from the stored draft.
let published (survey: string) (screen: Screen) =
    match screen.Editor with
    | Some e when e.Draft.SurveyId = survey -> { screen with Editor = None }
    | _ -> screen
