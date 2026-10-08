/// The template codec (WI-0057): a published template stored as its canonical
/// bytes reads back unchanged, so the stored catalog keeps every template's
/// hash. A kitchen-sink template uses every case of every union the canonical
/// writers spell (answer kinds, presets, scales, aggregates, transforms,
/// conditions, flow actions, validation checks, facts, recommendations,
/// composite methods, score expressions, interpretations, item keys, display).
module Echelon.Signal.Tests.TemplateDecodeTests

open System
open System.Text
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Keyed
open Echelon.Signal.Engine.Template

let private opt id label score : ChoiceOption = { Id = id; Label = label; Score = score }
let private opts = [ opt "a" "A" (Some 1.0); opt "b" "B" None; opt "c" "C" (Some 3.5) ]
let private bounded = { Minimum = 0.0; Maximum = 10.0; Step = 0.5; Decimals = 1 }

let private answers =
    [ Boolean
      Ordinal 5
      SingleChoice opts
      MultiChoice { Options = opts; Selection = AnyCount; Exclusive = []; WhenExclusive = RejectCombination }
      MultiChoice { Options = opts; Selection = Exactly 2; Exclusive = [ "c" ]; WhenExclusive = ClearOthers }
      MultiChoice { Options = opts; Selection = AtLeast 1; Exclusive = [ "a" ]; WhenExclusive = RejectCombination }
      MultiChoice { Options = opts; Selection = AtMost 2; Exclusive = []; WhenExclusive = RejectCombination }
      MultiChoice { Options = opts; Selection = Between(1, 2); Exclusive = []; WhenExclusive = RejectCombination }
      BoundedNumber bounded
      BoundedRange bounded
      Ranking { Options = opts; Positions = None }
      Ranking { Options = opts; Positions = Some 2 }
      Allocation { Options = opts; Total = 10; Step = 1.5; ItemMinimum = 0; ItemMaximum = 8 }
      BestWorst opts
      Hierarchical([ { Option = opt "p" "P" None; Parent = None }; { Option = opt "q" "Q" (Some 2.0); Parent = Some "p" } ], true)
      HierarchicalMulti([ { Option = opt "p" "P" None; Parent = None } ], ParentImpliesDescendants, AnyCount)
      HierarchicalMulti([ { Option = opt "p" "P" None; Parent = None } ], ParentForbidsDescendants, Exactly 1)
      HierarchicalMulti([ { Option = opt "p" "P" None; Parent = None } ], ParentIndependent, AtMost 1) ]

let private presets = (TemplateDecodeCore.presets |> List.map fst) @ [ SemanticDifferential("cold", "hot") ]

let private question (i: int) answer preset : Question =
    { Id = $"q{i}"
      Prompt = $"Prompt {i} \"quoted\" é 日本"
      HelpText = (if i % 2 = 0 then Some "help" else None)
      Answer = answer
      Selector = { Preset = preset; Labels = (if i % 3 = 0 then [ "one"; "two" ] else []) }
      SpecialStates = (if i % 2 = 0 then [ DontKnow; NotObserved; NotApplicable; Declined ] else [])
      Required = i % 2 = 1
      Tags = (if i % 4 = 0 then [ "t1"; "t2" ] else []) }

let private aggregates: Scoring.Aggregate list =
    [ Scoring.RawSum; Scoring.Mean; Scoring.Median; Scoring.Minimum; Scoring.Maximum; Scoring.CountAnswered; Scoring.CountAtLeast 3.0
      Scoring.WeightedSum [ 1.0; 2.5 ]; Scoring.WeightedMean [ 0.5; 0.5 ]; Scoring.PercentageOfMaximum 4.0; Scoring.PercentageOfRange(1.0, 5.0)
      Scoring.TrimmedMean 1; Scoring.CappedSum 10.0; Scoring.TopN 2; Scoring.BottomN 1; Scoring.TopKBox(2, 5); Scoring.BottomKBox(1, 5)
      Scoring.WeightedTopKBox([ 1.0; 2.0 ], 5); Scoring.FavorableRate 4.0; Scoring.UnfavorableRate 2.0; Scoring.NetFavorable(4.0, 2.0); Scoring.NetPromoterScore ]

let private scales: Scoring.ItemScale list = [ Scoring.Direct; Scoring.Reverse(0.0, 4.0); Scoring.Mapped(Map [ 0.0, 10.0; 1.0, 20.5 ]) ]

let private transforms: Scoring.Transform list =
    [ Scoring.LinearTransform(2.0, -1.0); Scoring.LinearNormalize(0.0, 4.0, 0.0, 100.0); Scoring.Clamp(0.0, 100.0); Scoring.Floor; Scoring.Ceiling ]

let private scorer i agg : Scoring.Scorer =
    { Scale = scales[i % scales.Length]
      Aggregate = agg
      Transforms = (if i % 2 = 0 then transforms else [])
      Missing = { MinimumObservations = 1 + i % 3; Special = (if i % 2 = 0 then Scoring.Exclude else Scoring.Substitute 1.5) }
      Decimals = i % 4 }

let private values =
    [ Flag true; Point 3; Choice "a"; Choices(Set [ "a"; "b" ]); Tick 4; TickRange(1, 3); Order [ "b"; "a" ]; Allocated(Map [ "a", 2; "b", 3 ]); BestWorstPick("a", "c") ]

let private conditions =
    [ Always
      Answered "q1"
      AnswerIs("q1", Point 2)
      AnswerIn("q2", values)
      IsSpecial("q2", Declined)
      Compare(Constant 1.5, GreaterOrEqual, AnswerNumber "q1")
      Compare(SectionScore "s0", Equal, NumberFact "n")
      Compare(Constant 0.0, NotEqual, Constant 1.0)
      Compare(Constant 0.0, Greater, Constant 1.0)
      Compare(Constant 0.0, Less, Constant 1.0)
      Compare(Constant 0.0, LessOrEqual, Constant 1.0)
      FactTrue "b"
      CategoryIs("c", "high")
      All [ Always; Not(Answered "q3") ]
      Any [ FactTrue "b"; All [] ] ]

let private rules: RuleSet =
    { Facts =
        [ { Id = "b"; Expr = BooleanFact(Any conditions) }
          { Id = "n"; Expr = NumberFactOf(AnswerNumber "q1") }
          { Id = "c"; Expr = CategoryFact([ Always, "high"; Answered "q2", "low" ], Some "none") }
          { Id = "d"; Expr = CategoryFact([ Always, "x" ], None) } ]
      Flow =
        [ ShowQuestion "q1"; HideQuestion "q2"; ShowSection "s0"; HideSection "s1"; SkipToQuestion("q1", "q3"); SkipToSection("q2", "s1"); Terminate("q3", "done") ]
        |> List.mapi (fun i a -> { Id = $"f{i}"; When = conditions[i % conditions.Length]; Then = a })
      Validation =
        [ RequiredWhen("q1", Always); Prohibited(Answered "q2"); AllowedRange("q3", 0.0, 4.5); AnsweredBetween([ "q1"; "q2" ], 1, 2); DistinctAnswers [ "q1"; "q2" ] ]
        |> List.mapi (fun i c -> { Id = $"v{i}"; Check = c; Message = $"message {i}" })
      Completion = { MinimumAnsweredPercent = Some 80.0; SpecialCountsAsAnswered = false; RequiredSections = [ "s0" ]; Condition = Some(FactTrue "b") }
      Recommendations =
        [ Recommendation, Informational, Some "s0", None
          Action true, Suggested, None, Some "q1"
          Action false, Important, Some "s1", Some "q2"
          Recommendation, Critical, None, None ]
        |> List.mapi (fun i (kind, priority, section, question) ->
            { Id = $"r{i}"; When = Always; Kind = kind; Priority = priority; Category = "cat"; Title = $"Title {i}"; Description = "desc"; RelatedSection = section; RelatedQuestion = question }) }

let private expression =
    When(
        Answered "q1",
        Add [ Num 1.5; Item "q1"; ScoreExpr.Section "s0"; FactNumber "n"; Subtract(Num 3.0, Num 1.0); Multiply [ Num 2.0; Num 3.0 ]; Divide(Num 1.0, Num 2.0) ],
        Bound(Sum [ ScoreExpr.Mean [ Num 1.0 ]; Min [ Num 2.0 ]; Max [ Num 3.0 ] ], 0.0, 100.0)
    )

let private itemKeys =
    [ SingleKeyed { Correct = Set [ "a" ]; PointsCorrect = 1.0; PointsIncorrect = -0.5; PointsBlank = 0.0 }
      MultiKeyed(ExactSetMatch(Set [ "a"; "b" ], 2.0))
      MultiKeyed(AnyCorrect(Set [ "a" ], 1.0))
      MultiKeyed(AllRequired(Set [ "a" ], 1.0, IgnoreExtra))
      MultiKeyed(AllRequired(Set [ "b" ], 1.0, ExtraLosesCredit))
      MultiKeyed(NoneForbidden(Set [ "c" ], 1.0))
      MultiKeyed(PartialCredit { Correct = Set [ "a" ]; PerCorrect = 1.0; PerIncorrect = -1.0; Floor = Some 0.0; Cap = None })
      MultiKeyed(PartialCredit { Correct = Set [ "b" ]; PerCorrect = 1.0; PerIncorrect = 0.0; Floor = None; Cap = Some 3.0 })
      MultiKeyed(OptionWeighted(Map [ "a", 1.0; "b", 2.0 ]))
      MultiKeyed CountSelected
      RankKeyed("a", RankPoints)
      RankKeyed("a", BordaCount)
      RankKeyed("a", InverseRank)
      RankKeyed("a", TopKRankCredit(2, false))
      RankKeyed("a", TopKRankCredit(2, true))
      RankKeyed("a", PositionWeighted [ 3.0; 2.0; 1.0 ])
      AllocationKeyed(DirectAllocation "a")
      AllocationKeyed(NormalizedAllocation "a")
      AllocationKeyed(AllocationSharePercent "a")
      AllocationKeyed(WeightedAllocation(Map [ "a", 1.0 ]))
      AllocationKeyed(DistanceFromTargetAllocation(Map [ "a", 5.0 ]))
      BestWorstKeyed("a", BestOnly)
      BestWorstKeyed("a", WorstOnly)
      BestWorstKeyed("a", BestMinusWorstOnly)
      RangeKeyed RangeWidth
      RangeKeyed RangeMidpoint
      RangeKeyed RangeLow
      RangeKeyed RangeHigh ]
    |> List.mapi (fun i k -> { Question = $"q{i}"; Key = k })

let private results overall : Results =
    { Overall = overall
      Interpretations =
        [ { Id = "i0"; Target = OverallResult; Kind = Bands [ { Label = "low"; From = 0.0; To = 49.5 }; { Label = "high"; From = 49.5; To = 100.0 } ] }
          { Id = "i1"; Target = SectionResultOf "s0"; Kind = PassFail(60.0, HigherIsWorse) }
          { Id = "i2"; Target = OverallResult; Kind = Stages("start", [ { Label = "next"; Requires = FactTrue "b" } ]) } ]
      Display = { Overall = AfterEachResponse; Sections = AfterPageAdvance; Interpretations = AfterSectionComplete; Explanation = RespondentsToo }
      ItemKeys = itemKeys }

let private composites =
    [ BalancedMean; CompositeMethod.WeightedMean [ "s0", 2.0; "s1", 1.0 ]; WeakestLink; GeometricMean; HarmonicMean; MinimumDomain(40.0, 60.0) ]

let private overalls =
    (composites
     |> List.mapi (fun i m -> Composite { Method = m; Sections = (if i % 2 = 0 then [ "s0"; "s1" ] else []); MinimumScoredSections = 1; Direction = [ HigherIsBetter; HigherIsWorse; Neutral ][i % 3]; Decimals = 1 }))
    @ [ Custom { LanguageVersion = 1; Expression = expression; Decimals = 2 } ]

/// One template that uses every case.
let private kitchenSink (overall: OverallScoring option) : Content =
    let questions = (answers |> List.mapi (fun i a -> question i a Likert5)) @ (presets |> List.mapi (fun i p -> question (100 + i) (Ordinal 5) p))

    let sections =
        aggregates
        |> List.mapi (fun i agg ->
            { Id = $"s{i}"
              Title = $"Section {i}"
              Description = (if i % 2 = 0 then Some "about" else None)
              Required = i % 2 = 0
              Questions = questions |> List.filter (fun q -> (int (q.Id.Substring 1)) % aggregates.Length = i)
              Presentation = { ItemsPerPage = (if i % 2 = 0 then Some 3 else None); StartOnNewPage = i % 3 = 0; PageBreaksBefore = (if i % 2 = 0 then [ "q1" ] else []) }
              Scoring = (if i % 5 = 4 then None else Some { Scorer = scorer i agg; Questions = (if i % 2 = 0 then [ "q1" ] else []) }) })

    { Metadata = { Title = "Kitchen sink"; ShortTitle = Some "KS"; Description = Some "Every case"; Instructions = Some "Answer"; Tags = [ "x"; "y" ] }
      Compatibility = { SchemaVersion = 1; MinimumEngineVersion = 1; ResponseEncodingVersion = 1; Capabilities = [ UsesBranching; UsesDerivedFacts; UsesCustomScoring; UsesRanking; UsesAllocation; UsesGroupScoring; UsesConditionalSections; UsesRecommendations; UsesAdvancedValidation; UsesLocalization ] |> List.sortBy TemplateCanonical.capabilityName }
      Presentation =
        { ItemsPerPage = Some 5
          ShowQuestionNumbers = false
          Progress = SectionProgress
          AllowBackNavigation = false
          ReviewBeforeSubmit = true
          SectionStartsOnNewPage = true
          Revisit = { Questions = LockPreviousQuestionsAfterAdvance; Sections = LockPreviousSectionsAfterExit } }
      Runtime = { AllowResume = false; AllowChangesAfterCompletion = true; ShowResults = false; ResultMode = Deferred }
      Sections = sections
      Rules = rules
      Results = results overall }

let private roundTrip (surveyId: string) (version: string) (content: Content) =
    let bytes = TemplateCanonical.bytes surveyId version content

    match TemplateDecode.decode bytes with
    | Error e -> failwith $"{surveyId}: {e}"
    | Ok decoded ->
        Assert.Equal((surveyId, version), (decoded.SurveyId, decoded.Version))
        // The same bytes, so the same hash: a stored template keeps its identity.
        Assert.Equal(Encoding.UTF8.GetString bytes, Encoding.UTF8.GetString(TemplateCanonical.bytes surveyId version decoded.Content))
        Assert.Equal(TemplateCanonical.templateHash surveyId version content, TemplateCanonical.templateHash surveyId version decoded.Content)
        decoded.Content

[<Fact>]
let ``the pilot reads back unchanged`` () =
    Assert.Equal(Pilot.content, roundTrip "SDRA" "1" Pilot.content)

[<Fact>]
let ``a template using every case reads back unchanged, with every overall scoring`` () =
    for overall in None :: (overalls |> List.map Some) do
        let content = kitchenSink overall
        Assert.Equal(content, roundTrip "kitchen-sink" "7" content)

[<Fact>]
let ``other result modes, progress modes and a template without rules or results read back unchanged`` () =
    let plain = { (kitchenSink None) with Rules = noRules; Results = noResults }

    for mode, progress in [ Immediate, NoProgress; ResultMode.Hidden, QuestionProgress; External, PageProgress ] do
        let content =
            { plain with
                Runtime = { plain.Runtime with ResultMode = mode }
                Presentation = { plain.Presentation with Progress = progress; Revisit = fullRevisit } }

        Assert.Equal(content, roundTrip "plain" "1" content)

[<Fact>]
let ``what is not a canonical template is refused with a reason`` () =
    let bytes = TemplateCanonical.bytes "SDRA" "1" Pilot.content
    let text = Encoding.UTF8.GetString bytes
    let refused (t: string) = match TemplateDecode.decode (Encoding.UTF8.GetBytes t) with Error e -> e | Ok _ -> "accepted"

    Assert.Contains("not the template form", refused (text.Replace("signal-template/1", "signal-template/9")))
    Assert.Contains("not JSON", refused (text.Substring(0, text.Length / 2)))
    Assert.Contains("is not a known selector preset", refused (text.Replace("\"frequency-5\"", "\"frequency-6\"")))
    Assert.Contains("'title' is missing", refused (text.Replace("\"title\":", "\"titel\":")))
