/// Escaped defect from #28 (WI-0097): the template diff missed a
/// single-choice option relabel (no change at all, so the versions looked
/// comparable) and classified a relabel on every other option primitive
/// as AnswerChanged (not comparable). A relabel changes the text a
/// respondent answers, like a wording change: it must be detected, and
/// classified the same way, for every primitive that has options.
module Echelon.Signal.Tests.OptionRelabelTests

open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.TemplateDiff

let private opt id : ChoiceOption = { Id = id; Label = $"Label {id}"; Score = Some 1.0 }
let private opts = [ opt "a"; opt "b"; opt "c" ]
let private node id parent : TreeNode = { Option = opt id; Parent = parent }
let private tree = [ node "a" None; node "b" (Some "a"); node "c" None ]

/// Every answer primitive that has options, by name.
let private primitives: (string * AnswerDefinition) list =
    [ "single", SingleChoice opts
      "multi", MultiChoice { Options = opts; Selection = AnyCount; Exclusive = []; WhenExclusive = RejectCombination }
      "ranking", Ranking { Options = opts; Positions = None }
      "allocation", Allocation { Options = opts; Total = 10; Step = 1.0; ItemMinimum = 0; ItemMaximum = 10 }
      "best-worst", BestWorst opts
      "tree", Hierarchical(tree, false)
      "tree-multi", HierarchicalMulti(tree, ParentImpliesDescendants, AnyCount) ]

let private question answer : Question =
    { Id = "q"
      Prompt = "Pick"
      HelpText = None
      Answer = answer
      Selector = { Preset = SingleSelect; Labels = []; Accessibility = noAccessibility }
      SpecialStates = []
      Required = true
      Tags = [] }

let private contentWith answer =
    { Pilot.content with
        Sections =
            [ { Pilot.content.Sections.Head with
                  Questions = [ question answer ]
                  Scoring = None } ] }

/// The same definition with option "b" given another label, nothing else.
let private relabelled (answer: AnswerDefinition) =
    let relabel (o: ChoiceOption) = if o.Id = "b" then { o with Label = "Something else entirely" } else o
    let relabelNode (n: TreeNode) = { n with Option = relabel n.Option }

    match answer with
    | SingleChoice os -> SingleChoice(List.map relabel os)
    | MultiChoice m -> MultiChoice { m with Options = List.map relabel m.Options }
    | Ranking r -> Ranking { r with Options = List.map relabel r.Options }
    | Allocation a -> Allocation { a with Options = List.map relabel a.Options }
    | BestWorst os -> BestWorst(List.map relabel os)
    | Hierarchical(nodes, terminal) -> Hierarchical(List.map relabelNode nodes, terminal)
    | HierarchicalMulti(nodes, rule, selection) -> HierarchicalMulti(List.map relabelNode nodes, rule, selection)
    | other -> other

[<Fact>]
let ``a single-choice relabel is detected`` () =
    let before = contentWith (SingleChoice opts)
    let changes = diff before (contentWith (relabelled (SingleChoice opts)))
    Assert.NotEmpty changes

[<Fact>]
let ``a relabel on the other option primitives is not answer-changed`` () =
    for name, answer in primitives |> List.filter (fun (n, _) -> n <> "single") do
        let changes = diff (contentWith answer) (contentWith (relabelled answer))
        Assert.False(changes |> List.contains (AnswerChanged "q"), $"{name}: %A{changes}")

[<Fact>]
let ``a relabel on any option primitive is a caution, never answer-changed and never silent`` () =
    for name, answer in primitives do
        let changes = diff (contentWith answer) (contentWith (relabelled answer))
        Assert.True(not changes.IsEmpty, $"{name}: a relabel was not detected")
        Assert.False(changes |> List.exists (function AnswerChanged _ -> true | _ -> false), $"{name}: %A{changes}")

        match comparability changes with
        | ComparableWithCaution [ reason ] -> Assert.Contains("'q'", reason)
        | other -> failwith $"{name}: %A{other}"

        // A relabel never changes how stored answers are read or scored.
        for c in changes do
            Assert.Equal({ Scoring = false; Encoding = false }, impact c)

[<Fact>]
let ``every option primitive is classified the same way`` () =
    let classified =
        primitives
        |> List.map (fun (_, answer) -> diff (contentWith answer) (contentWith (relabelled answer)), comparability (diff (contentWith answer) (contentWith (relabelled answer))))
        |> List.distinct

    Assert.Equal(1, classified.Length)

/// Applies `f` to the option list of any option primitive.
let private mapOptions (f: ChoiceOption list -> ChoiceOption list) (answer: AnswerDefinition) =
    let onTree (nodes: TreeNode list) =
        let kept = f (nodes |> List.map _.Option)
        kept |> List.map (fun o -> nodes |> List.tryFind (fun n -> n.Option.Id = o.Id) |> Option.map (fun n -> { n with Option = o }) |> Option.defaultValue { Option = o; Parent = None })

    match answer with
    | SingleChoice os -> SingleChoice(f os)
    | MultiChoice m -> MultiChoice { m with Options = f m.Options }
    | Ranking r -> Ranking { r with Options = f r.Options }
    | Allocation a -> Allocation { a with Options = f a.Options }
    | BestWorst os -> BestWorst(f os)
    | Hierarchical(nodes, terminal) -> Hierarchical(onTree nodes, terminal)
    | HierarchicalMulti(nodes, rule, selection) -> HierarchicalMulti(onTree nodes, rule, selection)
    | other -> other

[<Fact>]
let ``option ids, scores and structure keep their own classification on every primitive`` () =
    for name, answer in primitives do
        let changesTo f = diff (contentWith answer) (contentWith (mapOptions f answer))
        Assert.True(([ OptionsChanged "q" ] = changesTo List.rev), $"{name} reordered: %A{changesTo List.rev}")
        Assert.True(([ OptionsChanged "q" ] = changesTo (fun os -> os @ [ opt "d" ])), $"{name} added")

        let rescored = changesTo (List.map (fun o -> if o.Id = "a" then { o with Score = Some 9.0 } else o))
        Assert.True(([ OptionScoresChanged "q" ] = rescored), $"{name} rescored: %A{rescored}")

        // Relabel and rescore together: both, never AnswerChanged.
        let both = changesTo (List.map (fun o -> if o.Id = "a" then { o with Label = "x"; Score = Some 9.0 } else o))
        Assert.True(([ OptionsRelabelled "q"; OptionScoresChanged "q" ] = both), $"{name} both: %A{both}")

    // Structure apart from the options, and another primitive, are answer changes.
    let multi = snd primitives[1]
    let stricter = match multi with MultiChoice m -> MultiChoice { m with Selection = AtMost 1 } | other -> other
    Assert.Equal<Change list>([ AnswerChanged "q" ], diff (contentWith multi) (contentWith stricter))
    Assert.Equal<Change list>([ AnswerChanged "q" ], diff (contentWith (SingleChoice opts)) (contentWith (BestWorst opts)))
    Assert.Equal<Change list>([ AnswerChanged "q" ], diff (contentWith (Ordinal 5)) (contentWith (Ordinal 7)))
