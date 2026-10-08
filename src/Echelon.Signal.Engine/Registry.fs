/// Named, versioned scorer identities and authoring presets (AST-003,
/// SCS-002, SCS-005; DF-SIGNAL-2026-0002 Q2, Q3, Q9).
///
/// Every catalog scorer has a stable name and a semantics version. A result
/// records the identities that produced it, so it reproduces exactly; a
/// change to what a name means is a new version, never an edit of version 1.
/// Presets are recipes that compile to ordinary catalog scorers, so they add
/// no semantics of their own.
module Echelon.Signal.Engine.Registry

open Echelon.Signal.Engine.Scoring

type ScorerId = { Name: string; Version: int }

let text (id: ScorerId) = $"{id.Name}:v{id.Version}"

let private aggregateName =
    function
    | RawSum -> "raw-sum"
    | Mean -> "mean"
    | Median -> "median"
    | Minimum -> "minimum"
    | Maximum -> "maximum"
    | CountAnswered -> "count-answered"
    | CountAtLeast _ -> "count-at-least"
    | WeightedSum _ -> "weighted-sum"
    | WeightedMean _ -> "weighted-mean"
    | PercentageOfMaximum _ -> "percentage-of-maximum"
    | PercentageOfRange _ -> "percentage-of-range"
    | TrimmedMean _ -> "trimmed-mean"
    | CappedSum _ -> "capped-sum"
    | TopN _ -> "top-n"
    | BottomN _ -> "bottom-n"
    | TopKBox _ -> "top-k-box"
    | BottomKBox _ -> "bottom-k-box"
    | WeightedTopKBox _ -> "weighted-top-k-box"
    | FavorableRate _ -> "favorable-rate"
    | UnfavorableRate _ -> "unfavorable-rate"
    | NetFavorable _ -> "net-favorable"
    | NetPromoterScore -> "net-promoter-score"

/// The identity of a catalog scorer: its aggregate's name at semantics
/// version 1 (the WI-0035 catalog). Parameters are part of the template's
/// canonical form, not of the name.
let identify (scorer: Scorer) : ScorerId =
    { Name = aggregateName scorer.Aggregate
      Version = 1 }

let private build aggregate transforms decimals =
    { Scale = Direct
      Aggregate = aggregate
      Transforms = transforms
      Missing = { MinimumObservations = 1; Special = Exclude }
      Decimals = decimals }

/// SCS-005 presets for an ordinal scale with values 0..maximum.
module Presets =
    let likertSum = build RawSum [] 2
    let likertMean = build Mean [] 2
    let weightedLikert (weights: float list) = build (WeightedMean weights) [] 2
    /// Mean placed on 0-100 (the SDRA dimension recipe).
    let likertMean100 (maximum: int) = build Mean [ LinearNormalize(0.0, float maximum, 0.0, 100.0) ] 1
    let favorablePercent (atLeast: float) = build (FavorableRate atLeast) [] 1
    let topBoxPercent (maximum: int) = build (TopKBox(1, maximum)) [] 1
    let top2BoxPercent (maximum: int) = build (TopKBox(2, maximum)) [] 1
    let bottomBoxPercent (maximum: int) = build (BottomKBox(1, maximum)) [] 1
    let bottom2BoxPercent (maximum: int) = build (BottomKBox(2, maximum)) [] 1
    let netFavorable (favorableAtLeast: float) (unfavorableAtMost: float) = build (NetFavorable(favorableAtLeast, unfavorableAtMost)) [] 1
    /// Standard NPS: 0-10, promoters 9-10, detractors 0-6. Thresholds are
    /// not parameters: a changed threshold is not standard NPS (SCS-003).
    let nps = build NetPromoterScore [] 1
    /// CSAT-style: percentage of satisfied (top two of a 1-5 scale stored
    /// as 0-4).
    let csat = build (TopKBox(2, 4)) [] 1
    let effortMean = likertMean
    let confidenceMean = likertMean
    let maturityMean = likertMean

    /// BooleanMap with explicit values (never "yes = 1" by implication).
    let booleanMap (whenFalse: float) (whenTrue: float) aggregate =
        { build aggregate [] 2 with Scale = Mapped(Map [ 0.0, whenFalse; 1.0, whenTrue ]) }

    /// ProgressStateMap for a three-state ordinal stored as No = 0,
    /// In progress = 1, Yes = 2, with explicit values for each state.
    let progressStateMap (no: float) (inProgress: float) (yes: float) aggregate =
        { build aggregate [] 2 with Scale = Mapped(Map [ 0.0, no; 1.0, inProgress; 2.0, yes ]) }

    /// The recommended preset of DF-SIGNAL-2026-0002 Q3 (proposed): No 0,
    /// In progress 0.5, Yes 1. A preset, never an implied default.
    let progressStateDefault aggregate = progressStateMap 0.0 0.5 1.0 aggregate
