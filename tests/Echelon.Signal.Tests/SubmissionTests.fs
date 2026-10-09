/// Finalizing a response into a portable submission (WI-0033): LURL-002,
/// ID-002, ID-003 (randomness), ID-004, URLC-002, ARX-004 (entropy edge).
module Echelon.Signal.Tests.SubmissionTests

open System
open System.Text.Json.Nodes
open Xunit
open Aegis
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Application

let private pilot = Pilot.assessment
let private id (seed: byte) = (OpaqueId.ofBytes (Array.init 16 (fun i -> seed + byte i))).Value
let private instance = id 10uy
let private group = id 100uy
let private complete = pilot.Items |> List.map (fun item -> item.Id, Rated Often) |> Map.ofList
let private entropy (seed: byte) = Array.init 16 (fun i -> seed ^^^ byte (i * 7))

[<Fact>]
let ``an identified response keeps its instance and group`` () =
    let live = { Binding = IdentifiedInvitation(instance, group); Answers = complete }
    Assert.Equal(Ok { live with Binding = Identified(instance, group) }, Submission.finalize pilot (entropy 1uy) live)

[<Fact>]
let ``an anonymous response gets a fresh id and loses its instance, byte for byte`` () =
    let live = { Binding = AnonymousInvitation(instance, group); Answers = complete }

    match Submission.finalize pilot (entropy 1uy) live with
    | Ok submission ->
        Assert.Equal(Anonymous((OpaqueId.ofBytes (entropy 1uy)).Value, group), submission.Binding)
        Assert.Equal<Answers>(complete, submission.Answers)
        Assert.True(Submission.isUnlinkable pilot instance submission)
        // The live URL did carry the instance; the submission does not.
        Assert.False(Submission.isUnlinkable pilot instance live)
        Assert.DoesNotContain(string instance, encode pilot submission)
    | Error refusal -> failwith $"refused: {refusal}"

[<Fact>]
let ``the same response finalized twice is not linkable: the id comes only from entropy`` () =
    let live = { Binding = AnonymousInvitation(instance, group); Answers = complete }
    let first = Submission.finalize pilot (entropy 1uy) live
    let second = Submission.finalize pilot (entropy 2uy) live
    Assert.NotEqual(first, second)

[<Fact>]
let ``unusable entropy is refused rather than producing a linkable id`` () =
    let live = { Binding = AnonymousInvitation(instance, group); Answers = complete }
    Assert.Equal(Error Submission.UnusableEntropy, Submission.finalize pilot (OpaqueId.toBytes instance) live)
    Assert.Equal(Error Submission.UnusableEntropy, Submission.finalize pilot (OpaqueId.toBytes group) live)
    Assert.Equal(Error Submission.UnusableEntropy, Submission.finalize pilot (Array.zeroCreate 15) live)

[<Fact>]
let ``incomplete, uninvited and already-final responses are refused with a reason`` () =
    let partial = complete.Remove "CORE-001" |> fun m -> m.Remove "CORE-002"
    Assert.Equal(Error(Submission.Incomplete 2), Submission.finalize pilot (entropy 1uy) { Binding = AnonymousInvitation(instance, group); Answers = partial })
    Assert.Equal(Error Submission.NoInvitation, Submission.finalize pilot (entropy 1uy) { Binding = Unbound; Answers = complete })
    Assert.Equal(Error Submission.AlreadyFinal, Submission.finalize pilot (entropy 1uy) { Binding = Anonymous(id 1uy, group); Answers = complete })
    Assert.Equal(Error Submission.AlreadyFinal, Submission.finalize pilot (entropy 1uy) { Binding = Identified(instance, group); Answers = complete })

[<Fact>]
let ``a submitted session is sealed, and reopening a submission shows it sealed`` () =
    let session =
        { Session.start pilot with Binding = AnonymousInvitation(instance, group); Answers = complete; Phase = Session.Reviewing }
        |> Session.update (Session.SubmitRequested(entropy 3uy))

    Assert.Equal(Session.Submitted, session.Phase)
    let sealedSession = session |> Session.update (Session.Answered("CORE-001", None)) |> Session.update Session.Restarted
    Assert.Equal<Answers>(session.Answers, sealedSession.Answers)
    Assert.Equal(Session.Submitted, sealedSession.Phase)

    let reopened = Session.start pilot |> Session.update (Session.Resumed(Session.envelope session))
    Assert.Equal(Session.Submitted, reopened.Phase)

[<Fact>]
let ``the secure edge draws sixteen fresh bytes each time`` () =
    let draws = List.init 1000 (fun _ -> Wire.secureEdge.Entropy())
    Assert.All(draws, fun d -> Assert.Equal(16, d.Length))
    Assert.Equal(1000, draws |> List.map Convert.ToHexString |> List.distinct |> List.length)
    // Every bit position takes both values across the draws (a stuck or
    // constant source fails this; it is not a statistical randomness proof).
    for bit in 0..127 do
        let values = draws |> List.map (fun d -> (d[bit / 8] >>> (bit % 8)) &&& 1uy) |> List.distinct
        Assert.Equal(2, values.Length)

// ---------------------------------------------------------------------------
// Through the Limen boundary.
// ---------------------------------------------------------------------------

let private collector () =
    let sink = Sinks.Collector()
    sink, { Boundary.configure [ sink.Sink() ] with Persistence = Blocking }

let private initializeAt (hash: string) =
    $"""{{"kind":"Initialize","protocolVersion":1,"capabilities":["Navigation","Clipboard"],
        "location":{{"origin":"https://signal.example","path":"/web/","query":"","hash":"{hash}"}},
        "handshake":{{"protocol":{{"major":1,"minor":4}},
          "contract":{{"unit":"limen.core","version":1,"fingerprint":"{Limen.core.Fingerprint}"}},
          "capabilities":[]}}}}"""

let private eventNamed (name: string) (key: string) (value: string) =
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}","key":"{key}","value":"{value}"}}}}"""

let private view (reply: string) = (JsonNode.Parse reply).["view"]
let private text (name: string) (reply: string) = (view reply).[name].GetValue<string>()
let private flag (name: string) (reply: string) = (view reply).[name].GetValue<bool>()
let private effects (reply: string) = (JsonNode.Parse reply).["effects"].AsArray() |> Seq.toList

let private fixedEdge = { Wire.Entropy = (fun () -> entropy 9uy); Wire.Today = fun () -> System.DateOnly(2026, 1, 1) }

let private submitAnonymously aegis =
    let invitation = LiveUrl.fragment pilot { Binding = AnonymousInvitation(instance, group); Answers = Map.empty }

    [ initializeAt invitation ]
    @ (pilot.Items |> List.map (fun item -> eventNamed "answered" item.Id "3"))
    @ [ eventNamed "resultsRequested" "" ""; eventNamed "submitRequested" "" "" ]
    |> List.fold (fun (state, _) message -> Wire.handle aegis state message) (Wire.initialWith fixedEdge, "")

[<Fact>]
let ``an invited respondent submits; the URL becomes the anonymous submission`` () =
    let sink, aegis = collector ()
    let _, reply = submitAnonymously aegis
    Assert.True(flag "submitted" reply)
    Assert.Equal("anonymous", text "submissionKind" reply)
    let link = text "submissionLink" reply
    Assert.StartsWith("https://signal.example/web/#r=", link)

    match LiveUrl.read pilot (link.Substring(link.IndexOf '#')) with
    | LiveUrl.Saved envelope ->
        Assert.Equal(Anonymous((OpaqueId.ofBytes (entropy 9uy)).Value, group), envelope.Binding)
        Assert.Equal(15, envelope.Answers.Count)
    | other -> failwith $"{other}"

    // The live URL is replaced by the submission, so the instance leaves it.
    let replaced = (effects reply).Head.["url"].GetValue<string>()
    Assert.Equal(link, "https://signal.example" + replaced)
    Assert.Empty(sink.Events)

[<Fact>]
let ``the submission link is copied through Limen's clipboard, and the outcome is reported`` () =
    let sink, aegis = collector ()
    let state, submitted = submitAnonymously aegis
    let state, copy = Wire.handle aegis state (eventNamed "copyRequested" "" "")

    match effects copy with
    | [ effect ] ->
        Assert.Equal("Clipboard", effect.["kind"].GetValue<string>())
        Assert.Equal("writeText", effect.["operation"].GetValue<string>())
        Assert.Equal(text "submissionLink" submitted, effect.["text"].GetValue<string>())
        let id = effect.["correlationId"].GetValue<string>()
        let result outcome = $"""{{"kind":"EffectResult","result":{{"kind":"ClipboardResult","correlationId":"{id}","outcome":{outcome}}}}}"""
        let after, copied = Wire.handle aegis state (result """{"kind":"Success"}""")
        Assert.StartsWith("Submission link copied", text "copyNotice" copied)
        Assert.Empty(sink.Events)
        // Answered once; the same result again is stale.
        let _, stale = Wire.handle aegis after (result """{"kind":"Success"}""")
        Assert.True(flag "hasOperationalFault" stale)
        let _, denied = Wire.handle aegis state (result """{"kind":"Failure","reason":"denied"}""")
        Assert.Contains("did not allow copying", text "copyNotice" denied)
    | other -> failwith $"expected one effect, got {other.Length}"

[<Fact>]
let ``an uninvited page cannot submit, and copying before submission does nothing`` () =
    let _, aegis = collector ()

    let _, reply =
        [ initializeAt "" ] @ (pilot.Items |> List.map (fun item -> eventNamed "answered" item.Id "1")) @ [ eventNamed "resultsRequested" "" ""; eventNamed "copyRequested" "" "" ]
        |> List.fold (fun (state, _) message -> Wire.handle aegis state message) (Wire.initialWith fixedEdge, "")

    Assert.True(flag "reviewing" reply)
    Assert.False(flag "canSubmit" reply)
    Assert.Equal("", text "submissionLink" reply)
    Assert.Empty(effects reply)
