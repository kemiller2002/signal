/// A survey timer's start in the respondent link (DF-SIGNAL-2026-0010,
/// SIG-LINK-015, ARX-013): stamped at first open, kept across reloads,
/// inside the integrity check, a whole UTC minute, and nothing else added to
/// an anonymous link. An enforced timer that runs out holds the answers.
module Echelon.Signal.Tests.TimerStartTests

open System
open System.Buffers.Text
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession

let private timed = SurveyPageTests.timed
let private file = publishedFile "TIMED" "1" timed |> snd
let private form = GenericImport.formOf "TIMED" "1" timed
let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private opened = DateTimeOffset(2026, 3, 4, 9, 15, 42, TimeSpan.Zero)
let private ids = questions timed |> List.map (fun (_, q) -> q.Id)

let private openAt (now: DateTimeOffset) (hash: string) = start hash |> receivedAt now (Found file)

let private responding =
    function
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private invitation mode = "#r=" + GenericEnvelope.invitation "TIMED" "1" timed mode (oid 1) (oid 40)
let private first id = choices (tryQuestion timed id).Value |> List.head |> fst

[<Fact>]
let ``a timed survey opened without a start is stamped with the minute it was opened`` () =
    let r = responding (openAt opened (invitation Import.IdentifiedGroup))
    Assert.Equal(Some(DateTimeOffset(2026, 3, 4, 9, 15, 0, TimeSpan.Zero)), r.Terms.StartedAt)
    // The link now carries it: format 2.
    let payload = (payloadIn (fragment r) |> Result.toOption).Value
    Assert.Equal(2uy, Base64Url.DecodeFromChars(payload.AsSpan())[0])

[<Fact>]
let ``a reload keeps the start, however much later it is`` () =
    let r = responding (openAt opened (invitation Import.IdentifiedGroup))
    let later = responding (openAt (opened.AddMinutes 5.0) (fragment r))
    Assert.Equal(r.Terms.StartedAt, later.Terms.StartedAt)
    Assert.Equal(fragment r, fragment later)

[<Fact>]
let ``an untimed survey's link gains nothing`` () =
    let demo = SurveyPageTests.demo
    let link = "#r=" + GenericEnvelope.invitation "DEMO" "1" demo Import.IdentifiedGroup (oid 1) (oid 40)
    let r = responding (start link |> receivedAt opened (Found(publishedFile "DEMO" "1" demo |> snd)))
    Assert.Equal(None, r.Terms.StartedAt)
    Assert.Equal(link, fragment r)

[<Fact>]
let ``an edited start is refused by the integrity check`` () =
    let r = responding (openAt opened (invitation Import.IdentifiedGroup))
    let payload = (payloadIn (fragment r) |> Result.toOption).Value
    let bytes = Base64Url.DecodeFromChars(payload.AsSpan())
    // The start minute is the four bytes after the flags (no locale, no expiry).
    let startAt = 2 + 8 + 32 + 1
    bytes[startAt + 3] <- bytes[startAt + 3] - 30uy
    let tampered = "#r=" + Base64Url.EncodeToString(ReadOnlySpan bytes)
    Assert.Equal(Refused(UnreadableLink IntegrityFailed), openAt opened tampered)

[<Fact>]
let ``an enforced timer that runs out holds every answer, and the page says so`` () =
    let s = Responding(responding (openAt opened (invitation Import.IdentifiedGroup)))
    let s = s |> update (Ticked(opened.AddMinutes 3.0)) |> update (Chose(ids[0], first ids[0]))
    Assert.True((responding s).Answers.ContainsKey ids[0])
    Assert.Contains("Answer by 4 March 2026, 09:25 UTC", GenericTimer.notice timed (responding s).Terms.StartedAt (responding s).Now)

    let nearly = s |> update (Ticked(opened.AddMinutes 8.5))
    Assert.Contains("nearly up", GenericTimer.notice timed (responding nearly).Terms.StartedAt (responding nearly).Now)

    let late = s |> update (Ticked(opened.AddMinutes 11.0))
    let after = late |> update (Chose(ids[1], first ids[1])) |> update (Chose(ids[0], choices (tryQuestion timed ids[0]).Value |> List.item 1 |> fst))
    Assert.True((responding late).Answers = (responding after).Answers)
    Assert.Contains("Time is up", GenericTimer.notice timed (responding after).Terms.StartedAt (responding after).Now)

[<Fact>]
let ``an advisory timer only says the time ran out`` () =
    let advisory = { timed with Presentation = { timed.Presentation with Timers = [ { timed.Presentation.Timers.Head with Enforcement = Timers.Advisory } ] } }
    let link = "#r=" + GenericEnvelope.invitation "ADV" "1" advisory Import.IdentifiedGroup (oid 1) (oid 40)
    let s = start link |> receivedAt opened (Found(publishedFile "ADV" "1" advisory |> snd)) |> update (Ticked(opened.AddHours 1.0))
    let s = update (Chose(ids[0], first ids[0])) s
    Assert.True((responding s).Answers.ContainsKey ids[0])
    Assert.Contains("You can still finish", GenericTimer.notice advisory (responding s).Terms.StartedAt (responding s).Now)

[<Fact>]
let ``an anonymous link gains only the start minute, and so does its submission`` () =
    let r = responding (openAt opened (invitation Import.AnonymousGroup))
    Assert.Equal({ GenericEnvelope.noTerms with StartedAt = Some(GenericEnvelope.startMinute opened) }, r.Terms)
    let s = ids |> List.fold (fun s id -> update (Chose(id, first id)) s) (Responding r)

    match update (SubmitRequested(Array.init 16 (fun i -> byte (90 + i)))) s with
    | Responding submitted when submitted.Phase = Submitted ->
        match payloadIn (fragment submitted) |> Result.bind (GenericEnvelope.decodeWithTerms timed form.Reference >> Result.mapError UnreadableLink) with
        | Ok(envelope, terms) ->
            Assert.Equal({ GenericEnvelope.noTerms with StartedAt = Some(GenericEnvelope.startMinute opened) }, terms)
            Assert.True(match envelope.Binding with Anonymous _ -> true | _ -> false)
        | Error error -> failwith $"%A{error}"
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a start that is not a whole minute cannot be written`` () =
    Assert.True((GenericEnvelope.termsProblem { GenericEnvelope.noTerms with StartedAt = Some opened }).IsSome)
    Assert.True((GenericEnvelope.termsProblem { GenericEnvelope.noTerms with StartedAt = Some(GenericEnvelope.startMinute opened) }).IsNone)
