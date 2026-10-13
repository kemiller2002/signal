/// What a multi-choice question tells the person about its count and its
/// exclusive options (SCS-011, WI-0081): the rule before anything is chosen,
/// and where a selection stands as it changes, so a count is explained
/// before a submit or move is refused.
///
/// Pure.
module Echelon.Signal.Engine.SelectionGuidance

open Echelon.Signal.Engine.Primitives

/// The rule and exclusive options a question's selections follow, if it is a
/// multi-choice question.
let private ofAnswer (answer: AnswerDefinition) =
    match answer with
    | MultiChoice m -> Some(m.Selection, m.Options |> List.filter (fun o -> List.contains o.Id m.Exclusive), Some m.WhenExclusive)
    | HierarchicalMulti(_, _, selection) -> Some(selection, [], None)
    | _ -> None

let private ruleText (rule: SelectionRule) =
    match rule with
    | AnyCount -> ""
    | Exactly 1 -> "Choose 1."
    | Exactly n -> $"Choose {n}."
    | AtLeast n -> $"Choose at least {n}."
    | AtMost n -> $"Choose at most {n}."
    | Between(lo, hi) -> $"Choose {lo} to {hi}."

let private aloneText (option: ChoiceOption) policy =
    match policy with
    | Some ClearOthers -> $"Choosing \"{option.Label}\" clears the others."
    | _ -> $"\"{option.Label}\" can only be chosen on its own."

/// The rule, said before anything is chosen; "" when there is none.
let hint (answer: AnswerDefinition) : string =
    match ofAnswer answer with
    | Some(rule, exclusive, policy) -> ruleText rule :: (exclusive |> List.map (fun o -> aloneText o policy)) |> List.filter ((<>) "") |> String.concat " "
    | None -> ""

/// Where a selection stands; "" for nothing chosen or a question without a count.
let status (answer: AnswerDefinition) (selected: Set<string>) : string =
    match ofAnswer answer with
    | None -> ""
    | Some _ when selected.IsEmpty -> ""
    | Some(rule, exclusive, policy) ->
        let n = selected.Count

        match exclusive |> List.tryFind (fun o -> selected.Contains o.Id) with
        | Some option when n > 1 && policy = Some RejectCombination -> aloneText option policy
        | _ ->
            let fewer k = $"{n} chosen: choose at least {k - n} more."

            match rule with
            | Exactly k when n < k -> $"{n} chosen: choose {k - n} more."
            | Exactly k when n > k -> $"{n} chosen: choose exactly {k}."
            | AtLeast k
            | Between(k, _) when n < k -> fewer k
            | AtMost k
            | Between(_, k) when n > k -> $"{n} chosen: choose at most {k}."
            | _ -> $"{n} chosen."
