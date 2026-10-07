/// The built-in scoring catalog (ANS-003, SCS-002, SCS-003, SCS-005,
/// SCS-008, ALG-001): scorers are data, not code, and evaluation is a total,
/// deterministic function of a scorer and its observations.
///
/// A scorer is three explicit steps: an item scale (how one answer becomes a
/// number), an aggregate (how numbers become one value), and transforms
/// applied to that value. Missing and special answers are governed by an
/// explicit policy and are never zero by implication. No outcome is NaN or
/// infinite: an undefined value is `NotScored` with a reason.
module Echelon.Signal.Engine.Scoring

open System

/// Answer states that are not numbers (SCS-008).
type Special =
    | DontKnow
    | NotObserved
    | NotApplicable
    | Unanswered

type Observation =
    | Numeric of float
    | Special of Special

/// What to do with special states, declared, never assumed.
type SpecialPolicy =
    /// Leave them out of the numerator and the denominator.
    | Exclude
    /// Count them as this value. Must be declared to happen.
    | Substitute of value: float

type MissingPolicy =
    { /// Fewer usable observations than this is NotScored, not a low score.
      MinimumObservations: int
      Special: SpecialPolicy }

/// How one numeric answer becomes the number that is aggregated.
type ItemScale =
    /// DirectValue: the stored value is the score.
    | Direct
    /// Reverse within declared bounds: min + max - value.
    | Reverse of minimum: float * maximum: float
    /// MappedChoice, BooleanMap, ProgressStateMap: explicit values for each
    /// stored value. A value the map does not name is a configuration error.
    | Mapped of Map<float, float>

/// Which side of a cutoff a category counts on.
type Aggregate =
    | RawSum
    | Mean
    /// Even cardinality: the mean of the two middle values.
    | Median
    | Minimum
    | Maximum
    /// Answered (usable) observations, after the special policy.
    | CountAnswered
    /// Observations at or above the threshold.
    | CountAtLeast of threshold: float
    | WeightedSum of weights: float list
    | WeightedMean of weights: float list
    /// Sum over the sum of the per-item maxima of the usable items only, as a
    /// percentage (the denominator excludes excluded items).
    | PercentageOfMaximum of itemMaximum: float
    /// Mean placed within [minimum, maximum] as a percentage; handles scales
    /// whose minimum is not zero.
    | PercentageOfRange of minimum: float * maximum: float
    /// Mean after removing `trim` observations from each end.
    | TrimmedMean of trim: int
    | CappedSum of cap: float
    /// Mean of the n highest / lowest observations.
    | TopN of n: int
    | BottomN of n: int
    /// Percentage of observations in the top k categories of an integer
    /// scale 0..scaleMaximum.
    | TopKBox of k: int * scaleMaximum: int
    | BottomKBox of k: int * scaleMaximum: int
    /// TopKBox with an explicit weight per included category, highest first.
    | WeightedTopKBox of weights: float list * scaleMaximum: int
    /// Percentage at or above the favorable cutoff / at or below the
    /// unfavorable cutoff, and their difference (the neutral middle stays in
    /// neither side).
    | FavorableRate of atLeast: float
    | UnfavorableRate of atMost: float
    | NetFavorable of favorableAtLeast: float * unfavorableAtMost: float
    /// Standard NPS on a 0-10 scale: percent 9-10 minus percent 0-6. A group
    /// metric; a single observation is still an aggregate of one.
    | NetPromoterScore

/// Applied in order to the aggregate value.
type Transform =
    /// output = input * multiplier + offset
    | LinearTransform of multiplier: float * offset: float
    /// Maps [fromMin, fromMax] onto [toMin, toMax].
    | LinearNormalize of fromMinimum: float * fromMaximum: float * toMinimum: float * toMaximum: float
    | Clamp of minimum: float * maximum: float
    | Floor
    | Ceiling

type Scorer =
    { Scale: ItemScale
      Aggregate: Aggregate
      Transforms: Transform list
      Missing: MissingPolicy
      /// Decimal places, rounded half away from zero.
      Decimals: int }

type NotScoredReason =
    | InsufficientObservations of usable: int * minimum: int
    /// The value is mathematically undefined (for example, a zero range).
    | Undefined of reason: string
    | InvalidConfiguration of reason: string

type Outcome =
    | Score of value: float * included: int * excluded: int
    | NotScored of NotScoredReason

/// Configuration problems that make a scorer unusable, found before any
/// answer is scored (publication-time validation, SCS-015/AST-002).
let validate (scorer: Scorer) : string list =
    [ if scorer.Missing.MinimumObservations < 0 then "MinimumObservations must not be negative"
      if scorer.Decimals < 0 || scorer.Decimals > 10 then "Decimals must be between 0 and 10"
      match scorer.Scale with
      | Reverse(lo, hi) when lo >= hi -> "Reverse needs minimum < maximum"
      | _ -> ()
      match scorer.Aggregate with
      | TrimmedMean trim when trim < 0 -> "TrimmedMean trim must not be negative"
      | TopN n
      | BottomN n when n < 1 -> "TopN/BottomN need n >= 1"
      | TopKBox(k, m)
      | BottomKBox(k, m) when k < 1 || k > m + 1 -> "K must be between 1 and the scale's category count"
      | WeightedTopKBox(weights, m) when weights.IsEmpty || weights.Length > m + 1 -> "WeightedTopKBox needs 1..category-count weights"
      | WeightedTopKBox(weights, _) when weights |> List.exists (fun w -> w < 0.0 || w > 1.0) -> "WeightedTopKBox weights must be within 0..1"
      | PercentageOfRange(lo, hi) when lo >= hi -> "PercentageOfRange needs minimum < maximum"
      | PercentageOfMaximum m when m <= 0.0 -> "PercentageOfMaximum needs a positive item maximum"
      | NetFavorable(fav, unfav) when fav <= unfav -> "NetFavorable cutoffs must leave favorable above unfavorable"
      | _ -> ()
      for transform in scorer.Transforms do
          match transform with
          | LinearNormalize(a, b, _, _) when a = b -> "LinearNormalize needs a non-empty source range"
          | Clamp(lo, hi) when lo > hi -> "Clamp needs minimum <= maximum"
          | _ -> () ]

let private finite (value: float) = not (Double.IsNaN value || Double.IsInfinity value)

let private scaleItem (scale: ItemScale) (value: float) : Result<float, string> =
    match scale with
    | Direct -> Ok value
    | Reverse(lo, hi) when value < lo || value > hi -> Error $"value {value} is outside the declared scale {lo}..{hi}"
    | Reverse(lo, hi) -> Ok(lo + hi - value)
    | Mapped map ->
        match map.TryFind value with
        | Some mapped -> Ok mapped
        | None -> Error $"value {value} has no declared mapping"

let private percent part whole = 100.0 * float part / float whole

let private median (sorted: float list) =
    let n = sorted.Length
    if n % 2 = 1 then sorted[n / 2] else (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0

/// Aggregates usable values (already scaled). Positional aggregates
/// (weights) receive every observation's slot so weights stay aligned; a
/// slot is None when the observation was excluded.
let private aggregate (aggregate: Aggregate) (slots: float option list) : Result<float, NotScoredReason> =
    let values = slots |> List.choose id
    let sorted = List.sort values
    let n = values.Length

    let weighted (weights: float list) mean =
        if weights.Length <> slots.Length then
            Error(InvalidConfiguration $"{weights.Length} weights for {slots.Length} items")
        else
            let pairs = List.zip slots weights |> List.choose (fun (v, w) -> v |> Option.map (fun v -> v, w))
            let total = pairs |> List.sumBy (fun (v, w) -> v * w)
            let weight = pairs |> List.sumBy snd

            if not mean then Ok total
            elif weight = 0.0 then Error(Undefined "the included weights sum to zero")
            else Ok(total / weight)

    let integerScale (m: int) =
        if values |> List.forall (fun v -> v = Math.Round v && v >= 0.0 && v <= float m) then
            Ok()
        else
            Error(InvalidConfiguration $"values must be whole numbers within 0..{m}")

    match aggregate with
    | RawSum -> Ok(List.sum values)
    | Mean -> Ok(List.average values)
    | Median -> Ok(median sorted)
    | Minimum -> Ok(List.head sorted)
    | Maximum -> Ok(List.last sorted)
    | CountAnswered -> Ok(float n)
    | CountAtLeast threshold -> Ok(values |> List.filter (fun v -> v >= threshold) |> List.length |> float)
    | WeightedSum weights -> weighted weights false
    | WeightedMean weights -> weighted weights true
    | PercentageOfMaximum itemMaximum -> Ok(100.0 * List.sum values / (itemMaximum * float n))
    | PercentageOfRange(lo, hi) -> Ok(100.0 * (List.average values - lo) / (hi - lo))
    | TrimmedMean trim when 2 * trim >= n -> Error(InvalidConfiguration $"trimming {trim} from each end leaves nothing of {n}")
    | TrimmedMean trim -> Ok(sorted |> List.skip trim |> List.take (n - 2 * trim) |> List.average)
    | CappedSum cap -> Ok(min cap (List.sum values))
    | TopN k -> Ok(sorted |> List.rev |> List.truncate k |> List.average)
    | BottomN k -> Ok(sorted |> List.truncate k |> List.average)
    | TopKBox(k, m) -> integerScale m |> Result.map (fun () -> percent (values |> List.filter (fun v -> v > float (m - k)) |> List.length) n)
    | BottomKBox(k, m) -> integerScale m |> Result.map (fun () -> percent (values |> List.filter (fun v -> v < float k) |> List.length) n)
    | WeightedTopKBox(weights, m) ->
        integerScale m
        |> Result.map (fun () ->
            let credit (v: float) =
                let rank = m - int v // 0 = top category
                if rank < weights.Length then weights[rank] else 0.0

            100.0 * (values |> List.sumBy credit) / float n)
    | FavorableRate cutoff -> Ok(percent (values |> List.filter (fun v -> v >= cutoff) |> List.length) n)
    | UnfavorableRate cutoff -> Ok(percent (values |> List.filter (fun v -> v <= cutoff) |> List.length) n)
    | NetFavorable(fav, unfav) ->
        Ok(percent (values |> List.filter (fun v -> v >= fav) |> List.length) n
           - percent (values |> List.filter (fun v -> v <= unfav) |> List.length) n)
    | NetPromoterScore ->
        integerScale 10
        |> Result.map (fun () ->
            percent (values |> List.filter (fun v -> v >= 9.0) |> List.length) n
            - percent (values |> List.filter (fun v -> v <= 6.0) |> List.length) n)

let private transform (value: float) (step: Transform) : Result<float, NotScoredReason> =
    match step with
    | LinearTransform(multiplier, offset) -> Ok(value * multiplier + offset)
    | LinearNormalize(a, b, c, d) -> Ok(c + (value - a) * (d - c) / (b - a))
    | Clamp(lo, hi) -> Ok(max lo (min hi value))
    | Floor -> Ok(Math.Floor value)
    | Ceiling -> Ok(Math.Ceiling value)

/// Scores observations. Counting aggregates (CountAnswered) are defined on
/// zero observations; every other aggregate needs at least one.
let evaluate (scorer: Scorer) (observations: Observation list) : Outcome =
    match validate scorer with
    | problem :: _ -> NotScored(InvalidConfiguration problem)
    | [] ->
        let slots =
            observations
            |> List.map (function
                | Numeric v -> Some(Ok v)
                | Special _ ->
                    match scorer.Missing.Special with
                    | Exclude -> None
                    | Substitute v -> Some(Ok v))
            |> List.map (Option.map (Result.bind (scaleItem scorer.Scale)))

        let rec collect acc =
            function
            | [] -> Ok(List.rev acc)
            | None :: rest -> collect (None :: acc) rest
            | Some(Ok v) :: rest -> collect (Some v :: acc) rest
            | Some(Error e) :: _ -> Error e

        match collect [] slots with
        | Error problem -> NotScored(InvalidConfiguration problem)
        | Ok slots ->
            let usable = slots |> List.choose id |> List.length
            let excluded = observations.Length - usable

            let needsValues =
                match scorer.Aggregate with
                | CountAnswered -> false
                | _ -> true

            if usable < scorer.Missing.MinimumObservations || (needsValues && usable = 0) then
                NotScored(InsufficientObservations(usable, max scorer.Missing.MinimumObservations 1))
            else
                let result =
                    aggregate scorer.Aggregate slots
                    |> Result.bind (fun value -> scorer.Transforms |> List.fold (fun acc step -> acc |> Result.bind (fun v -> transform v step)) (Ok value))

                match result with
                | Ok value when finite value -> Score(Math.Round(value, scorer.Decimals, MidpointRounding.AwayFromZero), usable, excluded)
                | Ok _ -> NotScored(Undefined "the result is not a finite number")
                | Error reason -> NotScored reason

// ---------------------------------------------------------------------------
// Categorical results and comparisons of scores (SCS-002, SCS-003).
// ---------------------------------------------------------------------------

/// Mode never silently picks one of a tie (SCS-003).
type ModeResult =
    | NoValues
    | SingleMode of float
    | Tied of float list

let mode (values: float list) =
    match values |> List.countBy id with
    | [] -> NoValues
    | counts ->
        let top = counts |> List.map snd |> List.max

        match counts |> List.filter (fun (_, c) -> c = top) |> List.map fst |> List.sort with
        | [ single ] -> SingleMode single
        | tied -> Tied tied

/// A band with an inclusive lower bound and an exclusive upper bound; the
/// last band's upper bound is inclusive.
type Band =
    { Label: string
      From: float
      To: float }

/// Bands must be ordered, non-empty, without overlap or gap.
let validateBands (bands: Band list) =
    [ if bands.IsEmpty then "at least one band is required"
      for band in bands do
          if band.From >= band.To then $"band '{band.Label}' is empty or reversed"
      for a, b in List.pairwise bands do
          if b.From < a.To then $"bands '{a.Label}' and '{b.Label}' overlap"
          elif b.From > a.To then $"there is a gap between '{a.Label}' and '{b.Label}'" ]

let band (bands: Band list) (value: float) : Result<string, string> =
    match validateBands bands with
    | problem :: _ -> Error problem
    | [] ->
        let last = List.last bands

        match bands |> List.tryFind (fun b -> value >= b.From && value < b.To) with
        | Some b -> Ok b.Label
        | None when value = last.To -> Ok last.Label
        | None -> Error $"{value} is outside every band"

/// PassFail on a numeric threshold, inclusive.
let passes (threshold: float) (value: float) = value >= threshold

/// A minus B; the declared direction is the caller's (SCS-003).
let difference (a: float) (b: float) = a - b

/// A over B. A zero denominator is an explicit outcome, never NaN/Infinity.
let ratio (a: float) (b: float) =
    if b = 0.0 then NotScored(Undefined "the denominator is zero") else Score(a / b, 2, 0)

type DistanceMode =
    | Absolute
    | Signed

let distanceFromTarget (mode: DistanceMode) (target: float) (value: float) =
    match mode with
    | Absolute -> abs (value - target)
    | Signed -> value - target

/// 100 at the target, falling linearly to 0 at `tolerance` away and staying
/// 0 beyond it.
let proximityToTarget (target: float) (tolerance: float) (value: float) =
    if tolerance <= 0.0 then
        NotScored(InvalidConfiguration "tolerance must be positive")
    else
        Score(100.0 * max 0.0 (1.0 - abs (value - target) / tolerance), 1, 0)
