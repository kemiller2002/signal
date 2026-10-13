/// The respondent survey session as an ARX-002 state system (WI-0092): its
/// states, the explicit result of every message (legal, or illegal with a
/// reason), the capabilities its state grants, the obligations it carries
/// with the evidence that discharges them, and what is known about the
/// template the link names.
///
/// `GenericSession.update` stays the page's reducer: an illegal message
/// leaves the session as it was. `attempt` is the same transition with the
/// refusal made explicit, and a test holds the two to each other.
///
/// Pure.
module Echelon.Signal.Engine.SessionStates

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.StateModel
open Echelon.Signal.Engine.GenericSession

let SystemName = "session"

let stateName =
    function
    | Fetching _ -> "Fetching"
    | Refused _ -> "Refused"
    | Responding r when r.Phase = Submitted -> "Submitted"
    | Responding _ -> "Answering"

let commandName =
    function
    | Chose _ -> "choose"
    | Toggled _ -> "toggle"
    | Numbered _ -> "type"
    | Picked _ -> "pick"
    | Ticked _ -> "tick"
    | Turned GenericPaging.Back -> "back"
    | Turned GenericPaging.Next -> "next"
    | Reached _ -> "reach"
    | SubmitRequested _ -> "submit"

let private answerTarget =
    function
    | Chose(q, _)
    | Toggled(q, _, _)
    | Numbered(q, _, _)
    | Picked(q, _, _) -> Some q
    | _ -> None

/// Whether a message offers something the question has.
let private offered (q: Question) (msg: Msg) =
    match msg, q.Answer with
    | Chose(_, state), _ -> choices q |> List.exists (fst >> (=) state)
    | Toggled(_, optionId, _), MultiChoice m -> m.Options |> List.exists (fun o -> o.Id = optionId)
    | Toggled(_, optionId, _), HierarchicalMulti(nodes, _, _) -> nodes |> List.exists (fun n -> n.Option.Id = optionId)
    | Toggled _, _ -> false
    | Numbered(_, inputId, _), _ -> GenericInputs.numberInputs q |> List.exists (fun i -> i.Id = inputId)
    | Picked(_, _, optionId), _ -> GenericInputs.pairs q |> List.exists (fst >> (=) optionId)
    | _ -> true

let private completion (r: Response) =
    (Rules.evaluate r.Published.Form.Content r.Answers).Completion

/// Capabilities, from the session alone.
let capabilities (session: Session) : Set<string> =
    match session with
    | Responding r when r.Phase = Answering ->
        let content = r.Published.Form.Content
        let _, _, onPage = GenericPaging.current content r.Answers r.Position
        let locked = Set.union (GenericPaging.locked content r.Answers r.Position) (GenericTimer.locked content r.Terms r.Now)

        set
            [ if onPage |> List.exists (locked.Contains >> not) then Capability.CanAnswer
              if GenericPaging.canGoBack content r.Answers r.Position then Capability.CanNavigateBack
              if Rules.isSubmittable (completion r) && r.Pending.IsEmpty then Capability.CanFinalize ]
    | Responding _ -> set [ Capability.CanCopySubmission ]
    | Fetching _
    | Refused _ -> Set.empty

let requires =
    function
    | Chose _
    | Toggled _
    | Numbered _
    | Picked _ -> Some Capability.CanAnswer
    | Turned GenericPaging.Back -> Some Capability.CanNavigateBack
    | SubmitRequested _ -> Some Capability.CanFinalize
    | Turned GenericPaging.Next
    | Reached _
    | Ticked _ -> None

/// Whether a message is legal now, and if not, why.
let guard (msg: Msg) (session: Session) : Result<unit, Illegal> =
    let refuse reason = illegal SystemName (stateName session) (commandName msg) reason

    match session, msg with
    | Fetching _, _ -> refuse "the survey has not arrived yet"
    | Refused _, _ -> refuse "this link was refused; nothing can be answered"
    | Responding r, _ when r.Phase = Submitted -> refuse "the response is submitted and sealed"
    | Responding r, _ ->
        let content = r.Published.Form.Content
        let need = requires msg |> Option.filter (fun c -> not ((capabilities session).Contains c))

        match answerTarget msg, need with
        | _, Some capability -> refuse $"{capability} does not hold"
        | Some id, None ->
            match tryQuestion content id with
            | None -> refuse $"there is no question '{id}'"
            | Some _ when (GenericPaging.locked content r.Answers r.Position).Contains id -> refuse $"'{id}' is locked: its section or page was left"
            | Some _ when (GenericTimer.locked content r.Terms r.Now).Contains id -> refuse $"'{id}' is held: its time ran out"
            | Some q when not (offered q msg) -> refuse $"'{id}' does not offer that answer"
            | Some _ -> Ok()
        | None, None -> Ok()

/// The transition with its refusal explicit.
let attempt (msg: Msg) (session: Session) : Result<Session, Illegal> =
    guard msg session |> Result.map (fun () -> update msg session)

/// Unresolved work, each with the evidence that discharges it.
let obligations (session: Session) : Obligation list =
    match session with
    | Fetching _ ->
        [ { Code = "ResolveTemplate"
            Detail = "The survey file the link names has not arrived."
            DischargedBy = "the catalog file, verified against the link's template reference" } ]
    | Responding r when r.Phase = Answering ->
        [ match completion r with
          | Rules.NotStarted ->
              { Code = "MissingRequiredAnswer"
                Detail = "Nothing is answered yet."
                DischargedBy = "an answer to each applicable required question" }
          | Rules.InProgress missing ->
              { Code = "MissingRequiredAnswer"
                Detail = $"{missing} required question(s) unanswered."
                DischargedBy = "an answer to each applicable required question" }
          | Rules.Invalid problems ->
              { Code = "ResolveInvalidAnswer"
                Detail = $"{problems} answer(s) break the survey's rules."
                DischargedBy = "answers that satisfy the validation rules" }
          | _ -> ()
          if not r.Pending.IsEmpty then
              { Code = "FinishHeldAnswer"
                Detail = $"{r.Pending.Count} answer(s) are held, not yet in the link."
                DischargedBy = "completing or clearing each held input" } ]
    | Responding _
    | Refused _ -> []

/// What is known about the template the link names.
let template (session: Session) : Knowledge<string * string> =
    match session with
    | Responding r -> Known(r.Published.SurveyId, r.Published.Version)
    | Refused(TemplateNotPublished file) -> SearchedNotFound(file, "this site's published catalog, as served to this page")
    | Refused(CatalogUnreachable reason) -> Unknown $"the catalog could not be reached ({reason})"
    | Fetching _ -> Unknown "the catalog file has not arrived"
    | Refused other -> Unknown $"the link or file was refused before the template was known: {describe other}"

let terminal =
    function
    | Refused _ -> true
    | Responding r -> r.Phase = Submitted
    | Fetching _ -> false

let system: System<Session, Msg> =
    { Name = SystemName
      StateName = stateName
      CommandName = commandName
      Attempt = attempt
      Capabilities = capabilities
      Requires = requires
      Obligations = obligations
      Terminal = terminal }
