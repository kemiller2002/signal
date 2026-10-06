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

/// Every event the page may send, so a test can hold index.html to it. An
/// `answered` event always comes from a checked radio: the browser fires
/// `change` only on the radio being checked, and on submit Limen (0.7.1 and
/// later) re-sends only the controls a native submission would include,
/// which leaves out unchecked radios (limen#80/#81).
let events: Map<string, Session.Session -> Fields -> Msg> =
    Map.ofList
        [ "answered", (fun session fields -> Answered(itemOf session fields.Key, answerOf fields.Value))
          "resultsRequested", (fun _ _ -> ResultsRequested)
          "editRequested", (fun _ _ -> EditRequested)
          "restarted", (fun _ _ -> Restarted) ]

let private message (session: Session.Session) (name: string) (fields: Fields) =
    match events |> Map.tryFind name with
    | Some make -> make session fields
    // index.html and the engine disagree: a defect, not an operational failure.
    | None -> invalidOp $"The assessment page sent an event the engine does not know: '{name}'"

// ---------------------------------------------------------------------------
// One kernel message in, one reply out.
// ---------------------------------------------------------------------------

type State =
    { Session: Session.Session
      Fault: FaultView option }

let initial =
    { Session = start Pilot.assessment
      Fault = None }

let render (state: State) (handshake: Handshake option) =
    encode (view state.Session @ faultView state.Fault) handshake

let private step (state: State) (inbound: Inbound) =
    let next, handshake =
        match inbound with
        | Initialize offer -> state.Session, offer |> Option.map answer
        | Event(name, key, value) ->
            let fields =
                { Key = defaultArg key ""
                  Value = defaultArg value "" }

            update (message state.Session name fields) state.Session, None
        | LocationChanged -> state.Session, None

    let state = { state with Session = next }
    state, render state handshake

/// Handles one kernel message under the Aegis boundary. A fault leaves the
/// session as it was and is shown until the next message.
let handle (aegis: AegisConfig) (state: State) (messageJson: string) =
    let cleared = { state with Fault = None }

    match capture aegis "Signal.Assessment.dispatch" (fun () -> step cleared (decode messageJson)) with
    | Ok result -> result
    | Result.Error fault ->
        let faulted = { state with Fault = Some fault }
        faulted, render faulted None
