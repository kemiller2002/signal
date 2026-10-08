/// The closed-ended answer primitives (SCS-009, SCS-011, SCS-013, SCS-016,
/// ANS-002, ANS-004, CAN-001 §6): what a respondent can store, independent of
/// how it is presented or scored.
///
/// Every primitive has a finite, publication-time cardinality and a
/// bijection between its valid values and the integers 0..count-1 (`toIndex`
/// and `ofIndex`), which is what the URL encoding packs. Values are checked
/// against their definition (`check`); an invalid combination is refused,
/// never repaired silently.
module Echelon.Signal.Engine.Primitives

open System
open Echelon.Signal.Engine.Responses

type ChoiceOption =
    { Id: string
      Label: string
      /// The number this option contributes to scoring. A scored question
      /// whose option has no score is a publication blocker ("mapped scores
      /// complete"); an unscored question may leave it empty.
      Score: float option }

/// How many options a multi-choice answer may select (SCS-011).
type SelectionRule =
    | AnyCount
    | Exactly of int
    | AtLeast of int
    | AtMost of int
    | Between of minimum: int * maximum: int

/// What selecting an exclusive option ("None of the above", "Not
/// applicable") does to other selections.
type ExclusivePolicy =
    /// A selection holding an exclusive option and anything else is invalid.
    | RejectCombination
    /// Choosing an exclusive option clears the others, and choosing another
    /// option clears the exclusive one (`select`).
    | ClearOthers

type MultiChoiceSpec =
    { Options: ChoiceOption list
      Selection: SelectionRule
      /// Options that may only be selected alone.
      Exclusive: string list
      WhenExclusive: ExclusivePolicy }

/// A finite quantized number scale (BoundedInteger, BoundedDecimal, SCS-009).
type BoundedSpec =
    { Minimum: float
      Maximum: float
      Step: float
      /// Decimal places of a tick's value.
      Decimals: int }

type RankingSpec =
    { Options: ChoiceOption list
      /// Positions to fill; None ranks every option (a full permutation).
      Positions: int option }

/// A constant-sum allocation, in steps (SCS-013).
type AllocationSpec =
    { Options: ChoiceOption list
      /// Total steps that must be allocated.
      Total: int
      /// The value of one step.
      Step: float
      ItemMinimum: int
      ItemMaximum: int }

type TreeNode =
    { Option: ChoiceOption
      Parent: string option }

/// What selecting a parent means in a hierarchical multi-choice (SCS-013).
type ParentRule =
    | ParentImpliesDescendants
    | ParentForbidsDescendants
    | ParentIndependent

type AnswerDefinition =
    /// Two states: false (0) and true (1).
    | Boolean
    /// `points` ordered values 0..points-1.
    | Ordinal of points: int
    /// One option, by id, from an ordered list.
    | SingleChoice of options: ChoiceOption list
    | MultiChoice of MultiChoiceSpec
    | BoundedNumber of BoundedSpec
    /// An ordered low/high pair on one bounded scale; low <= high.
    | BoundedRange of BoundedSpec
    | Ranking of RankingSpec
    | Allocation of AllocationSpec
    /// Exactly one best and one different worst option (MaxDiff).
    | BestWorst of options: ChoiceOption list
    /// One node of an immutable option tree, optionally terminal only.
    | Hierarchical of nodes: TreeNode list * terminalOnly: bool
    | HierarchicalMulti of nodes: TreeNode list * rule: ParentRule * selection: SelectionRule

let options =
    function
    | SingleChoice os
    | BestWorst os -> os
    | MultiChoice m -> m.Options
    | Ranking r -> r.Options
    | Allocation a -> a.Options
    | Hierarchical(nodes, _)
    | HierarchicalMulti(nodes, _, _) -> nodes |> List.map _.Option
    | Boolean
    | Ordinal _
    | BoundedNumber _
    | BoundedRange _ -> []

/// Ticks of a bounded scale.
let ticks (b: BoundedSpec) =
    if b.Step <= 0.0 || b.Maximum < b.Minimum then 0 else int (Math.Round((b.Maximum - b.Minimum) / b.Step)) + 1

let tickValue (b: BoundedSpec) (tick: int) =
    Math.Round(b.Minimum + float tick * b.Step, b.Decimals, MidpointRounding.AwayFromZero)

// ---------------------------------------------------------------------------
// Cardinality and the value index (SCS-016).
// ---------------------------------------------------------------------------

/// The largest value count an encoding slot may hold (62 bits).
let MaximumValues = 1UL <<< 62

let private saturate (x: float) = if x >= float MaximumValues then MaximumValues else uint64 x

let private permutations (n: int) (k: int) =
    [ 0 .. k - 1 ] |> List.fold (fun acc i -> acc * float (n - i)) 1.0

let private positionsOf (r: RankingSpec) = r.Positions |> Option.defaultValue r.Options.Length

/// How many distinct valid-shaped values a primitive has; saturates at
/// `MaximumValues` (publication refuses that).
let valueCount (def: AnswerDefinition) : uint64 =
    match def with
    | Boolean -> 2UL
    | Ordinal p -> uint64 (max 0 p)
    | SingleChoice os -> uint64 os.Length
    | Hierarchical(nodes, _) -> uint64 nodes.Length
    | MultiChoice m -> saturate (2.0 ** float m.Options.Length)
    | HierarchicalMulti(nodes, _, _) -> saturate (2.0 ** float nodes.Length)
    | BoundedNumber b -> uint64 (ticks b)
    | BoundedRange b -> let t = float (ticks b) in saturate (t * (t + 1.0) / 2.0)
    | Ranking r -> saturate (permutations r.Options.Length (positionsOf r))
    | Allocation a -> saturate (float (a.ItemMaximum - a.ItemMinimum + 1) ** float a.Options.Length)
    | BestWorst os -> uint64 (os.Length * (os.Length - 1))

let private indexOf (os: ChoiceOption list) (id: string) = os |> List.tryFindIndex (fun o -> o.Id = id)

let private mask (os: ChoiceOption list) (ids: Set<string>) =
    os |> List.indexed |> List.sumBy (fun (i, o) -> if ids.Contains o.Id then 1UL <<< i else 0UL)

let private unmask (os: ChoiceOption list) (m: uint64) =
    os |> List.indexed |> List.filter (fun (i, _) -> (m >>> i) &&& 1UL = 1UL) |> List.map (fun (_, o) -> o.Id) |> Set.ofList

/// The value's index in 0..valueCount-1, for any value of the right shape;
/// None when the value does not fit the primitive's shape at all.
let toIndex (def: AnswerDefinition) (value: AnswerValue) : uint64 option =
    match def, value with
    | Boolean, Flag b -> Some(if b then 1UL else 0UL)
    | Ordinal p, Point v when v >= 0 && v < p -> Some(uint64 v)
    | (SingleChoice _ | Hierarchical _), Choice id -> indexOf (options def) id |> Option.map uint64
    | (MultiChoice _ | HierarchicalMulti _), Choices ids when ids |> Set.forall (fun id -> (indexOf (options def) id).IsSome) ->
        Some(mask (options def) ids)
    | BoundedNumber b, Tick t when t >= 0 && t < ticks b -> Some(uint64 t)
    | BoundedRange b, TickRange(lo, hi) when lo >= 0 && lo <= hi && hi < ticks b ->
        let n = uint64 (ticks b)
        let lo, hi = uint64 lo, uint64 hi
        // Pairs (lo, hi) with lo <= hi, ordered by lo then hi.
        Some(lo * n - lo * (lo - 1UL) / 2UL + (hi - lo))
    | Ranking r, Order ids when ids.Length = positionsOf r && List.distinct ids = ids ->
        let n = r.Options.Length
        let k = ids.Length

        ids
        |> List.fold
            (fun (acc: uint64 option, remaining: string list, i) id ->
                match acc, remaining |> List.tryFindIndex ((=) id) with
                | Some total, Some d ->
                    Some(total + uint64 d * uint64 (permutations (n - 1 - i) (k - 1 - i))), List.removeAt d remaining, i + 1
                | _ -> None, remaining, i + 1)
            (Some 0UL, r.Options |> List.map _.Id, 0)
        |> fun (acc, _, _) -> acc
    | Allocation a, Allocated steps when steps |> Map.forall (fun id v -> (indexOf a.Options id).IsSome && v >= a.ItemMinimum && v <= a.ItemMaximum) ->
        let radix = uint64 (a.ItemMaximum - a.ItemMinimum + 1)

        a.Options
        |> List.fold (fun acc o -> acc * radix + uint64 ((steps.TryFind o.Id |> Option.defaultValue a.ItemMinimum) - a.ItemMinimum)) 0UL
        |> Some
    | BestWorst os, BestWorstPick(b, w) when b <> w ->
        match indexOf os b, indexOf os w with
        | Some bi, Some wi -> Some(uint64 (bi * (os.Length - 1) + (if wi < bi then wi else wi - 1)))
        | _ -> None
    | _ -> None

/// The value at an index; the inverse of `toIndex`.
let ofIndex (def: AnswerDefinition) (index: uint64) : AnswerValue option =
    if index >= valueCount def then
        None
    else
        match def with
        | Boolean -> Some(Flag(index = 1UL))
        | Ordinal _ -> Some(Point(int index))
        | SingleChoice _
        | Hierarchical _ -> let os = options def in Some(Choice os[int index].Id)
        | MultiChoice _
        | HierarchicalMulti _ -> Some(Choices(unmask (options def) index))
        | BoundedNumber _ -> Some(Tick(int index))
        | BoundedRange b ->
            let n = uint64 (ticks b)
            let start lo = lo * n - lo * (lo - 1UL) / 2UL
            let lo = [ 0UL .. n - 1UL ] |> List.findBack (fun lo -> start lo <= index)
            Some(TickRange(int lo, int (lo + index - start lo)))
        | Ranking r ->
            let n = r.Options.Length
            let k = positionsOf r

            let rec unrank (i: int) (rest: uint64) (remaining: string list) =
                if i = k then
                    []
                else
                    let block = uint64 (permutations (n - 1 - i) (k - 1 - i))
                    let d = int (rest / block)
                    remaining[d] :: unrank (i + 1) (rest % block) (List.removeAt d remaining)

            Some(Order(unrank 0 index (r.Options |> List.map _.Id)))
        | Allocation a ->
            let radix = uint64 (a.ItemMaximum - a.ItemMinimum + 1)

            let digits =
                a.Options
                |> List.rev
                |> List.fold (fun (rest: uint64, acc) o -> rest / radix, (o.Id, int (rest % radix) + a.ItemMinimum) :: acc) (index, [])
                |> snd

            Some(Allocated(Map.ofList digits))
        | BestWorst os ->
            let m = os.Length - 1
            let bi = int index / m
            let r = int index % m
            let wi = if r < bi then r else r + 1
            Some(BestWorstPick(os[bi].Id, os[wi].Id))

// ---------------------------------------------------------------------------
// Validity of a value (SCS-011, SCS-013).
// ---------------------------------------------------------------------------

let private countFits (rule: SelectionRule) (n: int) =
    match rule with
    | AnyCount -> true
    | Exactly k -> n = k
    | AtLeast k -> n >= k
    | AtMost k -> n <= k
    | Between(lo, hi) -> n >= lo && n <= hi

let private descendants (nodes: TreeNode list) (id: string) =
    let rec go (frontier: string list) (acc: Set<string>) =
        match frontier with
        | [] -> acc
        | x :: rest ->
            let children = nodes |> List.filter (fun n -> n.Parent = Some x) |> List.map _.Option.Id
            go (rest @ children) (Set.union acc (Set.ofList children))

    go [ id ] Set.empty

/// Why a value does not fit its definition, or None when it does.
let check (def: AnswerDefinition) (value: AnswerValue) : string option =
    match toIndex def value with
    | None -> Some "the value does not fit the answer's shape or range"
    | Some _ ->
        match def, value with
        | MultiChoice m, Choices ids ->
            let exclusive = Set.intersect ids (Set.ofList m.Exclusive)

            if not exclusive.IsEmpty && ids.Count > 1 then Some "an exclusive option was selected with others"
            elif not (countFits m.Selection ids.Count) then Some $"{ids.Count} selections do not satisfy {m.Selection}"
            else None
        | HierarchicalMulti(nodes, rule, selection), Choices ids ->
            let nested = ids |> Set.exists (fun id -> not (Set.intersect ids (descendants nodes id)).IsEmpty)

            if not (countFits selection ids.Count) then Some $"{ids.Count} selections do not satisfy {selection}"
            elif rule = ParentForbidsDescendants && nested then Some "a parent and its descendant were both selected"
            else None
        | Hierarchical(nodes, true), Choice id when not (descendants nodes id).IsEmpty -> Some "only terminal options may be selected"
        | Allocation a, Allocated steps when (steps |> Map.toSeq |> Seq.sumBy snd) <> a.Total ->
            Some $"the allocation must total {a.Total} steps"
        | _ -> None

/// The canonical selection after toggling an option under the exclusive
/// policy (SCS-011): what the encoded answer holds, never a transient state.
let select (m: MultiChoiceSpec) (current: Set<string>) (option: string) : Set<string> =
    let exclusive = Set.ofList m.Exclusive

    if current.Contains option then current.Remove option
    elif m.WhenExclusive = RejectCombination then current.Add option
    elif exclusive.Contains option then Set.singleton option
    else (Set.difference current exclusive).Add option

/// The number a value contributes to scoring, before the item scale; None
/// when the primitive has no single number (multi-choice, ranking,
/// allocation, best-worst: those are scored by `Keyed`).
let numeric (def: AnswerDefinition) (value: AnswerValue) : float option =
    match def, value with
    | _, Flag b -> Some(if b then 1.0 else 0.0)
    | _, Point p -> Some(float p)
    | (SingleChoice _ | Hierarchical _), Choice id -> options def |> List.tryFind (fun o -> o.Id = id) |> Option.bind _.Score
    | BoundedNumber b, Tick t -> Some(tickValue b t)
    | _ -> None
