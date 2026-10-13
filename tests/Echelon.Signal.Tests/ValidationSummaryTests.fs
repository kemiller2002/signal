/// The validation summary lists every finding category (AUT-005 §49).
/// Escaped defect from #29 (WI-0087): the summary left out the Rules
/// category, so a rule blocker never showed as a failing category.
module Echelon.Signal.Tests.ValidationSummaryTests

open Microsoft.FSharp.Reflection
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Layout
open Echelon.Signal.Engine.Drafts
open Echelon.Signal.Engine.Findings
open Echelon.Signal.Engine.Validation

let private q id : Question =
    { Id = id
      Prompt = $"How often does {id} happen?"
      HelpText = None
      Answer = Ordinal 5
      Selector = { Preset = Frequency5; Labels = [ "0"; "1"; "2"; "3"; "4" ] }
      SpecialStates = [ DontKnow ]
      Required = true
      Tags = [] }

let private sec id (qs: Question list) : Section =
    { Id = id
      Title = $"Section {id}"
      Description = None
      Required = true
      Questions = qs
      Presentation = defaultSectionPresentation
      Scoring = None }

let private fixture =
    { Id = "all-fours"
      Name = "Every answer is 4"
      Answers = Map [ for i in 1..2 -> $"q{i}", Value(Point 4) ]
      Expect = [ ExpectComplete true ] }

let private ok (result: Result<'T, 'E>) : 'T =
    match result with
    | Ok v -> v
    | Error e -> failwith $"%A{e}"

let private draft =
    newDraft "summary" "Summary"
    |> addSection (sec "s" [ q "q1"; q "q2" ])
    |> Result.bind (addFixture fixture)
    |> ok

let private cyclic =
    { noRules with
        Facts =
            [ { Id = "a"; Expr = BooleanFact(FactTrue "b") }
              { Id = "b"; Expr = BooleanFact(Not(FactTrue "a")) } ] }

[<Fact>]
let ``the summary lists every finding category exactly once`` () =
    let cases = FSharpType.GetUnionCases(typeof<Category>) |> Array.map _.Name |> Set.ofArray
    let listed = categories |> List.map (fun c -> (FSharpValue.GetUnionFields(c, typeof<Category>) |> fst).Name)
    Assert.Equal<Set<string>>(cases, Set.ofList listed)
    Assert.Equal(List.length listed, Set.count cases)

    let report = validate defaultPolicy draft
    Assert.Equal<Category list>(categories, summary report |> List.map fst)

[<Fact>]
let ``a rule blocker fails the Rules category and nothing else`` () =
    let clean = validate defaultPolicy draft
    Assert.True(clean.Passes, $"%A{clean.Findings}")

    // Declare what the rules use, so the only blocker is the cycle itself.
    let withRules = { draft.Content with Rules = cyclic }
    let declared = (RuleChecks.requiredCapabilities withRules |> Set.toList) @ withRules.Compatibility.Capabilities |> List.distinct
    let broken = { draft with Content = { withRules with Compatibility = { withRules.Compatibility with Capabilities = declared } } }
    let report = validate defaultPolicy broken
    Assert.Contains("RULE-CYCLE", report.Blockers |> List.map _.Code)

    let failing = summary report |> List.filter (snd >> not) |> List.map fst
    Assert.Equal<Category list>([ RulesCategory ], failing)
