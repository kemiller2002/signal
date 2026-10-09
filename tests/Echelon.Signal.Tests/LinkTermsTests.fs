/// Locale and invitation expiry in respondent links (VER-003,
/// DF-SIGNAL-2026-0006, SIG-LINK-013): link format 2 carries them inside the
/// integrity check; format 1 links are unchanged; expiry is enforced on the
/// survey page and at intake; locale is presentation only.
module Echelon.Signal.Tests.LinkTermsTests

open System
open System.Buffers.Text
open System.Text.Json.Nodes
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericEnvelope
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Tests.Support

let private demo = SurveyPageTests.demo
let private survey, version = SurveyPageTests.DemoSurvey, SurveyPageTests.DemoVersion
let private form = GenericImport.formOf survey version demo
let private bytes = publishedFile survey version demo |> snd
let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private instance, group = oid 1, oid 40
let private lastDay = DateOnly(2026, 3, 31)
let private terms = { Locale = Some "fr-CA"; ExpiresOn = Some lastDay }

let private link (t: Terms) = invitationWith t survey version demo Import.IdentifiedGroup instance group
let private raw (text: string) = Base64Url.DecodeFromChars(text.AsSpan())
let private reseal (body: byte[]) = Base64Url.EncodeToString(ReadOnlySpan(Array.append body (checksum (ReadOnlySpan body))))

[<Fact>]
let ``a link without terms is format 1, byte for byte, and committed links read as before`` () =
    Assert.Equal(invitation survey version demo Import.IdentifiedGroup instance group, link noTerms)
    Assert.Equal(1uy, (raw (link noTerms))[0])

    let fixtures = JsonNode.Parse(readRepoFile "tests/browser/fixtures/survey-links.json")

    for name in [ "identified"; "anonymous"; "test" ] do
        let payload = fixtures[name].GetValue<string>().Split("#r=")[1]
        Assert.Equal(1uy, (raw payload)[0])

        match decodeWithTerms form.Content form.Reference payload with
        | Ok(envelope, read) ->
            Assert.Equal(noTerms, read)
            // Re-encoding gives the committed link back: nothing about format 1 moved.
            Assert.Equal(payload, encode form.Content form.Reference envelope)
        | Error e -> failwith $"{name}: %A{e}"

[<Fact>]
let ``format 2 carries the locale and the expiry day, and round-trips`` () =
    let text = link terms
    Assert.Equal(2uy, (raw text)[0])
    Assert.Equal(Ok terms, decodeWithTerms form.Content form.Reference text |> Result.map snd)
    Assert.Equal(Ok form.Reference, referenceIn text)

    for only in [ { noTerms with Locale = Some "en" }; { noTerms with ExpiresOn = Some lastDay } ] do
        Assert.Equal(Ok only, decodeWithTerms form.Content form.Reference (link only) |> Result.map snd)

[<Fact>]
let ``editing the expiry invalidates the link, and only the canonical spelling is read`` () =
    let body = (raw (link terms))[.. raw(link terms).Length - 5]
    // The expiry day is the last two bytes before the item count.
    let expiryAt = 2 + 8 + 32 + 1 + 1 + 5
    let edited = Array.copy (raw (link terms))
    edited[expiryAt + 1] <- edited[expiryAt + 1] + 1uy
    Assert.Equal(Error IntegrityFailed, decodeWithTerms form.Content form.Reference (Base64Url.EncodeToString(ReadOnlySpan edited)) |> Result.map ignore)

    let forged (change: byte[] -> unit) =
        let copy = Array.copy body
        change copy
        decodeWithTerms form.Content form.Reference (reseal copy) |> Result.map ignore

    let flagsAt = 2 + 8 + 32
    Assert.Equal(Error InvalidTerms, forged (fun b -> b[flagsAt] <- 0x07uy))
    Assert.Equal(Error InvalidTerms, forged (fun b -> b[flagsAt + 2] <- byte '!'))
    Assert.Equal(Error InvalidTerms, forged (fun b -> b[flagsAt + 1] <- 30uy))
    // Format 2 with no terms at all is not a spelling anyone writes.
    let empty = Array.concat [ body[.. flagsAt - 1]; [| 0uy |]; body[flagsAt + 1 + 1 + 5 + 2 ..] ]
    Assert.Equal(Error InvalidTerms, decodeWithTerms form.Content form.Reference (reseal empty) |> Result.map ignore)

    Assert.True((termsProblem { noTerms with Locale = Some "not a tag" }).IsSome)
    Assert.True((termsProblem { noTerms with Locale = Some(String('a', 2) + "-" + String('b', 30)) }).IsSome)
    Assert.True((termsProblem terms).IsNone)

// ---- Intake -----------------------------------------------------------------------------------

let private definition: Import.GroupDefinition =
    { Group = group
      Mode = Import.IdentifiedGroup
      ExpectedCount = 1
      Template = GenericImport.shapeOf survey version demo
      Generic = Some form }

let private submittedWith (t: Terms) =
    match start ("#r=" + link t) |> received lastDay (Found bytes) with
    | Responding r ->
        let answered =
            Template.questions demo
            |> List.map snd
            |> List.fold (fun session q -> update (Chose(q.Id, fst (choices q).Head)) session) (Responding r)

        match update (SubmitRequested(Array.zeroCreate 16)) answered with
        | Responding s when s.Phase = Submitted -> "https://signal.example/web/survey/" + fragment s
        | other -> failwith $"%A{other}"
    | other -> failwith $"%A{other}"

[<Fact>]
let ``intake refuses a submission whose invitation has expired, with a stable code`` () =
    let submission = submittedWith terms
    Assert.Equal(Import.Rejected(Import.InvitationExpired lastDay), GenericImport.evaluateOn (lastDay.AddDays 1) definition (fun _ -> None) submission)
    Assert.Equal("rejected:invitation-expired", Import.outcomeCode (GenericImport.evaluateOn (lastDay.AddDays 1) definition (fun _ -> None) submission))
    // The last day itself is still in time; re-reading an accepted submission ignores expiry.
    Assert.True(match GenericImport.evaluateOn lastDay definition (fun _ -> None) submission with Import.Accepted _ -> true | _ -> false)
    Assert.True(match GenericImport.evaluate definition (fun _ -> None) submission with Import.Accepted _ -> true | _ -> false)

[<Fact>]
let ``locale never changes scoring or identity`` () =
    let accepted t =
        match GenericImport.evaluateOn lastDay definition (fun _ -> None) (submittedWith t) with
        | Import.Accepted r -> r
        | other -> failwith $"%A{other}"

    let french, english, none = accepted terms, accepted { terms with Locale = Some "en-GB" }, accepted noTerms
    Assert.Equal(french.Identity, english.Identity)
    Assert.Equal(french.Identity, none.Identity)
    Assert.True((french.Dimensions = english.Dimensions))
    Assert.True((french.Dimensions = none.Dimensions))

// ---- The survey page --------------------------------------------------------------------------

let private lang session =
    GenericSessionView.view session |> List.pick (fun (k, v) -> if k = "lang" then Some v else None)

[<Fact>]
let ``an expired invitation cannot be filled in, and says so`` () =
    match start ("#r=" + link terms) |> received (lastDay.AddDays 1) (Found bytes) with
    | Refused(InvitationExpired day) ->
        Assert.Equal(lastDay, day)
        Assert.StartsWith("This invitation has expired.", describe (InvitationExpired day))
    | other -> failwith $"%A{other}"

    // A link already submitted still opens, sealed, to be sent on.
    let submitted = (submittedWith terms).Split("#r=")[1]

    match start ("#r=" + submitted) |> received (lastDay.AddDays 30) (Found bytes) with
    | Responding r -> Assert.Equal(Submitted, r.Phase)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``the locale sets the page's language, and the terms stay in the link as it changes`` () =
    let session = start ("#r=" + link terms) |> received lastDay (Found bytes)
    Assert.Equal(View.Value(View.Text "fr-CA"), lang session)
    Assert.Equal(View.Value(View.Text "en"), lang (start ("#r=" + link noTerms) |> received lastDay (Found bytes)))

    match update (Chose("CTX-001", Value(Flag true))) session with
    | Responding r ->
        Assert.Equal(Ok terms, decodeWithTerms form.Content form.Reference ((fragment r).Substring 3) |> Result.map snd)
    | other -> failwith $"%A{other}"
