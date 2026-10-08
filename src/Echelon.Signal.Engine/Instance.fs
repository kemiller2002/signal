/// Instance runtime status (VER-003), derived, never stored.
module Echelon.Signal.Engine.Instance

open System
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template

// ---------------------------------------------------------------------------
// Instance runtime status (VER-003), derived, never stored. The clock is an
// argument; nothing here reads one.
// ---------------------------------------------------------------------------

type InstanceStatus =
    | NotStarted
    | InProgress
    | Completed
    | Expired
    | Cancelled

/// What an instance is now. Completeness alone never makes it Completed:
/// only finalization does (VER-003 "do not assume that response completeness
/// alone is sufficient").
let instanceStatus (cancelled: bool) (finalized: bool) (expiresAt: DateTimeOffset option) (now: DateTimeOffset) (answers: Answers) =
    if cancelled then Cancelled
    elif finalized then Completed
    elif expiresAt |> Option.exists (fun e -> now >= e) then Expired
    elif answers.IsEmpty then NotStarted
    else InProgress

/// Whether answers may still change under the template's runtime policy.
let canChangeAnswers (runtime: RuntimePolicy) (status: InstanceStatus) =
    match status with
    | NotStarted
    | InProgress -> true
    | Completed -> runtime.AllowChangesAfterCompletion
    | Expired
    | Cancelled -> false
