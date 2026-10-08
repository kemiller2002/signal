/// Publication checks of answer definitions and selectors (CAN-005 §28
/// "Questions", AUT-004 §33, SCS-009..SCS-013, SCS-016): every primitive is
/// well formed, has a finite cardinality an encoding slot can hold, and is
/// presented by a selector that fits it.
module Echelon.Signal.Engine.PrimitiveChecks

open System
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Findings

/// Limits that keep cardinality, URL size and accessibility in bounds.
[<Literal>]
let MaximumMultiOptions = 24

[<Literal>]
let MaximumRankedOptions = 12

[<Literal>]
let MaximumTicks = 10000

let private block = finding AnswerCategory Blocker

let private selectionProblems (id: string) (optionCount: int) (rule: SelectionRule) =
    match rule with
    | Exactly n
    | AtLeast n
    | AtMost n when n < 0 || n > optionCount -> [ block "ANSWER-SELECTION" id $"The selection count of '{id}' does not fit its {optionCount} options." ]
    | Between(lo, hi) when lo < 0 || lo > hi || hi > optionCount ->
        [ block "ANSWER-SELECTION" id $"The selection range of '{id}' does not fit its {optionCount} options." ]
    | _ -> []

let private boundedProblems (id: string) (b: BoundedSpec) =
    let span = (b.Maximum - b.Minimum) / b.Step

    [ if b.Step <= 0.0 || b.Maximum <= b.Minimum then
          block "ANSWER-BOUNDS" id $"'{id}' needs minimum < maximum and a positive step."
      elif abs (span - Math.Round span) > 1e-9 then
          block "ANSWER-BOUNDS" id $"The step of '{id}' does not divide its range."
      elif ticks b > MaximumTicks then
          block "ANSWER-BOUNDS" id $"'{id}' has more than {MaximumTicks} ticks."
      if b.Decimals < 0 || b.Decimals > 6 then
          block "ANSWER-BOUNDS" id $"'{id}' needs 0 to 6 decimals." ]

let private treeProblems (id: string) (nodes: TreeNode list) =
    let ids = nodes |> List.map _.Option.Id |> Set.ofList
    let parentOf n = nodes |> List.tryFind (fun x -> x.Option.Id = n) |> Option.bind _.Parent

    let rec cyclic (seen: Set<string>) (n: string) =
        match parentOf n with
        | None -> false
        | Some p when seen.Contains p -> true
        | Some p -> cyclic (seen.Add p) p

    [ for n in nodes do
          match n.Parent with
          | Some p when not (ids.Contains p) -> block "ANSWER-TREE" id $"Node '{n.Option.Id}' of '{id}' has unknown parent '{p}'."
          | _ -> ()
      if nodes |> List.exists (fun n -> cyclic (Set.singleton n.Option.Id) n.Option.Id) then
          block "ANSWER-TREE" id $"The option tree of '{id}' has a cycle." ]

let private definitionProblems (q: Question) =
    let id = q.Id
    let os = options q.Answer

    [ if not os.IsEmpty then
          for d in duplicates (os |> List.map _.Id) do
              block "ANSWER-DUPLICATE-OPTION" id $"Option id '{d}' is used more than once in '{id}'."
          for o in os do
              if not (isIdentifier o.Id) then
                  block "ANSWER-OPTION-ID" id $"Option id '{o.Id}' in '{id}' is not a stable identifier."
              if String.IsNullOrWhiteSpace o.Label then
                  block "ANSWER-OPTION-LABEL" id $"Option '{o.Id}' in '{id}' has no accessible text label."
          if os |> List.exists (fun o -> o.Score |> Option.exists (fun s -> Double.IsNaN s || Double.IsInfinity s)) then
              block "ANSWER-OPTION-SCORE" id $"An option score in '{id}' is not a finite number."

      match q.Answer with
      | Ordinal points when points < 2 || points > 11 -> block "ANSWER-ORDINAL-POINTS" id $"Question '{id}' needs 2 to 11 ordinal points."
      | SingleChoice os
      | BestWorst os when os.Length < 2 -> block "ANSWER-TOO-FEW-OPTIONS" id $"Question '{id}' needs at least two options."
      | MultiChoice m ->
          if m.Options.Length < 2 || m.Options.Length > MaximumMultiOptions then
              block "ANSWER-TOO-FEW-OPTIONS" id $"Multi-choice '{id}' needs 2 to {MaximumMultiOptions} options."
          yield! selectionProblems id m.Options.Length m.Selection
          for e in m.Exclusive do
              if not (m.Options |> List.exists (fun o -> o.Id = e)) then
                  block "ANSWER-EXCLUSIVE" id $"Exclusive option '{e}' of '{id}' is not one of its options."
      | BoundedNumber b
      | BoundedRange b -> yield! boundedProblems id b
      | Ranking r ->
          if r.Options.Length < 2 || r.Options.Length > MaximumRankedOptions then
              block "ANSWER-TOO-FEW-OPTIONS" id $"Ranking '{id}' needs 2 to {MaximumRankedOptions} options."
          match r.Positions with
          | Some k when k < 1 || k > r.Options.Length -> block "ANSWER-SELECTION" id $"Ranking '{id}' ranks 1 to {r.Options.Length} positions."
          | _ -> ()
      | Allocation a ->
          let n = a.Options.Length

          if n < 2 then
              block "ANSWER-TOO-FEW-OPTIONS" id $"Allocation '{id}' needs at least two options."
          if a.Step <= 0.0 || a.ItemMinimum < 0 || a.ItemMinimum > a.ItemMaximum then
              block "ANSWER-BOUNDS" id $"Allocation '{id}' needs a positive step and 0 <= minimum <= maximum."
          elif a.Total < n * a.ItemMinimum || a.Total > n * a.ItemMaximum then
              block "ANSWER-BOUNDS" id $"Allocation '{id}' cannot reach its total within the per-item bounds."
      | Hierarchical(nodes, _) -> yield! treeProblems id nodes
      | HierarchicalMulti(nodes, _, selection) ->
          if nodes.Length > MaximumMultiOptions then
              block "ANSWER-TOO-FEW-OPTIONS" id $"Hierarchical multi-choice '{id}' allows at most {MaximumMultiOptions} nodes."
          yield! treeProblems id nodes
          yield! selectionProblems id nodes.Length selection
      | _ -> ()

      if valueCount q.Answer >= MaximumValues then
          block "ANSWER-CARDINALITY" id $"'{id}' has too many possible answers to encode." ]

let private selectorProblems (q: Question) =
    [ if not (fits q.Selector.Preset q.Answer q.SpecialStates) then
          block "ANSWER-SELECTOR-INCOMPATIBLE" q.Id $"Selector '{PrimitiveCanonical.presetName q.Selector.Preset}' does not present the answer of '{q.Id}'."
      else
          let required = labelsRequired q.Answer

          if q.Selector.Labels.Length <> required then
              if required = 0 then
                  block "ANSWER-LABELS" q.Id $"'{q.Id}' takes its labels from its options or scale, not the selector."
              else
                  block "ANSWER-LABELS" q.Id $"Question '{q.Id}' needs {required} selector labels."
          elif q.Selector.Labels |> List.exists String.IsNullOrWhiteSpace then
              block "ANSWER-LABELS" q.Id $"Every selector label of '{q.Id}' needs accessible text."
      if List.distinct q.SpecialStates <> q.SpecialStates
         || q.SpecialStates <> (specialStates |> List.filter (fun s -> List.contains s q.SpecialStates)) then
          block "ANSWER-SPECIAL-STATES" q.Id $"Special states of '{q.Id}' must be distinct and in canonical order." ]

let check (content: Content) : Finding list =
    questions content |> List.collect (fun (_, q) -> definitionProblems q @ selectorProblems q)
