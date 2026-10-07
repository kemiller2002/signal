/// The canonical form of an assessment template and its hash (VER-001,
/// VER-002, ARX-006).
///
/// The canonical form is compact JSON: fixed property order, no whitespace,
/// arrays in template order, and the default (ASCII-safe) escaper, so the same
/// template always gives the same bytes on every platform. Everything that
/// changes how answers are interpreted is in it: identity and version, the
/// dimensions and items in order, the answer domain in order (which fixes the
/// URL encoding layout) and the scoring parameter. The TemplateHash is
/// SHA-256 over those UTF-8 bytes.
///
/// A compact TemplateReference (the first eight hash bytes) travels in URLs;
/// it identifies the exact template and is verified against the full hash
/// of the template the reader resolves.
module Echelon.Signal.Engine.Canonical

open System
open System.Buffers
open System.Security.Cryptography
open System.Text.Json
open Echelon.Signal.Engine.Assessment

/// Version of the canonical form itself. A change to what is written, or how,
/// is a new version, never an edit of version 1.
[<Literal>]
let CanonicalFormVersion = 1

/// Bytes of the hash carried by a TemplateReference.
[<Literal>]
let ReferenceLength = 8

/// The canonical UTF-8 bytes of an assessment template.
let bytes (assessment: Assessment) : byte[] =
    let buffer = ArrayBufferWriter<byte>()

    (
        use w = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        w.WriteStartObject()
        w.WriteNumber("canonicalForm", CanonicalFormVersion)
        w.WriteString("id", assessment.Id)
        w.WriteString("version", assessment.Version)
        w.WriteString("title", assessment.Title)
        w.WriteStartArray "dimensions"

        for d in assessment.Dimensions do
            w.WriteStartObject()
            w.WriteString("id", d.Id)
            w.WriteString("label", d.Label)
            w.WriteEndObject()

        w.WriteEndArray()
        w.WriteStartArray "items"

        for item in assessment.Items do
            w.WriteStartObject()
            w.WriteString("id", item.Id)
            w.WriteString("dimension", item.DimensionId)
            w.WriteString("prompt", item.Prompt)
            w.WriteEndObject()

        w.WriteEndArray()
        w.WriteStartArray "answerDomain"

        for answer in answerDomain do
            w.WriteStringValue(answerCode answer)

        w.WriteEndArray()
        w.WriteStartObject "scoring"
        w.WriteString("dimension", "mean-over-4-times-100")
        w.WriteNumber("minimumNumericAnswers", assessment.MinimumNumericAnswers)
        w.WriteString("rounding", "1dp-half-away-from-zero")
        w.WriteEndObject()
        w.WriteEndObject()
    )

    buffer.WrittenSpan.ToArray()

/// The full SHA-256 of the canonical bytes.
let hashBytes (assessment: Assessment) : byte[] = SHA256.HashData(bytes assessment)

/// `sha256:<lowercase hex>`, the TemplateHash as it is written in records.
let templateHash (assessment: Assessment) =
    "sha256:" + Convert.ToHexString(hashBytes assessment).ToLowerInvariant()

/// The compact reference a URL carries: the first `ReferenceLength` bytes.
let reference (assessment: Assessment) : byte[] =
    (hashBytes assessment)[.. ReferenceLength - 1]

/// Whether a reference read from a URL names this exact template.
let matches (assessment: Assessment) (candidate: byte[]) =
    candidate = reference assessment
