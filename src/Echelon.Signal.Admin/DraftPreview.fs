/// The draft as a respondent would see it, inside the console (AUT-003
/// §§20-22, SCS-017; WI-0081). It is the survey page's own engine
/// (`GenericSession`) running the draft: pagination, section transitions,
/// selectors, conditional questions and sections, validation messages,
/// progress and completion behave exactly as they will once published.
///
/// - It is a test artifact (§21): its binding is marked test, its link is
///   never shown or written, and nothing is stored or sent. Submitting
///   seals the preview and nothing more.
/// - Its answers are scored live by `ScoringPreview`, and can be kept as a
///   fixture that pins what they score now.
/// - The console's update holds no clock, so timers run from the time the
///   dataset was last verified and do not tick while previewing.
///
/// Pure.
module Echelon.Signal.Admin.DraftPreview

open System
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession

/// The version name a preview runs under: never a published version.
[<Literal>]
let PreviewVersion = "draft-preview"

let private id (purpose: string) =
    (OpaqueId.ofBytes (Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes $"signal-draft-preview/1:{purpose}")[.. IdLength - 1])).Value

/// A fresh preview of the draft at `now`, or the page's own refusal (a
/// question kind or timer the page cannot run is refused, as it will be).
let start (draft: Drafts.Draft) (now: DateTimeOffset) : Session =
    let payload = GenericEnvelope.testLink draft.SurveyId PreviewVersion draft.Content Import.IdentifiedGroup (id "instance") (id "group") Map.empty
    let _, bytes = publishedFile draft.SurveyId PreviewVersion draft.Content
    GenericSession.start $"#{LiveUrl.FragmentKey}={payload}" |> receivedAt now (Found bytes)

let events =
    set
        [ "previewStart"; "previewClose"; "previewAnswered"; "previewToggled"; "previewNumbered"; "previewPickedBest"; "previewPickedWorst"
          "previewNext"; "previewBack"; "previewSubmit"; "previewReviewClosed"; "previewKeep" ]

/// The session after a preview event, as the survey page's wire maps the
/// same events; an event for a row the preview does not have changes nothing.
let step (name: string) (key: string option) (value: string) (session: Session) : Session =
    match session with
    | Responding r ->
        let key = defaultArg key ""

        let msg =
            match name with
            | "previewAnswered" -> choiceFor r key |> Option.map Chose
            | "previewToggled" -> toggleFor r key |> Option.map (fun (q, o) -> Toggled(q, o, value <> ""))
            | "previewNumbered" -> numberFor r key |> Option.map (fun (q, i) -> Numbered(q, i, value))
            | "previewPickedBest" -> pairFor r key |> Option.map (fun (q, o) -> Picked(q, GenericInputs.Best, o))
            | "previewPickedWorst" -> pairFor r key |> Option.map (fun (q, o) -> Picked(q, GenericInputs.Worst, o))
            | "previewNext" -> Some(Turned GenericPaging.Next)
            | "previewBack" -> Some(Turned GenericPaging.Back)
            // An identified test binding draws no identifier: no entropy is needed.
            | "previewSubmit" -> Some(SubmitRequested(Array.zeroCreate IdLength))
            | "previewReviewClosed" -> Some ReviewClosed
            | _ -> None

        msg |> Option.map (fun m -> update m session) |> Option.defaultValue session
    | Fetching _
    | Refused _ -> session

/// The preview's answers, if it is running.
let answers (session: Session) =
    match session with
    | Responding r -> Some r.Answers
    | Fetching _
    | Refused _ -> None

/// The preview's answers as a fixture that pins what they score now:
/// completion, each section's score and the overall score (AUT-003 §24).
let fixtureOf (draft: Drafts.Draft) (answers: Answers) : Drafts.Fixture =
    let result = SurveyResult.compute "preview" draft.Content answers false
    let score = function Scoring.Score(v, _, _) -> Some v | Scoring.NotScored _ -> None

    let taken = draft.Fixtures |> List.map _.Id
    let next = Seq.initInfinite (fun i -> $"preview-{i + 1}") |> Seq.find (fun id -> not (List.contains id taken))

    { Id = next
      Name = $"Preview answers {next.Substring 8}"
      Answers = answers
      Expect =
        [ yield Drafts.ExpectComplete(Rules.isSubmittable result.Evaluation.Completion)
          for sectionId, outcome in result.Evaluation.Sections do
              match outcome with
              | Rules.SectionScored o -> yield Drafts.ExpectSectionScore(sectionId, score o)
              | Rules.SectionNotApplicable -> ()
          match result.Overall with
          | Some o -> yield Drafts.ExpectOverall(score o.Outcome)
          | None -> () ] }
