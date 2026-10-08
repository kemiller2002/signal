/// Matrices, item keys, primitive checks, forward-only navigation, content
/// banking and timers (WI-0045): SCS-004, SCS-006, SCS-012, SCS-015,
/// SCS-016, ARX-013.
module Echelon.Signal.Tests.StructureTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Tests.PrimitivesTests

let private likert = { Preset = Likert5; Labels = [ "1"; "2"; "3"; "4"; "5" ] }

let private matrix: Matrix.Spec =
    { Id = "m"
      Prompt = "How true is each statement"
      Rows =
        [ { Id = "r1"; Prompt = "We plan"; AllowNotApplicable = false }
          { Id = "r2"; Prompt = "We review"; AllowNotApplicable = true }
          { Id = "r3"; Prompt = "We ship"; AllowNotApplicable = false } ]
      Scales = [ { Id = "agree"; Label = "Agreement"; Answer = Ordinal 5; Selector = likert } ]
      SpecialStates = []
      RequireAllRows = false
      AnsweredRows = Some(2, 3)
      OneUsePerColumn = true }

let private mean: Scoring.Scorer =
    { Scale = Scoring.Direct
      Aggregate = Scoring.Mean
      Transforms = []
      Missing = { MinimumObservations = 1; Special = Scoring.Exclude }
      Decimals = 2 }

let private contentWith (qs: Question list) (rules: ValidationRule list) =
    { Pilot.content with
        Sections =
            [ { Pilot.content.Sections.Head with
                  Id = "s"
                  Questions = qs
                  Scoring = Some { Scorer = mean; Questions = [] } } ]
        Rules = { noRules with Validation = rules } }

[<Fact>]
let ``a matrix expands to accessible row questions and its constraints`` () =
    let qs, rules = Matrix.expand matrix
    Assert.Equal<string list>([ "m.r1"; "m.r2"; "m.r3" ], qs |> List.map _.Id)
    Assert.Equal("How true is each statement: We review", qs[1].Prompt)
    Assert.Equal<SpecialState list>([ NotApplicable ], qs[1].SpecialStates)
    Assert.Equal<string list>([ "m.answered-rows"; "m.one-per-column" ], rules |> List.map _.Id)

    let sideBySide = { matrix with Scales = matrix.Scales @ [ { Id = "importance"; Label = "Importance"; Answer = Ordinal 5; Selector = likert } ] }
    let qs2, _ = Matrix.expand sideBySide
    Assert.Equal(6, qs2.Length)
    Assert.Contains("m.r1.importance", qs2 |> List.map _.Id)

[<Fact>]
let ``matrix scoring is row scoring, and its constraints hold`` () =
    let qs, rules = Matrix.expand matrix
    let c = { (contentWith qs rules) with Compatibility = { defaultCompatibility with Capabilities = [ UsesAdvancedValidation ] } }
    let p n = Value(Point n)
    let e = Rules.evaluate c (Map [ "m.r1", p 4; "m.r3", p 2 ])
    Assert.Equal(Rules.SectionScored(Scoring.Score(3.0, 2, 1)), snd e.Sections.Head) // r2 unanswered: excluded, not zero
    Assert.Equal(Rules.ReadyToSubmit, e.Completion)
    Assert.Equal(Rules.InProgress 0, (Rules.evaluate c (Map [ "m.r1", p 4 ])).Completion) // fewer than two rows
    let repeated = Rules.evaluate c (Map [ "m.r1", p 4; "m.r3", p 4 ])
    Assert.Equal<string list>([ "m.one-per-column" ], repeated.Violations |> List.map _.RuleId)
    Assert.Empty(RuleChecks.check c)

[<Fact>]
let ``item keys score quizzes and preference answers inside ordinary sections`` () =
    let single = { (fst (Matrix.expand matrix)).Head with Id = "capital"; Answer = SingleChoice os; Selector = { Preset = RadioList; Labels = [] } }
    let picks = { single with Id = "primes"; Answer = multi AnyCount [] RejectCombination; Selector = { Preset = CheckboxList; Labels = [] } }

    let keys =
        [ { Question = "capital"; Key = SingleKeyed { Correct = Set [ "b" ]; PointsCorrect = 1.0; PointsIncorrect = 0.0; PointsBlank = 0.0 } }
          { Question = "primes"
            Key =
              MultiKeyed(
                  Keyed.PartialCredit
                      { Correct = Set [ "b"; "c" ]
                        PerCorrect = 0.5
                        PerIncorrect = 0.5
                        Floor = Some 0.0
                        Cap = None }
              ) } ]

    let c = { contentWith [ single; picks ] [] with Results = { noResults with ItemKeys = keys } }
    let raw = { contentWith [ single; picks ] [] with Results = noResults }
    Assert.Empty(Compatibility.check c)
    Assert.Contains("SCORING-INCOMPATIBLE", Compatibility.check raw |> List.map _.Code) // multi-choice needs a key

    let e = Rules.evaluate c (Map [ "capital", Value(Choice "b"); "primes", Value(Choices(Set [ "b"; "d" ])) ])
    // capital: 1; primes: 0.5 - 0.5 = 0; mean 0.5.
    Assert.Equal(Rules.SectionScored(Scoring.Score(0.5, 2, 0)), snd e.Sections.Head)
    // An unanswered keyed single choice gets its declared blank points (0).
    let blank = Rules.evaluate c (Map [ "primes", Value(Choices(Set [ "b"; "c" ])) ])
    Assert.Equal(Rules.SectionScored(Scoring.Score(0.5, 2, 0)), snd blank.Sections.Head)

    let misfit = { c with Results = { noResults with ItemKeys = [ { Question = "capital"; Key = RangeKeyed RangeWidth } ] } }
    Assert.Contains("SCORING-INCOMPATIBLE", Compatibility.check misfit |> List.map _.Code)
    let text = Text.Encoding.UTF8.GetString(TemplateCanonical.bytes "quiz" "1" c)
    Assert.Contains("\"itemKeys\"", text)

[<Fact>]
let ``malformed primitives are publication blockers`` () =
    let codes def =
        let q = { (fst (Matrix.expand matrix)).Head with Answer = def; Selector = { Preset = GenericOrdinal; Labels = [] } }
        PrimitiveChecks.check (contentWith [ q ] []) |> List.map _.Code |> Set.ofList

    Assert.Contains("ANSWER-SELECTION", codes (multi (Exactly 9) [] RejectCombination))
    Assert.Contains("ANSWER-EXCLUSIVE", codes (multi AnyCount [ "zz" ] RejectCombination))
    Assert.Contains("ANSWER-BOUNDS", codes (BoundedNumber { bounded with Step = 0.7 }))
    Assert.Contains("ANSWER-BOUNDS", codes (Allocation { Options = os; Total = 100; Step = 1.0; ItemMinimum = 0; ItemMaximum = 10 }))
    Assert.Contains("ANSWER-TREE", codes (Hierarchical([ { Option = o "a"; Parent = Some "b" }; { Option = o "b"; Parent = Some "a" } ], false)))
    let wide = [ for i in 1..30 -> o $"x{i}" ]
    Assert.Contains("ANSWER-CARDINALITY", codes (Allocation { Options = wide; Total = 100; Step = 1.0; ItemMinimum = 0; ItemMaximum = 100 }))
    Assert.Contains("ANSWER-SELECTOR-INCOMPATIBLE", codes (BestWorst os))

[<Fact>]
let ``forward-only navigation locks what was left behind`` () =
    let qs = [ for i in 1..4 -> { (fst (Matrix.expand matrix)).Head with Id = $"q{i}"; Required = true } ]
    let base' = contentWith qs []
    let paged = { base' with Presentation = { base'.Presentation with ItemsPerPage = Some 2 } }
    let locking = { paged with Presentation = { paged.Presentation with Revisit = { fullRevisit with Questions = LockPreviousQuestionsAfterAdvance } } }
    let all = qs |> List.map _.Id |> Set.ofList
    let pages = Navigation.pages locking all
    Assert.Equal<string list list>([ [ "q1"; "q2" ]; [ "q3"; "q4" ] ], pages)

    Assert.Equal<Set<string>>(Set [ "q1"; "q2" ], Navigation.locked locking pages 1)
    Assert.Empty(Navigation.locked paged pages 1) // full revisit locks nothing
    Assert.False(Navigation.canNavigateTo locking pages 1 0)
    Assert.True(Navigation.canNavigateTo paged pages 1 0)
    Assert.Equal(Error [ "q2" ], Navigation.canAdvance locking pages (Map [ "q1", Value(Point 1) ]) 0)
    Assert.True((Navigation.change locking pages 1 "q1" (Some(Value(Point 2))) Map.empty) |> Result.isError)
    Assert.True((Navigation.change locking pages 1 "q3" (Some(Value(Point 2))) Map.empty) |> Result.isOk)
    // Pages hold only applicable questions.
    Assert.Equal<string list list>([ [ "q1" ]; [ "q3"; "q4" ] ], Navigation.pages locking (all.Remove "q2"))
    // The revisit policy is part of the canonical form only when not default.
    Assert.NotEqual<string>(TemplateCanonical.templateHash "t" "1" paged, TemplateCanonical.templateHash "t" "1" locking)

[<Fact>]
let ``bank selection is reproducible from the seed and pinned for version 1`` () =
    let bank: Banking.Bank = { Id = "core"; Pool = [ for i in 1..10 -> $"q{i}" ]; Select = 4 }
    let first = Banking.select 42UL bank
    Assert.Equal(4, first.Length)
    Assert.Equal<string list>(first, Banking.select 42UL bank)
    Assert.Equal<string list>(first |> List.sortBy (fun id -> List.findIndex ((=) id) bank.Pool), first) // pool order
    // Golden selection: a change here is a new SelectionAlgorithmVersion.
    Assert.Equal<string list>([ "q3"; "q5"; "q9"; "q10" ], Banking.select 42UL bank)
    let distinct = [ for seed in 0UL .. 49UL -> Banking.select seed bank ] |> List.distinct
    Assert.True(distinct.Length > 10)
    Assert.Equal(6, (Banking.excluded 42UL [ bank ]).Count)
    Assert.NotEmpty(Banking.check [ { bank with Select = 11 } ])

[<Fact>]
let ``timers are a pure function of elapsed time`` () =
    let timer: Timers.Timer =
        { Id = "t"
          Scope = Timers.SectionScope "s"
          Duration = TimeSpan.FromMinutes 10.0
          Warnings = [ TimeSpan.FromMinutes 5.0; TimeSpan.FromMinutes 1.0 ]
          Expiry = Timers.LockAndAdvance
          Enforcement = Timers.Enforced }

    Assert.Equal(Timers.NotStarted, Timers.status timer None)
    Assert.Equal(Timers.Running(TimeSpan.FromMinutes 4.0, 1), Timers.status timer (Some(TimeSpan.FromMinutes 6.0)))
    let expired = Timers.status timer (Some(TimeSpan.FromMinutes 10.0))
    Assert.Equal(Timers.Expired Timers.LockAndAdvance, expired)
    Assert.False(Timers.allowsChanges timer expired)
    Assert.True(Timers.allowsChanges { timer with Enforcement = Timers.Advisory } expired)
    Assert.NotEmpty(Timers.check { timer with Warnings = [ TimeSpan.FromMinutes 20.0 ] })
