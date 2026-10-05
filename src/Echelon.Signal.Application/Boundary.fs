/// The Aegis boundary of the Signal engine.
///
/// Every kernel message crosses here: it is parsed, routed into the
/// respondent session, and the reply is written. *Unexpected operational*
/// failure on that path (a message that is not the shape the engine relies
/// on, or names an item or answer the assessment does not have) is classified
/// once, here, into a stable SIGNAL.BOUNDARY.* fault and presented through
/// Forma's fault component. *Expected* outcomes are not faults: asking for
/// results with questions still unanswered is an ordinary refusal the page
/// shows as an alert. Programming defects keep Aegis's fail-loud semantics
/// (they are re-raised and surface as a Limen bridge error), and are never
/// disguised as recoverable faults.
///
/// The declared boundaries are in aegis-boundaries.json.
module Echelon.Signal.Application.Boundary

open System.Text.Json
open Aegis
open Echelon.Signal.Engine.View
open Echelon.Signal.Application.Json

[<Literal>]
let MessageInvalid = "SIGNAL.BOUNDARY.MESSAGE_INVALID"

[<Literal>]
let Unexpected = "SIGNAL.BOUNDARY.UNEXPECTED"

/// Aegis configured once for the application, and validated before it is
/// trusted. The sink is standard error, which the browser runtime routes to
/// the console; nothing is persisted (see aegis-boundaries.json).
let configure (sinks: Sinks.Sink list) =
    match Bootstrap.validate None (Aegis.configure "Signal" None sinks) with
    | Ok valid -> valid
    | Result.Error problems -> invalidOp $"Invalid Signal Aegis configuration: {problems}"

let private unchanged = "Your answers are unchanged."

/// One central translation from an exception at the boundary to a fault.
let classify (aegis: AegisConfig) (scope: Scope) (ex: exn) =
    let code, category, message =
        match ex with
        | :? JsonException
        | MalformedInput _ -> MessageInvalid, DataFailure, $"Signal could not read a message from the page. {unchanged}"
        | _ -> Unexpected, IntegrationFailure, $"Signal encountered an unexpected problem. {unchanged}"

    Aegis.faultOf aegis scope (FaultCode code) category FaultSeverity.Error OperationOnly Transient Continue message ex

/// The fault as the page shows it: safe presentation only (title, message,
/// reference), never the exception or its details.
type FaultView =
    { Title: string
      Message: string
      Reference: string }

let present (fault: Fault) =
    let presentation = Presentation.present "Signal could not complete that operation" fault

    { Title = presentation.Title
      Message = presentation.Message
      Reference = presentation.Reference }

/// The fault's named values, bound by the Forma fault-inline component.
let faultView (fault: FaultView option) : View =
    [ "hasOperationalFault", Value(Flag fault.IsSome)
      "operationalFaultTitle", Value(Text(fault |> Option.map _.Title |> Option.defaultValue ""))
      "operationalFaultMessage", Value(Text(fault |> Option.map _.Message |> Option.defaultValue ""))
      "operationalFaultReference", Value(Text(fault |> Option.map _.Reference |> Option.defaultValue "")) ]

/// Runs one step at the boundary: its result, or the presented fault when it
/// failed operationally. Programming defects and cancellation are re-raised
/// by Aegis, not returned.
let capture (aegis: AegisConfig) (operation: string) (step: unit -> 'result) : Result<'result, FaultView> =
    let scope = Aegis.scope aegis operation Map.empty

    Aegis.capture aegis scope (classify aegis) step |> Result.mapError present
