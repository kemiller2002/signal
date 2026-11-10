/// The survey page's scale questions and focus (SIG-ECHELON-APP-002 (d),
/// signal#139 review, WI-0138): a scale question is Forma's ordinal scale on
/// its question's row, answered by its index; focus moves to the top of a new
/// screen where the kernel offers the focus pack; #activity announces no
/// selection guidance on opening a page. Split from SurveyPageTests, whose
/// wire helpers it repeats (they are private there).
module Echelon.Signal.Tests.SurveyScaleFocusTests

open System
open System.Text.Json.Nodes
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Application

let private demo = SurveyPageTests.demo
let private demoBytes = snd (publishedFile SurveyPageTests.DemoSurvey SurveyPageTests.DemoVersion demo)
let private opaque (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value

let private invitation () =
    "#r=" + GenericEnvelope.invitation SurveyPageTests.DemoSurvey SurveyPageTests.DemoVersion demo Import.IdentifiedGroup (opaque 1) (opaque 40)

let private aegis () =
    let sink = Aegis.Sinks.Collector()
    sink, { Boundary.configure [ sink.Sink() ] with Persistence = Aegis.Blocking }

let private initialize (hash: string) =
    $"""{{"kind":"Initialize","protocolVersion":1,"capabilities":["Http","Storage","Clipboard","Navigation"],
        "location":{{"origin":"https://signal.example","path":"/web/survey/","query":"","hash":"{hash}"}},
        "handshake":{{"protocol":{{"major":1,"minor":4}},
          "contract":{{"unit":"limen.core","version":1,"fingerprint":"{Limen.core.Fingerprint}"}},
          "capabilities":[]}}}}"""

let private httpResult (id: string) (outcome: string) =
    $"""{{"kind":"EffectResult","result":{{"kind":"HttpResult","correlationId":"{id}","outcome":{outcome}}}}}"""

let private found = $"""{{"kind":"Success","status":200,"body":"{Convert.ToBase64String demoBytes}"}}"""

let private event (name: string) (key: string) =
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}","key":"{key}","value":"on"}}}}"""

let private run messages =
    let _, config = aegis ()
    messages |> List.fold (fun (state, _) message -> SurveyWire.handle config state message) (SurveyWire.initial, "")

let private effects (reply: string) = (JsonNode.Parse reply).["effects"].AsArray() |> Seq.toList
let private viewOf (reply: string) = (JsonNode.Parse reply).["view"]
let private flag (name: string) (reply: string) = (viewOf reply).[name].GetValue<bool>()
let private text (name: string) (reply: string) = (viewOf reply).[name].GetValue<string>()

let private response =
    function
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private eventWith (name: string) (key: string) (value: string) =
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}","key":"{key}","value":"{value}"}}}}"""

let private rowOf (id: string) (reply: string) =
    (viewOf reply).["rows"].AsArray() |> Seq.find (fun row -> row.["id"].GetValue<string>() = id)

/// The kernel's handshake, offering the focus pack the survey page loads.
let private offeringFocus (hash: string) =
    let offer = "\"capabilities\":[{\"id\":\"limen.focus\",\"version\":1,\"fingerprint\":\"" + Limen.focus.Fingerprint + "\"}]"
    (initialize hash).Replace("\"capabilities\":[]", offer)

[<Fact>]
let ``a scale question is Forma's ordinal scale on its own row, answered by its index, its choice rows hidden`` () =
    let opened = [ initialize (invitation ()); httpResult "fetch-1" found ]
    let state, reply = run (opened @ [ eventWith "answered" "0" "3" ])
    let scale = rowOf "0" reply
    Assert.False(scale.["hideScale"].GetValue<bool>())
    Assert.Equal("forma-ordinal-scale forma-ordinal-scale--5", scale.["scaleClass"].GetValue<string>())
    Assert.Equal("Often", scale.["s3Label"].GetValue<string>())
    // The marker is the stored value (point 3 is stored as 3), beside its label.
    Assert.Equal("3", scale.["s3Marker"].GetValue<string>())
    Assert.Equal("true", scale.["s3MarkerHidden"].GetValue<string>())
    Assert.False(scale.["hideS3Label"].GetValue<bool>())
    Assert.True(scale.["s3Checked"].GetValue<bool>())
    Assert.False(scale.["hideS4"].GetValue<bool>())
    Assert.True(scale.["hideS5"].GetValue<bool>())
    // Its special answers follow its points, valued by their index.
    Assert.Equal("Don't know", scale.["x0Label"].GetValue<string>())
    Assert.Equal("5", scale.["x0Value"].GetValue<string>())
    Assert.Equal("Not applicable", scale.["x2Label"].GetValue<string>())
    Assert.True(scale.["hideX3"].GetValue<bool>())
    Assert.False(scale.["hideSpecials"].GetValue<bool>())
    Assert.Equal("q0", scale.["group"].GetValue<string>())
    // Its own radio group, apart from its hidden choice rows' radios.
    Assert.Equal("q0-scale", scale.["scaleGroup"].GetValue<string>())
    Assert.Equal("", (rowOf "0-3" reply).["scaleGroup"].GetValue<string>())
    Assert.True((rowOf "0-3" reply).["hideRow"].GetValue<bool>())
    Assert.Equal(Some(Value(Point 3)), (response state.Session).Answers.TryFind "CORE-001")
    // A special answer by its index; the yes/no keeps its own rows.
    let state, reply = run (opened @ [ eventWith "answered" "0" "7"; event "answered" "3-1" ])
    Assert.Equal(Some(Responses.Special Responses.NotApplicable), (response state.Session).Answers.TryFind "CORE-001")
    Assert.True((rowOf "3" reply).["hideScale"].GetValue<bool>())
    Assert.False((rowOf "3-1" reply).["hideRow"].GetValue<bool>())
    Assert.Equal("2 of 5 answered", text "progress" reply)

[<Fact>]
let ``the page's first heading is where focus moves, and no guidance is announced on opening`` () =
    let _, reply = run [ initialize (invitation ()); httpResult "fetch-1" found ]
    let rows = (viewOf reply).["rows"].AsArray() |> Seq.toList
    Assert.Equal("page-start", rows.Head.["focusTarget"].GetValue<string>())
    Assert.False(rows.Head.["hideSection"].GetValue<bool>())
    Assert.Equal(1, rows |> List.filter (fun row -> row.["focusTarget"].GetValue<string>() <> "") |> List.length)
    Assert.Equal("", text "guidanceNotice" reply)
    // One page: no page section is shown.
    Assert.False(flag "hasPageSection" reply)

[<Fact>]
let ``focus moves to the submitted heading where the kernel offers the focus pack, and nowhere it does not`` () =
    let answers =
        [ eventWith "answered" "0" "3"; eventWith "answered" "1" "4"; eventWith "answered" "2" "2"; event "answered" "3-1"; event "answered" "4-2" ]

    let submit = [ event "submitRequested" "" ]
    let offered = [ offeringFocus (invitation ()); httpResult "fetch-1" found ]
    let _, handshake = run [ offeringFocus (invitation ()) ]
    let selected = (JsonNode.Parse handshake).["handshake"].["capabilities"].AsArray() |> Seq.map (fun c -> c.["id"].GetValue<string>()) |> List.ofSeq
    Assert.Equal<string list>([ "limen.focus" ], selected)

    let _, reply = run (offered @ answers @ submit)
    Assert.True(flag "submitted" reply)
    let focus = effects reply |> List.filter (fun e -> e.["kind"].GetValue<string>() = "Capability") |> List.exactlyOne
    Assert.Equal("limen.focus", focus.["capability"].GetValue<string>())
    Assert.Equal("submitted", focus.["request"].["target"].["name"].GetValue<string>())

    // Its result is taken; one for a move never asked for is a fault.
    let id = focus.["correlationId"].GetValue<string>()
    let result (id: string) =
        $"""{{"kind":"EffectResult","result":{{"kind":"CapabilityResult","correlationId":"{id}","capability":"limen.focus","outcome":{{"kind":"Completed","result":{{"kind":"Done"}}}}}}}}"""
    let _, settled = run (offered @ answers @ submit @ [ result id ])
    Assert.False(flag "hasOperationalFault" settled)
    let _, stray = run (offered @ answers @ submit @ [ result id; result id ])
    Assert.True(flag "hasOperationalFault" stray)

    // Without the pack: no focus request, the page still submits.
    let _, plain = run ([ initialize (invitation ()); httpResult "fetch-1" found ] @ answers @ submit)
    Assert.True(flag "submitted" plain)
    Assert.DoesNotContain(effects plain, fun e -> e.["kind"].GetValue<string>() = "Capability")

[<Fact>]
let ``a point's marker is its stored value beside a worded label, and a numeric label's own number`` () =
    // A worded point: the stored value as its marker, the words beside it.
    Assert.Equal(("0", false), GenericSessionView.pointShown 0 "Never or almost never")
    Assert.Equal(("4", false), GenericSessionView.pointShown 4 "Almost always")
    // A numeric label is the marker, shown once: the label's number, not the stored value.
    Assert.Equal(("7", true), GenericSessionView.pointShown 7 "7")
    Assert.Equal(("10", true), GenericSessionView.pointShown 10 " 10 ")
    Assert.Equal(("1", true), GenericSessionView.pointShown 0 "1")
    Assert.Equal(("-2", true), GenericSessionView.pointShown 0 "-2")
    Assert.Equal(("0", false), GenericSessionView.pointShown 0 "1 (never)")

let private numericScale (points: int) (labels: string list) : Template.Question =
    { Id = "Q1"
      Prompt = "How likely are you to recommend the team?"
      HelpText = None
      Answer = Primitives.Ordinal points
      Selector =
        { Preset = Selectors.GenericOrdinal
          Labels = labels
          Accessibility = Selectors.noAccessibility }
      SpecialStates = []
      Required = true
      Tags = [] }

let private fieldOf (name: string) (fields: (string * View.Scalar) list) = fields |> List.find (fst >> (=) name) |> snd

[<Fact>]
let ``an 11-point 0 to 10 rating shows each number once, as its marker`` () =
    let fields = GenericSessionView.scaleFields 0 (numericScale 11 [ for i in 0..10 -> string i ]) None
    Assert.Equal(View.Text "forma-ordinal-scale forma-ordinal-scale--11", fieldOf "scaleClass" fields)

    for n in 0..10 do
        Assert.Equal(View.Text(string n), fieldOf $"s{n}Marker" fields)
        Assert.Equal(View.Flag true, fieldOf $"hideS{n}Label" fields)
        // The marker names the point: it is not hidden from assistive technology.
        Assert.Equal(View.Text "false", fieldOf $"s{n}MarkerHidden" fields)

[<Fact>]
let ``a 1 to 10 rating shows its labels' numbers, never the stored 0 to 9`` () =
    let fields = GenericSessionView.scaleFields 0 (numericScale 10 [ for i in 1..10 -> string i ]) None
    Assert.Equal(View.Text "forma-ordinal-scale forma-ordinal-scale--10", fieldOf "scaleClass" fields)

    for n in 0..9 do
        Assert.Equal(View.Text(string (n + 1)), fieldOf $"s{n}Marker" fields)
        Assert.Equal(View.Flag true, fieldOf $"hideS{n}Label" fields)
