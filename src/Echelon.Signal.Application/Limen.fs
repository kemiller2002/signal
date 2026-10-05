/// Limen's browser/engine protocol (version 1), as the application tier reads
/// and writes it: the kernel's messages in, the engine's view and handshake
/// answer out. Mechanics only; no Signal decision is made here.
///
/// Signal's first slice requests no effects and negotiates no capability
/// pack, so the kernel can only send it the handshake, events and location
/// changes. Anything else is not a message this engine could have caused.
///
/// See the `protocol` export of `@echelon-foundry/limen` (0.7.0).
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

[<NoComparison; NoEquality>]
type Inbound =
    | Initialize of handshake: JsonNode option
    /// `isChecked` is the radio or checkbox state (protocol 1.2), when sent.
    | Event of name: string * key: string option * value: string option * isChecked: bool option
    | LocationChanged

/// Reads one message from the kernel.
let decode (messageJson: string) =
    let message = parse messageJson |> asObject "$"

    match required "kind" "$" asString message with
    | "Initialize" -> Initialize(tryField "handshake" message)
    | "Event" ->
        let event = required "event" "$" asObject message

        Event(
            required "name" "$.event" asString event,
            optional "key" "$.event" asString event,
            optional "value" "$.event" asString event,
            optional "checked" "$.event" asBool event
        )
    | "LocationChanged" -> LocationChanged
    // The engine requests no effect and negotiates no capability, so neither
    // a result nor a capability fact can answer anything it asked.
    | "EffectResult" -> raise (MalformedInput("$.kind", "a message this engine can receive, not an effect result it never requested"))
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
// The engine's reply: view, no effects, and (to Initialize only) the handshake.
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
let encode (view: View) (handshake: Handshake option) =
    write (fun writer ->
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        writeView writer view
        writer.WritePropertyName "effects"
        writer.WriteStartArray()
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
