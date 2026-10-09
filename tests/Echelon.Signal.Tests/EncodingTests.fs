/// The generic URL envelope (WI-0045): CAN-004, ANS-004, SCS-016, LURL-004,
/// ID-001.
module Echelon.Signal.Tests.EncodingTests

open System
open System.Buffers.Text
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Tests.PrimitivesTests

let private question (id: string) (def: AnswerDefinition) (preset: SelectorPreset) : Question =
    { Id = id
      Prompt = $"Prompt {id}"
      HelpText = None
      Answer = def
      Selector = { Preset = preset; Labels = [ for i in 1 .. labelsRequired def -> $"label {i}" ] }
      SpecialStates = [ DontKnow; Declined ]
      Required = false
      Tags = [] }

let private presets =
    [ YesNo; Likert5; RadioList; CheckboxList; Slider; RangeSlider; RankingList; RankingList; ConstantSum; BestWorstSet; CascadingSelect; TreeMultiSelect ]

/// One question of every primitive.
let mixed: Content =
    { Pilot.content with
        Sections =
            [ { Pilot.content.Sections.Head with
                  Id = "mixed"
                  Scoring = None
                  Questions = List.zip catalog presets |> List.mapi (fun i (def, preset) -> question $"q{i}" def preset) } ] }

let private reference = GenericEnvelope.referenceOf "mixed" "1" mixed
let private oid (n: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (n + i)))).Value

let private bindings =
    [ Unbound
      IdentifiedInvitation(oid 1, oid 2)
      AnonymousInvitation(oid 3, oid 4)
      Identified(oid 5, oid 6)
      Anonymous(oid 7, oid 8) ]

let private randomAnswers (random: Random) (content: Content) : Answers =
    questions content
    |> List.choose (fun (_, q) ->
        match random.Next 5 with
        | 0 -> None
        | 1 -> Some(q.Id, Special q.SpecialStates[random.Next q.SpecialStates.Length])
        | _ ->
            // A random valid value: draw indices until one passes the rules.
            let count = valueCount q.Answer

            Seq.initInfinite (fun _ -> ofIndex q.Answer (uint64 (random.NextInt64(int64 count))))
            |> Seq.choose id
            |> Seq.tryFind (fun v -> (Primitives.check q.Answer v).IsNone)
            |> Option.map (fun v -> q.Id, Value v))
    |> Map.ofList

[<Fact>]
let ``every primitive round-trips through the URL for every binding (500 seeded samples)`` () =
    let random = Random 45

    for i in 1..500 do
        let envelope: GenericEnvelope.Envelope = { Binding = bindings[i % bindings.Length]; Answers = randomAnswers random mixed }
        let text = GenericEnvelope.encode mixed reference envelope

        match GenericEnvelope.decode mixed reference text with
        | Ok decoded ->
            Assert.Equal<Answers>(envelope.Answers, decoded.Answers)
            Assert.Equal(sprintf "%A" envelope.Binding, sprintf "%A" decoded.Binding)
        | Error e -> failwith $"%A{e}"

[<Fact>]
let ``the generic layout is bit for bit the SDRA codec's (1000 samples)`` () =
    let random = Random 1
    let domain = Assessment.answerDomain |> List.toArray
    let sdraReference = GenericEnvelope.referenceOf "SDRA" "1" Pilot.content

    for i in 1..1000 do
        let answers =
            Pilot.assessment.Items
            |> List.choose (fun item -> if random.Next 6 = 0 then None else Some(item.Id, domain[random.Next domain.Length]))
            |> Map.ofList

        let binding = bindings[i % bindings.Length]
        let legacy = Base64Url.DecodeFromChars((UrlState.encode Pilot.assessment { Binding = binding; Answers = answers }).AsSpan())

        let generic =
            Base64Url.DecodeFromChars((GenericEnvelope.encode Pilot.content sdraReference { Binding = binding; Answers = Pilot.answers answers }).AsSpan())

        // Same header kind, ids, count and packed answers; only the template
        // reference (and so the checksum) differs.
        Assert.Equal<byte[]>(legacy[..1], generic[..1])
        Assert.Equal<byte[]>(legacy[10 .. legacy.Length - 5], generic[10 .. generic.Length - 5])

[<Fact>]
let ``capacity is the length of the largest real envelope`` () =
    let envelope: GenericEnvelope.Envelope = { Binding = Anonymous(oid 1, oid 2); Answers = Map.empty }
    let longest: GenericEnvelope.Terms = { Locale = Some "sgn-Latn-ABCDEFGH-123456"; ExpiresOn = Some(System.DateOnly(2030, 1, 1)) }
    Assert.Equal((Layout.capacity mixed).EncodedCharacters, (GenericEnvelope.encodeWith mixed reference longest envelope).Length)

[<Fact>]
let ``decoding fails explicitly: corruption, another template, impossible states`` () =
    let text = GenericEnvelope.encode mixed reference { Binding = Unbound; Answers = Map.empty }
    let corrupted = text.Substring(0, text.Length - 2) + (if text.EndsWith "AA" then "BA" else "AA")
    Assert.Equal(Error IntegrityFailed, GenericEnvelope.decode mixed reference corrupted |> Result.map ignore)
    let other = GenericEnvelope.referenceOf "mixed" "2" mixed
    Assert.Equal(Error TemplateMismatch, GenericEnvelope.decode mixed other text |> Result.map ignore)

    // A slot holding a state past the question's states: q0 (Boolean with
    // two specials) has 5 states in a 3-bit slot; write 7 and re-seal.
    let bytes = Base64Url.DecodeFromChars(text.AsSpan())
    let body = bytes[.. bytes.Length - 5]
    let header = 2 + 8 + 2
    body[header] <- body[header] ||| 0b1110_0000uy
    let sealed' = Array.append body (checksum (ReadOnlySpan body))
    let forged = Base64Url.EncodeToString(ReadOnlySpan sealed')
    Assert.Equal(Error(InvalidAnswerState(0, 7)), GenericEnvelope.decode mixed reference forged |> Result.map ignore)

[<Fact>]
let ``an invitation is bound to one exact template version`` () =
    let invitation = GenericEnvelope.invitation "mixed" "1" mixed Import.AnonymousGroup (oid 1) (oid 2)

    match GenericEnvelope.decode mixed reference invitation with
    | Ok e -> Assert.Equal(sprintf "%A" (AnonymousInvitation(oid 1, oid 2)), sprintf "%A" e.Binding)
    | Error e -> failwith $"%A{e}"

    Assert.Equal(Error TemplateMismatch, GenericEnvelope.decode mixed (GenericEnvelope.referenceOf "mixed" "2" mixed) invitation |> Result.map ignore)
