/// The built-in scoring catalog (WI-0035): ANS-003, SCS-002, SCS-003,
/// SCS-005, SCS-008, ALG-001.
module Echelon.Signal.Tests.ScoringTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Scoring

let private n (values: float list) = values |> List.map Numeric

let private scorer aggregate =
    { Scale = Direct
      Aggregate = aggregate
      Transforms = []
      Missing = { MinimumObservations = 1; Special = Exclude }
      Decimals = 2 }

let private valueOf outcome =
    match outcome with
    | Score(value, _, _) -> value
    | NotScored reason -> failwith $"not scored: {reason}"

let private run aggregate values = evaluate (scorer aggregate) (n values) |> valueOf

[<Fact>]
let ``basic aggregates`` () =
    let values = [ 4.0; 1.0; 3.0; 2.0 ]
    Assert.Equal(10.0, run RawSum values)
    Assert.Equal(2.5, run Mean values)
    Assert.Equal(2.5, run Median values) // even: mean of the two middle values
    Assert.Equal(3.0, run Median [ 5.0; 3.0; 1.0 ])
    Assert.Equal(1.0, run Minimum values)
    Assert.Equal(4.0, run Maximum values)
    Assert.Equal(4.0, run CountAnswered values)
    Assert.Equal(2.0, run (CountAtLeast 3.0) values)
    Assert.Equal(18.0, run (WeightedSum [ 2.0; 1.0; 1.0; 3.0 ]) values)
    Assert.Equal(2.57, run (WeightedMean [ 2.0; 1.0; 1.0; 3.0 ]) values)

[<Fact>]
let ``percentages and normalization`` () =
    Assert.Equal(62.5, run (PercentageOfMaximum 4.0) [ 4.0; 1.0; 3.0; 2.0 ])
    // A 1-7 scale: a mean of 4 is half way.
    Assert.Equal(50.0, run (PercentageOfRange(1.0, 7.0)) [ 4.0 ])
    let normalized = { scorer Mean with Transforms = [ LinearNormalize(1.0, 5.0, 0.0, 100.0) ] }
    Assert.Equal(75.0, evaluate normalized (n [ 4.0 ]) |> valueOf)
    let affine = { scorer Mean with Transforms = [ LinearTransform(10.0, -5.0) ] }
    Assert.Equal(20.0, evaluate affine (n [ 2.5 ]) |> valueOf)
    let bounded = { scorer RawSum with Transforms = [ Clamp(0.0, 10.0) ] }
    Assert.Equal(10.0, evaluate bounded (n [ 8.0; 8.0 ]) |> valueOf)
    Assert.Equal(2.0, evaluate { scorer Mean with Transforms = [ Floor ] } (n [ 2.0; 3.0 ]) |> valueOf)
    Assert.Equal(3.0, evaluate { scorer Mean with Transforms = [ Ceiling ] } (n [ 2.0; 3.0 ]) |> valueOf)

[<Fact>]
let ``the denominator of a percentage counts only usable items`` () =
    let observations = [ Numeric 4.0; Special NotApplicable; Numeric 2.0 ]
    // (4 + 2) / (2 * 4), not / (3 * 4): the N/A item is not a zero.
    Assert.Equal(Score(75.0, 2, 1), evaluate (scorer (PercentageOfMaximum 4.0)) observations)

[<Fact>]
let ``item scales: reverse within bounds and explicit maps`` () =
    Assert.Equal(4.0, evaluate { scorer Mean with Scale = Reverse(0.0, 4.0) } (n [ 0.0 ]) |> valueOf)
    Assert.Equal(2.0, evaluate { scorer Mean with Scale = Reverse(1.0, 5.0) } (n [ 4.0 ]) |> valueOf)
    // ProgressStateMap: No=0, InProgress=0.5, Yes=1, declared not assumed.
    let progress = Map.ofList [ 0.0, 0.0; 1.0, 0.5; 2.0, 1.0 ]
    Assert.Equal(1.5, evaluate { scorer RawSum with Scale = Mapped progress } (n [ 1.0; 2.0 ]) |> valueOf)
    // BooleanMap with explicit values: No counts 2, Yes counts 0.
    let boolean = Map.ofList [ 0.0, 2.0; 1.0, 0.0 ]
    Assert.Equal(2.0, evaluate { scorer RawSum with Scale = Mapped boolean } (n [ 0.0; 1.0 ]) |> valueOf)
    // An unmapped value or one outside the reverse bounds is a configuration error.
    Assert.True((evaluate { scorer RawSum with Scale = Mapped boolean } (n [ 3.0 ])).IsNotScored)
    Assert.True((evaluate { scorer Mean with Scale = Reverse(0.0, 4.0) } (n [ 5.0 ])).IsNotScored)

[<Fact>]
let ``special states are excluded or substituted only as declared, never zero by default`` () =
    let observations = [ Numeric 4.0; Special DontKnow; Special Unanswered; Numeric 2.0 ]
    Assert.Equal(Score(3.0, 2, 2), evaluate (scorer Mean) observations)
    let substitute = { scorer Mean with Missing = { MinimumObservations = 1; Special = Substitute 1.0 } }
    Assert.Equal(Score(2.0, 4, 0), evaluate substitute observations)
    let strict = { scorer Mean with Missing = { MinimumObservations = 3; Special = Exclude } }
    Assert.Equal(NotScored(InsufficientObservations(2, 3)), evaluate strict observations)
    Assert.Equal(NotScored(InsufficientObservations(0, 1)), evaluate (scorer Mean) [ Special NotApplicable ])
    // Counting is defined on nothing.
    Assert.Equal(Score(0.0, 0, 1), evaluate { scorer CountAnswered with Missing = { MinimumObservations = 0; Special = Exclude } } [ Special DontKnow ])

[<Fact>]
let ``robust aggregates`` () =
    let values = [ 1.0; 2.0; 3.0; 4.0; 100.0 ]
    Assert.Equal(3.0, run (TrimmedMean 1) values)
    Assert.Equal(NotScored(InvalidConfiguration "trimming 2 from each end leaves nothing of 4"), evaluate (scorer (TrimmedMean 2)) (n [ 1.0; 2.0; 3.0; 4.0 ]))
    Assert.Equal(10.0, run (CappedSum 10.0) values)
    Assert.Equal(52.0, run (TopN 2) values)
    Assert.Equal(1.5, run (BottomN 2) values)
    Assert.Equal(SingleMode 2.0, mode [ 1.0; 2.0; 2.0; 3.0 ])
    // A tie is reported as a tie, never resolved silently.
    Assert.Equal(Tied [ 1.0; 3.0 ], mode [ 3.0; 1.0; 3.0; 1.0; 2.0 ])
    Assert.Equal(NoValues, mode [])

[<Fact>]
let ``box, favorable and NPS metrics`` () =
    // 0-4 scale: top-2 box is categories 3 and 4.
    let likert = [ 4.0; 3.0; 2.0; 1.0; 0.0; 4.0; 4.0; 2.0 ]
    Assert.Equal(50.0, run (TopKBox(2, 4)) likert)
    Assert.Equal(37.5, run (TopKBox(1, 4)) likert)
    Assert.Equal(25.0, run (BottomKBox(2, 4)) likert)
    // Weighted: top category full credit, second half credit.
    Assert.Equal(43.75, run (WeightedTopKBox([ 1.0; 0.5 ], 4)) likert)
    Assert.Equal(50.0, run (FavorableRate 3.0) likert)
    Assert.Equal(25.0, run (UnfavorableRate 1.0) likert)
    // The neutral middle (2) is on neither side.
    Assert.Equal(25.0, run (NetFavorable(3.0, 1.0)) likert)
    // Standard NPS: 3 promoters, 2 passives, 5 detractors of 10.
    Assert.Equal(-20.0, run NetPromoterScore [ 10.0; 9.0; 9.0; 8.0; 7.0; 6.0; 5.0; 0.0; 3.0; 1.0 ])
    Assert.True((evaluate (scorer NetPromoterScore) (n [ 11.0 ])).IsNotScored)
    Assert.True((evaluate (scorer (TopKBox(2, 4))) (n [ 2.5 ])).IsNotScored)

[<Fact>]
let ``bands are validated for overlap and gaps and are deterministic at boundaries`` () =
    let bands =
        [ { Label = "Low"; From = 0.0; To = 40.0 }
          { Label = "Moderate"; From = 40.0; To = 70.0 }
          { Label = "High"; From = 70.0; To = 100.0 } ]

    Assert.Equal(Ok "Low", band bands 39.9)
    Assert.Equal(Ok "Moderate", band bands 40.0)
    Assert.Equal(Ok "High", band bands 100.0)
    Assert.True(Result.isError (band bands 100.1))
    Assert.Equal<string list>([ "bands 'A' and 'B' overlap" ], validateBands [ { Label = "A"; From = 0.0; To = 50.0 }; { Label = "B"; From = 40.0; To = 100.0 } ])
    Assert.Equal<string list>([ "there is a gap between 'A' and 'B'" ], validateBands [ { Label = "A"; From = 0.0; To = 40.0 }; { Label = "B"; From = 50.0; To = 100.0 } ])
    Assert.True(passes 50.0 50.0)
    Assert.False(passes 50.0 49.99)

[<Fact>]
let ``differences, ratios and targets never produce NaN or infinity`` () =
    Assert.Equal(-3.0, difference 2.0 5.0)
    Assert.Equal(Score(2.5, 2, 0), ratio 5.0 2.0)
    Assert.Equal(NotScored(Undefined "the denominator is zero"), ratio 5.0 0.0)
    Assert.Equal(3.0, distanceFromTarget Absolute 5.0 2.0)
    Assert.Equal(-3.0, distanceFromTarget Signed 5.0 2.0)
    Assert.Equal(Score(70.0, 1, 0), proximityToTarget 50.0 10.0 47.0)
    Assert.Equal(Score(0.0, 1, 0), proximityToTarget 50.0 10.0 90.0)
    // A zero-range normalization is a configuration error, not infinity.
    Assert.True((evaluate { scorer Mean with Transforms = [ LinearNormalize(1.0, 1.0, 0.0, 100.0) ] } (n [ 1.0 ])).IsNotScored)

[<Fact>]
let ``invalid configurations are found before scoring`` () =
    Assert.NotEmpty(validate (scorer (TopKBox(6, 4))))
    Assert.NotEmpty(validate (scorer (WeightedTopKBox([ 1.5 ], 4))))
    Assert.NotEmpty(validate (scorer (NetFavorable(1.0, 3.0))))
    Assert.NotEmpty(validate { scorer Mean with Scale = Reverse(4.0, 0.0) })
    Assert.Empty(validate (Assessment.dimensionScorer Pilot.assessment))
    Assert.Equal(NotScored(InvalidConfiguration "4 weights for 2 items"), evaluate (scorer (WeightedMean [ 1.0; 1.0; 1.0; 1.0 ])) (n [ 1.0; 2.0 ]))

/// The pilot's dimension score, as the slice originally hand-computed it.
let private handComputed (minimum: int) (answers: Assessment.Answer list) =
    let values = answers |> List.choose Assessment.numericValue

    if values.Length < minimum then
        None
    else
        Some(Math.Round(float (List.sum values) / float values.Length / 4.0 * 100.0, 1, MidpointRounding.AwayFromZero))

[<Fact>]
let ``the catalog scorer equals the hand-written pilot scoring on every possible dimension`` () =
    // Differential test over all 9^5 answer combinations of a five-item
    // dimension (eight answers plus unanswered).
    let states = None :: (Assessment.answerDomain |> List.map Some)
    let pilot = Pilot.assessment
    let dimension = pilot.Dimensions.Head
    let items = Assessment.itemsOf pilot dimension
    let mutable checkedCount = 0

    for a in states do
        for b in states do
            for c in states do
                for d in states do
                    for e in states do
                        let chosen = [ a; b; c; d; e ]
                        let answers = List.zip items chosen |> List.choose (fun (item, answer) -> answer |> Option.map (fun x -> item.Id, x)) |> Map.ofList
                        let expected = handComputed pilot.MinimumNumericAnswers (chosen |> List.choose id)

                        match Assessment.scoreDimension pilot answers dimension, expected with
                        | Assessment.Scored(score, _, _), Some value -> Assert.Equal(value, score)
                        | Assessment.Unscored _, None -> ()
                        | actual, _ -> failwith $"{chosen}: {actual} vs {expected}"

                        checkedCount <- checkedCount + 1

    Assert.Equal(59049, checkedCount)
