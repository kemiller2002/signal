/// Limen's browser/engine protocol (version 1), as the application tier reads
/// and writes it: the kernel's messages in, the engine's view and handshake
/// answer out. Mechanics only; no Signal decision is made here.
///
/// Signal requests two Core effects, a Navigation `replace` that keeps the
/// live URL equal to the respondent's state (LURL-001) and a Clipboard
/// `writeText` that copies a finalized submission link, and negotiates no
/// capability pack. The kernel can therefore send it the handshake, events,
/// location changes, and navigation and clipboard results. Anything else is not a message
/// this engine could have caused.
///
/// See the `protocol` export of `@echelon-foundry/limen` (0.7.1).
module Echelon.Signal.Application.Limen

open System.Text.Json
open System.Text.Json.Nodes
open Echelon.Signal.Engine.View
open Echelon.Signal.Application.Json

/// A generated contract unit the engine was written against. The kernel
/// offers its own identity in the handshake; the engine accepts only this.
type Contract =
    { Id: string
      Version: int
      Fingerprint: string }

/// Limen Core's contract identity (`limen.core` v1).
let core =
    { Id = "limen.core"
      Version = 1
      Fingerprint = "sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c" }

/// The newest protocol revision this engine speaks (1.4).
[<Literal>]
let ProtocolMinor = 4

/// The browser's URL, split mechanically by the kernel (`BrowserLocation`).
type Location =
    { Origin: string
      Path: string
      Query: string
      Hash: string }

/// A Navigation effect's outcome (`NavigationOutcome`).
type NavigationOutcome =
    | NavigationSucceeded of Location
    | NavigationDispatched
    | NavigationFailed of reason: string

[<NoComparison; NoEquality>]
type Inbound =
    | Initialize of handshake: JsonNode option * location: Location option
    /// The page reads no `checked` state (protocol 1.2): see `Wire.events`.
    | Event of name: string * key: string option * value: string option
    | LocationChanged of Location
    | NavigationResult of correlationId: string * outcome: NavigationOutcome
    /// A Clipboard writeText outcome: Ok, or the failure reason.
    | ClipboardResult of correlationId: string * outcome: Result<unit, string>

/// An effect the engine asks the kernel to perform.
type Effect =
    /// Replace the current history entry's URL (same-origin, root-relative).
    | ReplaceUrl of correlationId: string * url: string
    /// Write text to the clipboard (write-only by design in Limen).
    | CopyText of correlationId: string * text: string

let private location (path: string) (node: JsonNode) =
    let o = asObject path node

    { Origin = required "origin" path asString o
      Path = required "path" path asString o
      Query = required "query" path asString o
      Hash = required "hash" path asString o }

/// Reads one message from the kernel.
let decode (messageJson: string) =
    let message = parse messageJson |> asObject "$"

    match required "kind" "$" asString message with
    | "Initialize" -> Initialize(tryField "handshake" message, optional "location" "$" location message)
    | "Event" ->
        let event = required "event" "$" asObject message

        Event(
            required "name" "$.event" asString event,
            optional "key" "$.event" asString event,
            optional "value" "$.event" asString event
        )
    | "LocationChanged" -> LocationChanged(required "location" "$" location message)
    | "EffectResult" ->
        let result = required "result" "$" asObject message

        match required "kind" "$.result" asString result with
        | "NavigationResult" ->
            let outcome = required "outcome" "$.result" asObject result

            let read =
                match required "kind" "$.result.outcome" asString outcome with
                | "Success" -> NavigationSucceeded(required "location" "$.result.outcome" location outcome)
                | "Dispatched" -> NavigationDispatched
                | "Failure" -> NavigationFailed(required "reason" "$.result.outcome" asString outcome)
                | other -> raise (MalformedInput("$.result.outcome.kind", $"a navigation outcome, not '{other}'"))

            NavigationResult(required "correlationId" "$.result" asString result, read)
        | "ClipboardResult" ->
            let outcome = required "outcome" "$.result" asObject result

            let read =
                match required "kind" "$.result.outcome" asString outcome with
                | "Success" -> Ok()
                | "Failure" -> Error(required "reason" "$.result.outcome" asString outcome)
                | other -> raise (MalformedInput("$.result.outcome.kind", $"a clipboard outcome, not '{other}'"))

            ClipboardResult(required "correlationId" "$.result" asString result, read)
        // The engine requests only navigation and clipboard writes, so no
        // other result can answer anything it asked.
        | _ -> raise (MalformedInput("$.result.kind", "a result for an effect this engine requests (NavigationResult, ClipboardResult)"))
    | "CapabilityFact" -> raise (MalformedInput("$.kind", "a message this engine can receive, not a fact for a capability it never negotiated"))
    | other -> raise (MalformedInput("$.kind", $"a known message kind, not '{other}'"))

// ---------------------------------------------------------------------------
// The handshake answer.
// ---------------------------------------------------------------------------

[<NoComparison; NoEquality>]
type Handshake =
    | Accepted of minor: int * contract: JsonNode
    | Rejected of reason: (Utf8JsonWriter -> unit)

/// The engine's answer to the kernel's handshake offer: accept the Core
/// contract, selecting no capability pack, or say precisely why not.
let answer (offer: JsonNode) =
    let path = "$.handshake"
    let protocol = required "protocol" path asObject offer
    let major = required "major" $"{path}.protocol" asInt protocol
    let minor = required "minor" $"{path}.protocol" asInt protocol
    let contract = required "contract" path asObject offer

    let sameCore =
        required "unit" $"{path}.contract" asString contract = core.Id
        && required "version" $"{path}.contract" asInt contract = core.Version
        && required "fingerprint" $"{path}.contract" asString contract = core.Fingerprint

    if major <> 1 then
        Rejected(fun writer ->
            writer.WriteString("kind", "ProtocolUnsupported")
            writer.WritePropertyName "offered"
            writeNode writer protocol)
    elif not sameCore then
        Rejected(fun writer ->
            writer.WriteString("kind", "ContractMismatch")
            writer.WritePropertyName "expected"
            writer.WriteStartObject()
            writer.WriteString("unit", core.Id)
            writer.WriteNumber("version", core.Version)
            writer.WriteString("fingerprint", core.Fingerprint)
            writer.WriteEndObject()
            writer.WritePropertyName "offered"
            writeNode writer contract)
    else
        Accepted(min minor ProtocolMinor, contract)

// ---------------------------------------------------------------------------
// The engine's reply: view, effects, and (to Initialize only) the handshake.
// ---------------------------------------------------------------------------

let private writeScalar (writer: Utf8JsonWriter) =
    function
    | Text text -> writer.WriteStringValue text
    | Flag flag -> writer.WriteBooleanValue flag
    | Number number -> writer.WriteNumberValue number

let private writeView (writer: Utf8JsonWriter) (view: View) =
    writer.WriteStartObject()

    for name, value in view do
        writer.WritePropertyName name

        match value with
        | Value scalar -> writeScalar writer scalar
        | Items items ->
            writer.WriteStartArray()

            for item in items do
                writer.WriteStartObject()

                for field, scalar in item do
                    writer.WritePropertyName field
                    writeScalar writer scalar

                writer.WriteEndObject()

            writer.WriteEndArray()

    writer.WriteEndObject()

/// The engine's complete reply to one kernel message.
let encode (view: View) (effects: Effect list) (handshake: Handshake option) =
    write (fun writer ->
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        writeView writer view
        writer.WritePropertyName "effects"
        writer.WriteStartArray()

        for effect in effects do
            match effect with
            | ReplaceUrl(correlationId, url) ->
                writer.WriteStartObject()
                writer.WriteString("kind", "Navigation")
                writer.WriteString("operation", "replace")
                writer.WriteString("correlationId", correlationId)
                writer.WriteString("url", url)
                writer.WriteEndObject()
            | CopyText(correlationId, text) ->
                writer.WriteStartObject()
                writer.WriteString("kind", "Clipboard")
                writer.WriteString("correlationId", correlationId)
                writer.WriteString("operation", "writeText")
                writer.WriteString("text", text)
                writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WritePropertyName "cancellations"
        writer.WriteStartArray()
        writer.WriteEndArray()

        match handshake with
        | None -> ()
        | Some(Accepted(minor, contract)) ->
            writer.WritePropertyName "handshake"
            writer.WriteStartObject()
            writer.WriteString("kind", "Accepted")
            writer.WritePropertyName "protocol"
            writer.WriteStartObject()
            writer.WriteNumber("major", 1)
            writer.WriteNumber("minor", minor)
            writer.WriteEndObject()
            writer.WritePropertyName "contract"
            writeNode writer contract
            writer.WritePropertyName "capabilities"
            writer.WriteStartArray()
            writer.WriteEndArray()
            writer.WriteEndObject()
        | Some(Rejected reason) ->
            writer.WritePropertyName "handshake"
            writer.WriteStartObject()
            writer.WriteString("kind", "Rejected")
            writer.WritePropertyName "reason"
            writer.WriteStartObject()
            reason writer
            writer.WriteEndObject()
            writer.WriteEndObject()

        writer.WriteEndObject())
