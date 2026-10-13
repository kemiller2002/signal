/// Finalization (live response to portable submission, anonymous
/// unlinkability included) as an ARX-002 state system (WI-0092): the
/// binding's states, the explicit result of finalizing, what the state
/// grants, and what it still owes.
///
/// Pure.
module Echelon.Signal.Engine.FinalizationStates

open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.StateModel

let SystemName = "finalization"

/// Finalizing with fresh entropy from the application edge.
type Command = Finalize of entropy: byte[]

let commandName (Finalize _) = "finalize"

let stateName (e: Envelope) =
    match production e.Binding with
    | Unbound -> "Unbound"
    | IdentifiedInvitation _ -> "IdentifiedLive"
    | AnonymousInvitation _ -> "AnonymousLive"
    | Identified _ -> "IdentifiedSubmission"
    | Anonymous _ -> "AnonymousSubmission"
    | Test _ -> "Test"

let private invited (e: Envelope) =
    match e.Binding with
    | IdentifiedInvitation _
    | AnonymousInvitation _ -> true
    | _ -> false

let capabilities (assessment: Assessment) (e: Envelope) =
    set
        [ if Submission.isFinal e then Capability.CanCopySubmission
          elif invited e && (unanswered assessment e.Answers).IsEmpty then Capability.CanFinalize ]

let obligations (assessment: Assessment) (e: Envelope) =
    let missing = unanswered assessment e.Answers

    [ if invited e && not missing.IsEmpty then
          { Code = "MissingRequiredAnswer"
            Detail = $"{missing.Length} item(s) unanswered."
            DischargedBy = "an answer (or a special state) for each item" }
      match e.Binding with
      | AnonymousInvitation _ ->
          { Code = "DrawUnlinkableIdentifier"
            Detail = "An anonymous submission needs a fresh identifier."
            DischargedBy = "16 bytes of secure entropy that reproduce no invitation id" }
      | _ -> () ]

let attempt (assessment: Assessment) (Finalize entropy as command) (e: Envelope) : Result<Envelope, Illegal> =
    let refuse reason = illegal SystemName (stateName e) (commandName command) reason

    match Submission.finalize assessment entropy e with
    | Ok submission -> Ok submission
    | Error refusal -> refuse (Submission.describe refusal)

let system (assessment: Assessment) : System<Envelope, Command> =
    { Name = SystemName
      StateName = stateName
      CommandName = commandName
      Attempt = attempt assessment
      Capabilities = capabilities assessment
      Requires = fun _ -> Some Capability.CanFinalize
      Obligations = obligations assessment
      Terminal = Submission.isFinal }
