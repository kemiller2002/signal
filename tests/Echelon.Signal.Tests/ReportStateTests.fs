/// Incremental aggregation (ADM-011) and the administrator report state and
/// its persistence (ARP-003, ARP-004), WI-0041.
module Echelon.Signal.Tests.ReportStateTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Engine.Aggregation
open Echelon.Signal.Admin

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private pilot = Pilot.assessment
let private opaque (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed * 7 + i)))).Value
let private groupId = opaque 200
let private policy = { MinimumReportableCount = 3 }

let private answers = [| Rated Never; Rated Rarely; Rated Sometimes; Rated Often; Rated AlmostAlways; Withheld DontKnow |]

/// A deterministic pseudo-random generator, so failures reproduce.
let private random (seed: int) =
    let state = ref (uint32 seed ||| 1u)

    fun (bound: int) ->
        state.Value <- state.Value ^^^ (state.Value <<< 13)
        state.Value <- state.Value ^^^ (state.Value >>> 17)
        state.Value <- state.Value ^^^ (state.Value <<< 5)
        int (state.Value % uint32 bound)

let private submission (next: int -> int) (seed: int) mode =
    let binding =
        match mode with
        | AnonymousGroup -> Anonymous(opaque seed, groupId)
        | IdentifiedGroup -> Identified(opaque seed, groupId)

    let chosen = pilot.Items |> List.map (fun item -> item.Id, answers[next answers.Length]) |> Map.ofList
    "https://signal.example" + LiveUrl.urlFor pilot "/web/" "" { Binding = binding; Answers = chosen }

let private definition mode = { Group = groupId; Mode = mode; ExpectedCount = 12; Template = pilot; Generic = None }

let private accepted (state: GroupState) = state.Results |> Map.toList |> List.map snd

// ---- ADM-011: incremental equals full ---------------------------------------------------

[<Fact>]
let ``incremental aggregation equals full recomputation for every prefix of random sequences`` () =
    for trial in 1..25 do
        let next = random trial
        let mode = if trial % 2 = 0 then AnonymousGroup else IdentifiedGroup
        let texts = [ for i in 1..(3 + next 10) -> submission next (trial * 100 + i) mode ]

        let _ =
            texts
            |> List.fold
                (fun (state: GroupState, accumulator: Incremental.Accumulator) text ->
                    let nextState, outcome = importOne state text

                    let nextAccumulator =
                        match outcome with
                        | Accepted result -> Incremental.add result accumulator |> ok
                        | _ -> accumulator

                    Assert.Equal(sprintf "%A" (aggregate policy nextState), sprintf "%A" (Incremental.result policy nextAccumulator))
                    nextState, nextAccumulator)
                (empty (definition mode), Incremental.empty (definition mode))

        ()

[<Fact>]
let ``incremental additions commute and repeat harmlessly`` () =
    let next = random 7
    let texts = [ for i in 1..8 -> submission next i AnonymousGroup ]
    let state = texts |> List.fold (fun s t -> importOne s t |> fst) (empty (definition AnonymousGroup))
    let results = accepted state

    let forward = Incremental.ofResults (definition AnonymousGroup) results |> ok
    let backward = Incremental.ofResults (definition AnonymousGroup) (List.rev results) |> ok
    let twice = Incremental.ofResults (definition AnonymousGroup) (results @ results) |> ok

    let render acc = sprintf "%A" (Incremental.result policy acc)
    Assert.Equal(render forward, render backward)
    Assert.Equal(render forward, render twice)

    // A different submission for an accepted identity is refused, never merged.
    let other = { results.Head with SubmissionHash = "sha256:other" }
    Assert.True(Incremental.add other forward |> Result.isError)

// ---- ARP-003: AdminReportState is a projection --------------------------------------------

let private stateFor (count: int) mode =
    let next = random count
    let texts = [ for i in 1..count -> submission next i mode ]
    let state = texts |> List.fold (fun s t -> importOne s t |> fst) (empty (definition mode))
    let accumulator = Incremental.ofResults (definition mode) (accepted state) |> ok
    state, accumulator, ReportState.project policy accumulator

[<Fact>]
let ``the report state is a deterministic projection of the group result`` () =
    let state, accumulator, projected = stateFor 6 IdentifiedGroup
    let result = aggregate policy state

    Assert.Equal(result.AcceptedCount, projected.AcceptedSurveyCount)
    Assert.Equal(result.Lineage.DerivationHash, projected.DerivationHash)
    Assert.Equal<string list>(pilot.Dimensions |> List.map _.Id, projected.Sections |> List.map _.SectionId)
    Assert.Equal(projected, ReportState.project policy accumulator)

    let means =
        result.Dimensions
        |> List.choose (fun (d, a) -> match a with Aggregated s -> s.Mean |> Option.map (fun m -> d.Id, m) | _ -> None)

    Assert.Equal(means |> List.sortBy snd |> List.head |> fst |> Some, projected.WeakestArea)
    // No answers and no raw URLs: only identities, hashes and statistics.
    Assert.DoesNotContain("https://", ReportState.canonical projected)

[<Fact>]
let ``a small anonymous group's state suppresses its scores`` () =
    let _, _, projected = stateFor 2 AnonymousGroup

    Assert.All(projected.Sections, fun s -> Assert.True(s.Suppressed && s.AggregateScore.IsNone))
    Assert.Equal(None, projected.WeakestArea)

[<Fact>]
let ``the state round-trips through its canonical form, and a version 1 state is refused`` () =
    let _, _, projected = stateFor 5 IdentifiedGroup
    let back = ReportState.ofJson (ReportState.toJson projected) |> ok
    Assert.Equal(ReportState.canonical projected, ReportState.canonical back)
    // Version 1 states carried per-respondent evidence and identity keys (WI-0067).
    let fragment = ReportState.embedded projected
    Assert.Equal(Error(ReportState.UnsupportedVersion 1), ReportState.readEmbedded ("a=1" + fragment.Substring 3) |> Result.map ignore)

// ---- ARP-004: persistence chosen by size, with integrity ----------------------------------------

[<Fact>]
let ``a small state travels in the URL; a large one is stored and referenced`` () =
    let _, _, small = stateFor 3 IdentifiedGroup
    let _, _, large = stateFor 60 IdentifiedGroup
    // Aggregates only (WI-0067): the state no longer grows with the group, so a
    // state outgrows a deployment's budget only when that budget is small.
    let tight = { ReportState.defaultBudget with MaximumUrlLength = 1200 }

    match ReportState.persistence ReportState.defaultBudget small with
    | ReportState.EmbeddedInUrl fragment -> Assert.Equal(ReportState.canonical small, ReportState.readEmbedded fragment |> ok |> ReportState.canonical)
    | other -> failwith $"%A{other}"

    Assert.True((ReportState.embedded large).Length < (ReportState.embedded small).Length + 200)

    match ReportState.persistence tight large with
    | ReportState.ExternalStore(id, fragment) ->
        Assert.StartsWith("rs-", id)
        Assert.Equal(Some id, ReportState.referencedId fragment)
        Assert.Equal(large, ReportState.checkReference fragment groupId large |> ok)
    | other -> failwith $"%A{other}"

    // The threshold leaves a margin: a state at the theoretical limit is stored.
    let tight = { ReportState.defaultBudget with MaximumUrlLength = (ReportState.embedded small).Length + ReportState.defaultBudget.Reserved }

    match ReportState.persistence tight small with
    | ReportState.ExternalStore _ -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``tampered, truncated, wrong-group and unknown references are refused`` () =
    let _, _, small = stateFor 3 IdentifiedGroup
    let fragment = ReportState.embedded small
    let parts = fragment.Split('.')

    let flipped = parts[1].Substring(0, 10) + (if parts[1][10] = 'A' then "B" else "A") + parts[1].Substring 11
    Assert.Equal(Error ReportState.IntegrityMismatch, ReportState.readEmbedded $"{parts[0]}.{flipped}.{parts[2]}" |> Result.map ignore)
    Assert.Equal(Error ReportState.Malformed, ReportState.readEmbedded (fragment.Substring(0, fragment.Length / 2)) |> Result.map ignore)
    Assert.Equal(Error(ReportState.UnsupportedVersion 9), ReportState.readEmbedded ("a=9" + fragment.Substring 3) |> Result.map ignore)

    let reference = ReportState.reference small
    Assert.Equal(Error ReportState.WrongGroupReference, ReportState.checkReference reference (opaque 3) small |> Result.map ignore)
    let _, _, other = stateFor 4 IdentifiedGroup
    Assert.Equal(Error ReportState.IntegrityMismatch, ReportState.checkReference reference groupId other |> Result.map ignore)
    Assert.Equal(None, ReportState.referencedId "s=12.ab.cd")

// ---- SIG-LINK-008: what the report state can put in a URL --------------------------------------

// ---- WI-0067: aggregates that pass the group's privacy rules, nothing per respondent -----------

let private accumulate mode (texts: string list) =
    texts
    |> List.fold
        (fun acc text ->
            match evaluateAgainst (definition mode) (fun _ -> None) text with
            | Accepted result -> Incremental.add result acc |> ok
            | other -> failwith $"%A{other}")
        (Incremental.empty (definition mode))

/// The same answers, each under a respondent identity drawn from `seed`.
let private responses mode (seed: int) (answerSets: Map<string, Answer> list) =
    answerSets
    |> List.mapi (fun i answers ->
        let id = opaque (seed + i)
        let binding = match mode with AnonymousGroup -> Anonymous(id, groupId) | IdentifiedGroup -> Identified(id, groupId)
        "https://signal.example" + LiveUrl.urlFor pilot "/web/" "" { Binding = binding; Answers = answers })

let private answerSets (count: int) =
    let next = random 31
    [ for _ in 1..count -> pilot.Items |> List.map (fun item -> item.Id, answers[next answers.Length]) |> Map.ofList ]

[<Fact>]
let ``the report state holds only aggregates: the same answers from other respondents give the same state`` () =
    for mode in [ AnonymousGroup; IdentifiedGroup ] do
        let sets = answerSets 6
        let first = responses mode 300 sets
        let others = responses mode 700 (List.rev sets)
        let a = ReportState.project policy (accumulate mode first)
        let b = ReportState.project policy (accumulate mode others)
        // Different respondents, different order, same answers: nothing in the state tells them apart,
        // except the derivation hash, the one set-level lineage hash over which submissions were
        // accepted (ADM-020): it identifies the input set, never a respondent or a value.
        Assert.Equal(ReportState.canonical { a with DerivationHash = "" }, ReportState.canonical { b with DerivationHash = "" })
        Assert.StartsWith("sha256:", a.DerivationHash)

        let accumulator = accumulate mode first
        let fragment = ReportState.embedded (ReportState.project policy accumulator)
        let json = Text.Encoding.UTF8.GetString(Buffers.Text.Base64Url.DecodeFromChars((fragment.Split('.')[1]).AsSpan()))
        let document = Text.Json.JsonDocument.Parse json

        // A fixed schema of aggregates and hashes: a new field is a deliberate privacy review.
        let schema = set [ "version"; "group"; "mode"; "expected"; "accepted"; "complete"; "template"; "weakest"; "strongest"; "sections"; "coverage"; "derivation" ]
        Assert.Equal<Set<string>>(schema, document.RootElement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)

        // No identity key, submission hash, answer, item or URL.
        for key, hash in accumulator.Accepted |> Map.toList do
            Assert.DoesNotContain(key, json)
            Assert.DoesNotContain(hash, json)

        for item in pilot.Items do
            Assert.DoesNotContain(item.Id, json)

        for text in first do
            Assert.DoesNotContain(text.Substring(text.IndexOf "#r=" + 3, 24), json)

        Assert.DoesNotContain("http", json)

[<Fact>]
let ``below an anonymous group's minimum the report state holds only the counts`` () =
    let state = ReportState.project policy (accumulate AnonymousGroup (responses AnonymousGroup 500 (answerSets 2)))
    Assert.Equal(2, state.AcceptedSurveyCount)
    Assert.All(state.Sections, fun s -> Assert.True(s.Suppressed && s.AggregateScore.IsNone && s.Scored = 0 && s.Unscored = 0))
    Assert.Equal((None, None, None), (state.WeakestArea, state.StrongestArea, state.Coverage))

    // At the minimum the aggregates are reportable again.
    let reportable = ReportState.project policy (accumulate AnonymousGroup (responses AnonymousGroup 500 (answerSets 3)))
    Assert.True(reportable.Sections |> List.exists (fun s -> s.AggregateScore.IsSome))
    Assert.True(reportable.Coverage.IsSome)
