/// The Limen protocol and the Aegis boundary, driven exactly as the browser
/// kernel drives them: JSON messages in, JSON replies out. Faults go to a
/// deterministic Aegis collector sink, so each test can prove both that an
/// operational failure is captured and that an expected refusal is not.
module Echelon.Signal.Tests.BoundaryTests

open System
open System.Text.Json.Nodes
open Xunit
open Aegis
open Echelon.Signal.Application

let private collector () =
    let sink = Sinks.Collector()
    let aegis = { Boundary.configure [ sink.Sink() ] with Persistence = Blocking }
    sink, aegis

let private recordedCodes (sink: Sinks.Collector) =
    sink.Events
    |> List.map (fun event -> (JsonNode.Parse event).["code"] |> string)

let private offer (coreFingerprint: string) (major: int) =
    $"""{{"kind":"Initialize","protocolVersion":1,"capabilities":["Http","Storage","Clipboard","Navigation"],
        "location":{{"origin":"http://localhost","path":"/","query":"","hash":""}},
        "handshake":{{"protocol":{{"major":{major},"minor":4}},
          "contract":{{"unit":"limen.core","version":1,"fingerprint":"{coreFingerprint}"}},
          "capabilities":[]}}}}"""

let private initialize = offer Limen.core.Fingerprint 1

let private eventWith name (key: string option) (value: string option) (isChecked: bool option) =
    let key = key |> Option.map (fun k -> $",\"key\":\"{k}\"") |> Option.defaultValue ""
    let value = value |> Option.map (fun v -> $",\"value\":\"{v}\"") |> Option.defaultValue ""
    let isChecked =
        isChecked
        |> Option.map (fun c ->
            let literal = if c then "true" else "false"
            $",\"checked\":{literal}")
        |> Option.defaultValue ""
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}"{key}{value}{isChecked}}}}}"""

let private event name key value = eventWith name key value None

let private run aegis messages =
    messages
    |> List.fold (fun (state, _) message -> Wire.handle aegis state message) (Wire.initial, "")

let private viewOf (reply: string) = (JsonNode.Parse reply).["view"]
let private flag (name: string) (reply: string) = (viewOf reply).[name].GetValue<bool>()
let private text (name: string) (reply: string) = (viewOf reply).[name].GetValue<string>()

[<Fact>]
let ``the engine accepts the kernel's handshake with Core alone and requests nothing`` () =
    let _, aegis = collector ()
    let _, reply = Wire.handle aegis Wire.initial initialize
    let message = JsonNode.Parse reply
    let handshake = message.["handshake"]
    Assert.Equal("Accepted", handshake.["kind"].GetValue<string>())
    Assert.Equal(4, handshake.["protocol"].["minor"].GetValue<int>())
    Assert.Equal(0, handshake.["capabilities"].AsArray().Count)
    Assert.Equal(0, message.["effects"].AsArray().Count)
    Assert.Equal(0, message.["cancellations"].AsArray().Count)
    Assert.Equal(15, message.["view"].["items"].AsArray().Count)

[<Fact>]
let ``a contract or protocol the engine was not written against is refused, precisely`` () =
    let _, aegis = collector ()
    let _, mismatch = Wire.handle aegis Wire.initial (offer "sha256:other" 1)
    let handshake = (JsonNode.Parse mismatch).["handshake"]
    Assert.Equal("Rejected", handshake.["kind"].GetValue<string>())
    Assert.Equal("ContractMismatch", handshake.["reason"].["kind"].GetValue<string>())

    let _, protocol = Wire.handle aegis Wire.initial (offer Limen.core.Fingerprint 2)
    Assert.Equal("ProtocolUnsupported", (JsonNode.Parse protocol).["handshake"].["reason"].["kind"].GetValue<string>())

[<Fact>]
let ``an answer event reaches the session and the reply projects it`` () =
    let _, aegis = collector ()
    let _, reply = run aegis [ initialize; event "answered" (Some "CORE-003") (Some "4") ]
    let row = (viewOf reply).["items"].AsArray() |> Seq.find (fun r -> r.["id"].GetValue<string>() = "CORE-003")
    Assert.True(row.["is4"].GetValue<bool>())
    Assert.Equal("1 of 15 answered", text "progress" reply)

[<Fact>]
let ``an unchecked radio says nothing about the answer; a checked one sets it`` () =
    let _, aegis = collector ()

    // What the kernel sends when a form holding the radios is submitted: every
    // radio of the item, the unchecked ones with checked=false.
    let _, reply =
        run
            aegis
            [ initialize
              eventWith "answered" (Some "CORE-001") (Some "3") (Some true)
              eventWith "answered" (Some "CORE-001") (Some "0") (Some false)
              eventWith "answered" (Some "CORE-002") (Some "dont-know") (Some false) ]

    let row id = (viewOf reply).["items"].AsArray() |> Seq.find (fun r -> r.["id"].GetValue<string>() = id)
    Assert.Equal("3", (row "CORE-001").["answer"].GetValue<string>())
    Assert.Equal("", (row "CORE-002").["answer"].GetValue<string>())
    Assert.Equal("1 of 15 answered", text "progress" reply)

[<Fact>]
let ``asking for results too early is a typed refusal: shown as an alert, never an Aegis fault`` () =
    let sink, aegis = collector ()
    let _, reply = run aegis [ initialize; event "resultsRequested" None None ]
    Assert.True(flag "hasRefusal" reply)
    Assert.False(flag "hasOperationalFault" reply)
    Assert.Empty(sink.Events)

[<Fact>]
let ``an answer code or item the assessment does not define is an Aegis fault, presented safely`` () =
    let sink, aegis = collector ()
    let state, answered = run aegis [ initialize; event "answered" (Some "CORE-001") (Some "2") ]
    let state, badCode = Wire.handle aegis state (event "answered" (Some "CORE-001") (Some "7"))
    Assert.True(flag "hasOperationalFault" badCode)
    Assert.Contains("could not read", text "operationalFaultMessage" badCode)
    Assert.NotEqual<string>("", text "operationalFaultReference" badCode)
    // Safe presentation only: no exception names or paths reach the page.
    Assert.DoesNotContain("MalformedInput", badCode)
    Assert.DoesNotContain("$.event", badCode)
    // The session keeps the state it had.
    Assert.Equal(text "progress" answered, text "progress" badCode)

    let state, badItem = Wire.handle aegis state (event "answered" (Some "CORE-999") (Some "2"))
    Assert.True(flag "hasOperationalFault" badItem)
    Assert.Equal<string list>([ Boundary.MessageInvalid; Boundary.MessageInvalid ], recordedCodes sink)

    // The next message clears the fault.
    let _, next = Wire.handle aegis state (event "answered" (Some "CORE-001") (Some ""))
    Assert.False(flag "hasOperationalFault" next)
    Assert.Equal("0 of 15 answered", text "progress" next)

[<Fact>]
let ``a message that is not JSON is classified as an invalid message`` () =
    let sink, aegis = collector ()
    let _, reply = Wire.handle aegis Wire.initial "{not json"
    Assert.True(flag "hasOperationalFault" reply)
    Assert.Equal<string list>([ Boundary.MessageInvalid ], recordedCodes sink)

[<Fact>]
let ``a result for an effect the engine never requested is an invalid message`` () =
    let sink, aegis = collector ()

    let _, reply =
        run
            aegis
            [ initialize
              """{"kind":"EffectResult","result":{"kind":"HttpResult","correlationId":"c1","outcome":{"kind":"Cancelled"}}}""" ]

    Assert.True(flag "hasOperationalFault" reply)
    Assert.Equal<string list>([ Boundary.MessageInvalid ], recordedCodes sink)

[<Fact>]
let ``an event the page does not know is a defect: it fails loudly and is not turned into a fault`` () =
    let sink, aegis = collector ()
    let state, _ = Wire.handle aegis Wire.initial initialize
    Assert.Throws<InvalidOperationException>(fun () -> Wire.handle aegis state (event "noSuchEvent" None None) |> ignore)
    |> ignore
    Assert.Empty(sink.Events)
