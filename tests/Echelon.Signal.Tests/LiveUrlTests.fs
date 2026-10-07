/// The live respondent URL (WI-0032): LURL-001, ARX-007, URLC-001. The
/// engine keeps the URL fragment equal to the session through Limen's
/// Navigation `replace`, and a reopened URL resumes the same state.
module Echelon.Signal.Tests.LiveUrlTests

open System.Text.Json.Nodes
open Xunit
open Aegis
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Application

let private pilot = Pilot.assessment

let private collector () =
    let sink = Sinks.Collector()
    sink, { Boundary.configure [ sink.Sink() ] with Persistence = Blocking }

let private initializeAt (path: string) (query: string) (hash: string) =
    $"""{{"kind":"Initialize","protocolVersion":1,"capabilities":["Http","Storage","Clipboard","Navigation"],
        "location":{{"origin":"http://localhost","path":"{path}","query":"{query}","hash":"{hash}"}},
        "handshake":{{"protocol":{{"major":1,"minor":4}},
          "contract":{{"unit":"limen.core","version":1,"fingerprint":"{Limen.core.Fingerprint}"}},
          "capabilities":[]}}}}"""

let private answered (item: string) (code: string) =
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"answered","key":"{item}","value":"{code}","checked":true}}}}"""

let private navigationResult (id: string) (outcome: string) =
    $"""{{"kind":"EffectResult","result":{{"kind":"NavigationResult","correlationId":"{id}","outcome":{outcome}}}}}"""

let private locationChanged (hash: string) =
    $"""{{"kind":"LocationChanged","location":{{"origin":"http://localhost","path":"/web/","query":"","hash":"{hash}"}}}}"""

let private run aegis messages =
    messages |> List.fold (fun (state, _) message -> Wire.handle aegis state message) (Wire.initial, "")

let private effects (reply: string) = (JsonNode.Parse reply).["effects"].AsArray() |> Seq.toList
let private view (reply: string) = (JsonNode.Parse reply).["view"]
let private text (name: string) (reply: string) = (view reply).[name].GetValue<string>()
let private flag (name: string) (reply: string) = (view reply).[name].GetValue<bool>()

let private fragmentOf answers =
    LiveUrl.fragment pilot { Binding = Unbound; Answers = Map.ofList answers }

// ---------------------------------------------------------------------------
// Engine: where the state lives and how a URL is read.
// ---------------------------------------------------------------------------

[<Fact>]
let ``the state is in the fragment, never the path or query`` () =
    let envelope = { Binding = Unbound; Answers = Map.ofList [ "CORE-001", Rated Often ] }
    let url = LiveUrl.urlFor pilot "/web/" "?lang=en" envelope
    Assert.StartsWith("/web/?lang=en#r=", url)
    Assert.Equal(LiveUrl.Saved envelope, LiveUrl.read pilot (url.Substring(url.IndexOf '#')))

[<Fact>]
let ``a URL without saved answers is a fresh start, and unreadable answers are named`` () =
    Assert.Equal(LiveUrl.NoSavedState, LiveUrl.read pilot "")
    Assert.Equal(LiveUrl.NoSavedState, LiveUrl.read pilot "#section-2")
    Assert.Equal(LiveUrl.Unreadable NotBase64Url, LiveUrl.read pilot "#r=***")
    let good = (fragmentOf [ "CORE-001", Rated Often ]).Substring 1
    Assert.Equal(LiveUrl.Unreadable NotBase64Url, LiveUrl.read pilot $"#{good}&{good}")
    Assert.Equal(LiveUrl.Saved { Binding = Unbound; Answers = Map.ofList [ "CORE-001", Rated Often ] }, LiveUrl.read pilot $"#x=1&{good}")

[<Fact>]
let ``resuming restores answers and binding; starting over keeps the invitation`` () =
    let id seed = (OpaqueId.ofBytes (Array.create 16 seed)).Value
    let envelope = { Binding = IdentifiedInvitation(id 1uy, id 2uy); Answers = Map.ofList [ "CORE-004", Withheld NotObserved ] }
    let resumed = Session.start pilot |> Session.update (Session.Resumed envelope)
    Assert.Equal(envelope, Session.envelope resumed)
    let restarted = resumed |> Session.update Session.Restarted
    Assert.Equal(envelope.Binding, restarted.Binding)
    Assert.True(restarted.Answers.IsEmpty)

// ---------------------------------------------------------------------------
// Application: the replace effect, resume, results and notices.
// ---------------------------------------------------------------------------

[<Fact>]
let ``each answer requests one history replace whose URL carries exactly the session`` () =
    let _, aegis = collector ()
    let state, first = run aegis [ initializeAt "/web/" "?lang=en" ""; answered "CORE-001" "3" ]

    match effects first with
    | [ effect ] ->
        Assert.Equal("Navigation", effect.["kind"].GetValue<string>())
        Assert.Equal("replace", effect.["operation"].GetValue<string>())
        let url = effect.["url"].GetValue<string>()
        Assert.Equal("/web/?lang=en" + fragmentOf [ "CORE-001", Rated Often ], url)
    | other -> failwith $"expected one effect, got {other.Length}"

    // A message that changes nothing requests nothing.
    let _, refused = Wire.handle aegis state """{"kind":"Event","event":{"kind":"Event","name":"resultsRequested"}}"""
    Assert.Empty(effects refused)

[<Fact>]
let ``a reopened URL resumes the same answers, view and scores`` () =
    let _, aegis = collector ()
    let answers = pilot.Items |> List.map (fun item -> item.Id, Rated Sometimes)
    let _, original = run aegis (initializeAt "/" "" "" :: (answers |> List.map (fun (id, _) -> answered id "2")))
    let _, reopened = run aegis [ initializeAt "/" "" (fragmentOf answers) ]
    Assert.Equal("15 of 15 answered", text "progress" reopened)
    Assert.Equal((view original).["items"].ToJsonString(), (view reopened).["items"].ToJsonString())
    Assert.Equal((view original).["results"].ToJsonString(), (view reopened).["results"].ToJsonString())
    // Resuming is not a change: the URL already says this.
    Assert.Empty(effects reopened)

[<Fact>]
let ``a damaged link is an ordinary notice: nothing is restored, guessed or faulted`` () =
    let sink, aegis = collector ()
    let good = fragmentOf [ "CORE-001", Rated Often ]
    let damaged = good.Substring(0, good.Length - 3)
    let _, reply = run aegis [ initializeAt "/" "" damaged ]
    Assert.True(flag "hasNotice" reply)
    Assert.Contains("were not restored", text "notice" reply)
    Assert.Equal("0 of 15 answered", text "progress" reply)
    Assert.False(flag "hasOperationalFault" reply)
    Assert.Empty(sink.Events)
    // The damaged link is not overwritten until the respondent answers.
    Assert.Empty(effects reply)

[<Fact>]
let ``navigating to another saved state adopts it: the URL is authoritative`` () =
    let _, aegis = collector ()
    let _, reply = run aegis [ initializeAt "/web/" "" ""; answered "CORE-001" "3"; locationChanged (fragmentOf [ "CORE-002", Withheld DontKnow ]) ]
    let row id = (view reply).["items"].AsArray() |> Seq.find (fun r -> r.["id"].GetValue<string>() = id)
    Assert.Equal("", (row "CORE-001").["answer"].GetValue<string>())
    Assert.Equal("dont-know", (row "CORE-002").["answer"].GetValue<string>())

[<Fact>]
let ``navigation results are matched to requests; stale or unrequested ones are refused`` () =
    let sink, aegis = collector ()
    let state, first = run aegis [ initializeAt "/" "" ""; answered "CORE-001" "3" ]
    let id = (effects first).Head.["correlationId"].GetValue<string>()
    let success = """{"kind":"Success","location":{"origin":"http://localhost","path":"/","query":"","hash":"#r=x"}}"""
    let state, ok = Wire.handle aegis state (navigationResult id success)
    Assert.False(flag "hasOperationalFault" ok)
    Assert.Empty(sink.Events)
    // The same result again is stale.
    let _, stale = Wire.handle aegis state (navigationResult id success)
    Assert.True(flag "hasOperationalFault" stale)
    Assert.Equal(1, sink.Events.Length)

[<Fact>]
let ``a refused URL update tells the respondent the link is behind`` () =
    let sink, aegis = collector ()
    let state, first = run aegis [ initializeAt "/" "" ""; answered "CORE-001" "3" ]
    let id = (effects first).Head.["correlationId"].GetValue<string>()
    let state, failed = Wire.handle aegis state (navigationResult id """{"kind":"Failure","reason":"unavailable"}""")
    Assert.True(flag "hasUrlNotice" failed)
    Assert.Equal("1 of 15 answered", text "progress" failed)
    Assert.Empty(sink.Events)
    // The next successful update clears it.
    let _, second = Wire.handle aegis state (answered "CORE-002" "1")
    let id2 = (effects second).Head.["correlationId"].GetValue<string>()
    let _, cleared =
        Wire.handle aegis (fst (Wire.handle aegis state (answered "CORE-002" "1"))) (navigationResult id2 """{"kind":"Success","location":{"origin":"o","path":"/","query":"","hash":""}}""")
    Assert.False(flag "hasUrlNotice" cleared)
