/// Interpretation validation (AUT-004 §39) and the rule and result diff
/// kinds with their impact and comparability (AUT-005 §§44-48). Each check
/// and diff kind here had no test before WI-0081 item 6.
module Echelon.Signal.Tests.InterpretationDiffTests

open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.TemplateDiff

let private sdra = Pilot.content

let private balanced =
    Composite
        { Method = BalancedMean
          Sections = []
          MinimumScoredSections = 1
          Direction = HigherIsBetter
          Decimals = 1 }

let private bands: Scoring.Band list =
    [ { Label = "low"; From = 0.0; To = 2.0 }; { Label = "high"; From = 2.0; To = 4.0 } ]

let private interp id target kind = { Id = id; Target = target; Kind = kind }

let private codes content interpretations =
    Interpretation.check content interpretations |> List.map _.Code

let private withOverall = { sdra with Results = { sdra.Results with Overall = Some balanced } }

[<Fact>]
let ``well-formed interpretations of a scored section and the overall score pass`` () =
    let ok =
        [ interp "d01-band" (SectionResultOf "D01") (Bands bands)
          interp "overall-pass" OverallResult (PassFail(2.5, HigherIsBetter))
          interp "overall-stage" OverallResult (Stages("start", [ { Label = "grown"; Requires = Always } ])) ]

    Assert.Empty(Interpretation.check withOverall ok)

[<Fact>]
let ``each interpretation check reports its own code`` () =
    let one i = codes withOverall [ i ]
    let band = interp "b" (SectionResultOf "D01") (Bands bands)

    Assert.Contains("INTERPRETATION-DUPLICATE", codes withOverall [ band; band ])
    Assert.Contains("INTERPRETATION-ID", one { band with Id = "not an id!" })
    Assert.Contains("INTERPRETATION-TARGET", codes sdra [ interp "o" OverallResult (PassFail(1.0, HigherIsBetter)) ])
    Assert.Contains("INTERPRETATION-TARGET", one (interp "s" (SectionResultOf "nowhere") (Bands bands)))
    Assert.Contains("INTERPRETATION-BANDS", one (interp "e" (SectionResultOf "D01") (Bands [])))
    Assert.Contains("INTERPRETATION-BANDS", one (interp "r" (SectionResultOf "D01") (Bands [ ({ Label = "x"; From = 3.0; To = 1.0 }: Scoring.Band) ])))

    let gap: Scoring.Band list = [ { Label = "a"; From = 0.0; To = 1.0 }; { Label = "b"; From = 2.0; To = 4.0 } ]
    let overlap: Scoring.Band list = [ { Label = "a"; From = 0.0; To = 3.0 }; { Label = "b"; From = 2.0; To = 4.0 } ]
    Assert.Contains("INTERPRETATION-BANDS", one (interp "g" (SectionResultOf "D01") (Bands gap)))
    Assert.Contains("INTERPRETATION-BANDS", one (interp "v" (SectionResultOf "D01") (Bands overlap)))
    Assert.Contains("INTERPRETATION-DIRECTION", one (interp "n" OverallResult (PassFail(1.0, Neutral))))
    Assert.Contains("INTERPRETATION-STAGES", one (interp "t" OverallResult (Stages("start", []))))

    // A stage condition is checked like any rule condition.
    let unknown = interp "u" OverallResult (Stages("start", [ { Label = "x"; Requires = FactTrue "missing" } ]))
    Assert.Contains("RULE-REFERENCE", one unknown)

[<Fact>]
let ``interpretation blockers stop publication through validate`` () =
    let draft: Drafts.Draft =
        { SurveyId = "sdra-i"
          Parent = None
          Content = { withOverall with Results = { withOverall.Results with Interpretations = [ interp "n" OverallResult (PassFail(1.0, Neutral)) ] } }
          Fixtures = [] }

    let report = Validation.validate Validation.defaultPolicy draft
    Assert.Contains("INTERPRETATION-DIRECTION", report.Blockers |> List.map _.Code)

let private rules f = { sdra with Rules = f sdra.Rules }
let private results f = { sdra with Results = f sdra.Results }

/// One edit per rule and result diff kind, with the impact and
/// comparability each one has today.
let private cases: (string * Content * Change * Impact * string) list =
    [ "facts", rules (fun r -> { r with Facts = [ { Id = "f"; Expr = BooleanFact Always } ] }), FactsChanged, { Scoring = true; Encoding = false }, "not"
      "validation", rules (fun r -> { r with Validation = [ { Id = "v"; Check = AllowedRange("D01-01", 0.0, 4.0); Message = "m" } ] }), ValidationRulesChanged, { Scoring = false; Encoding = false }, "caution"
      "completion", rules (fun r -> { r with Completion = { r.Completion with MinimumAnsweredPercent = Some 50.0 } }), CompletionChanged, { Scoring = false; Encoding = false }, "caution"
      "overall", results (fun r -> { r with Overall = Some balanced }), OverallScoringChanged, { Scoring = true; Encoding = false }, "not"
      "interpretations", results (fun r -> { r with Interpretations = [ interp "b" (SectionResultOf "D01") (Bands bands) ] }), InterpretationThresholdsChanged, { Scoring = true; Encoding = false }, "not"
      "display", results (fun r -> { r with Display = { r.Display with Overall = AfterEachResponse } }), DisplayChanged, { Scoring = false; Encoding = false }, "comparable"
      "explanation", results (fun r -> { r with Display = { r.Display with Explanation = RespondentsToo } }), DisplayChanged, { Scoring = false; Encoding = false }, "comparable"
      "item keys", results (fun r -> { r with ItemKeys = [ { Question = "D01-01"; Key = RangeKeyed RangeWidth } ] }), ItemKeysChanged, { Scoring = true; Encoding = false }, "not" ]

[<Fact>]
let ``each rule and result change is diffed alone with its impact and comparability`` () =
    for name, after, change, expected, outcome in cases do
        let changes = diff sdra after
        Assert.True(([ change ] = changes), $"{name}: %A{changes}")
        Assert.True((expected = impact change), $"{name}: %A{impact change}")

        match outcome, comparability changes with
        | "not", NotComparable [ _ ] -> ()
        | "caution", ComparableWithCaution [ _ ] -> ()
        | "comparable", Comparable -> ()
        | _, other -> failwith $"{name}: expected {outcome}, got %A{other}"

[<Fact>]
let ``a not-comparable change outweighs a caution, and only its reason is given`` () =
    let after =
        { sdra with
            Rules = { sdra.Rules with Facts = [ { Id = "f"; Expr = BooleanFact Always } ]; Completion = { sdra.Rules.Completion with SpecialCountsAsAnswered = false } } }

    match comparability (diff sdra after) with
    | NotComparable reasons -> Assert.Equal<string list>([ "derived facts changed" ], reasons)
    | other -> failwith $"%A{other}"

// WI-0088 (DF-SIGNAL-2026-0019): a change to what decides an interpretation
// is not comparable; a change to its labels or order is comparable with
// caution (AUT-005 §§45, 48).

let private stage label cond: Stage = { Label = label; Requires = cond }

let private baseInterpretations =
    [ interp "d01-band" (SectionResultOf "D01") (Bands bands)
      interp "overall-pass" OverallResult (PassFail(2.5, HigherIsBetter))
      interp "overall-stage" OverallResult (Stages("start", [ stage "grown" (Answered "D01-01") ])) ]

let private withInterpretations list = { withOverall with Results = { withOverall.Results with Interpretations = list } }

let private edit id (f: Interpretation -> Interpretation) =
    baseInterpretations |> List.map (fun i -> if i.Id = id then f i else i) |> withInterpretations

let private relabelBand (b: Scoring.Band) = { b with Label = b.Label + "!" }

let private interpretationEdits: (string * Content * Change) list =
    [ "band label", edit "d01-band" (fun i -> { i with Kind = Bands(bands |> List.map relabelBand) }), InterpretationLabelsChanged
      "band bound",
      edit "d01-band" (fun i -> { i with Kind = Bands [ { bands[0] with To = 1.5 }; { bands[1] with From = 1.5 } ] }),
      InterpretationThresholdsChanged
      "pass/fail threshold", edit "overall-pass" (fun i -> { i with Kind = PassFail(3.0, HigherIsBetter) }), InterpretationThresholdsChanged
      "pass/fail direction", edit "overall-pass" (fun i -> { i with Kind = PassFail(2.5, HigherIsWorse) }), InterpretationThresholdsChanged
      "stage label", edit "overall-stage" (fun i -> { i with Kind = Stages("start", [ stage "mature" (Answered "D01-01") ]) }), InterpretationLabelsChanged
      "stage baseline label", edit "overall-stage" (fun i -> { i with Kind = Stages("begin", [ stage "grown" (Answered "D01-01") ]) }), InterpretationLabelsChanged
      "stage condition", edit "overall-stage" (fun i -> { i with Kind = Stages("start", [ stage "grown" (Answered "D01-02") ]) }), InterpretationThresholdsChanged
      "stage added", edit "overall-stage" (fun i -> { i with Kind = Stages("start", [ stage "grown" (Answered "D01-01"); stage "more" Always ]) }), InterpretationThresholdsChanged
      "target", edit "d01-band" (fun i -> { i with Target = SectionResultOf "D02" }), InterpretationThresholdsChanged
      "kind", edit "overall-pass" (fun i -> { i with Kind = Bands bands }), InterpretationThresholdsChanged
      "id", edit "overall-pass" (fun i -> { i with Id = "overall-gate" }), InterpretationThresholdsChanged
      "removed", withInterpretations baseInterpretations.Tail, InterpretationThresholdsChanged
      "reordered", withInterpretations (List.rev baseInterpretations), InterpretationLabelsChanged ]

[<Fact>]
let ``each interpretation field is classified: what decides the outcome is not comparable, labels and order are a caution`` () =
    let before = withInterpretations baseInterpretations

    for name, after, expected in interpretationEdits do
        let changes = diff before after
        Assert.True(([ expected ] = changes), $"{name}: %A{changes}")

        match expected, comparability changes, impact expected with
        | InterpretationThresholdsChanged, NotComparable [ "interpretation thresholds changed" ], { Scoring = true; Encoding = false } -> ()
        | InterpretationLabelsChanged, ComparableWithCaution [ "interpretation labels changed" ], { Scoring = false; Encoding = false } -> ()
        | _, other, i -> failwith $"{name}: %A{other} %A{i}"

    Assert.Empty(diff before before)

[<Fact>]
let ``a threshold change counts as a breaking scoring change and a label change does not`` () =
    let summary after =
        let changes = diff (withInterpretations baseInterpretations) after
        changes |> List.exists (fun c -> (impact c).Scoring)

    let _, threshold, _ = interpretationEdits |> List.find (fun (n, _, _) -> n = "pass/fail threshold")
    let _, label, _ = interpretationEdits |> List.find (fun (n, _, _) -> n = "band label")
    Assert.True(summary threshold)
    Assert.False(summary label)
