/// Answer primitives, value rules and selectors (WI-0045): ANS-002,
/// SCS-009..SCS-011, SCS-013, SCS-016, SCS-018.
module Echelon.Signal.Tests.PrimitivesTests

open Xunit
open Microsoft.FSharp.Reflection
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors

let o id : ChoiceOption = { Id = id; Label = $"Option {id}"; Score = None }
let os = [ o "a"; o "b"; o "c"; o "d" ]

let multi selection exclusive policy =
    MultiChoice { Options = os; Selection = selection; Exclusive = exclusive; WhenExclusive = policy }

let bounded = { Minimum = 0.0; Maximum = 2.5; Step = 0.5; Decimals = 1 }

/// A small instance of every primitive.
let catalog: AnswerDefinition list =
    [ Boolean
      Ordinal 5
      SingleChoice os
      multi AnyCount [] RejectCombination
      BoundedNumber bounded
      BoundedRange bounded
      Ranking { Options = os; Positions = None }
      Ranking { Options = os; Positions = Some 2 }
      Allocation { Options = os |> List.take 3; Total = 4; Step = 25.0; ItemMinimum = 0; ItemMaximum = 4 }
      BestWorst os
      Hierarchical([ { Option = o "a"; Parent = None }; { Option = o "b"; Parent = Some "a" }; { Option = o "c"; Parent = Some "a" } ], false)
      HierarchicalMulti([ { Option = o "a"; Parent = None }; { Option = o "b"; Parent = Some "a" } ], ParentIndependent, AnyCount) ]

[<Fact>]
let ``every primitive's value index is a bijection over its whole range`` () =
    for def in catalog do
        let count = valueCount def
        Assert.True(count > 0UL && count < 100000UL, $"%A{def}")

        for index in 0UL .. count - 1UL do
            match ofIndex def index with
            | None -> failwith $"%A{def}: no value at {index}"
            | Some value -> Assert.Equal(Some index, toIndex def value)

        Assert.Equal(None, ofIndex def count)

[<Fact>]
let ``cardinalities are the documented counts`` () =
    let counts = catalog |> List.map valueCount
    // 2, 5, 4, 2^4, 6 ticks, 6*7/2 pairs, 4!, 4*3, 5^3, 4*3, 3 nodes, 2^2.
    Assert.Equal<uint64 list>([ 2UL; 5UL; 4UL; 16UL; 6UL; 21UL; 24UL; 12UL; 125UL; 12UL; 3UL; 4UL ], counts)

[<Fact>]
let ``values are checked against the primitive's rules`` () =
    let ok def value = Assert.Equal(None, Primitives.check def value)
    let bad def value = Assert.True((Primitives.check def value).IsSome, $"%A{value} should be refused")

    let exclusive = multi (Between(1, 2)) [ "d" ] RejectCombination
    ok exclusive (Choices(Set [ "a"; "b" ]))
    bad exclusive (Choices(Set [ "a"; "d" ])) // "none of the above" with another option
    bad exclusive (Choices Set.empty) // fewer than one
    bad exclusive (Choices(Set [ "a"; "b"; "c" ])) // more than two

    bad (BoundedRange bounded) (TickRange(3, 1)) // low > high
    ok (BoundedRange bounded) (TickRange(1, 1))
    bad (BestWorst os) (BestWorstPick("a", "a"))
    bad (Ranking { Options = os; Positions = None }) (Order [ "a"; "a"; "b"; "c" ])
    bad (Ranking { Options = os; Positions = Some 2 }) (Order [ "a"; "b"; "c" ])
    let allocation = catalog[8]
    ok allocation (Allocated(Map [ "a", 2; "b", 1; "c", 1 ]))
    bad allocation (Allocated(Map [ "a", 2; "b", 1 ])) // totals 3, not 4

    let tree = [ { Option = o "root"; Parent = None }; { Option = o "leaf"; Parent = Some "root" } ]
    bad (Hierarchical(tree, true)) (Choice "root") // terminal options only
    ok (Hierarchical(tree, true)) (Choice "leaf")
    bad (HierarchicalMulti(tree, ParentForbidsDescendants, AnyCount)) (Choices(Set [ "root"; "leaf" ]))
    ok (HierarchicalMulti(tree, ParentIndependent, AnyCount)) (Choices(Set [ "root"; "leaf" ]))

[<Fact>]
let ``exclusive options clear or are rejected by declared policy`` () =
    let spec = { Options = os; Selection = AnyCount; Exclusive = [ "d" ]; WhenExclusive = ClearOthers }
    Assert.Equal<Set<string>>(Set [ "d" ], select spec (Set [ "a"; "b" ]) "d")
    Assert.Equal<Set<string>>(Set [ "a" ], select spec (Set [ "d" ]) "a")
    Assert.Equal<Set<string>>(Set [ "b" ], select spec (Set [ "a"; "b" ]) "a") // toggling off
    let reject = { spec with WhenExclusive = RejectCombination }
    Assert.Equal<Set<string>>(Set [ "a"; "d" ], select reject (Set [ "a" ]) "d") // kept, and then refused by `check`

[<Fact>]
let ``numbers come only from primitives that have one`` () =
    Assert.Equal(Some 1.5, numeric (BoundedNumber bounded) (Tick 3))
    Assert.Equal(None, numeric (multi AnyCount [] RejectCombination) (Choices(Set [ "a" ])))
    Assert.Equal(None, numeric (Ranking { Options = os; Positions = None }) (Order [ "a"; "b"; "c"; "d" ]))

[<Fact>]
let ``declined is a semantic state of its own`` () =
    Assert.Equal<SpecialState list>([ DontKnow; NotObserved; NotApplicable; Declined ], specialStates)
    let q: Template.Question =
        { Id = "q"
          Prompt = "p"
          HelpText = None
          Answer = Ordinal 5
          Selector = { Preset = Likert5; Labels = [ "1"; "2"; "3"; "4"; "5" ] }
          SpecialStates = [ Declined ]
          Required = true
          Tags = [] }

    Assert.Equal(Scoring.Special Scoring.Declined, Template.observation q (Some(Special Declined)))

[<Fact>]
let ``every selector preset fits its primitive and maps to a component family`` () =
    let fitsAny preset =
        catalog @ [ Ordinal 3; Ordinal 4; Ordinal 6; Ordinal 7; Ordinal 10; Ordinal 11; SingleChoice(List.take 2 os); SingleChoice(List.take 3 os) ]
        |> List.exists (fun def -> fits preset def specialStates)

    let presets =
        FSharpType.GetUnionCases(typeof<SelectorPreset>)
        |> Array.map (fun case ->
            let args =
                case.GetFields()
                |> Array.map (fun f -> if f.PropertyType = typeof<string> then box "end" else box Agreement)

            FSharpValue.MakeUnion(case, args) :?> SelectorPreset)

    for preset in presets do
        Assert.True(fitsAny preset, $"%A{preset} fits no primitive")
        let family, variant = componentOf preset
        Assert.StartsWith("signal-", family)
        Assert.False(System.String.IsNullOrWhiteSpace variant)

    Assert.False(fits YesNoNA Boolean [ DontKnow ]) // needs the not-applicable state
    Assert.True(fits YesNoNA Boolean [ NotApplicable ])
    Assert.False(fits Pairwise (SingleChoice os) [])
    Assert.True(fits Pairwise (SingleChoice(List.take 2 os)) [])
