/// One respondent session on the assessment page: its state, the legal
/// transitions, and the view projected from it.
///
/// Pure: `update` is a total function of the state and a message, and `view`
/// is a function of the state alone. The results are never stored; they are
/// recomputed from the answers each time, so they cannot drift from them.
module Echelon.Signal.Engine.Session

open System.Globalization
open Echelon.Signal.Engine.View
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState

type Phase =
    /// The respondent is answering.
    | Responding
    /// Every item has an answer and the results are shown.
    | Reviewing
    /// The response was finalized into a submission: sealed, read-only.
    | Submitted

[<NoComparison>]
type Session =
    { Assessment: Assessment
      /// What the response is bound to (an invitation, or nothing). It
      /// travels in the URL with the answers.
      Binding: Binding
      Answers: Answers
      Phase: Phase
      /// Why the last request to see results was refused, if it was.
      Refusal: string option
      /// Why saved answers in the link could not be restored, if they could not.
      Notice: string option }

/// The envelope the live URL carries for this session.
let envelope (session: Session) =
    { Binding = session.Binding
      Answers = session.Answers }

[<NoComparison>]
type Msg =
    /// An item's answer was chosen; None clears it.
    | Answered of itemId: string * answer: Answer option
    | ResultsRequested
    | EditRequested
    | Restarted
    /// The URL carried saved answers that were read (LURL-001 resume).
    | Resumed of Envelope
    /// The URL carried saved answers that could not be read.
    | ResumeRefused of DecodeError
    /// Finalize into a submission. The bytes are fresh cryptographically
    /// secure entropy from the application edge; only an anonymous group
    /// uses them (`Submission.finalize`).
    | SubmitRequested of entropy: byte[]

let start (assessment: Assessment) =
    { Assessment = assessment
      Binding = Unbound
      Answers = Map.empty
      Phase = Responding
      Refusal = None
      Notice = None }

/// Whether `itemId` names an item of this session's assessment.
let knows (session: Session) (itemId: string) =
    session.Assessment.Items |> List.exists (fun item -> item.Id = itemId)

let private plural count singular pluralForm =
    if count = 1 then $"{count} {singular}" else $"{count} {pluralForm}"

let update (msg: Msg) (session: Session) =
    match msg with
    // A submission is sealed: only a different URL (resume) replaces it.
    | Answered _
    | ResultsRequested
    | EditRequested
    | Restarted
    | SubmitRequested _ when session.Phase = Submitted -> session
    | Answered(itemId, _) when not (knows session itemId) -> session
    | Answered(itemId, Some answer) ->
        { session with
            Answers = session.Answers.Add(itemId, answer)
            Refusal = None }
    | Answered(itemId, None) ->
        { session with
            Answers = session.Answers.Remove itemId
            // Clearing an answer while reviewing leaves the session unfinished.
            Phase = Responding
            Refusal = None }
    | ResultsRequested ->
        match unanswered session.Assessment session.Answers with
        | [] -> { session with Phase = Reviewing; Refusal = None }
        | missing ->
            { session with
                Phase = Responding
                Refusal =
                    Some(
                        $"""{plural missing.Length "question still needs" "questions still need"} an answer. "Don't know", "Not observed" and "Not applicable" are answers too."""
                    ) }
    | EditRequested -> { session with Phase = Responding; Refusal = None }
    // Starting over clears the answers, not the invitation they answer.
    | Restarted -> { start session.Assessment with Binding = session.Binding }
    | Resumed envelope ->
        { session with
            Binding = envelope.Binding
            Answers = envelope.Answers |> Map.filter (fun itemId _ -> knows session itemId)
            Phase = if Submission.isFinal envelope then Submitted else Responding
            Refusal = None
            Notice = None }
    | SubmitRequested entropy ->
        match Submission.finalize session.Assessment entropy (envelope session) with
        | Ok submission ->
            { session with
                Binding = submission.Binding
                Phase = Submitted
                Refusal = None }
        | Error refusal ->
            { session with
                Refusal = Some(Submission.describe refusal) }
    | ResumeRefused error ->
        { session with
            Notice = Some $"{describe error} Your earlier answers were not restored, and nothing was guessed." }

// ---------------------------------------------------------------------------
// Projection.
// ---------------------------------------------------------------------------

let private invariant (value: float) = value.ToString("0.0", CultureInfo.InvariantCulture)

let private dimensionLabel (assessment: Assessment) (dimensionId: string) =
    assessment.Dimensions
    |> List.tryFind (fun dimension -> dimension.Id = dimensionId)
    |> Option.map _.Label
    |> Option.defaultValue dimensionId

let private itemRow (session: Session) (index: int) (item: Item) =
    let answer = session.Answers.TryFind item.Id

    [ "id", Text item.Id
      "number", Text(string (index + 1))
      "dimension", Text(dimensionLabel session.Assessment item.DimensionId)
      "prompt", Text item.Prompt
      "answer", Text(answer |> Option.map answerCode |> Option.defaultValue "")
      "answerLabel", Text(answer |> Option.map answerLabel |> Option.defaultValue "Not answered")
      "answerState", Text(if answer.IsSome then "answered" else "unanswered")
      // One radio group per item, and which of its choices is checked.
      "group", Text $"answer-{item.Id}"
      "is0", Flag(answer = Some(Rated Never))
      "is1", Flag(answer = Some(Rated Rarely))
      "is2", Flag(answer = Some(Rated Sometimes))
      "is3", Flag(answer = Some(Rated Often))
      "is4", Flag(answer = Some(Rated AlmostAlways))
      "isDontKnow", Flag(answer = Some(Withheld DontKnow))
      "isNotObserved", Flag(answer = Some(Withheld NotObserved))
      "isNotApplicable", Flag(answer = Some(Withheld NotApplicable)) ]

let private resultRow (dimension: Dimension, result: DimensionResult) =
    let score, state, coverage, note =
        match result with
        | Scored(score, numeric, items) ->
            invariant score, "scored", $"{numeric} of {items} numeric", ""
        | Unscored(numeric, items, minimum) ->
            "Not scored",
            "unscored",
            $"{numeric} of {items} numeric",
            $"Needs at least {minimum} numeric answers; missing data is not counted as zero."

    [ "id", Text dimension.Id
      "dimension", Text dimension.Label
      "score", Text score
      "scoreState", Text state
      "coverage", Text coverage
      "note", Text note ]

/// How the scores were computed, stated once for the screen and the print.
let methodology (assessment: Assessment) =
    $"Each dimension score is the mean of its numeric answers (0 = never or almost never, 4 = almost always) divided by 4 and multiplied by 100, rounded to one decimal place. A dimension needs at least {assessment.MinimumNumericAnswers} numeric answers to be scored. Higher means more of the named trait; it is not inherently better."

let private scoredCount results =
    results
    |> List.filter (fun (_, result) ->
        match result with
        | Scored _ -> true
        | Unscored _ -> false)
    |> List.length

let private invited =
    function
    | IdentifiedInvitation _
    | AnonymousInvitation _ -> true
    | Unbound
    | Identified _
    | Anonymous _ -> false

let view (session: Session) : View =
    let assessment = session.Assessment
    let answered = assessment.Items.Length - (unanswered assessment session.Answers).Length
    let results = score assessment session.Answers

    [ "assessmentTitle", Value(Text assessment.Title)
      "assessmentVersion", Value(Text $"{assessment.Id} {assessment.Version}")
      "responding", Value(Flag(session.Phase = Responding))
      "reviewing", Value(Flag(session.Phase = Reviewing))
      "submitted", Value(Flag(session.Phase = Submitted))
      "canSubmit", Value(Flag(session.Phase = Reviewing && invited session.Binding))
      "submissionKind",
      Value(
          Text(
              match session.Binding with
              | Anonymous _ -> "anonymous"
              | Identified _ -> "identified"
              | _ -> ""
          )
      )
      "progress", Value(Text $"{answered} of {assessment.Items.Length} answered")
      "answeredCount", Value(Number(float answered))
      "itemCount", Value(Number(float assessment.Items.Length))
      "hasNotice", Value(Flag session.Notice.IsSome)
      "notice", Value(Text(session.Notice |> Option.defaultValue ""))
      "hasRefusal", Value(Flag session.Refusal.IsSome)
      "refusal", Value(Text(session.Refusal |> Option.defaultValue ""))
      "items", Items(assessment.Items |> List.mapi (itemRow session))
      "results", Items(results |> List.map resultRow)
      "scoredCount", Value(Text $"{scoredCount results} of {results.Length} dimensions scored")
      "methodology", Value(Text(methodology assessment)) ]
