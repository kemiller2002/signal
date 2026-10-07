/// The assessment page's side of the Limen boundary: maps kernel events onto
/// session messages and runs each step under the Aegis boundary.
module Echelon.Signal.Application.Wire

open Aegis
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Session
open Echelon.Signal.Application.Json
open Echelon.Signal.Application.Limen
open Echelon.Signal.Application.Boundary

// ---------------------------------------------------------------------------
// Kernel events: the `data-event` names web/index.html uses.
// ---------------------------------------------------------------------------

/// The answer an `answered` event carries: the empty choice clears it, and a
/// code the assessment does not define is not a message the page can send.
let private answerOf (value: string) =
    match value with
    | "" -> None
    | code ->
        match Assessment.parseAnswer code with
        | Some answer -> Some answer
        | None -> raise (MalformedInput("$.event.value", $"an answer code, not '{code}'"))

let private itemOf (session: Session.Session) (key: string) =
    if knows session key then
        key
    else
        raise (MalformedInput("$.event.key", $"an item of {session.Assessment.Id}, not '{key}'"))

/// An event as the wire reads it: the item key and the value.
type Fields = { Key: string; Value: string }

/// What the application edge supplies to turn an event into a message.
[<NoComparison; NoEquality>]
type Edge =
    { /// Fresh cryptographically secure bytes for an anonymous submission id.
      Entropy: unit -> byte[] }

/// Every event the page may send, so a test can hold index.html to it. An
/// `answered` event always comes from a checked radio: the browser fires
/// `change` only on the radio being checked, and on submit Limen (0.7.1 and
/// later) re-sends only the controls a native submission would include,
/// which leaves out unchecked radios (limen#80/#81).
let events: Map<string, Edge -> Session.Session -> Fields -> Msg> =
    Map.ofList
        [ "answered", (fun _ session fields -> Answered(itemOf session fields.Key, answerOf fields.Value))
          "resultsRequested", (fun _ _ _ -> ResultsRequested)
          "editRequested", (fun _ _ _ -> EditRequested)
          "restarted", (fun _ _ _ -> Restarted)
          "submitRequested", (fun edge _ _ -> SubmitRequested(edge.Entropy())) ]

/// Events the wire handles itself (effects, not session transitions).
[<Literal>]
let CopyRequested = "copyRequested"

let private message (edge: Edge) (session: Session.Session) (name: string) (fields: Fields) =
    match events |> Map.tryFind name with
    | Some make -> make edge session fields
    // index.html and the engine disagree: a defect, not an operational failure.
    | None -> invalidOp $"The assessment page sent an event the engine does not know: '{name}'"

// ---------------------------------------------------------------------------
// One kernel message in, one reply out.
// ---------------------------------------------------------------------------

[<NoComparison; NoEquality>]
type State =
    { Session: Session.Session
      Fault: FaultView option
      /// The browser URL as last reported (or as last requested).
      Location: Location option
      /// Navigation effects requested and not yet answered.
      Pending: Set<string>
      NextCorrelation: int
      /// Set when the browser refused to update the URL: the link no longer
      /// carries the latest answers, and the respondent is told so.
      UrlNotice: string option
      /// The outcome of the last copy of the submission link, if any.
      CopyNotice: string option
      Edge: Edge }

/// The secure default edge: .NET's CSPRNG, which in the browser is the Web
/// Crypto `getRandomValues` source (ARX-004).
let secureEdge =
    { Entropy = fun () -> System.Security.Cryptography.RandomNumberGenerator.GetBytes UrlState.IdLength }

let initialWith (edge: Edge) =
    { Session = start Pilot.assessment
      Fault = None
      Location = None
      Pending = Set.empty
      NextCorrelation = 1
      UrlNotice = None
      CopyNotice = None
      Edge = edge }

let initial = initialWith secureEdge

/// The full URL of a finalized submission, which the respondent sends to
/// the administrator; empty until the response is submitted.
let submissionLink (state: State) =
    match state.Location with
    | Some location when state.Session.Phase = Submitted ->
        location.Origin + LiveUrl.urlFor state.Session.Assessment location.Path location.Query (Session.envelope state.Session)
    | _ -> ""

/// The wire's own named values: URL and copy notices, and the submission link.
let wireView (urlNotice: string option) (copyNotice: string option) (link: string) : Echelon.Signal.Engine.View.View =
    let value = Echelon.Signal.Engine.View.Value
    let flag = Echelon.Signal.Engine.View.Flag
    let text = Echelon.Signal.Engine.View.Text

    [ "hasUrlNotice", value (flag urlNotice.IsSome)
      "urlNotice", value (text (defaultArg urlNotice ""))
      "hasCopyNotice", value (flag copyNotice.IsSome)
      "copyNotice", value (text (defaultArg copyNotice ""))
      "submissionLink", value (text link) ]

let render (state: State) (effects: Effect list) (handshake: Handshake option) =
    encode (view state.Session @ wireView state.UrlNotice state.CopyNotice (submissionLink state) @ faultView state.Fault) effects handshake

/// Applies what a URL says about saved answers (LURL-001 resume). A URL
/// whose saved answers equal the session's changes nothing.
let private resumeFrom (location: Location) (session: Session.Session) =
    match LiveUrl.read session.Assessment location.Hash with
    | LiveUrl.NoSavedState -> session
    | LiveUrl.Saved envelope when envelope = Session.envelope session -> session
    | LiveUrl.Saved envelope -> update (Resumed envelope) session
    | LiveUrl.Unreadable error -> update (ResumeRefused error) session

/// Keeps the URL equal to the session (LURL-001): when the fragment the
/// session implies differs from the URL's, one `replace` is requested, so
/// answering never adds history entries.
let private synchronize (state: State) =
    match state.Location with
    | None -> state, []
    | Some location ->
        let fragment = LiveUrl.fragment state.Session.Assessment (Session.envelope state.Session)

        if fragment = location.Hash then
            state, []
        else
            let id = $"url-{state.NextCorrelation}"
            let url = LiveUrl.urlFor state.Session.Assessment location.Path location.Query (Session.envelope state.Session)

            { state with
                Location = Some { location with Hash = fragment }
                Pending = state.Pending.Add id
                NextCorrelation = state.NextCorrelation + 1 },
            [ ReplaceUrl(id, url) ]

let private step (state: State) (inbound: Inbound) =
    let state, effects, handshake =
        match inbound with
        | Initialize(offer, location) ->
            let session =
                match location with
                | Some l -> resumeFrom l state.Session
                | None -> state.Session

            { state with Session = session; Location = location }, [], offer |> Option.map answer
        | Event(name, key, value) ->
            let fields =
                { Key = defaultArg key ""
                  Value = defaultArg value "" }

            if name = CopyRequested then
                match submissionLink state with
                | "" -> state, [], None // nothing to copy until submitted
                | link ->
                    let id = $"copy-{state.NextCorrelation}"

                    { state with
                        Pending = state.Pending.Add id
                        NextCorrelation = state.NextCorrelation + 1
                        CopyNotice = None },
                    [ CopyText(id, link) ],
                    None
            else
                let next, effects =
                    synchronize { state with Session = update (message state.Edge state.Session name fields) state.Session }

                next, effects, None
        | LocationChanged location ->
            { state with
                Session = resumeFrom location state.Session
                Location = Some location },
            [],
            None
        | ClipboardResult(id, _) when not (state.Pending.Contains id) ->
            raise (MalformedInput("$.result.correlationId", $"a pending clipboard write, not '{id}'"))
        | ClipboardResult(id, outcome) ->
            let notice =
                match outcome with
                | Ok() -> "Submission link copied. Send it to the person who invited you."
                | Error "denied" -> "The browser did not allow copying. Select the link and copy it yourself, or try again."
                | Error _ -> "The link could not be copied. Select it and copy it yourself."

            { state with
                Pending = state.Pending.Remove id
                CopyNotice = Some notice },
            [],
            None
        | NavigationResult(id, _) when not (state.Pending.Contains id) ->
            // A result for a navigation this engine did not request, or one it
            // already heard: stale or forged, never applied (ARX-007).
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

    match capture aegis "Signal.Assessment.dispatch" (fun () -> step cleared (decode messageJson)) with
    | Ok result -> result
    | Result.Error fault ->
        let faulted = { state with Fault = Some fault }
        faulted, render faulted [] None
