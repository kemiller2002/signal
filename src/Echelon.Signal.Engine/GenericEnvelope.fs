/// The URL answer envelope for generic templates (CAN-004, ANS-004, SCS-016,
/// LURL-004, ID-001): ResponseEncodingVersion 1 with a slot per question
/// whose width follows that question's own cardinality.
///
/// The byte layout is `UrlState`'s exactly (version, binding kind, 8-byte
/// template reference, binding ids, 2-byte item count, MSB-first packed
/// slots, 4-byte integrity check, unpadded base64url); only the slot widths
/// differ. Slot state 0 is unanswered, states 1..n are the primitive's values
/// in `Primitives.toIndex` order, and the offered special states follow in
/// canonical order. For the SDRA template this layout is bit-for-bit the
/// `UrlState` layout (a test holds them equal).
///
/// Decoding fails explicitly and never guesses: a slot holding a state the
/// question does not offer, or a value that breaks the primitive's rules, is
/// an error, not a repair.
module Echelon.Signal.Engine.GenericEnvelope

open System
open System.Buffers.Text
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Layout
open Echelon.Signal.Engine.UrlState

[<NoComparison>]
type Envelope = { Binding: Binding; Answers: Answers }

/// The reference a URL carries for a published template version.
let referenceOf (surveyId: string) (version: string) (content: Content) = TemplateCanonical.reference surveyId version content

let private stateOf (q: Question) (state: AnswerState option) : uint64 =
    match state with
    | None -> 0UL
    | Some(Value v) ->
        match toIndex q.Answer v with
        | Some i -> 1UL + i
        | None -> 0UL
    | Some(Special s) ->
        match q.SpecialStates |> List.tryFindIndex ((=) s) with
        | Some i -> 1UL + valueCount q.Answer + uint64 i
        | None -> 0UL

let private answerOf (q: Question) (state: uint64) : Result<AnswerState option, unit> =
    let values = valueCount q.Answer

    if state = 0UL then
        Ok None
    elif state <= values then
        match ofIndex q.Answer (state - 1UL) with
        | Some v when (Primitives.check q.Answer v).IsNone -> Ok(Some(Value v))
        | _ -> Error()
    elif state <= values + uint64 q.SpecialStates.Length then
        Ok(Some(Special q.SpecialStates[int (state - values - 1UL)]))
    else
        Error()

/// The binding kind without the test marker: generic envelopes carry test
/// and preview artifacts in the kind byte's high bit (AUT-006 §60).
let private baseKind (kind: byte) = kind &&& ~~~TestKindFlag

let private packedLength (slots: Slot list) = ((slots |> List.sumBy _.Bits) + 7) / 8

/// Encodes an envelope. Answers the template cannot interpret (unknown
/// questions, invalid values) are not state, and are not written; check
/// answers before finalizing.
let encode (content: Content) (reference: byte[]) (envelope: Envelope) : string =
    let slots = layout content
    let qs = questions content |> List.map snd
    let packed = Array.zeroCreate<byte> (packedLength slots)

    List.zip qs slots
    |> List.fold
        (fun offset (q, slot) ->
            let state = stateOf q (envelope.Answers.TryFind q.Id)

            for b in 0 .. slot.Bits - 1 do
                if (state >>> (slot.Bits - 1 - b)) &&& 1UL = 1UL then
                    let position = offset + b
                    packed[position / 8] <- packed[position / 8] ||| (0x80uy >>> (position % 8))

            offset + slot.Bits)
        0
    |> ignore

    let body =
        [| yield byte ResponseEncodingVersion
           yield kindOf envelope.Binding
           yield! reference
           for id in idsOf envelope.Binding do
               yield! OpaqueId.toBytes id
           yield byte (qs.Length >>> 8)
           yield byte (qs.Length &&& 0xFF)
           yield! packed |]

    Base64Url.EncodeToString(ReadOnlySpan(Array.append body (checksum (ReadOnlySpan body))))

/// Reads an envelope against the template the caller resolved, in
/// `UrlState`'s order: alphabet, length, version, integrity, binding,
/// template, cardinality, length, states, padding.
let decode (content: Content) (reference: byte[]) (text: string) : Result<Envelope, DecodeError> =
    let referenceLength = TemplateCanonical.ReferenceLength
    let minimum = 2 + referenceLength + 2 + IntegrityLength

    match tryFromBase64Url text with
    | None -> Error NotBase64Url
    | Some bytes when bytes.Length >= 1 && int bytes[0] <> ResponseEncodingVersion -> Error(UnsupportedVersion(int bytes[0]))
    | Some bytes when bytes.Length < minimum -> Error(Truncated(minimum, bytes.Length))
    | Some bytes ->
        let body = bytes[.. bytes.Length - IntegrityLength - 1]

        if checksum (ReadOnlySpan body) <> bytes[bytes.Length - IntegrityLength ..] then
            Error IntegrityFailed
        else
            match idCount (baseKind bytes[1]) with
            | None -> Error(UnknownBinding(int bytes[1]))
            | Some ids ->
                let header = 2 + referenceLength + ids * IdLength + 2

                if body.Length < header then
                    Error(Truncated(header + IntegrityLength, bytes.Length))
                elif body[2 .. 1 + referenceLength] <> reference then
                    Error TemplateMismatch
                else
                    let idAt i =
                        let start = 2 + referenceLength + i * IdLength
                        (OpaqueId.ofBytes body[start .. start + IdLength - 1]).Value

                    let binding =
                        let marked binding = if bytes[1] &&& TestKindFlag = TestKindFlag then asTest binding else binding

                        match baseKind bytes[1] with
                        | 1uy -> IdentifiedInvitation(idAt 0, idAt 1)
                        | 4uy -> AnonymousInvitation(idAt 0, idAt 1)
                        | 2uy -> Identified(idAt 0, idAt 1)
                        | 3uy -> Anonymous(idAt 0, idAt 1)
                        | _ -> Unbound
                        |> marked

                    let slots = layout content
                    let qs = questions content |> List.map snd
                    let count = (int body[header - 2] <<< 8) ||| int body[header - 1]
                    let packed = body[header..]

                    if count <> qs.Length then
                        Error(CardinalityMismatch(qs.Length, count))
                    elif packed.Length <> packedLength slots then
                        Error(LengthMismatch(packedLength slots, packed.Length))
                    else
                        let bit position = (packed[position / 8] >>> (7 - position % 8)) &&& 1uy |> uint64

                        let read offset bits =
                            [ 0 .. bits - 1 ] |> List.fold (fun acc b -> (acc <<< 1) ||| bit (offset + b)) 0UL

                        let decoded, used =
                            List.zip qs slots
                            |> List.indexed
                            |> List.fold
                                (fun (acc: Result<(string * AnswerState) list, DecodeError>, offset) (index, (q, slot)) ->
                                    let state = read offset slot.Bits

                                    let acc =
                                        acc
                                        |> Result.bind (fun pairs ->
                                            match answerOf q state with
                                            | Ok None -> Ok pairs
                                            | Ok(Some a) -> Ok((q.Id, a) :: pairs)
                                            | Error() -> Error(InvalidAnswerState(index, int (min state (uint64 Int32.MaxValue)))))

                                    acc, offset + slot.Bits)
                                (Ok [], 0)

                        match decoded with
                        | Error e -> Error e
                        | Ok _ when [ used .. packed.Length * 8 - 1 ] |> List.exists (fun p -> bit p = 1UL) -> Error NonCanonicalPadding
                        | Ok pairs -> Ok { Binding = binding; Answers = Map.ofList pairs }

/// The template reference an envelope names, read before the template is
/// known (DF-SIGNAL-2026-0005): the respondent page needs it to find the
/// published template. The envelope's alphabet, version, length, integrity
/// and binding are checked first; its answers can only be read with the
/// template (`decode`).
let referenceIn (text: string) : Result<byte[], DecodeError> =
    let referenceLength = TemplateCanonical.ReferenceLength
    let minimum = 2 + referenceLength + 2 + IntegrityLength

    match tryFromBase64Url text with
    | None -> Error NotBase64Url
    | Some bytes when bytes.Length >= 1 && int bytes[0] <> ResponseEncodingVersion -> Error(UnsupportedVersion(int bytes[0]))
    | Some bytes when bytes.Length < minimum -> Error(Truncated(minimum, bytes.Length))
    | Some bytes when checksum (ReadOnlySpan(bytes, 0, bytes.Length - IntegrityLength)) <> bytes[bytes.Length - IntegrityLength ..] ->
        Error IntegrityFailed
    | Some bytes when (idCount (baseKind bytes[1])).IsNone -> Error(UnknownBinding(int bytes[1]))
    | Some bytes -> Ok bytes[2 .. 1 + referenceLength]

/// An invitation bound to one exact published template version (ID-001):
/// the envelope carries the instance and group ids and that version's
/// reference, and nothing about a person.
let invitationBinding (mode: Import.IdentityMode) (instance: OpaqueId) (group: OpaqueId) =
    match mode with
    | Import.IdentifiedGroup -> IdentifiedInvitation(instance, group)
    | Import.AnonymousGroup -> AnonymousInvitation(instance, group)

let invitation (surveyId: string) (version: string) (content: Content) (mode: Import.IdentityMode) (instance: OpaqueId) (group: OpaqueId) =
    encode content (referenceOf surveyId version content) { Binding = invitationBinding mode instance group; Answers = Map.empty }

/// A test link (AUT-006 §§60-61): an invitation marked as a test artifact,
/// optionally carrying sample answers (a fixture's). The survey page says it
/// is a test, and production import refuses what it submits.
let testLink (surveyId: string) (version: string) (content: Content) (mode: Import.IdentityMode) (instance: OpaqueId) (group: OpaqueId) (answers: Answers) =
    encode content (referenceOf surveyId version content) { Binding = asTest (invitationBinding mode instance group); Answers = answers }
