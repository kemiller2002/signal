/// Scoring AST, composites, interpretation, explainability and the canonical
/// survey result (WI-0044): AST-001..006, ALG-002..004, CAN-006, VER-005,
/// ARX-014, SCS-002/003, SCS-015; built against DF-SIGNAL-2026-0002.
module Echelon.Signal.Tests.ScoringAstTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Template

let private sdra = Pilot.content
let private p n = Value(Point n)

let private randomAnswers (random: Random) =
    let domain = Assessment.answerDomain |> List.toArray

    Pilot.assessment.Items
    |> List.choose (fun i -> if random.Next 6 = 0 then None else Some(i.Id, domain[random.Next domain.Length]))
    |> Map.ofList
    |> Pilot.answers

let private withOverall overall = { sdra with Results = { noResults with Overall = Some overall } }

let private balanced =
    Composite
        { Method = BalancedMean
          Sections = []
          MinimumScoredSections = 1
          Direction = HigherIsBetter
          Decimals = 1 }

let private overallOf content answers =
    (SurveyResult.compute "" content answers false).Overall |> Option.map _.Outcome

[<Fact>]
let ``the trace explains the outcome from the same evaluation`` () =
    let scorer: Scoring.Scorer =
        { Scale = Scoring.Reverse(0.0, 4.0)
          Aggregate = Scoring.Mean
          Transforms = [ Scoring.LinearNormalize(0.0, 4.0, 0.0, 100.0) ]
          Missing = { MinimumObservations = 1; Special = Scoring.Exclude }
          Decimals = 1 }

    let observations = [ Scoring.Numeric 4.0; Scoring.Special Scoring.DontKnow; Scoring.Numeric 1.0 ]
    let outcome, trace = Scoring.explain scorer observations
    Assert.Equal(Scoring.evaluate scorer observations, outcome)
    Assert.Equal(Scoring.Score(37.5, 2, 1), outcome)
    Assert.Equal<float option list>([ Some 0.0; None; Some 3.0 ], trace.Items |> List.map _.Scaled) // reversed; the special is excluded
    Assert.Equal(Some 1.5, trace.Aggregated)
    Assert.Equal<(Scoring.Transform * float * float) list>([ Scoring.LinearNormalize(0.0, 4.0, 0.0, 100.0), 1.5, 37.5 ], trace.Transformed)

[<Fact>]
let ``a custom expression equal to a built-in gives the same result (differential, 1000 samples)`` () =
    let custom =
        Custom
            { LanguageVersion = ExpressionLanguageVersion
              Expression = Mean [ Section "D01"; Section "D02"; Section "D03" ]
              Decimals = 1 }

    let random = Random 2026

    for _ in 1..1000 do
        let a = randomAnswers random
        let builtIn = overallOf (withOverall balanced) a
        let expressed = overallOf (withOverall custom) a

        match builtIn, expressed with
        | Some(Scoring.Score(x, _, _)), Some(Scoring.Score(y, _, _)) -> Assert.Equal(x, y)
        | Some(Scoring.NotScored _), Some(Scoring.NotScored _) -> ()
        | other -> failwith $"%A{other}"

[<Fact>]
let ``expressions keep missing explicit and undefined arithmetic out of results`` () =
    let env: Rules.Env =
        { Content = sdra
          Applicable = questions sdra |> List.map (fun (_, q) -> q.Id) |> Set.ofList
          Answers = Map [ "CORE-001", p 4; "CORE-002", p 2; "CORE-003", Special DontKnow ] }

    let eval e = Expression.evaluate env e |> fst
    Assert.Equal(Ok(Some 3.0), eval (Mean [ Item "CORE-001"; Item "CORE-002"; Item "CORE-003" ])) // the special is skipped
    Assert.Equal(Ok None, eval (Add [ Item "CORE-001"; Item "CORE-003" ])) // arithmetic on missing is missing
    Assert.Equal(Error "division by zero", eval (Divide(Item "CORE-001", Subtract(Item "CORE-002", Num 2.0))))
    Assert.Equal(Ok(Some 10.0), eval (Bound(Multiply [ Item "CORE-001"; Num 5.0 ], 0.0, 10.0)))
    Assert.Equal(Ok(Some 1.0), eval (When(AnswerIs("CORE-001", Point 4), Num 1.0, Num 0.0)))
    Assert.Equal(Ok None, eval (When(AnswerIs("CORE-009", Point 4), Num 1.0, Num 0.0))) // unknown condition

    let _, node = Expression.evaluate env (Add [ Item "CORE-001"; Item "CORE-002" ])
    Assert.Equal(Some 6.0, node.Value)
    Assert.Equal<float option list>([ Some 4.0; Some 2.0 ], node.Children |> List.map _.Value)

[<Fact>]
let ``expressions are checked: version, references, literal zero, limits`` () =
    let codes e =
        Expression.check Expression.defaultLimits sdra "overall" { LanguageVersion = 1; Expression = e; Decimals = 1 }
        |> List.map _.Code
        |> Set.ofList

    Assert.Empty(codes (Mean [ Section "D01"; Item "CORE-001" ]))
    Assert.Contains("EXPR-REFERENCE", codes (Item "nope"))
    Assert.Contains("EXPR-DIVISION-BY-ZERO", codes (Divide(Num 1.0, Num 0.0)))
    Assert.Contains("EXPR-EMPTY", codes (Sum []))
    let deep = List.fold (fun e _ -> Add [ e ]) (Num 1.0) [ 1..20 ]
    Assert.Contains("EXPR-TOO-COMPLEX", codes deep)
    Assert.Contains("RULE-REFERENCE", codes (When(Answered "nope", Num 1.0, Num 0.0)))

    let v2 = Expression.check Expression.defaultLimits sdra "overall" { LanguageVersion = 2; Expression = Num 1.0; Decimals = 1 }
    Assert.Contains("EXPR-LANGUAGE-VERSION", v2 |> List.map _.Code)

[<Fact>]
let ``composites exclude unscored sections, renormalize weights and keep weak links visible`` () =
    let sections =
        [ "a", Rules.SectionScored(Scoring.Score(80.0, 3, 0))
          "b", Rules.SectionScored(Scoring.Score(20.0, 3, 0))
          "c", Rules.SectionNotApplicable ]

    let spec m = { Method = m; Sections = []; MinimumScoredSections = 1; Direction = HigherIsBetter; Decimals = 1 }
    let value m = fst (Composite.compose (spec m) sections)

    Assert.Equal(Scoring.Score(50.0, 2, 1), value BalancedMean)
    // c's weight is renormalized away: (80*1 + 20*3) / 4.
    Assert.Equal(Scoring.Score(35.0, 2, 1), value (WeightedMean [ "a", 1.0; "b", 3.0; "c", 5.0 ]))
    Assert.Equal(Scoring.Score(20.0, 2, 1), value WeakestLink)
    Assert.Equal(Scoring.Score(40.0, 2, 1), value GeometricMean)
    Assert.Equal(Scoring.Score(32.0, 2, 1), value HarmonicMean)
    Assert.Equal(Scoring.Score(40.0, 2, 1), value (MinimumDomain(30.0, 40.0))) // the mean of 50 is capped
    let _, trace = Composite.compose (spec (MinimumDomain(30.0, 40.0))) sections
    Assert.True(trace.Capped)
    Assert.Equal<(string * string) list>([ "c", "not applicable" ], trace.Excluded)

    match fst (Composite.compose { spec BalancedMean with MinimumScoredSections = 3 } sections) with
    | Scoring.NotScored(Scoring.InsufficientObservations(2, 3)) -> ()
    | other -> failwith $"%A{other}"

    match fst (Composite.compose { spec WeakestLink with Direction = Neutral } sections) with
    | Scoring.NotScored(Scoring.Undefined _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``interpretation labels results without changing them`` () =
    let bands =
        { Id = "level"
          Target = OverallResult
          Kind =
            Bands
                [ { Label = "low"; From = 0.0; To = 50.0 }
                  { Label = "high"; From = 50.0; To = 100.0 } ] }

    let stages =
        { Id = "maturity"
          Target = OverallResult
          Kind =
            Stages(
                "initial",
                [ { Label = "repeatable"; Requires = Compare(SectionScore "D01", GreaterOrEqual, Constant 50.0) }
                  { Label = "defined"; Requires = Compare(SectionScore "D02", GreaterOrEqual, Constant 50.0) } ]
            ) }

    let pass = { Id = "pass"; Target = SectionResultOf "D03"; Kind = PassFail(75.0, HigherIsBetter) }
    let content = { sdra with Results = { noResults with Overall = Some balanced; Interpretations = [ bands; stages; pass ] } }

    let answers =
        Pilot.assessment.Items
        |> List.map (fun i -> i.Id, (if i.DimensionId = "D02" then p 0 else p 3))
        |> Map.ofList

    let result = SurveyResult.compute "" content answers false
    let labels = result.Interpretations |> List.map (fun i -> i.Id, i.Label)
    Assert.Equal<(string * string option) list>([ "level", Some "high"; "maturity", Some "repeatable"; "pass", Some "pass" ], labels)
    Assert.Equal(overallOf (withOverall balanced) answers, result.Overall |> Option.map _.Outcome)
    // Unknown stays unknown: with nothing answered no stage can be decided.
    Assert.Equal(None, (SurveyResult.compute "" content Map.empty false).Interpretations[1].Label)

[<Fact>]
let ``the survey result carries coverage, status and reproducible lineage`` () =
    let answers = Pilot.assessment.Items |> List.mapi (fun i item -> item.Id, (if i < 2 then Special DontKnow else p 2)) |> Map.ofList
    let live = SurveyResult.compute "sha256:abc" (withOverall balanced) answers false
    Assert.Equal(SurveyResult.Provisional, live.Status)
    Assert.Equal({ SurveyResult.Applicable = 15; SurveyResult.Answered = 13; SurveyResult.Special = 2; SurveyResult.Unanswered = 0 }, live.Coverage)
    Assert.Equal<string list>([ "mean:v1"; "mean:v1"; "mean:v1" ], live.Lineage.Scorers |> List.map _.Scorer)
    Assert.Equal("sha256:abc", live.Lineage.TemplateHash)
    Assert.Equal(3, live.SectionTraces.Length)
    let final = SurveyResult.compute "sha256:abc" (withOverall balanced) answers true
    Assert.Equal(SurveyResult.Final, final.Status)
    // Incomplete responses never become final, finalized or not.
    Assert.Equal(SurveyResult.Provisional, (SurveyResult.compute "" sdra (Map [ "CORE-001", p 1 ]) true).Status)

[<Fact>]
let ``the display policy filters what the respondent sees, never what is computed`` () =
    let display d = { sdra with Results = { noResults with Overall = Some balanced; Display = d } }
    let answers = Pilot.assessment.Items |> List.take 5 |> List.map (fun i -> i.Id, p 4) |> Map.ofList
    let live = { SurveyResult.PageAdvanced = false; SurveyResult.Finalized = false }

    let view d ctx =
        let content = display d
        SurveyResult.respondentView content answers ctx (SurveyResult.compute "" content answers false)

    let finalOnly = view defaultDisplay live
    Assert.Equal(None, finalOnly.Overall)
    Assert.Empty(finalOnly.Sections)

    let each = view { defaultDisplay with Overall = AfterEachResponse; Sections = AfterEachResponse } live
    Assert.True(each.Overall.IsSome)
    Assert.Equal(3, each.Sections.Length)

    // Only the completed section (D01) shows under AfterSectionComplete.
    let bySection = view { defaultDisplay with Sections = AfterSectionComplete } live
    Assert.Equal<string list>([ "D01" ], bySection.Sections |> List.map fst)

    let hidden = view { defaultDisplay with Overall = DisplayPolicy.Hidden } { live with Finalized = true }
    Assert.Equal(None, hidden.Overall)
    Assert.False(hidden.ShowExplanation)

[<Fact>]
let ``selector-scorer incompatibilities block publication with a stable code`` () =
    let rescore scorer = { sdra with Sections = sdra.Sections |> List.map (fun s -> { s with Scoring = Some { Scorer = scorer; Questions = [] } }) }
    let codes c = Compatibility.check c |> List.map _.Code |> Set.ofList
    Assert.Empty(codes sdra)
    Assert.Contains("SCORING-INCOMPATIBLE", codes (rescore Registry.Presets.nps)) // NPS needs a 0-10 rating
    Assert.Empty(codes (rescore (Registry.Presets.top2BoxPercent 4)))
    Assert.Contains("SCORING-INCOMPATIBLE", codes (rescore (Registry.Presets.top2BoxPercent 6)))
    Assert.Contains("SCORING-INCOMPATIBLE", codes (rescore { Registry.Presets.likertMean with Scale = Scoring.Reverse(1.0, 5.0) }))
    Assert.Contains("SCORING-INCOMPATIBLE", codes (rescore { Registry.Presets.likertMean with Aggregate = Scoring.PercentageOfMaximum 3.0 }))

[<Fact>]
let ``presets compile to catalog scorers with versioned identities`` () =
    Assert.Equal("top-k-box:v1", Registry.text (Registry.identify (Registry.Presets.top2BoxPercent 4)))
    Assert.Equal("net-promoter-score:v1", Registry.text (Registry.identify Registry.Presets.nps))
    // The Q3 preset is explicit data: No 0, In progress 0.5, Yes 1.
    let progress = Registry.Presets.progressStateDefault Scoring.Mean
    Assert.Equal(Scoring.Score(0.5, 3, 0), Scoring.evaluate progress [ Scoring.Numeric 0.0; Scoring.Numeric 1.0; Scoring.Numeric 2.0 ])
    let yesNo = Registry.Presets.booleanMap -1.0 2.0 Scoring.RawSum
    Assert.Equal(Scoring.Score(1.0, 2, 0), Scoring.evaluate yesNo [ Scoring.Numeric 0.0; Scoring.Numeric 1.0 ])

[<Fact>]
let ``custom expressions follow the authoring policy, results enter the hash, rule-free templates keep theirs`` () =
    let custom = Custom { LanguageVersion = 1; Expression = Mean [ Section "D01"; Section "D02" ]; Decimals = 1 }

    let draft: Drafts.Draft =
        { SurveyId = "sdra-x"
          Parent = None
          Content = { (withOverall custom) with Compatibility = { defaultCompatibility with Capabilities = [ UsesCustomScoring ] } }
          Fixtures = [ { Id = "empty"; Name = "Nothing answered"; Answers = Map.empty; Expect = [ Drafts.ExpectOverall None ] } ] }

    let report policy = Validation.validate policy draft
    Assert.True((report Validation.defaultPolicy).Passes, $"%A{(report Validation.defaultPolicy).Findings}")
    let external = { Validation.defaultPolicy with CustomExpressionsAllowed = false }
    Assert.Contains("POLICY-CUSTOM-EXPRESSION", (report external).Blockers |> List.map _.Code)
    let undeclared = { draft with Content = withOverall custom }
    Assert.Contains("COMPAT-UNDECLARED-CAPABILITY", (Validation.validate Validation.defaultPolicy undeclared).Blockers |> List.map _.Code)

    let h c = TemplateCanonical.templateHash "SDRA" "1" c
    Assert.Equal("sha256:d9649e8a9b3d9616157d4d7a5ef1f6482bd4eedb5742b385243a46ea9aeebccd", h sdra)
    Assert.NotEqual<string>(h sdra, h (withOverall balanced))
    Assert.NotEqual<string>(h (withOverall balanced), h (withOverall custom))

    let changes = TemplateDiff.diff (withOverall balanced) (withOverall custom)
    Assert.Equal<TemplateDiff.Change list>([ TemplateDiff.OverallScoringChanged ], changes)
