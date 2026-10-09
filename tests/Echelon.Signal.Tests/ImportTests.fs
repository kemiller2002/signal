/// The administrator import pipeline and group aggregation (WI-0034):
/// ARP-001, ARP-002, ARP-005, LURL-003, ID-003, ARX-008, ARX-012.
module Echelon.Signal.Tests.ImportTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Engine.Aggregation

let private pilot = Pilot.assessment
let private id (seed: byte) = (OpaqueId.ofBytes (Array.init 16 (fun i -> seed + byte i))).Value
let private group = id 200uy

let private anonymousGroup = empty { Group = group; Mode = AnonymousGroup; ExpectedCount = 6; Template = pilot; Generic = None }
let private identifiedGroup = empty { Group = group; Mode = IdentifiedGroup; ExpectedCount = 3; Template = pilot; Generic = None }

let private all (answer: Answer) = pilot.Items |> List.map (fun item -> item.Id, answer) |> Map.ofList

let private link (binding: Binding) (answers: Answers) =
    "https://signal.example" + LiveUrl.urlFor pilot "/web/" "" { Binding = binding; Answers = answers }

let private anonymous (seed: byte) answers = link (Anonymous(id seed, group)) answers

/// A respondent's submission through the real finalization path.
let private finalized (instance: OpaqueId) (entropy: byte[]) (answers: Answers) =
    match Submission.finalize pilot entropy { Binding = AnonymousInvitation(instance, group); Answers = answers } with
    | Ok envelope -> "https://signal.example" + LiveUrl.urlFor pilot "/web/" "" envelope
    | Error refusal -> failwith $"{refusal}"

[<Fact>]
let ``an anonymous submission is accepted and scored exactly as the respondent saw it`` () =
    let answers = all (Rated Often) |> Map.add "CORE-011" (Withheld DontKnow)
    let state, outcome = importOne anonymousGroup (finalized (id 1uy) (Array.create 16 7uy) answers)

    match outcome with
    | Accepted result ->
        Assert.Equal<(Dimension * DimensionResult) list>(score pilot answers, result.Dimensions)
        Assert.Equal(Canonical.templateHash pilot, result.TemplateHash)
        Assert.Equal(AnonymousSubmission((OpaqueId.ofBytes (Array.create 16 7uy)).Value), result.Identity)
        Assert.Equal(15, result.AnsweredCount)
        Assert.Equal(1, result.NonNumericCount)
        Assert.StartsWith("sha256:", result.SubmissionHash)
        Assert.Equal(1, state.Results.Count)
    | other -> failwith $"{other}"

[<Fact>]
let ``importing the same artifact again is idempotent`` () =
    let submission = anonymous 1uy (all (Rated Sometimes))
    let once, _ = importOne anonymousGroup submission
    let twice, outcome = importOne once submission
    Assert.Equal(AlreadyImported(AnonymousSubmission(id 1uy)), outcome)
    Assert.Equal(1, twice.Results.Count)
    Assert.Equal<string list>([ "accepted"; "already-imported" ], twice.Log)
    // The bare payload and the full URL are the same artifact.
    let _, again = importOne once (submission.Substring(submission.IndexOf "#r=" + 3))
    Assert.Equal(AlreadyImported(AnonymousSubmission(id 1uy)), again)

[<Fact>]
let ``a second, different submission for an identified instance is rejected, not substituted`` () =
    let first = link (Identified(id 5uy, group)) (all (Rated Often))
    let second = link (Identified(id 5uy, group)) (all (Rated Never))
    let state, _ = importOne identifiedGroup first
    let after, outcome = importOne state second

    match outcome with
    | Rejected(DuplicateInstance existing) -> Assert.Equal(state.Results["instance:" + string (id 5uy)].SubmissionHash, existing)
    | other -> failwith $"{other}"

    Assert.Equal<Map<string, SurveyResult>>(state.Results, after.Results)

[<Fact>]
let ``every invalid submission is rejected with an explicit reason and changes nothing`` () =
    let complete = all (Rated Often)
    let valid = anonymous 1uy complete
    let corrupt = valid.Substring(0, valid.Length - 2) + (if valid.EndsWith "AA" then "BA" else "AA")
    let otherTemplate = { pilot with Version = "0.2.0" }

    let otherTemplateLink =
        "https://x/#r=" + encode otherTemplate { Binding = Anonymous(id 1uy, group); Answers = complete }

    let cases =
        [ "not a submission at all /", Rejected NoSubmissionFound
          "https://x/#section", Rejected NoSubmissionFound
          corrupt, Rejected(Unreadable IntegrityFailed)
          otherTemplateLink, Rejected(Unreadable TemplateMismatch)
          link (AnonymousInvitation(id 9uy, group)) complete, Rejected NotFinalized
          link Unbound complete, Rejected NotFinalized
          link (Anonymous(id 1uy, id 99uy)) complete, Rejected WrongGroup
          link (Identified(id 1uy, group)) complete, Rejected IdentityModeMismatch
          anonymous 1uy (complete.Remove "CORE-001"), Rejected(IncompleteSubmission 1) ]

    for text, expected in cases do
        let state, outcome = importOne anonymousGroup text
        Assert.Equal(expected, outcome)
        Assert.True(state.Results.IsEmpty)

    // The log carries outcome codes only: no answers, no ids.
    let state, _ = importAll anonymousGroup (cases |> List.map fst)
    Assert.All(state.Log, fun entry -> Assert.StartsWith("rejected:", entry))
    Assert.All(state.Log, fun entry -> Assert.DoesNotContain(string (id 1uy), entry))

// ---------------------------------------------------------------------------
// Aggregation.
// ---------------------------------------------------------------------------

let private eight =
    [ for seed in 1uy .. 8uy ->
          let answer = if seed % 2uy = 0uy then Rated AlmostAlways else Rated Sometimes
          anonymous seed (all answer |> Map.add "CORE-015" (if seed = 3uy then Withheld NotApplicable else answer)) ]

[<Fact>]
let ``the group result does not depend on import order`` () =
    let reference = aggregate defaultPolicy (fst (importAll anonymousGroup eight))
    let random = Random 7

    for _ in 1..20 do
        let shuffled = eight |> List.sortBy (fun _ -> random.Next())
        let result = aggregate defaultPolicy (fst (importAll anonymousGroup shuffled))
        Assert.Equal(sprintf "%A" reference, sprintf "%A" result)

[<Fact>]
let ``statistics cover scored respondents only, and coverage is separate`` () =
    let result = aggregate defaultPolicy (fst (importAll anonymousGroup eight))
    Assert.Equal(8, result.AcceptedCount)
    Assert.Equal(0, result.MissingCount)
    Assert.Equal(Complete, result.Completion)

    match result.Dimensions |> List.map snd with
    | [ Aggregated d1; Aggregated d2; Aggregated d3 ] ->
        // Four respondents at 50.0 and four at 100.0.
        Assert.Equal({ Scored = 8; Unscored = 0; Mean = Some 75.0; Median = Some 75.0; Minimum = Some 50.0; Maximum = Some 100.0 }, d1)
        Assert.Equal(d1, d2)
        Assert.Equal(8, d3.Scored)
    | other -> failwith $"{other}"

    Assert.Equal(Some(0.008), result.NonNumericShare) // 1 of 120 answers, to 0.1%

[<Fact>]
let ``a small anonymous group shows counts but suppresses score aggregates`` () =
    let state, _ = importAll anonymousGroup (eight |> List.take 4)
    let result = aggregate defaultPolicy state
    Assert.Equal(4, result.AcceptedCount)
    Assert.Equal(2, result.MissingCount)
    Assert.Equal(WaitingForResponses, result.Completion)
    Assert.All(result.Dimensions, fun (_, aggregate) -> Assert.Equal(Suppressed(4, 5), aggregate))

    // An identified group is not suppressed: its administrator already
    // knows who each instance is.
    let identified =
        importAll identifiedGroup [ link (Identified(id 1uy, group)) (all (Rated Often)) ] |> fst |> aggregate defaultPolicy

    Assert.All(identified.Dimensions, fun (_, aggregate) -> Assert.True(aggregate.IsAggregated))

[<Fact>]
let ``unscored respondents are counted, never zeroed`` () =
    let tooFew = all (Rated Often) |> Map.add "CORE-011" (Withheld DontKnow) |> Map.add "CORE-012" (Withheld DontKnow) |> Map.add "CORE-013" (Withheld DontKnow)
    let state, _ = importAll identifiedGroup [ link (Identified(id 1uy, group)) tooFew; link (Identified(id 2uy, group)) (all (Rated Never)) ]
    let result = aggregate defaultPolicy state

    match result.Dimensions |> List.find (fun (d, _) -> d.Id = "D03") |> snd with
    | Aggregated d3 -> Assert.Equal({ Scored = 1; Unscored = 1; Mean = Some 0.0; Median = Some 0.0; Minimum = Some 0.0; Maximum = Some 0.0 }, d3)
    | other -> failwith $"{other}"

[<Fact>]
let ``lineage names the template and every accepted submission, and changes with them`` () =
    let five, _ = importAll anonymousGroup (eight |> List.take 5)
    let six, _ = importOne five eight[5]
    let a = aggregate defaultPolicy five
    let b = aggregate defaultPolicy six
    Assert.Equal(Canonical.templateHash pilot, a.Lineage.TemplateHash)
    Assert.Equal(5, a.Lineage.SubmissionHashes.Length)
    Assert.Equal<string list>(List.sort a.Lineage.SubmissionHashes, a.Lineage.SubmissionHashes)
    Assert.NotEqual<string>(a.Lineage.DerivationHash, b.Lineage.DerivationHash)
    // A rejected or repeated import does not change the derivation.
    let repeated, _ = importAll six [ eight[5]; "garbage" ]
    Assert.Equal(b.Lineage.DerivationHash, (aggregate defaultPolicy repeated).Lineage.DerivationHash)
