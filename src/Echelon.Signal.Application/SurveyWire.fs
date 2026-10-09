/// The survey page's side of the Limen boundary (web/survey/,
/// DF-SIGNAL-2026-0005): reads the template reference from the link, asks
/// the kernel for that one catalog file on the page's own origin, and runs
/// the respondent session (`GenericSession`) under the Aegis boundary.
module Echelon.Signal.Application.SurveyWire

open Aegis
open Echelon.Signal.Engine
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Application.Json
open Echelon.Signal.Application.Limen
open Echelon.Signal.Application.Boundary

/// Where the site root is, relative to the survey page (web/survey/). The
/// catalog address is built here from the reference alone: relative, so the
/// request never leaves the page's origin (and the page's CSP holds
/// connect-src to 'self').
[<Literal>]
let SiteRoot = "../../"

[<NoComparison; NoEquality>]
type State =
    { Session: Session
      Fault: FaultView option
      Location: Location option
      /// Navigation and clipboard effects requested and not yet answered.
      Pending: Set<string>
      /// The catalog request in flight, if any.
      Fetch: string option
      NextCorrelation: int
      UrlNotice: string option
      CopyNotice: string option
      Edge: Wire.Edge }

let initialWith (edge: Wire.Edge) =
    { Session = Refused NoAnswerLink
      Fault = None
      Location = None
      Pending = Set.empty
      Fetch = None
      NextCorrelation = 1
      UrlNotice = None
      CopyNotice = None
      Edge = edge }

let initial = initialWith Wire.secureEdge

/// Every event the page may send, so a test can hold the page to it.
let events = set [ "answered"; "submitRequested"; Wire.CopyRequested ]

let private responding (state: State) =
    match state.Session with
    | Responding r -> Some r
    | Fetching _
    | Refused _ -> None

/// The full URL of a finalized submission; empty until submitted.
let submissionLink (state: State) =
    match state.Location, responding state with
    | Some location, Some r when r.Phase = Submitted -> location.Origin + location.Path + location.Query + fragment r
    | _ -> ""

let render (state: State) (effects: Effect list) (handshake: Handshake option) =
    encode (view state.Session @ Wire.wireView state.UrlNotice state.CopyNotice (submissionLink state) @ faultView state.Fault) effects handshake

let private correlation (prefix: string) (state: State) =
    $"{prefix}-{state.NextCorrelation}", { state with NextCorrelation = state.NextCorrelation + 1 }

/// Starts a session for a link, requesting its catalog file when it names one.
let private open' (state: State) (hash: string) =
    let session = start hash

    match wanted session with
    | Some file ->
        let id, state = correlation "fetch" state
        { state with Session = session; Fetch = Some id }, [ FetchBytes(id, SiteRoot + file) ]
    | None -> { state with Session = session; Fetch = None }, []

/// Keeps the URL equal to the response (LURL-001) with one `replace`.
let private synchronize (state: State) =
    match state.Location, responding state with
    | Some location, Some r when fragment r <> location.Hash ->
        let id, state = correlation "url" state

        { state with
            Location = Some { location with Hash = fragment r }
            Pending = state.Pending.Add id },
        [ ReplaceUrl(id, location.Path + location.Query + fragment r) ]
    | _ -> state, []

let private fetched (outcome: HttpOutcome) =
    match outcome with
    | HttpResponded(200, body) ->
        try
            Found(System.Convert.FromBase64String body)
        with :? System.FormatException ->
            Unreachable "the site's answer could not be read"
    | HttpResponded(404, _) -> NotFound
    | HttpResponded(status, _) -> Unreachable $"the site answered {status}"
    | HttpFailed reason -> Unreachable reason

let private message (state: State) (name: string) (key: string) =
    match name, responding state with
    | "answered", Some r ->
        match choiceFor r key with
        | Some(questionId, answer) -> Chose(questionId, answer)
        | None -> raise (MalformedInput("$.event.key", $"a choice of this survey, not '{key}'"))
    | "submitRequested", Some _ -> SubmitRequested(state.Edge.Entropy())
    | _, None -> raise (MalformedInput("$.event.name", $"an event the page can send while a survey is shown, not '{name}'"))
    // The page and the engine disagree: a defect, not an operational failure.
    | _ -> invalidOp $"The survey page sent an event the engine does not know: '{name}'"

let private step (state: State) (inbound: Inbound) =
    let state, effects, handshake =
        match inbound with
        | Initialize(offer, location) ->
            let state, effects =
                match location with
                | Some l -> open' { state with Location = location } l.Hash
                | None -> state, []

            state, effects, offer |> Option.map answer
        | Event(name, _, _) when name = Wire.CopyRequested ->
            match submissionLink state with
            | "" -> state, [], None
            | link ->
                let id, state = correlation "copy" state
                { state with Pending = state.Pending.Add id; CopyNotice = None }, [ CopyText(id, link) ], None
        | Event(name, key, _) ->
            let state = { state with Session = update (message state name (defaultArg key "")) state.Session }
            let state, effects = synchronize state
            state, effects, None
        | LocationChanged location ->
            match state.Session with
            | Responding r when fragment r = location.Hash -> { state with Location = Some location }, [], None
            | _ ->
                // A different link in the same tab: start again from it.
                let state, effects = open' { state with Location = Some location; UrlNotice = None; CopyNotice = None } location.Hash
                state, effects, None
        | HttpResult(id, outcome) when state.Fetch = Some id ->
            { state with
                Session = received (fetched outcome) state.Session
                Fetch = None },
            [],
            None
        | HttpResult(id, _) -> raise (MalformedInput("$.result.correlationId", $"the pending catalog request, not '{id}'"))
        | ClipboardResult(id, _) when not (state.Pending.Contains id) ->
            raise (MalformedInput("$.result.correlationId", $"a pending clipboard write, not '{id}'"))
        | ClipboardResult(id, outcome) ->
            let notice =
                match outcome with
                | Ok() -> "Submission link copied. Send it to the person who invited you."
                | Error "denied" -> "The browser did not allow copying. Select the link and copy it yourself, or try again."
                | Error _ -> "The link could not be copied. Select it and copy it yourself."

            { state with Pending = state.Pending.Remove id; CopyNotice = Some notice }, [], None
        | NavigationResult(id, _) when not (state.Pending.Contains id) ->
            raise (MalformedInput("$.result.correlationId", $"a pending navigation, not '{id}'"))
        | NavigationResult(id, outcome) ->
            let state = { state with Pending = state.Pending.Remove id }

            match outcome with
            | NavigationSucceeded location -> { state with Location = Some location; UrlNotice = None }, [], None
            | NavigationDispatched -> state, [], None
            | NavigationFailed _ ->
                { state with
                    UrlNotice =
                        Some "This page's address could not be updated, so a copy of the link would not include your latest answers. Your answers on this page are unchanged." },
                [],
                None

    state, render state effects handshake

/// Handles one kernel message under the Aegis boundary. A fault leaves the
/// session as it was and is shown until the next message.
let handle (aegis: AegisConfig) (state: State) (messageJson: string) =
    let cleared = { state with Fault = None }

    match capture aegis "Signal.Survey.dispatch" (fun () -> step cleared (decode messageJson)) with
    | Ok result -> result
    | Result.Error fault ->
        let faulted = { state with Fault = Some fault }
        faulted, render faulted [] None
