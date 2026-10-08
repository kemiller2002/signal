/// The canonical form of item keys (answer keys and preference scoring
/// declarations, SCS-004, SCS-006): part of the immutable template semantics.
module Echelon.Signal.Engine.ItemKeyCanonical

open System.Text.Json
open Echelon.Signal.Engine.Keyed
open Echelon.Signal.Engine.ResultModel

let private ids (w: Utf8JsonWriter) (name: string) (values: Set<string>) =
    w.WriteStartArray name
    values |> Set.iter w.WriteStringValue
    w.WriteEndArray()

let private weights (w: Utf8JsonWriter) (name: string) (values: Map<string, float>) =
    w.WriteStartArray name

    for KeyValue(id, v) in values do
        w.WriteStartArray()
        w.WriteStringValue id
        w.WriteNumberValue v
        w.WriteEndArray()

    w.WriteEndArray()

let private optionalNumber (w: Utf8JsonWriter) (name: string) (v: float option) =
    v |> Option.iter (fun x -> w.WriteNumber(name, x))

let write (w: Utf8JsonWriter) (key: KeyKind) =
    w.WriteStartObject "key"

    match key with
    | SingleKeyed k ->
        w.WriteString("kind", "single")
        ids w "correct" k.Correct
        w.WriteNumber("correct", k.PointsCorrect)
        w.WriteNumber("incorrect", k.PointsIncorrect)
        w.WriteNumber("blank", k.PointsBlank)
    | MultiKeyed m ->
        match m with
        | ExactSetMatch(c, p) ->
            w.WriteString("kind", "exact-set")
            ids w "correct" c
            w.WriteNumber("points", p)
        | AnyCorrect(c, p) ->
            w.WriteString("kind", "any-correct")
            ids w "acceptable" c
            w.WriteNumber("points", p)
        | AllRequired(c, p, extra) ->
            w.WriteString("kind", "all-required")
            ids w "required" c
            w.WriteNumber("points", p)
            w.WriteString("extra", (match extra with IgnoreExtra -> "ignore" | ExtraLosesCredit -> "loses-credit"))
        | NoneForbidden(c, p) ->
            w.WriteString("kind", "none-forbidden")
            ids w "forbidden" c
            w.WriteNumber("points", p)
        | PartialCredit k ->
            w.WriteString("kind", "partial-credit")
            ids w "correct" k.Correct
            w.WriteNumber("perCorrect", k.PerCorrect)
            w.WriteNumber("perIncorrect", k.PerIncorrect)
            optionalNumber w "floor" k.Floor
            optionalNumber w "cap" k.Cap
        | OptionWeighted ws ->
            w.WriteString("kind", "option-weighted")
            weights w "weights" ws
        | CountSelected -> w.WriteString("kind", "count-selected")
    | RankKeyed(item, m) ->
        w.WriteString("kind", "rank")
        w.WriteString("item", item)

        match m with
        | RankPoints -> w.WriteString("method", "rank-points")
        | BordaCount -> w.WriteString("method", "borda")
        | InverseRank -> w.WriteString("method", "inverse-rank")
        | TopKRankCredit(k, positional) ->
            w.WriteString("method", (if positional then "top-k-positional" else "top-k"))
            w.WriteNumber("k", k)
        | PositionWeighted points ->
            w.WriteString("method", "position-weighted")
            w.WriteStartArray "points"
            points |> List.iter w.WriteNumberValue
            w.WriteEndArray()
    | AllocationKeyed m ->
        w.WriteString("kind", "allocation")

        match m with
        | DirectAllocation item ->
            w.WriteString("method", "direct")
            w.WriteString("item", item)
        | NormalizedAllocation item ->
            w.WriteString("method", "normalized")
            w.WriteString("item", item)
        | AllocationSharePercent item ->
            w.WriteString("method", "share-percent")
            w.WriteString("item", item)
        | WeightedAllocation ws ->
            w.WriteString("method", "weighted")
            weights w "weights" ws
        | DistanceFromTargetAllocation ts ->
            w.WriteString("method", "distance-from-target")
            weights w "targets" ts
    | BestWorstKeyed(item, measure) ->
        w.WriteString("kind", "best-worst")
        w.WriteString("item", item)
        w.WriteString("measure", (match measure with BestOnly -> "best" | WorstOnly -> "worst" | BestMinusWorstOnly -> "best-minus-worst"))
    | RangeKeyed measure ->
        w.WriteString("kind", "range")
        w.WriteString("measure", (match measure with RangeWidth -> "width" | RangeMidpoint -> "midpoint" | RangeLow -> "low" | RangeHigh -> "high"))

    w.WriteEndObject()
