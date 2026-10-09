/// The portable, URL-safe response envelope (ResponseEncodingVersion 1).
///
/// One envelope carries a respondent's answer state against one exact
/// template, plus the identity binding the identity mode allows. It holds
/// only state the template cannot reconstruct (URLC-003 §3, LURL-001): no
/// question ids, labels, scores or results.
///
/// Byte layout, version 1 (all integers big-endian):
///
///   [0]        ResponseEncodingVersion (1)
///   [1]        binding kind: 0 unbound, 1 identified invitation,
///              2 identified submission, 3 anonymous submission,
///              4 anonymous invitation
///   [2..9]     TemplateReference: first 8 bytes of the TemplateHash
///   [...]      binding identifiers, 16 bytes each, in the order listed
///   [2 bytes]  item count (the template's, so cardinality is checked)
///   [...]      answers bit-packed most-significant-bit first, one fixed-width
///              state per item in template order; 0 is "not answered" and
///              `i + 1` is the `i`th entry of `Assessment.answerDomain`;
///              unused trailing bits are zero
///   [4 bytes]  integrity: the first 4 bytes of SHA-256 over every byte above
///
/// The bytes are written as unpadded base64url (CAN-004 §23).
///
/// Decoding fails explicitly and never guesses (URLC-003 §6). The order is:
/// alphabet, minimum length, version, integrity, binding, template,
/// cardinality, length, states, padding. Integrity is checked before anything
/// that depends on the payload's content, so corruption (including most
/// truncation) is reported as corruption, not as a wrong template or an
/// impossible answer (ACR-004: corruption and authenticity are separate; this
/// version provides integrity only, not authenticity).
module Echelon.Signal.Engine.UrlState

open System
open System.Buffers.Text
open System.Security.Cryptography
open Echelon.Signal.Engine.Assessment

[<Literal>]
let ResponseEncodingVersion = 1

[<Literal>]
let IntegrityLength = 4

[<Literal>]
let IdLength = 16

/// An opaque 16-byte identifier: never a person, never derived from one
/// (ID-001). Written as 22 base64url characters.
[<CustomEquality; NoComparison>]
type OpaqueId =
    private
    | OpaqueId of byte[]

    member this.Bytes =
        let (OpaqueId bytes) = this
        Array.copy bytes

    override this.Equals other =
        match other with
        | :? OpaqueId as o -> o.Bytes = this.Bytes
        | _ -> false

    override this.GetHashCode() = hash this.Bytes
    override this.ToString() = Base64Url.EncodeToString(ReadOnlySpan this.Bytes)

/// Strict unpadded base64url: the alphabet only, and only the canonical
/// spelling (unused trailing bits zero), so one value has one text.
let tryFromBase64Url (text: string) : byte[] option =
    let value (c: char) =
        if c >= 'A' && c <= 'Z' then int c - int 'A'
        elif c >= 'a' && c <= 'z' then int c - int 'a' + 26
        elif c >= '0' && c <= '9' then int c - int '0' + 52
        elif c = '-' then 62
        elif c = '_' then 63
        else -1

    // Bits of the last character that encode no byte, by length mod 4.
    let unusedMask =
        match text.Length % 4 with
        | 2 -> 0b1111
        | 3 -> 0b11
        | _ -> 0

    if text.Length = 0 || text.Length % 4 = 1 || Seq.exists (fun c -> value c < 0) text then
        None
    elif value text[text.Length - 1] &&& unusedMask <> 0 then
        None
    else
        Some(Base64Url.DecodeFromChars(text.AsSpan()))

module OpaqueId =
    let ofBytes (bytes: byte[]) =
        if bytes.Length <> IdLength then
            None
        else
            Some(OpaqueId(Array.copy bytes))

    let toBytes (id: OpaqueId) = id.Bytes

    /// Reads the 22-character base64url form; anything else is None.
    let tryParse (text: string) =
        if text.Length <> 22 then None else tryFromBase64Url text |> Option.bind ofBytes

/// What, besides answers, the envelope binds the response to.
[<NoComparison>]
type Binding =
    /// A live response with no invitation (the open pilot page).
    | Unbound
    /// A live response to an invitation in an identified group: instance
    /// and group (LURL-002 §19).
    | IdentifiedInvitation of instance: OpaqueId * group: OpaqueId
    /// A live response to an invitation in an anonymous group. The instance
    /// is present while answering, for resume, and is removed at
    /// finalization (LURL-002 §13, §16).
    | AnonymousInvitation of instance: OpaqueId * group: OpaqueId
    /// A finalized identified submission (LURL-002 §19).
    | Identified of instance: OpaqueId * group: OpaqueId
    /// A finalized anonymous submission: a fresh unlinkable id and the group,
    /// and no instance (LURL-002 §15, ID-002).
    | Anonymous of submission: OpaqueId * group: OpaqueId
    /// A test or preview artifact (AUT-006 §§21, 60): the binding it stands
    /// in for, marked so it is never taken for a production response. Only
    /// generic envelopes carry it (the kind byte's high bit,
    /// `GenericEnvelope`); the pilot codec never writes or reads it.
    | Test of Binding

/// The kind byte's test marker (generic envelopes only).
[<Literal>]
let TestKindFlag = 0x80uy

/// Marks a binding as a test artifact; marking twice changes nothing.
let asTest =
    function
    | Test _ as marked -> marked
    | binding -> Test binding

/// The binding a test artifact stands in for; a production binding as it is.
let rec production =
    function
    | Test binding -> production binding
    | binding -> binding

let isTest =
    function
    | Test _ -> true
    | _ -> false

let rec kindOf =
    function
    | Unbound -> 0uy
    | IdentifiedInvitation _ -> 1uy
    | Identified _ -> 2uy
    | Anonymous _ -> 3uy
    | AnonymousInvitation _ -> 4uy
    | Test binding -> TestKindFlag ||| kindOf (production binding)

let rec idsOf =
    function
    | Unbound -> []
    | IdentifiedInvitation(a, b)
    | AnonymousInvitation(a, b)
    | Identified(a, b)
    | Anonymous(a, b) -> [ a; b ]
    | Test binding -> idsOf binding

/// Identifiers that follow the kind byte, by kind.
let idCount =
    function
    | 0uy -> Some 0
    | 1uy
    | 2uy
    | 3uy
    | 4uy -> Some 2
    | _ -> None

[<NoComparison>]
type Envelope = { Binding: Binding; Answers: Answers }

type DecodeError =
    /// Not unpadded base64url, or not its canonical spelling.
    | NotBase64Url
    /// Shorter than the smallest envelope this version can be.
    | Truncated of minimum: int * actual: int
    | UnsupportedVersion of version: int
    /// The integrity check failed: the payload was corrupted or truncated.
    | IntegrityFailed
    | UnknownBinding of kind: int
    /// The payload was encoded against a different template.
    | TemplateMismatch
    | CardinalityMismatch of expected: int * actual: int
    /// The packed answers are not the length the item count implies.
    | LengthMismatch of expected: int * actual: int
    | InvalidAnswerState of itemIndex: int * state: int
    | NonCanonicalPadding
    /// A generic link's invitation terms (locale, expiry; link format 2)
    /// are malformed or not in their one canonical spelling.
    | InvalidTerms

/// Bits per item: enough for "not answered" plus every answer.
let bitsPerItem =
    let states = answerDomain.Length + 1
    let rec bits n = if (1 <<< n) >= states then n else bits (n + 1)
    bits 1

let private stateOf (answer: Answer option) =
    match answer with
    | None -> 0
    | Some a -> 1 + List.findIndex ((=) a) answerDomain

let private packedLength count = (count * bitsPerItem + 7) / 8

let checksum (data: ReadOnlySpan<byte>) = SHA256.HashData(data).AsSpan(0, IntegrityLength).ToArray()

/// The envelope as unpadded base64url. Answers for ids the template does not
/// have are not state the template can interpret, and are not written.
let encode (assessment: Assessment) (envelope: Envelope) : string =
    let items = assessment.Items
    let packed = Array.zeroCreate<byte> (packedLength items.Length)

    items
    |> List.iteri (fun index item ->
        let state = stateOf (envelope.Answers.TryFind item.Id)

        for bit in 0 .. bitsPerItem - 1 do
            if (state >>> (bitsPerItem - 1 - bit)) &&& 1 = 1 then
                let position = index * bitsPerItem + bit
                packed[position / 8] <- packed[position / 8] ||| (0x80uy >>> (position % 8)))

    let body =
        [| yield byte ResponseEncodingVersion
           yield kindOf envelope.Binding
           yield! Canonical.reference assessment
           for id in idsOf envelope.Binding do
               yield! OpaqueId.toBytes id
           yield byte (items.Length >>> 8)
           yield byte (items.Length &&& 0xFF)
           yield! packed |]

    Base64Url.EncodeToString(ReadOnlySpan(Array.append body (checksum (ReadOnlySpan body))))

/// The smallest version-1 envelope: header, reference, count, checksum.
let minimumLength = 2 + Canonical.ReferenceLength + 2 + IntegrityLength

/// Reads an envelope against the template the caller resolved.
let decode (assessment: Assessment) (text: string) : Result<Envelope, DecodeError> =
    match tryFromBase64Url text with
    | None -> Error NotBase64Url
    | Some bytes when bytes.Length >= 1 && int bytes[0] <> ResponseEncodingVersion -> Error(UnsupportedVersion(int bytes[0]))
    | Some bytes when bytes.Length < minimumLength -> Error(Truncated(minimumLength, bytes.Length))
    | Some bytes ->
        let body = bytes[.. bytes.Length - IntegrityLength - 1]
        let check = bytes[bytes.Length - IntegrityLength ..]

        if checksum (ReadOnlySpan body) <> check then
            Error IntegrityFailed
        else
            match idCount bytes[1] with
            | None -> Error(UnknownBinding(int bytes[1]))
            | Some ids ->
                let header = 2 + Canonical.ReferenceLength + ids * IdLength + 2

                if body.Length < header then
                    Error(Truncated(header + IntegrityLength, bytes.Length))
                elif not (Canonical.matches assessment body[2 .. 1 + Canonical.ReferenceLength]) then
                    Error TemplateMismatch
                else
                    let idAt i =
                        let start = 2 + Canonical.ReferenceLength + i * IdLength
                        (OpaqueId.ofBytes body[start .. start + IdLength - 1]).Value

                    let binding =
                        match bytes[1] with
                        | 1uy -> IdentifiedInvitation(idAt 0, idAt 1)
                        | 4uy -> AnonymousInvitation(idAt 0, idAt 1)
                        | 2uy -> Identified(idAt 0, idAt 1)
                        | 3uy -> Anonymous(idAt 0, idAt 1)
                        | _ -> Unbound

                    let count = (int body[header - 2] <<< 8) ||| int body[header - 1]
                    let items = assessment.Items
                    let packed = body[header..]

                    if count <> items.Length then
                        Error(CardinalityMismatch(items.Length, count))
                    elif packed.Length <> packedLength count then
                        Error(LengthMismatch(packedLength count, packed.Length))
                    else
                        let bit position = (packed[position / 8] >>> (7 - position % 8)) &&& 1uy |> int

                        let stateAt index =
                            seq { 0 .. bitsPerItem - 1 }
                            |> Seq.fold (fun acc b -> (acc <<< 1) ||| bit (index * bitsPerItem + b)) 0

                        let states = items |> List.mapi (fun index item -> index, item, stateAt index)

                        match states |> List.tryFind (fun (_, _, s) -> s > answerDomain.Length) with
                        | Some(index, _, state) -> Error(InvalidAnswerState(index, state))
                        | None ->
                            let used = count * bitsPerItem
                            let padding = [ used .. packed.Length * 8 - 1 ] |> List.exists (fun p -> bit p = 1)

                            if padding then
                                Error NonCanonicalPadding
                            else
                                let answers =
                                    states
                                    |> List.choose (fun (_, item, state) ->
                                        if state = 0 then None else Some(item.Id, answerDomain[state - 1]))
                                    |> Map.ofList

                                Ok { Binding = binding; Answers = answers }

/// A short, stable description of a decode error, safe to show: it names the
/// problem, never the payload.
let describe =
    function
    | NotBase64Url -> "The saved answers in this link are not in a form Signal can read."
    | Truncated _ -> "The saved answers in this link are incomplete; the link may have been cut off."
    | UnsupportedVersion v -> $"The saved answers in this link use encoding version {v}, which this version of Signal does not read."
    | IntegrityFailed -> "The saved answers in this link fail their integrity check; the link was damaged or cut off."
    | UnknownBinding _ -> "The saved answers in this link are of a kind Signal does not recognise."
    | TemplateMismatch -> "The saved answers in this link belong to a different version of this assessment."
    | CardinalityMismatch _
    | LengthMismatch _ -> "The saved answers in this link do not match this assessment's questions."
    | InvalidAnswerState _ -> "The saved answers in this link contain an answer this assessment does not offer."
    | NonCanonicalPadding -> "The saved answers in this link are not in canonical form."
    | InvalidTerms -> "This link's invitation details (language or expiry) are damaged."
