/// Limen's browser/engine protocol (version 1) as the administrator page
/// reads and writes it: the kernel's messages in; the view, effects and
/// handshake answer out. Mechanics only; no Signal decision is made here.
///
/// The page requests Navigation (its areas), Clipboard, Http (the
/// deployment's configuration, Fides' exchange and GitHub through Arca's
/// adapter) and Storage, and two optional capability packs: `limen.schedule`
/// (Arca's back-off waits) and `signal.host` (this tab's session storage,
/// leaving for the identity provider, tidying the address bar after its
/// callback, and telling the origin's other tabs about sign-in).
///
/// See the `protocol` export of `@echelon-foundry/limen` (0.7.1).
module Echelon.Signal.Application.AdminProtocol

open System.Text.Json
open System.Text.Json.Nodes
open Echelon.Signal.Engine.View
open Echelon.Signal.Application.Json

type CapabilityOffer =
    { Id: string
      Version: int
      Fingerprint: string }

/// `limen.schedule` v1 (from the installed package's generated contract).
let schedule =
    { Id = "limen.schedule"
      Version = 1
      Fingerprint = "sha256:627657798f0fa735ac6f955eb4f413e7c397f0aef6942b340a87683a30db54d0" }

/// `signal.host` v1: Signal's own pack (web-kernel/host.js).
let host =
    { Id = "signal.host"
      Version = 1
      Fingerprint = "signal.host/1: tab storage, leave, replace address, broadcast" }

let wanted = [ schedule; host ]

type StorageResult =
    | StorageValue of value: string option
    | StorageFailed of reason: string

type NavigationOutcome =
    | Moved of hash: string
    | Dispatched
    | NavigationFailed of reason: string

[<NoComparison; NoEquality>]
type CapabilityOutcome =
    | Completed of result: JsonNode
    | NotExecuted of reason: string

[<NoComparison; NoEquality>]
type Inbound =
    | Initialize of
        protocolVersion: int *
        effects: string list *
        origin: string *
        path: string *
        query: (string * string) list *
        hash: string *
        handshake: JsonNode option
    | Event of name: string * key: string option * value: string option
    | LocationChanged of hash: string
    | NavigationResult of correlationId: string * NavigationOutcome
    | CapabilityResult of correlationId: string * capability: string * CapabilityOutcome
    | ClipboardResult of correlationId: string * succeeded: bool
    | CapabilityFact of capability: string * fact: JsonNode
    | HttpResponse of correlationId: string * Bridge.HttpResult
    | StorageResponse of correlationId: string * StorageResult

/// `?a=1&b=two%20words` as decoded pairs, in order.
let queryPairs (query: string) =
    query.TrimStart('?').Split('&', System.StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun pair ->
        let decode (text: string) = System.Uri.UnescapeDataString(text.Replace('+', ' '))

        match pair.IndexOf '=' with
        | -1 -> decode pair, ""
        | index -> decode (pair.Substring(0, index)), decode (pair.Substring(index + 1)))
    |> List.ofArray

let private location (path: string) (node: JsonNode) =
    required "path" path asString node, (optional "hash" path asString node |> Option.defaultValue "")

let private navigation (path: string) (node: JsonNode) =
    match required "kind" path asString node with
    | "Success" -> Moved(snd (location $"{path}.location" (required "location" path asObject node)))
    | "Dispatched" -> Dispatched
    | "Failure" -> NavigationFailed(required "reason" path asString node)
    | other -> raise (MalformedInput($"{path}.kind", $"a known navigation outcome, not '{other}'"))

let private capability (path: string) (node: JsonNode) =
    match required "kind" path asString node with
    | "Completed" -> Completed(required "result" path asObject node)
    | "Unsupported"
    | "Rejected" -> NotExecuted(required "reason" path asString node)
    | other -> raise (MalformedInput($"{path}.kind", $"a known capability outcome, not '{other}'"))

let private httpOutcome (at: string) (outcome: JsonNode) =
    match required "kind" at asString outcome with
    | "Success" ->
        let headers =
            match tryField "headers" outcome with
            | Some(:? JsonObject as found) ->
                found
                |> Seq.choose (fun pair ->
                    pair.Value
                    |> Option.ofObj
                    |> Option.map (fun value -> pair.Key.ToLowerInvariant(), asString $"{at}.headers.{pair.Key}" value))
                |> List.ofSeq
            | _ -> []

        Bridge.HttpSucceeded(required "status" at asInt outcome, headers, optional "body" at asString outcome |> Option.defaultValue "")
    | "Failure" -> Bridge.HttpFailed(required "reason" at asString outcome)
    | "Cancelled" -> Bridge.HttpCancelled
    | "OutcomeUnknown" -> Bridge.HttpUnknown(required "reason" at asString outcome)
    | other -> raise (MalformedInput($"{at}.kind", $"a known Http outcome, not '{other}'"))

let private effectResult (node: JsonNode) =
    let path = "$.result"
    let correlation = required "correlationId" path asString node

    match required "kind" path asString node with
    | "NavigationResult" -> NavigationResult(correlation, navigation $"{path}.outcome" (required "outcome" path asObject node))
    | "ClipboardResult" ->
        match required "kind" $"{path}.outcome" asString (required "outcome" path asObject node) with
        | "Success" -> ClipboardResult(correlation, true)
        | "Failure" -> ClipboardResult(correlation, false)
        | other -> raise (MalformedInput($"{path}.outcome.kind", $"a known clipboard outcome, not '{other}'"))
    | "CapabilityResult" ->
        CapabilityResult(correlation, required "capability" path asString node, capability $"{path}.outcome" (required "outcome" path asObject node))
    | "HttpResult" -> HttpResponse(correlation, httpOutcome $"{path}.outcome" (required "outcome" path asObject node))
    | "StorageResult" ->
        let outcome = required "outcome" path asObject node
        let at = $"{path}.outcome"

        StorageResponse(
            correlation,
            match required "kind" at asString outcome with
            | "Success" -> StorageValue(optional "value" at asString outcome)
            | "Failure" -> StorageFailed(required "reason" at asString outcome)
            | other -> raise (MalformedInput($"{at}.kind", $"a known Storage outcome, not '{other}'"))
        )
    | other -> raise (CapabilityFailed(other, $"Unexpected {other} ({correlation}): the page requests no such effect"))

/// Reads one message from the kernel.
let decode (messageJson: string) =
    let message = parse messageJson |> asObject "$"

    match required "kind" "$" asString message with
    | "Initialize" ->
        let where = required "location" "$" asObject message
        let path, hash = location "$.location" where

        Initialize(
            required "protocolVersion" "$" asInt message,
            required "capabilities" "$" asArray message |> List.mapi (fun index item -> asString $"$.capabilities[{index}]" item),
            required "origin" "$.location" asString where,
            path,
            optional "query" "$.location" asString where |> Option.defaultValue "" |> queryPairs,
            hash,
            tryField "handshake" message
        )
    | "Event" ->
        let event = required "event" "$" asObject message
        Event(required "name" "$.event" asString event, optional "key" "$.event" asString event, optional "value" "$.event" asString event)
    | "LocationChanged" -> LocationChanged(snd (location "$.location" (required "location" "$" asObject message)))
    | "EffectResult" -> effectResult (required "result" "$" asObject message)
    | "CapabilityFact" -> CapabilityFact(required "capability" "$" asString message, required "fact" "$" asObject message)
    | other -> raise (MalformedInput("$.kind", $"a known message kind, not '{other}'"))

// ---- The handshake answer ------------------------------------------------------------------

[<NoComparison; NoEquality>]
type Handshake =
    | Accepted of minor: int * contract: JsonNode * capabilities: CapabilityOffer list
    | Rejected of reason: (Utf8JsonWriter -> unit)

/// Accepts the Core contract, selecting each wanted pack the kernel offers at
/// exactly the version and fingerprint this engine was written against.
let answer (offer: JsonNode option) : Handshake =
    match offer with
    | None -> Rejected(fun writer -> writer.WriteString("kind", "HandshakeMissing"))
    | Some offer ->
        match Limen.answer offer with
        | Limen.Rejected reason -> Rejected reason
        | Limen.Accepted(minor, contract) ->
            let offered =
                match tryField "capabilities" offer with
                | Some list ->
                    asArray "$.handshake.capabilities" list
                    |> List.mapi (fun index item ->
                        let path = $"$.handshake.capabilities[{index}]"

                        { Id = required "id" path asString item
                          Version = required "version" path asInt item
                          Fingerprint = required "fingerprint" path asString item })
                | None -> []

            Accepted(minor, contract, wanted |> List.filter (fun w -> List.contains w offered))

// ---- The reply ------------------------------------------------------------------------------

/// An effect, as Limen requests it.
type Request =
    | Push of correlationId: string * url: string
    | Wake of correlationId: string * delayMs: int
    | Copy of correlationId: string * text: string
    | Http of correlationId: string * method: string * url: string * headers: (string * string) list * body: string option * timeoutMs: int * responseHeaders: string list
    | StorageGet of correlationId: string * key: string
    | StorageSet of correlationId: string * key: string * value: string
    | StorageRemove of correlationId: string * key: string
    /// A `signal.host` request: its operation and string arguments.
    | Host of correlationId: string * operation: string * arguments: (string * string) list

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

let private writeCapability (writer: Utf8JsonWriter) (correlationId: string) (offer: CapabilityOffer) (request: Utf8JsonWriter -> unit) =
    writer.WriteString("kind", "Capability")
    writer.WriteString("correlationId", correlationId)
    writer.WriteString("capability", offer.Id)
    writer.WriteNumber("version", offer.Version)
    writer.WritePropertyName "request"
    writer.WriteStartObject()
    request writer
    writer.WriteEndObject()

let private writeStorage (writer: Utf8JsonWriter) (operation: string) (correlationId: string) (key: string) =
    writer.WriteString("kind", "Storage")
    writer.WriteString("operation", operation)
    writer.WriteString("correlationId", correlationId)
    writer.WriteString("key", key)

let private writeRequest (writer: Utf8JsonWriter) (request: Request) =
    writer.WriteStartObject()

    match request with
    | Push(correlationId, url) ->
        writer.WriteString("kind", "Navigation")
        writer.WriteString("operation", "push")
        writer.WriteString("correlationId", correlationId)
        writer.WriteString("url", url)
    | Wake(correlationId, delayMs) ->
        writeCapability writer correlationId schedule (fun w ->
            w.WriteString("operation", "timeout")
            w.WriteNumber("delayMs", delayMs))
    | Copy(correlationId, text) ->
        writer.WriteString("kind", "Clipboard")
        writer.WriteString("correlationId", correlationId)
        writer.WriteString("operation", "writeText")
        writer.WriteString("text", text)
    | Http(correlationId, method, url, headers, body, timeoutMs, responseHeaders) ->
        writer.WriteString("kind", "Http")
        writer.WriteString("correlationId", correlationId)
        writer.WriteString("method", method)
        writer.WriteString("url", url)

        if not headers.IsEmpty then
            writer.WritePropertyName "headers"
            writer.WriteStartObject()
            headers |> List.iter (fun (name, value) -> writer.WriteString(name, value))
            writer.WriteEndObject()

        body |> Option.iter (fun body -> writer.WriteString("body", body))
        writer.WriteNumber("timeoutMs", timeoutMs)
        writer.WriteString("response", "text")
        writer.WriteString("credentials", "omit")

        if not responseHeaders.IsEmpty then
            writer.WritePropertyName "responseHeaders"
            writer.WriteStartArray()
            responseHeaders |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
    | StorageGet(correlationId, key) -> writeStorage writer "get" correlationId key
    | StorageSet(correlationId, key, value) ->
        writeStorage writer "set" correlationId key
        writer.WriteString("value", value)
    | StorageRemove(correlationId, key) -> writeStorage writer "remove" correlationId key
    | Host(correlationId, operation, arguments) ->
        writeCapability writer correlationId host (fun w ->
            w.WriteString("operation", operation)
            arguments |> List.iter (fun (name, value) -> w.WriteString(name, value)))

    writer.WriteEndObject()

let private writeOffer (writer: Utf8JsonWriter) (offer: CapabilityOffer) =
    writer.WriteStartObject()
    writer.WriteString("id", offer.Id)
    writer.WriteNumber("version", offer.Version)
    writer.WriteString("fingerprint", offer.Fingerprint)
    writer.WriteEndObject()

/// The engine's complete reply to one kernel message.
let encode (view: View) (requests: Request list) (handshake: Handshake option) =
    write (fun writer ->
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        writeView writer view
        writer.WritePropertyName "effects"
        writer.WriteStartArray()
        requests |> List.iter (writeRequest writer)
        writer.WriteEndArray()
        // Effects to cancel: none; Limen requires the list once packs are negotiated.
        writer.WritePropertyName "cancellations"
        writer.WriteStartArray()
        writer.WriteEndArray()

        match handshake with
        | None -> ()
        | Some(Accepted(minor, contract, capabilities)) ->
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
            capabilities |> List.iter (writeOffer writer)
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
