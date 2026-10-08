/// The analysis layer (ADM-012, ADM-013, ADM-011 remainder): measures over a
/// group's accepted results, each a calculated value that never replaces
/// the canonical SurveyResult or SurveyGroupResult.
///
/// - The source is the incremental aggregation evidence (scores per
///   section in ascending order, counts): no answer, no identity.
/// - Every measure states its prerequisites (minimum sample, scale,
///   missing-value policy, descriptive or inferential, the method). A
///   measure whose prerequisites fail is a typed `Unavailable` with the
///   reason, never a number; an anonymous group below its privacy minimum
///   has its score measures suppressed.
/// - What the stored results do not retain (per-item answers, so
///   Don't-Know and Not-Applicable rates, item-total correlation and
///   Cronbach's alpha) is `NotRetained`, never estimated.
///
/// Pure.
module Echelon.Signal.Admin.Analysis

open System
open Echelon.Signal.Engine.Import

/// Why a measure has no value.
type Unavailable =
    | InsufficientSample of required: int * found: int
    /// Withheld by the privacy policy.
    | Suppressed of minimum: int * accepted: int
    /// The stored results do not retain what it needs.
    | NotRetained of what: string
    /// The metric's semantics make it meaningless here.
    | NotMeaningful of reason: string
    | DivisionByZero
    | UnknownSection of section: string

/// A calculated value, or why there is none.
type Metric =
    | Value of float
    | Unavailable of Unavailable

/// Descriptive or inferential: the page shows which (ADM-013).
type Kind =
    | Descriptive
    | Inferential

/// What a group's analysis reads: evidence, never answers.
type Source =
    { Mode: IdentityMode
      Expected: int
      Accepted: int
      MinimumReportable: int
      /// Scores in ascending order, by section id.
      Scores: Map<string, float list>
      Unscored: Map<string, int>
      Answered: int
      NonNumeric: int }

/// The source for a group's accumulator under its configuration.
let source (config: GroupRecord.GroupConfig) (accumulator: Echelon.Signal.Engine.Incremental.Accumulator) =
    { Mode = config.Mode
      Expected = config.ExpectedCount
      Accepted = accumulator.Accepted.Count
      MinimumReportable = config.MinimumReportableCount
      Scores = accumulator.Evidence |> Map.map (fun _ e -> e.Scores)
      Unscored = accumulator.Evidence |> Map.map (fun _ e -> e.Unscored)
      Answered = accumulator.Answered
      NonNumeric = accumulator.NonNumeric }

/// The measures ADM-012 and ADM-013 name.
type Measure =
    | AcceptedCount
    | CompletionRate
    | Coverage
    | Mean
    | Median
    | Minimum
    | Maximum
    | StandardDeviation
    | Percentile of int
    | InterquartileRange
    | MedianAbsoluteDeviation
    | ConfidenceLower95
    | ConfidenceUpper95
    | CeilingShare
    | FloorShare
    | DontKnowRate
    | NotApplicableRate
    | MissingRate
    | CronbachAlpha
    | RecommendationFrequency

let measureName (measure: Measure) =
    match measure with
    | Percentile p -> $"Percentile{p}"
    | other -> $"%A{other}"

/// What a measure needs and what it is (ADM-013).
type Prerequisites =
    { MinimumSample: int
      Kind: Kind
      Scale: string
      MissingPolicy: string
      Method: string }

let prerequisites (measure: Measure) =
    let descriptive n method =
        { MinimumSample = n
          Kind = Descriptive
          Scale = "section score 0-100 (interval, as SDRA declares)"
          MissingPolicy = "unscored respondents are counted, never zero"
          Method = method }

    match measure with
    | AcceptedCount
    | CompletionRate
    | Coverage -> { descriptive 0 "count" with Scale = "count" }
    | Mean -> descriptive 1 "arithmetic mean"
    | Median -> descriptive 1 "median, even counts averaged"
    | Minimum
    | Maximum -> descriptive 1 "extreme value"
    | StandardDeviation -> descriptive 2 "sample standard deviation (n - 1)"
    | Percentile _ -> descriptive 1 "nearest rank"
    | InterquartileRange -> descriptive 4 "nearest-rank Q3 - Q1"
    | MedianAbsoluteDeviation -> descriptive 2 "median of absolute deviations from the median"
    | ConfidenceLower95
    | ConfidenceUpper95 ->
        { descriptive 5 "95% interval for the mean, Student's t" with
            Kind = Inferential }
    | CeilingShare
    | FloorShare -> descriptive 5 "share of scores at the scale's end (ceiling 100, floor 0)"
    | DontKnowRate
    | NotApplicableRate
    | MissingRate
    | CronbachAlpha -> { descriptive 0 "needs per-item answers" with Scale = "item level" }
    | RecommendationFrequency -> { descriptive 0 "needs recommendations" with Scale = "recommendation" }

/// Student's t critical values for a two-sided 95% interval, by degrees of
/// freedom 1..30; beyond 30 the normal 1.96.
let private tCritical (df: int) =
    let table =
        [| 12.706; 4.303; 3.182; 2.776; 2.571; 2.447; 2.365; 2.306; 2.262; 2.228; 2.201; 2.179; 2.160; 2.145; 2.131
           2.120; 2.110; 2.101; 2.093; 2.086; 2.080; 2.074; 2.069; 2.064; 2.060; 2.056; 2.052; 2.048; 2.045; 2.042 |]

    if df >= 1 && df <= 30 then table[df - 1] else 1.96

let private nearestRank (sorted: float list) (p: int) =
    let n = sorted.Length
    let rank = int (Math.Ceiling(float p / 100.0 * float n))
    sorted[max 0 (rank - 1)]

let private median (sorted: float list) =
    let n = sorted.Length
    if n % 2 = 1 then sorted[n / 2] else (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0

let private standardDeviation (scores: float list) =
    let mean = List.average scores
    Math.Sqrt((scores |> List.sumBy (fun s -> (s - mean) ** 2.0)) / float (scores.Length - 1))

let private round (value: float) = Math.Round(value, 6, MidpointRounding.AwayFromZero)

/// A measure over a group, for a section (score measures) or the group.
let measure (source: Source) (section: string option) (m: Measure) : Metric =
    let needs = prerequisites m

    let scores () =
        match section with
        | None -> Error(NotMeaningful "a score measure needs a section")
        | Some id ->
            match source.Scores.TryFind id with
            | None -> Error(UnknownSection id)
            | Some found when source.Mode = AnonymousGroup && source.Accepted < source.MinimumReportable ->
                Error(Suppressed(source.MinimumReportable, source.Accepted))
            | Some found when found.Length < needs.MinimumSample -> Error(InsufficientSample(needs.MinimumSample, found.Length))
            | Some found -> Ok found

    let over (f: float list -> float) =
        match scores () with
        | Ok found -> Value(round (f found))
        | Error reason -> Unavailable reason

    match m with
    | AcceptedCount -> Value(float source.Accepted)
    | CompletionRate -> if source.Expected = 0 then Unavailable DivisionByZero else Value(round (float source.Accepted / float source.Expected))
    | Coverage -> if source.Answered = 0 then Unavailable DivisionByZero else Value(round (1.0 - float source.NonNumeric / float source.Answered))
    | Mean -> over List.average
    | Median -> over median
    | Minimum -> over List.head
    | Maximum -> over List.last
    | StandardDeviation -> over standardDeviation
    | Percentile p when p < 1 || p > 100 -> Unavailable(NotMeaningful $"percentile {p} is not 1-100")
    | Percentile p -> over (fun s -> nearestRank s p)
    | InterquartileRange -> over (fun s -> nearestRank s 75 - nearestRank s 25)
    | MedianAbsoluteDeviation -> over (fun s -> let m = median s in s |> List.map (fun x -> abs (x - m)) |> List.sort |> median)
    | ConfidenceLower95 -> over (fun s -> List.average s - tCritical (s.Length - 1) * standardDeviation s / Math.Sqrt(float s.Length))
    | ConfidenceUpper95 -> over (fun s -> List.average s + tCritical (s.Length - 1) * standardDeviation s / Math.Sqrt(float s.Length))
    | CeilingShare -> over (fun s -> float (s |> List.filter (fun x -> x >= 100.0) |> List.length) / float s.Length)
    | FloorShare -> over (fun s -> float (s |> List.filter (fun x -> x <= 0.0) |> List.length) / float s.Length)
    | DontKnowRate
    | NotApplicableRate
    | MissingRate -> Unavailable(NotRetained "per-answer special states (results keep only answer counts)")
    | CronbachAlpha -> Unavailable(NotRetained "per-item answers (results keep section scores only)")
    | RecommendationFrequency -> Unavailable(NotMeaningful "this template declares no recommendations")

/// A ceiling or floor effect: at least 15% of scores at the scale's end.
let ceilingOrFloor (source: Source) (section: string) =
    match measure source (Some section) CeilingShare, measure source (Some section) FloorShare with
    | Value ceiling, _ when ceiling >= 0.15 -> Some "ceiling"
    | _, Value floor when floor >= 0.15 -> Some "floor"
    | _ -> None

/// The score distribution in `bins` equal bins over 0-100, as counts
/// (ADM-011's distribution node; computed from the incremental evidence).
let distribution (source: Source) (section: string) (bins: int) : Result<(float * float * int) list, Unavailable> =
    match source.Scores.TryFind section with
    | None -> Error(UnknownSection section)
    | Some _ when source.Mode = AnonymousGroup && source.Accepted < source.MinimumReportable -> Error(Suppressed(source.MinimumReportable, source.Accepted))
    | Some _ when bins < 1 || bins > 100 -> Error(NotMeaningful "bins must be 1-100")
    | Some scores ->
        let width = 100.0 / float bins

        Ok
            [ for i in 0 .. bins - 1 ->
                  let lower = float i * width
                  let upper = if i = bins - 1 then 100.0 else lower + width
                  lower, upper, scores |> List.filter (fun s -> s >= lower && (s < upper || (i = bins - 1 && s <= upper))) |> List.length ]

// ---- AnalysisExpression (version 1) -------------------------------------------------------

/// The expression language's version.
[<Literal>]
let ExpressionVersion = 1

/// A calculated measure: pure, deterministic, bounded; no I/O, clock,
/// randomness or code.
type Expr =
    | Const of float
    | Ref of Measure * section: string option
    | Add of Expr * Expr
    | Sub of Expr * Expr
    | Mul of Expr * Expr
    | Div of Expr * Expr
    | Smaller of Expr * Expr
    | Larger of Expr * Expr
    | Round of Expr * digits: int

/// Resource limits (ADM-046): nodes and depth.
[<Literal>]
let MaxNodes = 64

[<Literal>]
let MaxDepth = 12

let rec private size =
    function
    | Const _
    | Ref _ -> 1, 1
    | Round(e, _) -> let n, d = size e in n + 1, d + 1
    | Add(a, b)
    | Sub(a, b)
    | Mul(a, b)
    | Div(a, b)
    | Smaller(a, b)
    | Larger(a, b) ->
        let na, da = size a
        let nb, db = size b
        na + nb + 1, max da db + 1

/// Ok when the expression is within the limits.
let validate (expr: Expr) =
    let nodes, depth = size expr

    if nodes > MaxNodes then Error $"{nodes} nodes is over the {MaxNodes}-node limit"
    elif depth > MaxDepth then Error $"depth {depth} is over the {MaxDepth}-level limit"
    else Ok()

/// Evaluates a valid expression; an unavailable input makes it unavailable.
let rec evaluate (source: Source) (expr: Expr) : Metric =
    let binary a b (f: float -> float -> Metric) =
        match evaluate source a, evaluate source b with
        | Value x, Value y -> f x y
        | Unavailable reason, _
        | _, Unavailable reason -> Unavailable reason

    let checkedValue (v: float) = if Double.IsFinite v then Value v else Unavailable(NotMeaningful "not a finite number")

    match expr with
    | Const value -> checkedValue value
    | Ref(m, section) -> measure source section m
    | Add(a, b) -> binary a b (fun x y -> checkedValue (x + y))
    | Sub(a, b) -> binary a b (fun x y -> checkedValue (x - y))
    | Mul(a, b) -> binary a b (fun x y -> checkedValue (x * y))
    | Div(a, b) -> binary a b (fun x y -> if y = 0.0 then Unavailable DivisionByZero else checkedValue (x / y))
    | Smaller(a, b) -> binary a b (fun x y -> Value(min x y))
    | Larger(a, b) -> binary a b (fun x y -> Value(max x y))
    | Round(e, digits) ->
        match evaluate source e with
        | Value v when digits >= 0 && digits <= 6 -> Value(Math.Round(v, digits, MidpointRounding.AwayFromZero))
        | Value _ -> Unavailable(NotMeaningful "digits must be 0-6")
        | other -> other
