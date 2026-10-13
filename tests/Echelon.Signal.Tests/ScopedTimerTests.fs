/// Section and page timers, and intake's timer flags (WI-0081, ARX-013,
/// DF-SIGNAL-2026-0015, SIG-LINK-016): each starts when the respondent first
/// reaches its section or page, the start is stamped into the integrity-checked
/// link and survives a reload, an edited start is refused, an expired enforced
/// timer holds only the answers it covers, an anonymous link gains only those
/// minutes and the submission minute, and intake flags a late submission
/// without refusing it.
module Echelon.Signal.Tests.ScopedTimerTests

open System
open System.Buffers.Text
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericEnvelope
open Echelon.Signal.Engine.GenericSession

let private minutes (n: float) = TimeSpan.FromMinutes n

let private timer id scope duration : Timers.Timer =
    { Id = id; Scope = scope; Duration = minutes duration; Warnings = []; Expiry = Timers.LockOnly; Enforcement = Timers.Enforced }

/// The paged demo without locks: pages [Q1, Q2], [Q3], [CTX-001, CTX-002];
/// a 3-minute timer on layout page 1 and a 5-minute timer on section CONTEXT.
let private content =
    let paged = SurveyPageTests.paged
    { paged with
        Presentation =
            { paged.Presentation with
                Revisit = fullRevisit
                Timers = [ timer "p1" (Timers.PageScope 1) 3.0; timer "context" (Timers.SectionScope "CONTEXT") 5.0 ] } }

let private ids = questions content |> List.map (fun (_, q) -> q.Id)
let private file = publishedFile "SCOPED" "1" content |> snd
let private form = GenericImport.formOf "SCOPED" "1" content
let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private t0 = DateTimeOffset(2026, 5, 6, 10, 20, 30, TimeSpan.Zero)
let private minute = startMinute t0

let private responding =
    function
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private openAt (now: DateTimeOffset) (hash: string) = start hash |> receivedAt now (Found file)
let private invitation mode = "#r=" + GenericEnvelope.invitation "SCOPED" "1" content mode (oid 1) (oid 40)
let private first id = choices (tryQuestion content id).Value |> List.head |> fst
let private answer id s = update (Chose(id, first id)) s
let private at (now: DateTimeOffset) s = update (Ticked now) s

/// Opened at t0, page 1 answered, then on to page 3 at t0 + 2 minutes.
let private onPage3 mode =
    Responding(responding (openAt t0 (invitation mode)))
    |> answer ids[0]
    |> answer ids[1]
    |> at (t0.AddMinutes 1.0)
    |> update (Turned GenericPaging.Next)
    |> answer ids[2]
    |> at (t0.AddMinutes 2.0)
    |> update (Turned GenericPaging.Next)

[<Fact>]
let ``a page timer starts when its page is first shown, and a section timer when its section is`` () =
    let opened = responding (openAt t0 (invitation Import.IdentifiedGroup))
    Assert.Equal<(ScopeStart * DateTimeOffset) list>([ PageStart 1, minute ], opened.Terms.ScopeStarts)
    Assert.Equal(None, opened.Terms.StartedAt)
    let later = responding (onPage3 Import.IdentifiedGroup)
    Assert.Equal<(ScopeStart * DateTimeOffset) list>([ SectionStart 1, minute.AddMinutes 2.0; PageStart 1, minute ], later.Terms.ScopeStarts)

[<Fact>]
let ``a reload keeps every start, and an old link without starts still opens`` () =
    let r = responding (onPage3 Import.IdentifiedGroup)
    let reloaded = responding (openAt (t0.AddMinutes 3.0) (fragment r))
    Assert.Equal<(ScopeStart * DateTimeOffset) list>(r.Terms.ScopeStarts, reloaded.Terms.ScopeStarts)
    // Format 1 without any terms opens and is stamped.
    Assert.Equal(1uy, Base64Url.DecodeFromChars((invitation Import.IdentifiedGroup).Substring(3).AsSpan())[0])

[<Fact>]
let ``an edited start is refused, and only the canonical spelling is written`` () =
    let r = responding (openAt t0 (invitation Import.IdentifiedGroup))
    let payload = (payloadIn (fragment r) |> Result.toOption).Value
    let bytes = Base64Url.DecodeFromChars(payload.AsSpan())
    // flags, count, then kind, index (2) and the minute (4): its last byte.
    let minuteEnd = 2 + 8 + 32 + 1 + 1 + 1 + 2 + 3
    bytes[minuteEnd] <- bytes[minuteEnd] - 1uy
    Assert.Equal(Refused(UnreadableLink IntegrityFailed), openAt t0 ("#r=" + Base64Url.EncodeToString(ReadOnlySpan bytes)))

    Assert.True((termsProblem { noTerms with ScopeStarts = [ PageStart 2, minute; PageStart 1, minute ] }).IsSome)
    Assert.True((termsProblem { noTerms with ScopeStarts = [ PageStart 1, minute; PageStart 1, minute ] }).IsSome)
    Assert.True((termsProblem { noTerms with ScopeStarts = [ PageStart 0, minute ] }).IsSome)
    Assert.True((termsProblem { noTerms with ScopeStarts = [ PageStart 1, t0 ] }).IsSome)
    Assert.True((termsProblem { noTerms with SubmittedAt = Some t0 }).IsSome)

[<Fact>]
let ``an expired page or section timer holds only the answers it covers`` () =
    let s = onPage3 Import.IdentifiedGroup
    // The page 1 timer ran out at minute + 3: page 1's answers hold, page 2's still change.
    let late = s |> at (t0.AddMinutes 4.0) |> update (Turned GenericPaging.Back) |> update (Turned GenericPaging.Back)
    let other id = choices (tryQuestion content id).Value |> List.item 1 |> fst
    Assert.Equal(Some(first ids[0]), (responding (update (Chose(ids[0], other ids[0])) late)).Answers.TryFind ids[0])
    Assert.Equal(Some(other ids[2]), (responding (update (Chose(ids[2], other ids[2])) late)).Answers.TryFind ids[2])
    Assert.Contains("Time is up for this page", GenericTimer.notice content (responding late).Terms (responding late).Now ids[0..1])
    // The CONTEXT timer started at minute + 2 and runs out at minute + 7.
    let s = s |> at (t0.AddMinutes 6.0) |> answer ids[3]
    Assert.True((responding s).Answers.ContainsKey ids[3])
    let s = s |> at (t0.AddMinutes 8.0) |> answer ids[4]
    Assert.False((responding s).Answers.ContainsKey ids[4])

let private submitted mode (submitAt: DateTimeOffset) =
    let s = onPage3 mode |> answer ids[3] |> answer ids[4] |> at submitAt

    match update (SubmitRequested(Array.init 16 (fun i -> byte (90 + i)))) s with
    | Responding r when r.Phase = Submitted -> r
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an anonymous link gains only the start minutes, and its submission only the submission minute`` () =
    let r = submitted Import.AnonymousGroup (t0.AddMinutes 2.5)
    let terms = snd (decodeWithTerms content form.Reference ((payloadIn (fragment r) |> Result.toOption).Value) |> Result.toOption).Value

    Assert.Equal(
        { noTerms with
            ScopeStarts = [ SectionStart 1, minute.AddMinutes 2.0; PageStart 1, minute ]
            SubmittedAt = Some(minute.AddMinutes 3.0) },
        terms
    )

    Assert.True(match r.Binding with Anonymous _ -> true | _ -> false)

[<Fact>]
let ``intake flags a late submission and accepts it; an on-time one has no flag`` () =
    let definition: Import.GroupDefinition =
        { Group = oid 40; Mode = Import.IdentifiedGroup; ExpectedCount = 2; Template = GenericImport.shapeOf "SCOPED" "1" content; Generic = Some form }

    let read (r: Response) =
        match GenericImport.evaluate definition (fun _ -> None) ("https://signal.example/web/survey/" + fragment r) with
        | Import.Accepted result -> result
        | other -> failwith $"%A{other}"

    // Submitted in the page timer's last minute (10:23): it may have been in time.
    let onTime = read (submitted Import.IdentifiedGroup (t0.AddMinutes 2.5))
    Assert.Empty(onTime.LateTimers)
    let late = read (submitted Import.IdentifiedGroup (t0.AddMinutes 9.0))
    Assert.Equal<string list>([ "p1"; "context" ], late.LateTimers)
    let accumulator = Incremental.ofResults { definition with Group = oid 40 } [ late ] |> Result.toOption
    Assert.Equal(Some 1, accumulator |> Option.map _.Late)

[<Fact>]
let ``an untimed survey's submission gains no minute`` () =
    let demo = SurveyPageTests.demo
    let link = "#r=" + GenericEnvelope.invitation "DEMO" "1" demo Import.IdentifiedGroup (oid 1) (oid 40)
    let s = start link |> receivedAt t0 (Found(publishedFile "DEMO" "1" demo |> snd))
    let s = questions demo |> List.fold (fun s (_, q) -> update (Chose(q.Id, choices q |> List.head |> fst)) s) s

    match update (SubmitRequested(Array.zeroCreate 16)) s with
    | Responding r when r.Phase = Submitted -> Assert.Equal(noTerms, r.Terms)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``the stored contribution carries the late flag`` () =
    let definition: Import.GroupDefinition =
        { Group = oid 40; Mode = Import.IdentifiedGroup; ExpectedCount = 1; Template = GenericImport.shapeOf "SCOPED" "1" content; Generic = Some form }

    match GenericImport.evaluate definition (fun _ -> None) ("https://signal.example/web/survey/" + fragment (submitted Import.IdentifiedGroup (t0.AddMinutes 9.0))) with
    | Import.Accepted result ->
        let provenance: Echelon.Signal.Admin.ResultRecord.Provenance = { Origin = Echelon.Signal.Admin.ResultRecord.PastedUrl; BatchId = "b"; ArtifactHash = "sha256:00"; EncodingVersion = 1 }
        let body = Echelon.Signal.Admin.ResultRecord.body "ds" { Group = oid 40; Result = result; Provenance = provenance; Submission = None }

        match Echelon.Signal.Admin.ResultRecord.ofBody definition.Template body with
        | Ok stored -> Assert.Equal<string list>([ "p1"; "context" ], stored.Contribution.Result.LateTimers)
        | Error e -> failwith $"%A{e}"
    | other -> failwith $"%A{other}"

// ---- The browser's links (tests/browser/fixtures/timer-links.json) -------------------------------

let private timerLinks =
    [ "scoped", "web/survey/" + invitation Import.IdentifiedGroup
      "scopedExpired",
      "web/survey/#r="
      + GenericEnvelope.invitationWith { noTerms with ScopeStarts = [ PageStart 1, DateTimeOffset(2020, 1, 1, 9, 0, 0, TimeSpan.Zero) ] } "SCOPED" "1" content Import.IdentifiedGroup (oid 1) (oid 40)
      "scopedFile", fst (publishedFile "SCOPED" "1" content) ]

let private timerLinksJson () =
    let o = Text.Json.Nodes.JsonObject()
    timerLinks |> List.iter (fun (name, value) -> o[name] <- Text.Json.Nodes.JsonValue.Create value)
    o.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)) + "\n"

/// SIGNAL_WRITE_FIXTURES=1 rewrites the committed files from the code.
[<Fact>]
let ``the scoped timer survey file and the browser links are what the code produces`` () =
    let path, bytes = publishedFile "SCOPED" "1" content

    if Environment.GetEnvironmentVariable "SIGNAL_WRITE_FIXTURES" = "1" then
        IO.File.WriteAllBytes(Support.repoFile path, bytes)
        IO.File.WriteAllText(Support.repoFile "tests/browser/fixtures/timer-links.json", timerLinksJson ())

    Assert.Equal<byte[]>(bytes, IO.File.ReadAllBytes(Support.repoFile path))
    Assert.Equal(timerLinksJson (), Support.readRepoFile "tests/browser/fixtures/timer-links.json")
