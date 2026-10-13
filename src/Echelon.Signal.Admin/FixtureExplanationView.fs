/// The draft editor's explanation of a fixture's scoring (AUT-006 §63,
/// SCS-017, AST-005; WI-0081): the fixture chosen (the first when none is),
/// and every line `ScoringPreview` gives for its answers.
///
/// Pure.
module Echelon.Signal.Admin.FixtureExplanationView

open Echelon.Signal.Engine.View

let project (editor: Authoring.Editor option) (explaining: string option) : (string * ViewValue) list =
    let fixture =
        editor
        |> Option.bind (fun e ->
            match explaining |> Option.bind (fun id -> e.Draft.Fixtures |> List.tryFind (fun f -> f.Id = id)) with
            | Some f -> Some(e, f)
            | None -> e.Draft.Fixtures |> List.tryHead |> Option.map (fun f -> e, f))

    let lines =
        fixture
        |> Option.map (fun (e, f) -> ScoringPreview.explain e.Draft.Content f.Answers)
        |> Option.defaultValue []

    [ "hasFixtureExplanation", Value(Flag fixture.IsSome)
      "explainingFixture", Value(Text(fixture |> Option.map (fun (_, f) -> f.Name) |> Option.defaultValue ""))
      "fixtureExplanation",
      Items(
          lines
          |> List.map (fun l ->
              [ "key", Text l.Key
                "kind", Text(ScoringPreview.kindName l.Kind)
                "text", Text l.Text
                "indented", Flag(l.Kind = ScoringPreview.Contribution || l.Kind = ScoringPreview.Calculation) ])
      ) ]
