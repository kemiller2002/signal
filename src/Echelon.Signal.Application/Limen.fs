/// Limen's browser/engine protocol (version 1), as the application tier reads
/// and writes it: the kernel's messages in, the engine's view and handshake
/// answer out. Mechanics only; no Signal decision is made here.
///
/// The respondent pages request Core effects: a Navigation `replace` that
/// keeps the live URL equal to the respondent's state (LURL-001), a Clipboard
/// `writeText` that copies a finalized submission link, and (the survey page
/// only) one same-origin Http GET of a published template from the site's
/// catalog (DF-SIGNAL-2026-0005). The survey page may also select one pack,
/// `limen.focus`, to move focus to the top of a new page (SIG-ECHELON-APP-002
/// (d)); the assessment page selects none. The kernel can therefore send the
/// handshake, events, location changes, and navigation, clipboard, Http and
/// focus results. Anything else is not a message this engine could have
/// caused.
///
/// See the `protocol` export of `@echelon-foundry/limen` (0.9.0; protocol unchanged since 0.7.1).
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

/// `limen.focus` v1 (from the installed package's generated contract, as the
/// administrator page selects it): moving focus to an element named by
/// `data-focus-target`.
let focus =
    { Id = "limen.focus"
      Version = 1
      Fingerprint = "sha256:6180bce797be6bf993594d62570550a2dc72507edc136263c52e262c5f7c66ec" }

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

/// An Http effect's outcome: the status and the body (base64, as requested),
/// or why there is no response.
type HttpOutcome =
    | HttpResponded of status: int * body: string
    | HttpFailed of reason: string

[<NoComparison; NoEquality>]
type Inbound =
    | Initialize of handshake: JsonNode option * location: Location option
    /// The page reads no `checked` state (protocol 1.2): see `Wire.events`.
    | Event of name: string * key: string option * value: string option
    | LocationChanged of Location
    | NavigationResult of correlationId: string * outcome: NavigationOutcome
    /// A Clipboard writeText outcome: Ok, or the failure reason.
    | ClipboardResult of correlationId: string * outcome: Result<unit, string>
    | HttpResult of correlationId: string * outcome: HttpOutcome
    /// A `limen.focus` outcome: whether the pack moved focus (`Completed`).
    | FocusResult of correlationId: string * moved: bool

/// An effect the engine asks the kernel to perform.
type Effect =
    /// Replace the current history entry's URL (same-origin, root-relative).
    | ReplaceUrl of correlationId: string * url: string
    /// Add a history entry (same-origin, root-relative), so Back returns to
    /// the URL before it.
    | PushUrl of correlationId: string * url: string
    /// Write text to the clipboard (write-only by design in Limen).
    | CopyText of correlationId: string * text: string
    /// GET a file's exact bytes (base64), without credentials. The address
    /// is relative to the page, so the request stays on the page's origin.
    | FetchBytes of correlationId: string * url: string
    /// Move focus to the element named `data-focus-target="target"` (the
    /// `limen.focus` pack; only where the handshake selected it).
    | FocusOn of correlationId: string * target: string

/// How long the survey page waits for its template file.
[<Literal>]
let FetchTimeoutMs = 20000

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

        // A checkbox reports its value attribute either way, and whether it
        // is checked (protocol 1.2): an unchecked box means "not this value".
        let value =
            match optional "checked" "$.event" asBool event with
            | Some false -> Some ""
            | _ -> optional "value" "$.event" asString event

        Event(required "name" "$.event" asString event, optional "key" "$.event" asString event, value)
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
        | "HttpResult" ->
            let outcome = required "outcome" "$.result" asObject result

            let read =
                match required "kind" "$.result.outcome" asString outcome with
                | "Success" ->
                    HttpResponded(required "status" "$.result.outcome" asInt outcome, optional "body" "$.result.outcome" asString outcome |> Option.defaultValue "")
                | "Failure"
                | "OutcomeUnknown" -> HttpFailed(required "reason" "$.result.outcome" asString outcome)
                | "Cancelled" -> HttpFailed "cancelled"
                | other -> raise (MalformedInput("$.result.outcome.kind", $"an Http outcome, not '{other}'"))

            HttpResult(required "correlationId" "$.result" asString result, read)
        | "CapabilityResult" when required "capability" "$.result" asString result = focus.Id ->
            let outcome = required "outcome" "$.result" asObject result
            FocusResult(required "correlationId" "$.result" asString result, required "kind" "$.result.outcome" asString outcome = "Completed")
        // The engine requests only navigation, clipboard writes, Http reads
        // and focus moves, so no other result can answer anything it asked.
        | _ -> raise (MalformedInput("$.result.kind", "a result for an effect this engine requests (NavigationResult, ClipboardResult, HttpResult, a limen.focus CapabilityResult)"))
    | "CapabilityFact" -> raise (MalformedInput("$.kind", "a message this engine can receive, not a fact for a capability it never negotiated"))
    | other -> raise (MalformedInput("$.kind", $"a known message kind, not '{other}'"))

// ---------------------------------------------------------------------------
// The handshake answer.
// ---------------------------------------------------------------------------

[<NoComparison; NoEquality>]
type Handshake =
    | Accepted of minor: int * contract: JsonNode
    | Rejected of reason: (Utf8JsonWriter -> unit)

/// The packs the kernel offers in its handshake, as it names them.
let offered (offer: JsonNode) : Contract list =
    match tryField "capabilities" offer with
    | Some list ->
        asArray "$.handshake.capabilities" list
        |> List.mapi (fun index item ->
            let path = $"$.handshake.capabilities[{index}]"

            { Id = required "id" path asString item
              Version = required "version" path asInt item
              Fingerprint = required "fingerprint" path asString item })
    | None -> []

/// Of the packs a page wants, those the kernel offers at exactly the version
/// and fingerprint this engine was written against.
let selecting (wanted: Contract list) (offer: JsonNode) =
    let available = offered offer
    wanted |> List.filter (fun w -> List.contains w available)

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

/// The engine's complete reply to one kernel message, selecting the packs
/// named (in the handshake answer only).
let encodeSelecting (selected: Contract list) (view: View) (effects: Effect list) (handshake: Handshake option) =
    write (fun writer ->
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        writeView writer view
        writer.WritePropertyName "effects"
        writer.WriteStartArray()

        for effect in effects do
            match effect with
            | ReplaceUrl(correlationId, url)
            | PushUrl(correlationId, url) ->
                writer.WriteStartObject()
                writer.WriteString("kind", "Navigation")
                writer.WriteString("operation", (match effect with PushUrl _ -> "push" | _ -> "replace"))
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
            | FetchBytes(correlationId, url) ->
                writer.WriteStartObject()
                writer.WriteString("kind", "Http")
                writer.WriteString("correlationId", correlationId)
                writer.WriteString("method", "GET")
                writer.WriteString("url", url)
                writer.WriteNumber("timeoutMs", FetchTimeoutMs)
                writer.WriteString("response", "base64")
                writer.WriteString("credentials", "omit")
                writer.WriteEndObject()
            | FocusOn(correlationId, target) ->
                writer.WriteStartObject()
                writer.WriteString("kind", "Capability")
                writer.WriteString("correlationId", correlationId)
                writer.WriteString("capability", focus.Id)
                writer.WriteNumber("version", focus.Version)
                writer.WritePropertyName "request"
                writer.WriteStartObject()
                writer.WriteString("operation", "focus")
                writer.WritePropertyName "target"
                writer.WriteStartObject()
                writer.WriteString("name", target)
                writer.WriteEndObject()
                writer.WriteBoolean("preventScroll", false)
                writer.WriteEndObject()
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

            for pack in selected do
                writer.WriteStartObject()
                writer.WriteString("id", pack.Id)
                writer.WriteNumber("version", pack.Version)
                writer.WriteString("fingerprint", pack.Fingerprint)
                writer.WriteEndObject()

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

/// The engine's complete reply to one kernel message, selecting no pack.
let encode (view: View) (effects: Effect list) (handshake: Handshake option) = encodeSelecting [] view effects handshake
