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

// ---- Invitation terms (link format 2, DF-SIGNAL-2026-0006, VER-003) -------------------------

/// The link format that carries invitation terms. A link without terms is
/// written in format 1 exactly as before, so every existing link, golden
/// vector and submission hash is unchanged.
[<Literal>]
let TermsFormatVersion = 2

[<Literal>]
let MaximumLocaleLength = 24

/// The most bytes terms add: flags, the locale's length and text, and the
/// expiry day.
[<Literal>]
let TermsMaximumBytes = 1 + 1 + MaximumLocaleLength + 2

/// What an invitation says beyond its binding. Locale changes presentation
/// only; it never changes scoring or identity. Expiry is the last UTC day
/// the invitation may be answered and imported; both are inside the
/// envelope's integrity check, so editing either invalidates the link.
type Terms =
    { Locale: string option
      ExpiresOn: DateOnly option }

let noTerms = { Locale = None; ExpiresOn = None }

let private epoch = DateOnly(1970, 1, 1)
let private localePattern = Text.RegularExpressions.Regex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}$")

/// Why terms cannot be written, if they cannot.
let termsProblem (terms: Terms) : string option =
    match terms.Locale, terms.ExpiresOn with
    | Some locale, _ when locale.Length > MaximumLocaleLength || not (localePattern.IsMatch locale) ->
        Some $"'{locale}' is not a language tag of at most {MaximumLocaleLength} characters (for example en-GB)."
    | _, Some day when day < epoch || day.DayNumber - epoch.DayNumber > 0xFFFF -> Some $"{day} is outside the dates a link can carry."
    | _ -> None

let private termsBytes (terms: Terms) : byte[] =
    if terms = noTerms then
        [||]
    else
        [| yield (if terms.Locale.IsSome then 1uy else 0uy) ||| (if terms.ExpiresOn.IsSome then 2uy else 0uy)
           match terms.Locale with
           | Some locale ->
               yield byte locale.Length
               yield! Text.Encoding.ASCII.GetBytes locale
           | None -> ()
           match terms.ExpiresOn with
           | Some day ->
               let days = day.DayNumber - epoch.DayNumber
               yield byte (days >>> 8)
               yield byte (days &&& 0xFF)
           | None -> () |]

/// Reads terms at `at`; the terms and where the item count starts. Only the
/// one canonical spelling is read: no empty flags, no unknown flag, a
/// well-formed locale.
let private readTerms (body: byte[]) (at: int) : Result<Terms * int, DecodeError> =
    let within n = at + n <= body.Length

    if not (within 1) then
        Error InvalidTerms
    else
        let flags = body[at]

        if flags = 0uy || flags &&& 0xFCuy <> 0uy then
            Error InvalidTerms
        else
            let locale, next =
                if flags &&& 1uy = 0uy then
                    Ok None, at + 1
                elif not (within 2) || not (within (2 + int body[at + 1])) then
                    Error InvalidTerms, at
                else
                    let length = int body[at + 1]
                    let text = Text.Encoding.ASCII.GetString(body, at + 2, length)

                    (if body[at + 2 .. at + 1 + length] |> Array.forall (fun b -> b < 0x80uy) && termsProblem { noTerms with Locale = Some text } = None then
                         Ok(Some text)
                     else
                         Error InvalidTerms),
                    at + 2 + length

            match locale with
            | Error e -> Error e
            | Ok locale when flags &&& 2uy = 0uy -> Ok({ Locale = locale; ExpiresOn = None }, next)
            | Ok _ when next + 2 > body.Length -> Error InvalidTerms
            | Ok locale -> Ok({ Locale = locale; ExpiresOn = Some(epoch.AddDays((int body[next] <<< 8) ||| int body[next + 1])) }, next + 2)

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
let encodeWith (content: Content) (reference: byte[]) (terms: Terms) (envelope: Envelope) : string =
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
        [| yield byte (if terms = noTerms then ResponseEncodingVersion else TermsFormatVersion)
           yield kindOf envelope.Binding
           yield! reference
           for id in idsOf envelope.Binding do
               yield! OpaqueId.toBytes id
           yield! termsBytes terms
           yield byte (qs.Length >>> 8)
           yield byte (qs.Length &&& 0xFF)
           yield! packed |]

    Base64Url.EncodeToString(ReadOnlySpan(Array.append body (checksum (ReadOnlySpan body))))

/// Encodes an envelope with no invitation terms (link format 1).
let encode (content: Content) (reference: byte[]) (envelope: Envelope) : string = encodeWith content reference noTerms envelope

/// Reads an envelope against the template the caller resolved, in
/// `UrlState`'s order: alphabet, length, version, integrity, binding,
/// template, cardinality, length, states, padding.
let decodeWithTerms (content: Content) (reference: byte[]) (text: string) : Result<Envelope * Terms, DecodeError> =
    let referenceLength = TemplateCanonical.ReferenceLength
    let minimum = 2 + referenceLength + 2 + IntegrityLength

    match tryFromBase64Url text with
    | None -> Error NotBase64Url
    | Some bytes when bytes.Length >= 1 && int bytes[0] <> ResponseEncodingVersion && int bytes[0] <> TermsFormatVersion ->
        Error(UnsupportedVersion(int bytes[0]))
    | Some bytes when bytes.Length < minimum -> Error(Truncated(minimum, bytes.Length))
    | Some bytes ->
        let body = bytes[.. bytes.Length - IntegrityLength - 1]

        if checksum (ReadOnlySpan body) <> bytes[bytes.Length - IntegrityLength ..] then
            Error IntegrityFailed
        else
            match idCount (baseKind bytes[1]) with
            | None -> Error(UnknownBinding(int bytes[1]))
            | Some ids ->
                let termsAt = 2 + referenceLength + ids * IdLength

                // Format 2 carries terms between the binding and the item count.
                let termsRead =
                    if int bytes[0] = TermsFormatVersion then readTerms body termsAt else Ok(noTerms, termsAt)

                let terms, header =
                    match termsRead with
                    | Ok(terms, countAt) -> terms, countAt + 2
                    | Error _ -> noTerms, termsAt + 2

                if Result.isError termsRead then
                    Error InvalidTerms
                elif body.Length < header then
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
                        | Ok pairs -> Ok({ Binding = binding; Answers = Map.ofList pairs }, terms)

/// Reads an envelope, in either link format, without its terms.
let decode (content: Content) (reference: byte[]) (text: string) : Result<Envelope, DecodeError> =
    decodeWithTerms content reference text |> Result.map fst

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
    | Some bytes when bytes.Length >= 1 && int bytes[0] <> ResponseEncodingVersion && int bytes[0] <> TermsFormatVersion ->
        Error(UnsupportedVersion(int bytes[0]))
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

/// An invitation with terms: a locale and an expiry day (link format 2).
let invitationWith (terms: Terms) (surveyId: string) (version: string) (content: Content) (mode: Import.IdentityMode) (instance: OpaqueId) (group: OpaqueId) =
    encodeWith content (referenceOf surveyId version content) terms { Binding = invitationBinding mode instance group; Answers = Map.empty }

/// A test link (AUT-006 §§60-61): an invitation marked as a test artifact,
/// optionally carrying sample answers (a fixture's). The survey page says it
/// is a test, and production import refuses what it submits.
let testLink (surveyId: string) (version: string) (content: Content) (mode: Import.IdentityMode) (instance: OpaqueId) (group: OpaqueId) (answers: Answers) =
    encode content (referenceOf surveyId version content) { Binding = asTest (invitationBinding mode instance group); Answers = answers }
