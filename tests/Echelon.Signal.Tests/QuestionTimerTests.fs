/// Question timers (WI-0081, ARX-013; DF-SIGNAL-2026-0015 §9, SIG-LINK-016
/// entry kind 3): a question timer starts when its question is first shown,
/// the start is stamped into the integrity-checked link as entry kind 3, an
/// expired enforced timer holds only that question, and intake flags a late
/// submission. The link contract holds: every existing link decodes and
/// re-encodes byte for byte, an entry kind a decoder does not know refuses
/// the whole link with its coded, safe description (never a partial read),
/// and terms with every entry kind round-trip, as a property.
module Echelon.Signal.Tests.QuestionTimerTests

open System
open System.Buffers.Text
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericEnvelope
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Tests.Support

let private timer id scope (minutes: float) : Timers.Timer =
    { Id = id; Scope = scope; Duration = TimeSpan.FromMinutes minutes; Warnings = []; Expiry = Timers.LockOnly; Enforcement = Timers.Enforced }

/// The paged demo without locks: pages [Q1, Q2], [Q3], [CTX-001, CTX-002];
/// a 3-minute timer on the first question and a 2-minute timer on the third.
let private paged = SurveyPageTests.paged
let private ids = questions paged |> List.map (fun (_, q) -> q.Id)

let private content =
    { paged with
        Presentation =
            { paged.Presentation with
                Revisit = fullRevisit
                Timers = [ timer "first" (Timers.QuestionScope ids[0]) 3.0; timer "third" (Timers.QuestionScope ids[2]) 2.0 ] } }

let private file = publishedFile "QTIMED" "1" content |> snd
let private form = GenericImport.formOf "QTIMED" "1" content
let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private t0 = DateTimeOffset(2026, 5, 6, 10, 20, 30, TimeSpan.Zero)
let private minute = startMinute t0

let private responding =
    function
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private invitation = "#r=" + GenericEnvelope.invitation "QTIMED" "1" content Import.IdentifiedGroup (oid 1) (oid 40)
let private openAt (now: DateTimeOffset) (hash: string) = start hash |> receivedAt now (Found file)
let private first id = choices (tryQuestion content id).Value |> List.head |> fst
let private other id = choices (tryQuestion content id).Value |> List.item 1 |> fst
let private answer id s = update (Chose(id, first id)) s
let private at (now: DateTimeOffset) s = update (Ticked now) s

/// Opened at t0, page 1 answered, then on to page 2 at t0 + 1 minute.
let private onPage2 () =
    Responding(responding (openAt t0 invitation)) |> answer ids[0] |> answer ids[1] |> at (t0.AddMinutes 1.0) |> update (Turned GenericPaging.Next)

[<Fact>]
let ``a question timer starts when its question is first shown, and the start survives a reload`` () =
    let opened = responding (openAt t0 invitation)
    Assert.Equal<(ScopeStart * DateTimeOffset) list>([ QuestionStart 0, minute ], opened.Terms.ScopeStarts)
    let later = responding (onPage2 ())
    Assert.Equal<(ScopeStart * DateTimeOffset) list>([ QuestionStart 0, minute; QuestionStart 2, minute.AddMinutes 1.0 ], later.Terms.ScopeStarts)
    let reloaded = responding (openAt (t0.AddMinutes 1.5) (fragment later))
    Assert.Equal<(ScopeStart * DateTimeOffset) list>(later.Terms.ScopeStarts, reloaded.Terms.ScopeStarts)

[<Fact>]
let ``an expired question timer holds only its question, and the page names it`` () =
    // The first question's timer started at 10:20 and ran out at 10:23.
    let late = onPage2 () |> at (t0.AddMinutes 4.0) |> update (Turned GenericPaging.Back)
    Assert.Equal(Some(first ids[0]), (responding (update (Chose(ids[0], other ids[0])) late)).Answers.TryFind ids[0])
    Assert.Equal(Some(other ids[1]), (responding (update (Chose(ids[1], other ids[1])) late)).Answers.TryFind ids[1])
    Assert.Contains("Time is up for this question", GenericTimer.notice content (responding late).Terms (responding late).Now ids[0..1])

[<Fact>]
let ``intake flags a submission after a question timer ran out, and accepts it`` () =
    let definition: Import.GroupDefinition =
        { Group = oid 40; Mode = Import.IdentifiedGroup; ExpectedCount = 1; Template = GenericImport.shapeOf "QTIMED" "1" content; Generic = Some form }

    let submitted (at': DateTimeOffset) =
        let s = onPage2 () |> answer ids[2] |> update (Turned GenericPaging.Next) |> answer ids[3] |> answer ids[4] |> at at'

        match update (SubmitRequested(Array.zeroCreate 16)) s with
        | Responding r when r.Phase = Submitted -> r
        | other -> failwith $"%A{other}"

    let read (r: Response) =
        match GenericImport.evaluate definition (fun _ -> None) ("https://signal.example/web/survey/" + fragment r) with
        | Import.Accepted result -> result
        | other -> failwith $"%A{other}"

    Assert.Empty((read (submitted (t0.AddMinutes 1.5))).LateTimers)
    // "third" ran out at 10:23 (started 10:21) and "first" at 10:23 (started 10:20).
    Assert.Equal<string list>([ "first"; "third" ], (read (submitted (t0.AddMinutes 5.0))).LateTimers)

[<Fact>]
let ``the console says a question timer counts whole minutes from first display`` () =
    Assert.Contains("whole minute", Echelon.Signal.Admin.AreaAuthoring.timerHelp)
    Assert.Contains("up to a minute less", Echelon.Signal.Admin.AreaAuthoring.timerHelp)

    let added =
        Echelon.Signal.Admin.AreaAuthoring.apply (Assessment.dimensionScorer Pilot.assessment) (Echelon.Signal.Admin.AreaAuthoring.AddQuestionTimer(ids[2].ToLowerInvariant(), "4", "")) paged

    match added with
    | Ok c ->
        Assert.Equal<Timers.Scope list>([ Timers.QuestionScope ids[2] ], c.Presentation.Timers |> List.map _.Scope)
        Assert.Contains("from when it is first shown", Echelon.Signal.Admin.AreaAuthoring.describeTimer c.Presentation.Timers.Head)
    | Error e -> failwith e

// ---- SIG-LINK-016 entry kind 3: the link contract --------------------------------------------------

/// Every committed link fixture the browser tests open.
let private fixtureLinks =
    [ for name in [ "survey-links"; "timer-links"; "localized-links"; "max-link" ] do
          let o = Text.Json.Nodes.JsonNode.Parse(readRepoFile $"tests/browser/fixtures/{name}.json").AsObject()

          for pair in o do
              let value = pair.Value.GetValue<string>()

              if value.Contains "#r=" then
                  yield $"{name}.{pair.Key}", value.Substring(value.IndexOf '#') ]

[<Fact>]
let ``every existing link fixture decodes and re-encodes byte for byte`` () =
    let checkedLinks =
        [ for name, hash in fixtureLinks do
              match start hash with
              | Fetching(reference, payload, _) as waiting ->
                  let path = (wanted waiting).Value

                  if IO.File.Exists(repoFile path) then
                      // The published file itself, verified against the link's
                      // reference: no page rule (expiry, timers) is involved.
                      match verify reference (IO.File.ReadAllBytes(repoFile path)) with
                      | Error refusal -> failwith $"{name}: %A{refusal}"
                      | Ok published ->
                          match decodeWithTerms published.Form.Content reference payload with
                          | Ok(envelope, terms) ->
                              Assert.True(encodeWith published.Form.Content reference terms envelope = payload, $"{name} re-encodes differently")
                              yield name
                          | Error e -> failwith $"{name}: %A{e}"
              | other -> failwith $"{name}: %A{other}" ]

    // Every fixture with a published file is checked (only "unpublished" has none).
    Assert.Equal(fixtureLinks.Length - 1, checkedLinks.Length)

/// A link whose first timer entry is rewritten to `kind`, with its integrity check recomputed.
let private withEntryKind (kind: byte) =
    let terms = { noTerms with ScopeStarts = [ QuestionStart 0, minute ] }
    let payload = GenericEnvelope.invitationWith terms "QTIMED" "1" content Import.IdentifiedGroup (oid 1) (oid 40)
    let bytes = Base64Url.DecodeFromChars(payload.AsSpan())
    // version, kind, reference (8), two ids (32), flags, count: then the entry's kind byte.
    let at = 1 + 1 + 8 + 32 + 1 + 1
    Assert.Equal(3uy, bytes[at])
    bytes[at] <- kind
    let body = bytes[.. bytes.Length - UrlState.IntegrityLength - 1]
    Base64Url.EncodeToString(ReadOnlySpan(Array.append body (UrlState.checksum (ReadOnlySpan body))))

[<Fact>]
let ``an entry kind the decoder does not know refuses the whole link, coded and safely described`` () =
    // Kind 3 decodes now. A decoder that does not know a kind refuses it the
    // same way the decoder before this change refused kind 3: its kind match
    // read only 1 and 2 and failed every other byte (InvalidTerms). Kinds 0
    // and 4..255 stand in for that older decoder here.
    let link3 = withEntryKind 3uy
    Assert.True(decodeWithTerms content form.Reference link3 |> Result.isOk)

    for kind in [ 0uy; 4uy; 5uy; 128uy; 255uy ] do
        let link = withEntryKind kind
        Assert.Equal(Error InvalidTerms, decodeWithTerms content form.Reference link |> Result.map ignore)
        // Never a partial read: the page refuses the link, and says so without its bytes.
        let refused = openAt t0 ("#r=" + link)
        Assert.Equal(Refused(UnreadableLink InvalidTerms), refused)
        let said = describe (UnreadableLink InvalidTerms)
        Assert.DoesNotContain(link, said)
        Assert.False(String.IsNullOrWhiteSpace said)

[<Fact>]
let ``terms with section, page and question starts round-trip, as a property`` () =
    let random = Random 7
    let pick (items: 'a list) = items[random.Next items.Length]

    for case in 1..300 do
        let keys =
            [ for kind in [ 1; 2; 3 ] do
                  for index in 0..4 do
                      if random.Next 3 = 0 && not (kind = 2 && index = 0) then
                          yield kind, index ]

        let starts =
            keys
            |> List.map (fun (kind, index) ->
                let start = minute.AddMinutes(float (random.Next 100000))

                (match kind with
                 | 1 -> SectionStart index
                 | 2 -> PageStart index
                 | _ -> QuestionStart index),
                start)

        let terms =
            { noTerms with
                Locale = pick [ None; Some "fr-FR"; Some "en" ]
                StartedAt = pick [ None; Some minute ]
                ScopeStarts = starts
                SubmittedAt = pick [ None; Some(minute.AddMinutes 9.0) ] }

        Assert.Equal(None, termsProblem terms)
        let payload = GenericEnvelope.invitationWith terms "QTIMED" "1" content Import.IdentifiedGroup (oid 1) (oid 40)

        match decodeWithTerms content form.Reference payload with
        | Ok(_, back) -> Assert.True((back = terms), $"case {case}: %A{terms} came back as %A{back}")
        | Error e -> failwith $"case {case}: %A{e}"

[<Fact>]
let ``after every step through a question-timed survey, the link holds exactly the response's terms and answers`` () =
    let random = Random 11
    let qs = questions content |> List.map snd |> List.toArray

    for seed in 1..20 do
        let mutable now = t0
        let mutable s = Responding(responding (openAt now invitation))

        for step in 1..40 do
            now <- now.AddSeconds(float (random.Next 90))

            let msg =
                match random.Next 4 with
                | 0 -> Turned GenericPaging.Next
                | 1 -> Turned GenericPaging.Back
                | _ ->
                    let q = qs[random.Next qs.Length]
                    Chose(q.Id, choices q |> List.item (random.Next (choices q).Length) |> fst)

            s <- s |> at now |> update msg
            let r = responding s

            match decodeWithTerms content form.Reference (payloadIn (fragment r) |> Result.toOption).Value with
            | Ok(envelope, terms) ->
                Assert.True((envelope.Answers = r.Answers), $"seed {seed} step {step}: answers differ")
                Assert.True((terms = r.Terms), $"seed {seed} step {step}: %A{terms} vs %A{r.Terms}")
            | Error e -> failwith $"seed {seed} step {step}: %A{e}"
