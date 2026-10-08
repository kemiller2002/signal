/// Rules: flow, derived facts, validation, completion and recommendations
/// (WI-0043): ACR-001, ACR-007, CAN-002, VER-006.
module Echelon.Signal.Tests.RulesTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Rules

let private ord id : Question =
    { Id = id
      Prompt = $"How often does {id} hold?"
      HelpText = None
      Answer = Ordinal 5
      Selector = { Preset = Frequency5; Labels = [ "0"; "1"; "2"; "3"; "4" ] }
      SpecialStates = [ DontKnow; NotApplicable ]
      Required = true
      Tags = [] }

let private yesNo id : Question =
    { ord id with
        Answer = Boolean
        Selector = { Preset = YesNo; Labels = [ "No"; "Yes" ] }
        SpecialStates = [] }

let private mean: Scoring.Scorer =
    { Scale = Scoring.Direct
      Aggregate = Scoring.Mean
      Transforms = []
      Missing = { MinimumObservations = 1; Special = Scoring.Exclude }
      Decimals = 2 }

let private section id (qs: Question list) scored : Section =
    { Id = id
      Title = id
      Description = None
      Required = true
      Questions = qs
      Presentation = defaultSectionPresentation
      Scoring = if scored then Some { Scorer = mean; Questions = [] } else None }

let private secScore = SectionScore "sec"

let private rules =
    { noRules with
        Facts =
            [ { Id = "weak"; Expr = BooleanFact(Compare(secScore, Less, Constant 2.0)) }
              { Id = "band"
                Expr =
                  CategoryFact(
                      [ Compare(secScore, GreaterOrEqual, Constant 3.0), "strong"; Compare(secScore, GreaterOrEqual, Constant 2.0), "fair" ],
                      Some "weak"
                  ) } ]
        Flow =
            [ { Id = "show-sec"; When = AnswerIs("q1", Flag true); Then = ShowSection "sec" }
              { Id = "stop"; When = AnswerIs("q2", Point 0); Then = Terminate("q2", "no-delivery") } ]
        Validation =
            [ { Id = "o2-needed"; Check = RequiredWhen("o2", AnswerIs("o1", Point 4)); Message = "Explain o2 when o1 is 4." }
              { Id = "contradiction"
                Check = Prohibited(All [ AnswerIs("q1", Flag false); AnswerIs("o1", Point 4) ])
                Message = "No production systems but always on call." } ]
        Recommendations =
            [ { Id = "info"
                When = Always
                Kind = Recommendation
                Priority = Informational
                Category = "general"
                Title = "Read the guide"
                Description = ""
                RelatedSection = None
                RelatedQuestion = None }
              { Id = "fix-sec"
                When = FactTrue "weak"
                Kind = Action true
                Priority = Critical
                Category = "security"
                Title = "Review security now"
                Description = ""
                RelatedSection = Some "sec"
                RelatedQuestion = None } ] }

let private content =
    { Pilot.content with
        Compatibility =
            { defaultCompatibility with
                Capabilities = [ UsesBranching; UsesConditionalSections; UsesDerivedFacts; UsesRecommendations; UsesAdvancedValidation ] }
        Sections =
            [ section "gate" [ yesNo "q1"; ord "q2" ] false
              { section "sec" [ ord "s1"; ord "s2"; ord "s3" ] true with Required = false }
              { section "ops" [ { ord "o1" with Required = false }; { ord "o2" with Required = false } ] false with Required = false } ]
        Rules = rules }

let private answers (pairs: (string * AnswerState) list) = Map.ofList pairs
let private p n = Value(Point n)

let private sectionOf (e: Evaluation) id = e.Sections |> List.find (fun (s, _) -> s = id) |> snd
let private factOf (e: Evaluation) id = e.Facts |> List.find (fun (f, _) -> f = id) |> snd

[<Fact>]
let ``a template with rules passes the static checks`` () =
    Assert.Empty(RuleChecks.check content)

[<Fact>]
let ``unknown stays unknown: nothing is decided from an unanswered question`` () =
    let e = evaluate content Map.empty
    Assert.False(e.Applicability.Sections.Contains "sec") // ShowSection is unknown, so not shown
    Assert.Equal(None, factOf e "weak")
    Assert.Equal(None, factOf e "band") // the first case is unknown
    Assert.Equal<string list>([ "info" ], e.Recommendations |> List.map _.Id)
    Assert.Equal(NotStarted, e.Completion)

[<Fact>]
let ``a shown section is scored, facts follow its score, and actions come first`` () =
    let e = evaluate content (answers [ "q1", Value(Flag true); "q2", p 3; "s1", p 1; "s2", p 1; "s3", Special DontKnow ])
    Assert.True(e.Applicability.Sections.Contains "sec")
    Assert.Equal(SectionScored(Scoring.Score(1.0, 2, 1)), sectionOf e "sec")
    Assert.Equal(Some(BoolValue true), factOf e "weak")
    Assert.Equal(Some(CategoryValue "weak"), factOf e "band")
    Assert.Equal<string list>([ "fix-sec"; "info" ], e.Recommendations |> List.map _.Id)
    Assert.Equal(ReadyToSubmit, e.Completion) // sec and ops are optional

[<Fact>]
let ``a hidden section is not applicable, never zero, and its answers are ignored`` () =
    let e = evaluate content (answers [ "q1", Value(Flag false); "q2", p 3; "s1", p 0; "s2", p 0 ])
    Assert.Equal(SectionNotApplicable, sectionOf e "sec")
    Assert.Equal<string list>([ "s1"; "s2" ], e.IgnoredAnswers)
    Assert.Equal(None, factOf e "weak")

[<Fact>]
let ``termination ends the survey and makes later content not applicable`` () =
    let e = evaluate content (answers [ "q1", Value(Flag false); "q2", p 0 ])
    Assert.Equal(Terminated "no-delivery", e.Completion)
    Assert.True(isSubmittable e.Completion)
    Assert.False(e.Applicability.Questions.Contains "o1")
    Assert.Equal(Some("stop", "no-delivery"), e.Applicability.Terminated)

[<Fact>]
let ``completion is explicit: missing required, violations and invalid answers`` () =
    Assert.Equal(InProgress 1, (evaluate content (answers [ "q1", Value(Flag false) ])).Completion)

    let requiredWhen = evaluate content (answers [ "q1", Value(Flag true); "q2", p 2; "o1", p 4 ])
    Assert.Equal<string list>([ "o2-needed" ], requiredWhen.Violations |> List.map _.RuleId)
    Assert.Equal(Invalid 1, requiredWhen.Completion)

    let prohibited = evaluate content (answers [ "q1", Value(Flag false); "q2", p 2; "o1", p 4; "o2", p 1 ])
    Assert.Equal<string list>([ "contradiction" ], prohibited.Violations |> List.map _.RuleId)

    let bad = evaluate content (answers [ "q1", Value(Flag true); "q2", p 9 ])
    Assert.Equal<AnswerProblem list>([ OutOfRange("q2", 9) ], bad.AnswerProblems)
    Assert.Equal(Invalid 1, bad.Completion)

[<Fact>]
let ``the answered percentage counts special states only when the policy says so`` () =
    let strict =
        { content with Rules = { rules with Completion = { defaultCompletion with MinimumAnsweredPercent = Some 100.0; SpecialCountsAsAnswered = false } } }

    let all = [ "q1", Value(Flag false); "q2", p 2; "o1", p 1; "o2", Special DontKnow ]
    Assert.Equal(InProgress 0, (evaluate strict (answers all)).Completion)
    let lenient = { strict with Rules = { strict.Rules with Completion = { strict.Rules.Completion with SpecialCountsAsAnswered = true } } }
    Assert.Equal(ReadyToSubmit, (evaluate lenient (answers all)).Completion)

[<Fact>]
let ``skips, show and hide: hide wins, and skipped questions are not applicable`` () =
    let flow =
        [ { Id = "skip"; When = AnswerIs("q1", Flag false); Then = SkipToQuestion("q1", "s3") }
          { Id = "show-o1"; When = Answered "q2"; Then = ShowQuestion "o1" }
          { Id = "hide-o1"; When = AnswerIs("q2", Point 4); Then = HideQuestion "o1" } ]

    let c = { content with Rules = { noRules with Flow = flow } }
    let applicable a = (evaluate c (answers a)).Applicability.Questions

    let skipped = applicable [ "q1", Value(Flag false) ]
    Assert.False(skipped.Contains "q2")
    Assert.False(skipped.Contains "s2")
    Assert.True(skipped.Contains "s3")
    Assert.True((applicable [ "q1", Value(Flag true) ]).Contains "q2")
    Assert.False((applicable [ "q1", Value(Flag true) ]).Contains "o1") // shown only once q2 is answered
    Assert.True((applicable [ "q1", Value(Flag true); "q2", p 2 ]).Contains "o1")
    Assert.False((applicable [ "q1", Value(Flag true); "q2", p 4 ]).Contains "o1")

[<Fact>]
let ``answers to questions that do not apply never change any result (1000 seeded samples)`` () =
    let random = Random 43
    let ids = questions content |> List.map (fun (_, q) -> q.Id)

    let draw () =
        ids
        |> List.choose (fun id ->
            match random.Next 7 with
            | 0 -> None
            | 1 when id <> "q1" -> Some(id, Special DontKnow)
            | n when id = "q1" -> Some(id, Value(Flag(n % 2 = 0)))
            | n -> Some(id, p (n % 5)))
        |> Map.ofList

    for _ in 1..1000 do
        let a = draw ()
        let e = evaluate content a
        let pruned = a |> Map.filter (fun id _ -> not (List.contains id e.IgnoredAnswers))
        let again = evaluate content pruned
        Assert.Equal({ e with IgnoredAnswers = [] }, { again with IgnoredAnswers = [] })

[<Fact>]
let ``without rules, evaluation scores exactly like the plain scoring preview (SDRA, 1000 samples)`` () =
    let random = Random 7
    let domain = Assessment.answerDomain |> List.toArray

    for _ in 1..1000 do
        let a =
            Pilot.assessment.Items
            |> List.choose (fun i -> if random.Next 8 = 0 then None else Some(i.Id, domain[random.Next domain.Length]))
            |> Map.ofList
            |> Pilot.answers

        let plain = scoreSections Pilot.content a |> List.map (fun (s, o) -> s.Id, SectionScored o)
        Assert.Equal<(string * SectionResult) list>(plain, (evaluate Pilot.content a).Sections)

let private codes (c: Content) = RuleChecks.check c |> List.map _.Code |> Set.ofList

[<Fact>]
let ``static checks reject cycles, later-content flow, bad references and types`` () =
    let withRules r = { content with Rules = r }

    let cyclic =
        { rules with
            Facts = [ { Id = "a"; Expr = BooleanFact(FactTrue "b") }; { Id = "b"; Expr = BooleanFact(Not(FactTrue "a")) } ] }

    Assert.Contains("RULE-CYCLE", codes (withRules cyclic))

    let later = { rules with Flow = [ { Id = "late"; When = AnswerIs("o1", Point 1); Then = ShowSection "sec" } ] }
    Assert.Contains("RULE-FLOW-ORDER", codes (withRules later))

    // A fact that reads a later section's score, used to gate an earlier one.
    let viaFact =
        { rules with Flow = [ { Id = "via"; When = FactTrue "weak"; Then = ShowQuestion "q2" } ] }

    Assert.Contains("RULE-FLOW-ORDER", codes (withRules viaFact))

    let backwards = { rules with Flow = [ { Id = "back"; When = Always; Then = SkipToQuestion("s2", "q2") } ] }
    Assert.Contains("RULE-FLOW-ORDER", codes (withRules backwards))

    let unknown = { rules with Recommendations = [ { rules.Recommendations.Head with When = AnswerIs("nope", Flag true) } ] }
    Assert.Contains("RULE-REFERENCE", codes (withRules unknown))

    let typed =
        { rules with
            Recommendations =
                [ { rules.Recommendations.Head with When = Any [ FactTrue "band"; CategoryIs("band", "excellent"); AnswerIs("q2", Point 9) ] } ] }

    Assert.Equal(3, RuleChecks.check (withRules typed) |> List.filter (fun f -> f.Code = "RULE-TYPE") |> List.length)

    let hidden = { rules with Flow = [ { Id = "gone"; When = Always; Then = HideQuestion "q2" } ] }
    Assert.Contains("RULE-UNREACHABLE", codes (withRules hidden))

    let dupes = { rules with Facts = rules.Facts @ [ rules.Facts.Head ] }
    Assert.Contains("RULE-DUPLICATE-ID", codes (withRules dupes))

    let undeclared = { content with Compatibility = defaultCompatibility }
    Assert.Contains("COMPAT-UNDECLARED-CAPABILITY", codes undeclared)

[<Fact>]
let ``rules are part of the canonical form, and templates without rules keep their hash`` () =
    let h c = TemplateCanonical.templateHash "t" "1" c
    let noRulesContent = { content with Rules = noRules }
    Assert.NotEqual<string>(h noRulesContent, h content)
    let changed = { content with Rules = { rules with Flow = [ rules.Flow.Head ] } }
    Assert.NotEqual<string>(h content, h changed)
    let text = Text.Encoding.UTF8.GetString(TemplateCanonical.bytes "t" "1" noRulesContent)
    Assert.DoesNotContain("\"rules\"", text)

[<Fact>]
let ``rule changes are diffed and change comparability`` () =
    let changed = { content with Rules = { rules with Flow = [] } }
    let changes = TemplateDiff.diff content changed
    Assert.Equal<TemplateDiff.Change list>([ TemplateDiff.FlowChanged ], changes)

    match TemplateDiff.comparability changes with
    | TemplateDiff.NotComparable _ -> ()
    | other -> failwith $"%A{other}"

    let reworded = { content with Rules = { rules with Recommendations = [ rules.Recommendations.Head ] } }

    match TemplateDiff.comparability (TemplateDiff.diff content reworded) with
    | TemplateDiff.ComparableWithCaution _ -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``fixtures assert applicability, facts and recommendations, and the template publishes`` () =
    let fixture: Drafts.Fixture =
        { Id = "weak-security"
          Name = "Production systems, weak security"
          Answers = answers [ "q1", Value(Flag true); "q2", p 3; "s1", p 1; "s2", p 1; "s3", p 1 ]
          Expect =
            [ Drafts.ExpectApplicable("s1", true)
              Drafts.ExpectFact("weak", Some(BoolValue true))
              Drafts.ExpectRecommended("fix-sec", true)
              Drafts.ExpectSectionScore("sec", Some 1.0)
              Drafts.ExpectComplete true ] }

    let draft: Drafts.Draft = { SurveyId = "ops-health"; Parent = None; Content = content; Fixtures = [ fixture ] }
    let report = Validation.validate Validation.defaultPolicy draft
    Assert.True(report.Passes, $"%A{report.Findings}")

    let wrong = { fixture with Id = "wrong"; Expect = [ Drafts.ExpectRecommended("fix-sec", false) ] }
    let failing = Validation.validate Validation.defaultPolicy { draft with Fixtures = [ fixture; wrong ] }
    Assert.Contains("FIXTURES-FAILED", failing.Blockers |> List.map _.Code)

    let published, _ =
        match Publication.publish Validation.defaultPolicy Publication.emptyCatalog Set.empty DateTimeOffset.UnixEpoch "author" draft with
        | Ok v -> v
        | Error e -> failwith $"%A{e}"

    Assert.True(Publication.verify published)
