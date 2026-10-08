/// Answer-key and preference scoring (SCS-004, SCS-006) as pure functions
/// over plain inputs: a selected option or option set, a ranking, an
/// allocation, pairwise winners, best-worst choices. The answer primitives
/// that produce these inputs are WI-0045's; the scoring is defined here so
/// it is the same whatever selector presents it.
///
/// Every function explains itself: what was credited, what was penalized,
/// the raw credit and penalty, and any cap or floor (SCS-004
/// "Explainability"). Undefined results (an empty denominator) are explicit.
module Echelon.Signal.Engine.Keyed

/// How a key produced its contribution.
type Explanation =
    { Credited: string list
      Penalized: string list
      Credit: float
      Penalty: float
      Capped: bool
      Floored: bool
      Final: float }

let private explained credited penalized credit penalty =
    { Credited = credited
      Penalized = penalized
      Credit = credit
      Penalty = penalty
      Capped = false
      Floored = false
      Final = credit - penalty }

// ---------------------------------------------------------------------------
// Single-choice answer keys (CorrectIncorrect, CorrectIncorrectBlank).
// ---------------------------------------------------------------------------

type SingleKey =
    { Correct: Set<string>
      PointsCorrect: float
      /// Usually 0; negative for negative marking.
      PointsIncorrect: float
      /// Independent of incorrect (SCS-004).
      PointsBlank: float }

let single (key: SingleKey) (selected: string option) : Explanation =
    match selected with
    | None -> explained [] [] key.PointsBlank 0.0
    | Some id when key.Correct.Contains id -> explained [ id ] [] key.PointsCorrect 0.0
    | Some id -> explained [] [ id ] (max 0.0 key.PointsIncorrect) (max 0.0 -key.PointsIncorrect)

// ---------------------------------------------------------------------------
// Multi-choice keys.
// ---------------------------------------------------------------------------

type ExtraSelections =
    | IgnoreExtra
    /// Any selection outside the required set loses the credit.
    | ExtraLosesCredit

type PartialCreditKey =
    { Correct: Set<string>
      PerCorrect: float
      /// Deducted per incorrect selection; 0 disables penalties.
      PerIncorrect: float
      Floor: float option
      Cap: float option }

type MultiKey =
    | ExactSetMatch of correct: Set<string> * points: float
    | AnyCorrect of acceptable: Set<string> * points: float
    | AllRequired of required: Set<string> * points: float * extra: ExtraSelections
    | NoneForbidden of forbidden: Set<string> * points: float
    | PartialCredit of PartialCreditKey
    | OptionWeighted of weights: Map<string, float>
    | CountSelected

let multi (key: MultiKey) (selected: Set<string>) : Explanation =
    let list = Set.toList

    match key with
    | ExactSetMatch(correct, points) ->
        if selected = correct then explained (list selected) [] points 0.0
        else explained [] (list (Set.difference selected correct)) 0.0 0.0
    | AnyCorrect(acceptable, points) ->
        let hit = Set.intersect selected acceptable
        if hit.IsEmpty then explained [] [] 0.0 0.0 else explained (list hit) [] points 0.0
    | AllRequired(required, points, extra) ->
        let extras = Set.difference selected required
        let allThere = Set.isSubset required selected

        match allThere, extra with
        | true, IgnoreExtra -> explained (list required) [] points 0.0
        | true, ExtraLosesCredit when extras.IsEmpty -> explained (list required) [] points 0.0
        | _ -> explained (list (Set.intersect selected required)) (list extras) 0.0 0.0
    | NoneForbidden(forbidden, points) ->
        let hit = Set.intersect selected forbidden
        if hit.IsEmpty then explained [] [] points 0.0 else explained [] (list hit) 0.0 0.0
    | PartialCredit k ->
        let right = Set.intersect selected k.Correct
        let wrong = Set.difference selected k.Correct
        let credit = float right.Count * k.PerCorrect
        let penalty = float wrong.Count * k.PerIncorrect
        let raw = credit - penalty
        let capped = k.Cap |> Option.exists (fun c -> raw > c)
        let floored = k.Floor |> Option.exists (fun f -> raw < f)

        let final =
            let capped = k.Cap |> Option.fold min raw
            k.Floor |> Option.fold max capped

        { Credited = list right
          Penalized = list wrong
          Credit = credit
          Penalty = penalty
          Capped = capped
          Floored = floored
          Final = final }
    | OptionWeighted weights ->
        let credited = selected |> Set.filter weights.ContainsKey
        explained (list credited) [] (credited |> Seq.sumBy (fun id -> weights[id])) 0.0
    | CountSelected -> explained (list selected) [] (float selected.Count) 0.0

/// Percentage of items correct; undefined with no items (never 0/0).
let percentCorrect (correct: bool list) : Scoring.Outcome =
    match correct with
    | [] -> Scoring.NotScored(Scoring.Undefined "no items to mark")
    | marks ->
        let right = marks |> List.filter id |> List.length
        Scoring.Score(100.0 * float right / float marks.Length, marks.Length, 0)

// ---------------------------------------------------------------------------
// Ranking (best first).
// ---------------------------------------------------------------------------

type RankMethod =
    /// n points for first, n - 1 for second, ... 1 for last.
    | RankPoints
    /// n - 1 for first down to 0 for last.
    | BordaCount
    /// 1 / position.
    | InverseRank
    /// Credit inside the top K: equal (1 each) or by position (K, K-1, ...).
    | TopKRankCredit of k: int * positional: bool
    /// Declared points per position, first position first.
    | PositionWeighted of points: float list

/// The score of one item in a ranking of `itemCount` items; None when the
/// item was not ranked.
let rank (method: RankMethod) (itemCount: int) (ranking: string list) (item: string) : float option =
    ranking
    |> List.tryFindIndex ((=) item)
    |> Option.map (fun index ->
        let position = index + 1

        match method with
        | RankPoints -> float (itemCount - position + 1)
        | BordaCount -> float (itemCount - position)
        | InverseRank -> 1.0 / float position
        | TopKRankCredit(k, false) -> if position <= k then 1.0 else 0.0
        | TopKRankCredit(k, true) -> if position <= k then float (k - position + 1) else 0.0
        | PositionWeighted points -> if position <= points.Length then points[position - 1] else 0.0)

// ---------------------------------------------------------------------------
// Allocation (constant sum).
// ---------------------------------------------------------------------------

type AllocationMethod =
    | DirectAllocation of item: string
    /// The item's share of the total, 0..1.
    | NormalizedAllocation of item: string
    | AllocationSharePercent of item: string
    | WeightedAllocation of weights: Map<string, float>
    /// Sum of absolute distances from declared targets.
    | DistanceFromTargetAllocation of targets: Map<string, float>

let allocation (method: AllocationMethod) (allocated: Map<string, float>) : Scoring.Outcome =
    let total = allocated |> Map.toSeq |> Seq.sumBy snd
    let valueOf id = allocated.TryFind id |> Option.defaultValue 0.0
    let score v = Scoring.Score(v, allocated.Count, 0)

    match method with
    | DirectAllocation id -> score (valueOf id)
    | NormalizedAllocation _
    | AllocationSharePercent _ when total = 0.0 -> Scoring.NotScored(Scoring.Undefined "nothing was allocated")
    | NormalizedAllocation id -> score (valueOf id / total)
    | AllocationSharePercent id -> score (100.0 * valueOf id / total)
    | WeightedAllocation weights -> score (allocated |> Map.toSeq |> Seq.sumBy (fun (id, v) -> v * (weights.TryFind id |> Option.defaultValue 0.0)))
    | DistanceFromTargetAllocation targets ->
        let ids = Set.union (allocated |> Map.keys |> Set.ofSeq) (targets |> Map.keys |> Set.ofSeq)
        score (ids |> Seq.sumBy (fun id -> abs (valueOf id - (targets.TryFind id |> Option.defaultValue 0.0))))

// ---------------------------------------------------------------------------
// Pairwise comparison and best-worst (MaxDiff).
// ---------------------------------------------------------------------------

/// (winner, loser) per comparison.
type Comparison = string * string

let winCount (comparisons: Comparison list) (item: string) =
    comparisons |> List.filter (fun (w, _) -> w = item) |> List.length |> float

/// Wins weighted by the declared weight of the beaten item.
let weightedWinCount (weights: Map<string, float>) (comparisons: Comparison list) (item: string) =
    comparisons
    |> List.filter (fun (w, _) -> w = item)
    |> List.sumBy (fun (_, l) -> weights.TryFind l |> Option.defaultValue 1.0)

let winLossDifference (comparisons: Comparison list) (item: string) =
    winCount comparisons item - (comparisons |> List.filter (fun (_, l) -> l = item) |> List.length |> float)

/// (best, worst) per best-worst set.
type BestWorst = string * string

let bestCount (choices: BestWorst list) (item: string) =
    choices |> List.filter (fun (b, _) -> b = item) |> List.length |> float

let worstCount (choices: BestWorst list) (item: string) =
    choices |> List.filter (fun (_, w) -> w = item) |> List.length |> float

let bestMinusWorst (choices: BestWorst list) (item: string) = bestCount choices item - worstCount choices item

/// Best minus worst over the number of sets the item appeared in; undefined
/// for an item that never appeared.
let normalizedBestMinusWorst (appearances: Map<string, int>) (choices: BestWorst list) (item: string) : Scoring.Outcome =
    match appearances.TryFind item with
    | Some n when n > 0 -> Scoring.Score(bestMinusWorst choices item / float n, n, 0)
    | _ -> Scoring.NotScored(Scoring.Undefined $"'{item}' never appeared")
