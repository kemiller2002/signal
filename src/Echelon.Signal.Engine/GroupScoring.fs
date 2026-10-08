/// Group-level scoring (ACR-002 §6 GroupScoring, VER-006 "Group-Level
/// Scoring", CAN-002 §8): one result from the overall scores of a group's
/// contributions.
///
/// Contributions that are not scored are excluded and counted, never
/// zeroed. In an anonymous group below the minimum reportable count the
/// score is suppressed, not computed (ID-003 §22, RPT-003); the count is
/// still reported.
module Echelon.Signal.Engine.GroupScoring

open System
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Import
open Echelon.Signal.Engine.Groups

type Method =
    | GroupMean
    /// The worst contribution by the declared direction.
    | GroupWeakest of Direction
    /// The mean of per-role means, so each role has the declared influence
    /// whatever its count (360-style). Roles without contributions are
    /// left out and the weights renormalized.
    | RoleBalanced of weights: (Role * float) list

type GroupScore =
    | GroupScored of value: float * included: int * excluded: int
    | GroupNotScored of reason: string
    /// Withheld by the disclosure policy: accepted below minimum.
    | Suppressed of accepted: int * minimum: int

let score
    (mode: IdentityMode)
    (minimumReportable: int)
    (decimals: int)
    (method: Method)
    (contributions: (Role option * float option) list)
    : GroupScore =
    let scored = contributions |> List.choose (fun (role, v) -> v |> Option.map (fun v -> role, v))
    let excluded = contributions.Length - scored.Length
    let round (v: float) = Math.Round(v, decimals, MidpointRounding.AwayFromZero)

    if mode = AnonymousGroup && contributions.Length < minimumReportable then
        Suppressed(contributions.Length, minimumReportable)
    elif scored.IsEmpty then
        GroupNotScored "no contribution was scored"
    else
        let values = scored |> List.map snd

        match method with
        | GroupMean -> GroupScored(round (List.average values), scored.Length, excluded)
        | GroupWeakest HigherIsBetter -> GroupScored(round (List.min values), scored.Length, excluded)
        | GroupWeakest HigherIsWorse -> GroupScored(round (List.max values), scored.Length, excluded)
        | GroupWeakest Neutral -> GroupNotScored "a weakest contribution needs a direction"
        | RoleBalanced weights ->
            let perRole =
                weights
                |> List.choose (fun (role, weight) ->
                    match scored |> List.filter (fun (r, _) -> r = Some role) |> List.map snd with
                    | [] -> None
                    | vs -> Some(List.average vs, weight))

            let total = perRole |> List.sumBy snd

            if perRole.IsEmpty || total <= 0.0 then
                GroupNotScored "no weighted role has a scored contribution"
            else
                GroupScored(round ((perRole |> List.sumBy (fun (v, w) -> v * w)) / total), scored.Length, excluded)
