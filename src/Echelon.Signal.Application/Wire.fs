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

/// An event as the wire reads it: the item key, the value and, for a radio,
/// whether it is now checked.
type Fields =
    { Key: string
      Value: string
      Checked: bool option }

/// Every event the page may send, so a test can hold index.html to it. An
/// event can carry no message: the kernel also reports a radio that is
/// *not* checked (for example when the form is submitted), and an unchecked
/// choice says nothing about the item's answer.
let events: Map<string, Session.Session -> Fields -> Msg option> =
    Map.ofList
        [ "answered",
          (fun session fields ->
              match fields.Checked with
              | Some false -> None
              | Some true
              | None -> Some(Answered(itemOf session fields.Key, answerOf fields.Value)))
          "resultsRequested", (fun _ _ -> Some ResultsRequested)
          "editRequested", (fun _ _ -> Some EditRequested)
          "restarted", (fun _ _ -> Some Restarted) ]

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
        | Event(name, key, value, isChecked) ->
            let fields =
                { Key = defaultArg key ""
                  Value = defaultArg value ""
                  Checked = isChecked }

            match message state.Session name fields with
            | Some msg -> update msg state.Session, None
            | None -> state.Session, None
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
