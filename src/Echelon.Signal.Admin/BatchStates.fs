/// Administrator import (an intake batch) as an ARX-002 state system
/// (WI-0092): the batch's status, the explicit result of recording an
/// item's outcome or cancelling the rest, the capabilities the batch
/// grants, the obligations it carries (an unknown write outcome is
/// reconciled, never assumed failed), and what is known about each item.
///
/// Pure.
module Echelon.Signal.Admin.BatchStates

open Echelon.Signal.Engine.StateModel
open Echelon.Signal.Admin.Intake

let SystemName = "import"

type Command =
    | RecordOutcome of artifactHash: string * ItemOutcome
    | CancelRemainder

let commandName =
    function
    | RecordOutcome _ -> "record"
    | CancelRemainder -> "cancel"

let stateName (batch: Batch) = statusName (summarize batch).Status

let private has f (batch: Batch) = batch.Items |> List.exists (fun (_, o) -> o |> Option.exists f)

let private reconciling = function ReconciliationRequired -> true | _ -> false
let private blocked = function BlockedMissingTemplate _ | BlockedUnsupportedEncoding _ -> true | _ -> false

let capabilities (batch: Batch) =
    let open' = not batch.Cancelled && not (remaining batch).IsEmpty

    set
        [ if open' then Capability.CanImportSubmission
          if open' && has reconciling batch then Capability.CanReconcileEffect
          if open' && has blocked batch then Capability.CanRetryEffect ]

let obligations (batch: Batch) =
    let count f = batch.Items |> List.filter (snd >> Option.exists f) |> List.length

    if batch.Cancelled then
        []
    else
        [ match count reconciling with
          | 0 -> ()
          | n ->
              { Code = "ReconcileUnknownPersistenceOutcome"
                Detail = $"{n} item(s) whose write outcome is unknown."
                DischargedBy = "reading the store to learn whether each write landed" }
          match count (function BlockedMissingTemplate _ -> true | _ -> false) with
          | 0 -> ()
          | n ->
              { Code = "ResolveTemplate"
                Detail = $"{n} item(s) blocked: the group's template is not in the catalog."
                DischargedBy = "the group's exact template in the catalog" }
          match count (function BlockedUnsupportedEncoding _ -> true | _ -> false) with
          | 0 -> ()
          | n ->
              { Code = "ResolveUnsupportedCapability"
                Detail = $"{n} item(s) blocked: an encoding this version cannot read."
                DischargedBy = "a Signal version that reads the encoding" } ]

/// What is known about one item: its decided outcome; unknown while its
/// write outcome is unknown or it is undecided; searched-not-found when the
/// batch never held it.
let item (batch: Batch) (artifactHash: string) : Knowledge<ItemOutcome> =
    match batch.Items |> List.tryFind (fst >> (=) artifactHash) with
    | None -> SearchedNotFound(artifactHash, $"batch {batch.BatchId} at revision {batch.Revision}")
    | Some(_, Some ReconciliationRequired) -> Unknown "the write's outcome is unknown, not failed"
    | Some(_, Some outcome) -> Known outcome
    | Some(_, None) -> Unknown "not processed yet"

let attempt (command: Command) (batch: Batch) : Result<Batch, Illegal> =
    let refuse reason = illegal SystemName (stateName batch) (commandName command) reason
    let need = (if (capabilities batch).Contains Capability.CanImportSubmission then None else Some Capability.CanImportSubmission)

    match command, need with
    | _, Some _ when batch.Cancelled -> refuse "the batch was cancelled; what it committed stays"
    | _, Some _ -> refuse "every item is decided"
    | RecordOutcome(hash, _), None when not (batch.Items |> List.exists (fst >> (=) hash)) -> refuse $"the batch holds no artifact {hash}"
    | RecordOutcome(hash, _), None when not (List.contains hash (remaining batch)) -> refuse $"artifact {hash} is decided; a decided item never changes"
    | RecordOutcome(hash, outcome), None -> Ok(record [ hash, outcome ] batch)
    | CancelRemainder, None -> Ok(cancel batch)

let terminal (batch: Batch) = batch.Cancelled || (remaining batch).IsEmpty

let system: System<Batch, Command> =
    { Name = SystemName
      StateName = stateName
      CommandName = commandName
      Attempt = attempt
      Capabilities = capabilities
      Requires = fun _ -> Some Capability.CanImportSubmission
      Obligations = obligations
      Terminal = terminal }
