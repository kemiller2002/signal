/// The survey page's compound inputs (SCS-013, WI-0081): rankings and
/// allocations typed as numbers per option, a range as its two ends, a
/// best-worst pair picked on two scales, and option trees shown indented.
/// Each turns what the person typed or picked into the answer the session
/// holds; a value that is not yet complete (part of a ranking, one end of a
/// range) is held as it is and reported by the rules, never guessed.
///
/// Pure.
module Echelon.Signal.Engine.GenericInputs

open System
open System.Globalization
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Template

let private invariant = CultureInfo.InvariantCulture

/// Positions a ranking fills.
let positions (r: RankingSpec) = r.Positions |> Option.defaultValue r.Options.Length

let private shown (b: BoundedSpec) (value: float) = value.ToString($"F{b.Decimals}", invariant)

/// A tree's options in order, indented by depth, and whether each is a leaf.
let treeLabels (nodes: TreeNode list) : (string * string * bool) list =
    let parentOf id = nodes |> List.tryFind (fun n -> n.Option.Id = id) |> Option.bind _.Parent

    let rec depth id =
        match parentOf id with
        | Some parent -> 1 + depth parent
        | None -> 0

    nodes
    |> List.map (fun n ->
        let leaf = nodes |> List.forall (fun other -> other.Parent <> Some n.Option.Id)
        n.Option.Id, String.replicate (depth n.Option.Id) "— " + n.Option.Label, leaf)

type NumberInput =
    { Id: string
      Label: string
      Minimum: string
      Maximum: string
      Step: string }

/// The number inputs a question takes, if it takes any.
let numberInputs (q: Question) : NumberInput list =
    match q.Answer with
    | Ranking r ->
        r.Options |> List.map (fun o -> { Id = o.Id; Label = o.Label; Minimum = "1"; Maximum = string (positions r); Step = "1" })
    | Allocation a ->
        a.Options
        |> List.map (fun o -> { Id = o.Id; Label = o.Label; Minimum = string a.ItemMinimum; Maximum = string a.ItemMaximum; Step = "1" })
    | BoundedRange b ->
        [ for id, label in [ "low", "From"; "high", "To" ] ->
              { Id = id; Label = label; Minimum = shown b b.Minimum; Maximum = shown b b.Maximum; Step = shown b b.Step } ]
    | _ -> []

/// What an input shows for the answer held now.
let numberShown (q: Question) (answer: AnswerState option) (inputId: string) : string =
    match q.Answer, answer with
    | Ranking _, Some(Value(Order ids)) -> ids |> List.tryFindIndex ((=) inputId) |> Option.map (fun i -> string (i + 1)) |> Option.defaultValue ""
    | Allocation _, Some(Value(Allocated steps)) -> steps.TryFind inputId |> Option.map string |> Option.defaultValue ""
    | BoundedRange b, Some(Value(TickRange(lo, hi))) ->
        let tick = if inputId = "low" then lo else hi
        if tick >= 0 then shown b (tickValue b tick) else ""
    | _ -> ""

/// What the question asks of the person, for its compound input.
let hint (q: Question) : string =
    match q.Answer with
    | Ranking r -> $"Number your choices from 1 (first) to {positions r}."
    | Allocation a when a.Step = 1.0 -> $"Share {a.Total} points between the options."
    | Allocation a -> $"Share {a.Total} points between the options; each point is worth {a.Step.ToString(invariant)}."
    | BoundedRange b -> $"Give a range from {shown b b.Minimum} to {shown b b.Maximum}."
    | BestWorst _ -> "Choose the one that fits best and the one that fits least."
    | MultiChoice _
    | HierarchicalMulti _ -> SelectionGuidance.hint q.Answer
    | _ -> ""

let private integer (text: string) =
    match Int32.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, invariant) with
    | true, n -> Some n
    | _ -> None

/// The answer after a number is typed: `Some next` (next is None when the
/// answer is cleared), or None when the text is not a value this input
/// takes, so nothing changes.
let numbered (q: Question) (answer: AnswerState option) (inputId: string) (text: string) : AnswerState option option =
    let blank = String.IsNullOrWhiteSpace text

    match q.Answer with
    | Ranking r when r.Options |> List.exists (fun o -> o.Id = inputId) ->
        let rest =
            match answer with
            | Some(Value(Order ids)) -> ids |> List.filter ((<>) inputId)
            | _ -> []

        match blank, integer text with
        | true, _ -> Some(if rest.IsEmpty then None else Some(Value(Order rest)))
        | false, Some p when p >= 1 && p <= positions r -> Some(Some(Value(Order(rest |> List.insertAt (min (p - 1) rest.Length) inputId))))
        | _ -> None
    | Allocation a when a.Options |> List.exists (fun o -> o.Id = inputId) ->
        let steps =
            match answer with
            | Some(Value(Allocated steps)) -> steps
            | _ -> Map.empty

        let held (next: Map<string, int>) = Some(if next.IsEmpty then None else Some(Value(Allocated next)))

        match blank, integer text with
        | true, _ -> held (steps.Remove inputId)
        | false, Some n when n >= a.ItemMinimum && n <= a.ItemMaximum -> held (steps.Add(inputId, n))
        | _ -> None
    | BoundedRange b when inputId = "low" || inputId = "high" ->
        let lo, hi =
            match answer with
            | Some(Value(TickRange(lo, hi))) -> lo, hi
            | _ -> -1, -1

        let tick =
            match Double.TryParse(text.Trim(), NumberStyles.Float, invariant) with
            | true, v when b.Step > 0.0 ->
                let t = int (Math.Round((v - b.Minimum) / b.Step))
                if t >= 0 && t < ticks b && abs (tickValue b t - v) < 1e-9 then Some t else None
            | _ -> None

        let held (lo, hi) = Some(if lo < 0 && hi < 0 then None else Some(Value(TickRange(lo, hi))))

        match blank, tick, inputId with
        | true, _, "low" -> held (-1, hi)
        | true, _, _ -> held (lo, -1)
        | false, Some t, "low" -> held (t, hi)
        | false, Some t, _ -> held (lo, t)
        | false, None, _ -> None
    | _ -> None

type Side =
    | Best
    | Worst

/// A best-worst question's options, picked on two scales.
let pairs (q: Question) : (string * string) list =
    match q.Answer with
    | BestWorst os -> os |> List.map (fun o -> o.Id, o.Label)
    | _ -> []

let private pickOf (answer: AnswerState option) =
    match answer with
    | Some(Value(BestWorstPick(b, w))) -> b, w
    | _ -> "", ""

/// The answer after an option is picked as best or worst; picking the
/// option already on the other scale moves it.
let picked (q: Question) (answer: AnswerState option) (side: Side) (optionId: string) : AnswerState option option =
    if pairs q |> List.exists (fst >> (=) optionId) then
        let b, w = pickOf answer

        let b, w =
            match side with
            | Best -> optionId, (if w = optionId then "" else w)
            | Worst -> (if b = optionId then "" else b), optionId

        Some(Some(Value(BestWorstPick(b, w))))
    else
        None

/// Whether an option is the current best, and the current worst.
let pairShown (answer: AnswerState option) (optionId: string) =
    let b, w = pickOf answer
    b = optionId, w = optionId

// ---------------------------------------------------------------------------
// Input the link cannot carry yet (WI-0086, escaped defects from #73).
//
// The respondent link carries only answers the envelope can encode: a whole
// ranking, a range with both ends in order, best and least both picked. What
// a person has typed or picked short of that is held here, on the page: it
// is not an answer, it is not counted, and it is not in the link, so the
// link always equals the response (LURL-001). The page says so until the
// answer is finished or cleared. A ranking is held as the positions typed,
// so they can be typed in any order.
// ---------------------------------------------------------------------------

/// What a compound input holds while it is not yet an answer the link can carry.
type Held =
    /// A ranking's positions, by option, as typed.
    | RankPositions of Map<string, int>
    /// A range with one end, or a best-worst with one side.
    | Partial of AnswerState

/// Whether the link can carry a state as this question's answer: it has an
/// index, and it is a value the decoder accepts (`GenericEnvelope.answerOf`
/// refuses any value `Primitives.check` rejects, such as an allocation that
/// does not share exactly its points).
let encodable (q: Question) (state: AnswerState) =
    match state with
    | Value v -> (toIndex q.Answer v).IsSome && (check q.Answer v).IsNone
    | Special s -> List.contains s q.SpecialStates

/// What the inputs show: what is held, else the answer.
let working (answer: AnswerState option) (held: Held option) =
    match held with
    | Some(Partial state) -> Some state
    | _ -> answer

/// A next value split into what is answered and what is held.
let private settle (q: Question) (next: AnswerState option) : AnswerState option * Held option =
    match next with
    | None -> None, None
    | Some state when encodable q state -> Some state, None
    | Some state -> None, Some(Partial state)

let private rankPositions (answer: AnswerState option) (held: Held option) =
    match held, answer with
    | Some(RankPositions positions), _ -> positions
    | _, Some(Value(Order ids)) -> ids |> List.mapi (fun i id -> id, i + 1) |> Map.ofList
    | _ -> Map.empty

/// The answer and what is held after a number is typed; None when the text
/// is not a value this input takes, so nothing changes.
let typed (q: Question) (answer: AnswerState option) (held: Held option) (inputId: string) (text: string) : (AnswerState option * Held option) option =
    match q.Answer with
    | Ranking r when r.Options |> List.exists (fun o -> o.Id = inputId) ->
        let current = rankPositions answer held

        let next =
            match String.IsNullOrWhiteSpace text, integer text with
            | true, _ -> Some(current.Remove inputId)
            // A position another option holds moves to this one.
            | false, Some p when p >= 1 && p <= positions r -> Some((current |> Map.filter (fun _ v -> v <> p)).Add(inputId, p))
            | _ -> None

        next
        |> Option.map (fun positionsByOption ->
            if positionsByOption.IsEmpty then
                None, None
            elif positionsByOption.Count = positions r then
                Some(Value(Order(positionsByOption |> Map.toList |> List.sortBy snd |> List.map fst))), None
            else
                None, Some(RankPositions positionsByOption))
    // An allocation carries every option, a blank at the minimum, so the link
    // and the page agree on it exactly.
    | Allocation a ->
        numbered q (working answer held) inputId text
        |> Option.map (fun next ->
            match next with
            | Some(Value(Allocated steps)) ->
                settle q (Some(Value(Allocated(a.Options |> List.map (fun o -> o.Id, steps.TryFind o.Id |> Option.defaultValue a.ItemMinimum) |> Map.ofList))))
            | other -> settle q other)
    | _ -> numbered q (working answer held) inputId text |> Option.map (settle q)

/// The answer and what is held after an option is picked as best or worst.
let pickedHeld (q: Question) (answer: AnswerState option) (held: Held option) (side: Side) (optionId: string) =
    picked q (working answer held) side optionId |> Option.map (settle q)

/// What an input shows, from what is held or answered.
let shownWith (q: Question) (answer: AnswerState option) (held: Held option) (inputId: string) =
    match q.Answer with
    | Ranking _ -> rankPositions answer held |> Map.tryFind inputId |> Option.map string |> Option.defaultValue ""
    | _ -> numberShown q (working answer held) inputId

/// What a multi-choice question shows as chosen: what is held, else the answer.
let selectedWith (answer: AnswerState option) (held: Held option) =
    match working answer held with
    | Some(Value(Choices ids)) -> ids
    | _ -> Set.empty

/// The answer and what is held after an option is selected or cleared
/// (SCS-011): a selection the question's count or exclusive rule refuses is
/// held on the page, never written into the link, which could not read it
/// back (LURL-001). None when nothing changes.
let toggled (q: Question) (answer: AnswerState option) (held: Held option) (optionId: string) (selected: bool) : (AnswerState option * Held option) option =
    let current = selectedWith answer held

    let next =
        match q.Answer with
        | MultiChoice m when m.Options |> List.exists (fun o -> o.Id = optionId) && current.Contains optionId <> selected -> Some(select m current optionId)
        | HierarchicalMulti(nodes, _, _) when nodes |> List.exists (fun n -> n.Option.Id = optionId) && current.Contains optionId <> selected ->
            Some(if selected then current.Add optionId else current.Remove optionId)
        | _ -> None

    next |> Option.map (fun ids -> settle q (if Set.isEmpty ids then None else Some(Value(Choices ids))))

