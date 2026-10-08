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

let private definition mode = { Group = groupId; Mode = mode; ExpectedCount = 12; Template = pilot }

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
let ``the state round-trips and resumes importing where it left off`` () =
    let state, _, projected = stateFor 5 IdentifiedGroup
    let back = ReportState.ofJson (ReportState.toJson projected) |> ok
    Assert.Equal(ReportState.canonical projected, ReportState.canonical back)

    // Resume: the next import is decided against the state's identities.
    let resumed = ReportState.resume (definition IdentifiedGroup) back |> ok
    let next = random 99
    let newcomer = submission next 77 IdentifiedGroup
    let duplicate = submission (random 5) 1 IdentifiedGroup

    match evaluateAgainst (definition IdentifiedGroup) (Incremental.acceptedFor resumed) newcomer with
    | Accepted result ->
        let after = Incremental.add result resumed |> ok
        let full = importOne state newcomer |> fst
        Assert.Equal(sprintf "%A" (aggregate policy full), sprintf "%A" (Incremental.result policy after))
    | other -> failwith $"%A{other}"

    Assert.Equal(AlreadyImported(Instance(opaque 1)), evaluateAgainst (definition IdentifiedGroup) (Incremental.acceptedFor resumed) duplicate)
    Assert.Equal(Error ReportState.OtherGroup, ReportState.resume { definition IdentifiedGroup with Group = opaque 3 } back |> Result.map ignore)

// ---- ARP-004: persistence chosen by size, with integrity ----------------------------------------

[<Fact>]
let ``a small state travels in the URL; a large one is stored and referenced`` () =
    let _, _, small = stateFor 3 IdentifiedGroup
    let _, _, large = stateFor 60 IdentifiedGroup

    match ReportState.persistence ReportState.defaultBudget small with
    | ReportState.EmbeddedInUrl fragment -> Assert.Equal(ReportState.canonical small, ReportState.readEmbedded fragment |> ok |> ReportState.canonical)
    | other -> failwith $"%A{other}"

    match ReportState.persistence ReportState.defaultBudget large with
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

/// The embedded report state is the only report state meant for a URL. It
/// carries no item-level answer, no submission URL and no respondent text:
/// its fields are a fixed schema of section summaries, counts, hashes and
/// opaque identity keys. (Its per-respondent dimension scores and, in
/// identified groups, invitation-linked keys are recorded as a privacy
/// obligation for WI-0051; see DF-SIGNAL-2026-0003.) No administrator route
/// can carry it: route parameters are an allow-list (RoutesTests).
[<Fact>]
let ``the embedded report state carries no answers, submission URLs or respondent text`` () =
    for mode in [ AnonymousGroup; IdentifiedGroup ] do
        let next = random 77
        let texts = [ for i in 1..6 -> submission next (900 + i) mode ]

        let accumulator =
            texts
            |> List.fold
                (fun acc text ->
                    match evaluateAgainst (definition mode) (fun _ -> None) text with
                    | Accepted result -> Incremental.add result acc |> ok
                    | other -> failwith $"%A{other}")
                (Incremental.empty (definition mode))

        let fragment = ReportState.embedded (ReportState.project policy accumulator)
        let payload = fragment.Split('.')[1]
        let json = Text.Encoding.UTF8.GetString(Buffers.Text.Base64Url.DecodeFromChars(payload.AsSpan()))
        let document = Text.Json.JsonDocument.Parse json

        let schema = set [ "version"; "group"; "mode"; "expected"; "accepted"; "complete"; "template"; "weakest"; "strongest"; "sections"; "coverage"; "derivation"; "identities"; "evidence"; "answered"; "nonNumeric" ]
        let fields = document.RootElement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
        // A fixed schema: a new field is a deliberate privacy review.
        Assert.Equal<Set<string>>(schema, fields)
        // `answered` and `nonNumeric` are counts, not answers.
        Assert.Equal(Text.Json.JsonValueKind.Number, document.RootElement.GetProperty("answered").ValueKind)

        for item in pilot.Items do
            Assert.DoesNotContain(item.Id, json)

        for text in texts do
            Assert.DoesNotContain("#r=", json)
            Assert.DoesNotContain(text.Substring(text.IndexOf "#r=" + 3, 24), json)

        Assert.DoesNotContain("http", json)
