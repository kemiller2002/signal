/// The template hash and the portable response envelope (WI-0031):
/// VER-002, ARX-006, LURL-004, ANS-004, CAN-004, ACR-004, URLC-003.
module Echelon.Signal.Tests.UrlStateTests

open System
open System.Buffers.Text
open System.Security.Cryptography
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState

let private pilot = Pilot.assessment

let private id (seed: byte) = (OpaqueId.ofBytes (Array.init 16 (fun i -> seed + byte i))).Value

let private bindings =
    [ Unbound
      Invitation(id 1uy, id 40uy)
      Identified(id 2uy, id 40uy)
      Anonymous(id 3uy, id 40uy) ]

/// A deterministic stream of valid answer states (seeded, so a failure
/// reproduces): each item unanswered or any answer in the domain.
let private randomAnswers (random: Random) : Answers =
    pilot.Items
    |> List.choose (fun item ->
        let state = random.Next(answerDomain.Length + 1)
        if state = 0 then None else Some(item.Id, answerDomain[state - 1]))
    |> Map.ofList

let private bytesOf (text: string) = Base64Url.DecodeFromChars(text.AsSpan())

/// Re-signs a tampered body so the test reaches the check after integrity.
let private resign (body: byte[]) =
    let check = SHA256.HashData(body).AsSpan(0, IntegrityLength).ToArray()
    Base64Url.EncodeToString(ReadOnlySpan(Array.append body check))

let private bodyOf (text: string) =
    let bytes = bytesOf text
    bytes[.. bytes.Length - IntegrityLength - 1]

// ---------------------------------------------------------------------------
// Template hash (VER-002, ARX-006).
// ---------------------------------------------------------------------------

[<Fact>]
let ``the template hash is a pinned golden vector`` () =
    // Changing the pilot, the answer domain or the canonical form changes
    // this value; that is a new template version, never an edit.
    Assert.Equal(
        "sha256:95def6fd513336f0618dc527068b65ed97d16f08c4d89ea38297c32e77ea6529",
        Canonical.templateHash pilot
    )

[<Fact>]
let ``the canonical form is deterministic and sensitive to every interpretive field`` () =
    Assert.Equal<byte[]>(Canonical.bytes pilot, Canonical.bytes pilot)

    let variants =
        [ { pilot with Version = "0.1.1-draft" }
          { pilot with MinimumNumericAnswers = 2 }
          { pilot with Items = List.rev pilot.Items }
          { pilot with Dimensions = pilot.Dimensions |> List.map (fun d -> { d with Label = d.Label + " " }) }
          { pilot with Items = pilot.Items |> List.mapi (fun i item -> if i = 0 then { item with Prompt = item.Prompt + "." } else item) } ]

    for variant in variants do
        Assert.NotEqual<string>(Canonical.templateHash pilot, Canonical.templateHash variant)

[<Fact>]
let ``the template reference is the hash prefix and names only its template`` () =
    let reference = Canonical.reference pilot
    Assert.Equal(Canonical.ReferenceLength, reference.Length)
    Assert.True(Canonical.matches pilot reference)
    Assert.False(Canonical.matches { pilot with Version = "other" } reference)

// ---------------------------------------------------------------------------
// Round trip (LURL-004 §33, ANS-004).
// ---------------------------------------------------------------------------

[<Fact>]
let ``every valid response state round-trips, for every binding`` () =
    let random = Random 20261007

    for _ in 1..500 do
        for binding in bindings do
            let envelope = { Binding = binding; Answers = randomAnswers random }
            let text = encode pilot envelope
            Assert.Equal(Ok envelope, decode pilot text)
            // Deterministic: the same state always encodes the same way.
            Assert.Equal(text, encode pilot envelope)

[<Fact>]
let ``the encoding layout is a pinned golden vector`` () =
    // Reproduced independently of the F# encoder from the layout in
    // UrlState.fs: [1][0][hash[0..7]][0 15][0x15 0 0 0 0 0 0 0x80][sha256[0..3]].
    let answers = Map.ofList [ "CORE-001", Rated Never; "CORE-002", Rated AlmostAlways; "CORE-015", Withheld NotApplicable ]
    let golden = "AQCV3vb9UTM28AAPFQAAAAAAAIC_qXED"
    Assert.Equal(golden, encode pilot { Binding = Unbound; Answers = answers })
    Assert.Equal(Ok { Binding = Unbound; Answers = answers }, decode pilot golden)

[<Fact>]
let ``the empty and the complete states round-trip, and every answer is distinct`` () =
    for answer in answerDomain do
        let all = pilot.Items |> List.map (fun item -> item.Id, answer) |> Map.ofList
        Assert.Equal(Ok { Binding = Unbound; Answers = all }, decode pilot (encode pilot { Binding = Unbound; Answers = all }))

    Assert.Equal(Ok { Binding = Unbound; Answers = Map.empty }, decode pilot (encode pilot { Binding = Unbound; Answers = Map.empty }))
    // Unanswered, don't know, not observed and not applicable stay distinct.
    let states = answerDomain |> List.map (fun a -> encode pilot { Binding = Unbound; Answers = Map.ofList [ "CORE-001", a ] })
    Assert.Equal(answerDomain.Length, List.distinct states |> List.length)

[<Fact>]
let ``the payload is compact: four bits an item and a fixed header`` () =
    Assert.Equal(4, bitsPerItem)
    let text = encode pilot { Binding = Unbound; Answers = Map.empty }
    // 1 version + 1 kind + 8 reference + 2 count + 8 packed (15 x 4 bits) + 4 check.
    Assert.Equal(24, (bytesOf text).Length)
    Assert.Equal(32, text.Length)
    let anonymous = encode pilot { Binding = Anonymous(id 3uy, id 40uy); Answers = Map.empty }
    Assert.Equal(24 + 32, (bytesOf anonymous).Length)
    Assert.True(text |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_'), "URL-safe alphabet")

[<Fact>]
let ``the payload carries no question ids, labels or results`` () =
    let all = pilot.Items |> List.map (fun item -> item.Id, Rated Often) |> Map.ofList
    let bytes = bytesOf (encode pilot { Binding = Unbound; Answers = all })
    let text = Text.Encoding.ASCII.GetString bytes
    Assert.DoesNotContain("CORE", text)
    Assert.DoesNotContain("D01", text)

// ---------------------------------------------------------------------------
// Explicit failure (URLC-003 §6, ACR-004).
// ---------------------------------------------------------------------------

let private sample = encode pilot { Binding = Invitation(id 1uy, id 40uy); Answers = Map.ofList [ "CORE-003", Rated Often; "CORE-015", Withheld DontKnow ] }

[<Fact>]
let ``text that is not canonical unpadded base64url is refused`` () =
    for bad in [ ""; "a"; sample + "="; "+" + sample[1..]; "abc def"; sample + "!" ] do
        Assert.Equal(Error NotBase64Url, decode pilot bad)

[<Fact>]
let ``an unsupported encoding version is named`` () =
    let bytes = bytesOf sample
    bytes[0] <- 2uy
    Assert.Equal(Error(UnsupportedVersion 2), decode pilot (Base64Url.EncodeToString(ReadOnlySpan bytes)))

[<Fact>]
let ``every single-byte corruption and every truncation is detected`` () =
    let bytes = bytesOf sample

    for index in 0 .. bytes.Length - 1 do
        let damaged = Array.copy bytes
        damaged[index] <- damaged[index] ^^^ 0x10uy

        match decode pilot (Base64Url.EncodeToString(ReadOnlySpan damaged)) with
        | Error _ -> ()
        | Ok _ -> failwith $"corruption at byte {index} was not detected"

    for length in 0 .. bytes.Length - 1 do
        match decode pilot (Base64Url.EncodeToString(ReadOnlySpan bytes[.. length - 1])) with
        | Error _ -> ()
        | Ok _ -> failwith $"truncation to {length} bytes was not detected"

    let damaged = Array.copy bytes
    damaged[20] <- damaged[20] ^^^ 0x01uy
    Assert.Equal(Error IntegrityFailed, decode pilot (Base64Url.EncodeToString(ReadOnlySpan damaged)))
    Assert.Equal(Error(Truncated(minimumLength, 5)), decode pilot (Base64Url.EncodeToString(ReadOnlySpan bytes[..4])))

[<Fact>]
let ``a payload for another template is a template mismatch, not corruption`` () =
    let other = { pilot with Version = "0.2.0" }
    Assert.Equal(Error TemplateMismatch, decode other sample)

[<Fact>]
let ``impossible content behind a valid checksum is refused precisely`` () =
    let body = bodyOf sample
    let countAt = 2 + Canonical.ReferenceLength + 2 * IdLength

    let kind = Array.copy body
    kind[1] <- 9uy
    Assert.Equal(Error(UnknownBinding 9), decode pilot (resign kind))

    let count = Array.copy body
    count[countAt + 1] <- 14uy
    Assert.Equal(Error(CardinalityMismatch(15, 14)), decode pilot (resign count))

    Assert.Equal(Error(LengthMismatch(8, 7)), decode pilot (resign body[.. body.Length - 2]))
    Assert.Equal(Error(LengthMismatch(8, 9)), decode pilot (resign (Array.append body [| 0uy |])))

    let state = Array.copy body
    state[countAt + 2] <- 0xF0uy // item 0 -> state 15; the domain has 8 answers
    Assert.Equal(Error(InvalidAnswerState(0, 15)), decode pilot (resign state))

    let padding = Array.copy body
    padding[body.Length - 1] <- padding[body.Length - 1] ||| 0x01uy // bit 63 is unused
    Assert.Equal(Error NonCanonicalPadding, decode pilot (resign padding))

    let header = body[.. countAt - 10]
    Assert.Equal(Error(Truncated(countAt + 2 + IntegrityLength, header.Length + IntegrityLength)), decode pilot (resign header))

[<Fact>]
let ``every decode error has a safe description that never echoes the payload`` () =
    let errors =
        [ NotBase64Url; Truncated(1, 0); UnsupportedVersion 9; IntegrityFailed; UnknownBinding 7; TemplateMismatch
          CardinalityMismatch(1, 2); LengthMismatch(1, 2); InvalidAnswerState(0, 9); NonCanonicalPadding ]

    for error in errors do
        let text = describe error
        Assert.False(String.IsNullOrWhiteSpace text)
        Assert.DoesNotContain(sample, text)

[<Fact>]
let ``opaque ids are exactly sixteen bytes and read back only from their canonical form`` () =
    Assert.True((OpaqueId.ofBytes (Array.zeroCreate 15)).IsNone)
    let value = id 7uy
    Assert.Equal(Some value, OpaqueId.tryParse (string value))
    Assert.Equal(None, OpaqueId.tryParse ((string value)[..20] + "B"))
    Assert.Equal(None, OpaqueId.tryParse "not an id")
