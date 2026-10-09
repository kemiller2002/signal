/// The survey page and the static template catalog (DF-SIGNAL-2026-0005,
/// WI-0078): the link names a published version by reference, the page
/// reads that version from published-templates/, and only a file that is
/// canonical and hashes to the reference is used. Anything else is refused,
/// with nothing tried in its place.
module Echelon.Signal.Tests.SurveyPageTests

open System
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Application
open Echelon.Signal.Tests.Support

// ---------------------------------------------------------------------------
// The demo survey the site publishes, and the browser tests' links to it.
// ---------------------------------------------------------------------------

let private option id label : ChoiceOption = { Id = id; Label = label; Score = None }

let private question id prompt answer preset labels special : Question =
    { Id = id
      Prompt = prompt
      HelpText = None
      Answer = answer
      Selector = { Preset = preset; Labels = labels }
      SpecialStates = special
      Required = true
      Tags = [] }

/// One scored section of SDRA questions, and one unscored section with a
/// yes/no and a single-choice question: the three kinds the page renders.
let demo: Content =
    let first = Pilot.content.Sections.Head

    { Pilot.content with
        Metadata =
            { Pilot.content.Metadata with
                Title = "Signal demo survey"
                ShortTitle = Some "signal-demo" }
        Sections =
            [ { first with Questions = first.Questions |> List.truncate 3 }
              { Id = "CONTEXT"
                Title = "Working context"
                Description = None
                Required = true
                Questions =
                  [ question "CTX-001" "The team released working software in the last eight weeks." Boolean Selectors.YesNo [ "No"; "Yes" ] []
                    question
                        "CTX-002"
                        "Where does the team mostly work?"
                        (SingleChoice [ option "remote" "Remote"; option "office" "In an office"; option "mixed" "A mix of both" ])
                        Selectors.RadioList
                        []
                        [ Declined ] ]
                Presentation = defaultSectionPresentation
                Scoring = None } ] }

let DemoSurvey = "signal-demo"
let DemoVersion = "1"

let private demoForm = GenericImport.formOf DemoSurvey DemoVersion demo
let private demoFile, demoBytes = publishedFile DemoSurvey DemoVersion demo
let private opaque (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private instance = opaque 1
let private group = opaque 40

let private invitation mode version =
    "#r=" + GenericEnvelope.invitation DemoSurvey version demo mode instance group

/// The links the browser tests open (tests/browser/fixtures/survey-links.json).
let private links =
    [ "templateFile", demoFile
      "identified", "web/survey/" + invitation Import.IdentifiedGroup DemoVersion
      "anonymous", "web/survey/" + invitation Import.AnonymousGroup DemoVersion
      // A version this site does not publish: its file is missing.
      "unpublished", "web/survey/" + invitation Import.IdentifiedGroup "2"
      // A test link (AUT-006 §60): marked on the page and refused by production import.
      "test", "web/survey/#r=" + GenericEnvelope.testLink DemoSurvey DemoVersion demo Import.IdentifiedGroup instance group Map.empty ]

let private linksJson () =
    let o = JsonObject()
    links |> List.iter (fun (name, value) -> o[name] <- JsonValue.Create value)
    o.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)) + "\n"

/// SIGNAL_WRITE_FIXTURES=1 rewrites the committed files from the code; the
/// assertions then hold them equal.
[<Fact>]
let ``the demo survey file and the browser links are what the code produces`` () =
    if Environment.GetEnvironmentVariable "SIGNAL_WRITE_FIXTURES" = "1" then
        File.WriteAllBytes(repoFile demoFile, demoBytes)
        File.WriteAllText(repoFile "tests/browser/fixtures/survey-links.json", linksJson ())

    Assert.Equal<byte[]>(demoBytes, File.ReadAllBytes(repoFile demoFile))
    Assert.Equal(linksJson (), readRepoFile "tests/browser/fixtures/survey-links.json")

[<Fact>]
let ``every file the site publishes is a canonical template named by its own reference`` () =
    let files = Directory.GetFiles(repoFile CatalogFolder)
    Assert.NotEmpty files

    for path in files do
        let name = Path.GetFileName path
        Assert.Matches(Regex "^[0-9a-f]{16}\\.json$", name)
        let reference = Convert.FromHexString(name.Substring(0, 16))

        match verify reference (File.ReadAllBytes path) with
        | Ok _ -> ()
        | Error refusal -> failwith $"{name}: {describe refusal}"

// ---------------------------------------------------------------------------
// The session: link, fetch, verify, answer, submit.
// ---------------------------------------------------------------------------

let private loaded mode =
    match start (invitation mode DemoVersion) |> received (Found demoBytes) with
    | Responding r -> r
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a link names its catalog file and nothing about the respondent`` () =
    let session = start (invitation Import.IdentifiedGroup DemoVersion)
    Assert.Equal(Some demoFile, wanted session)
    Assert.Equal($"published-templates/{referenceHex demoForm.Reference}.json", demoFile)

[<Fact>]
let ``a page without a usable link is refused before anything is fetched`` () =
    Assert.Equal(Refused NoAnswerLink |> phaseName, start "" |> phaseName)
    Assert.Equal(None, wanted (start ""))
    Assert.Equal(None, wanted (start "#r=a&r=b"))
    Assert.Equal(None, wanted (start "#r=not-an-envelope"))
    let damaged = invitation Import.IdentifiedGroup DemoVersion
    let flipped = damaged.Substring(0, 10) + (if damaged[10] = 'A' then "B" else "A") + damaged.Substring 11
    Assert.Equal(None, wanted (start flipped))

[<Fact>]
let ``the published file is verified before it is used`` () =
    let r = loaded Import.IdentifiedGroup
    Assert.Equal(DemoSurvey, r.Published.SurveyId)
    Assert.Equal(demoForm.Hash, r.Published.Form.Hash)
    Assert.Equal(Answering, r.Phase)

let private refusalFor fetched =
    match start (invitation Import.IdentifiedGroup DemoVersion) |> received fetched with
    | Refused refusal -> refusal
    | other -> failwith $"not refused: %A{other}"

[<Fact>]
let ``a missing, altered, re-spelled or different file is refused with no fallback`` () =
    Assert.Equal(TemplateNotPublished, refusalFor NotFound)
    Assert.Equal(CatalogUnreachable "offline", refusalFor (Unreachable "offline"))
    // One label changed: still JSON, still a template, but not the published bytes.
    let altered = Text.Encoding.UTF8.GetBytes((Text.Encoding.UTF8.GetString demoBytes).Replace("In an office", "In an 0ffice"))
    Assert.Equal(WrongTemplate, refusalFor (Found altered))
    // Re-indented: the same template, not its canonical form.
    let reindented = Text.Encoding.UTF8.GetBytes(JsonNode.Parse(demoBytes).ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)))
    Assert.True(match refusalFor (Found reindented) with NotATemplate _ -> true | _ -> false)
    Assert.True(match refusalFor (Found [| 0x7Buy |]) with NotATemplate _ -> true | _ -> false)
    // A genuine published template, but another one.
    let _, other = publishedFile "SDRA" "1" Pilot.content
    Assert.Equal(WrongTemplate, refusalFor (Found other))
    Assert.All([ TemplateNotPublished; WrongTemplate; NotATemplate "x" ], fun r -> Assert.Contains("Ask the person who sent you the link", describe r))

[<Fact>]
let ``a template with a question this page cannot show is refused`` () =
    let section = demo.Sections[1]
    let ranking = question "CTX-003" "Rank these." (Ranking { Options = [ option "a" "A"; option "b" "B" ]; Positions = None }) Selectors.RankingList [] []
    let content = { demo with Sections = [ demo.Sections[0]; { section with Questions = section.Questions @ [ ranking ] } ] }
    let file, bytes = publishedFile DemoSurvey "3" content
    let session = start ("#r=" + GenericEnvelope.invitation DemoSurvey "3" content Import.IdentifiedGroup instance group)
    Assert.Equal(Some file, wanted session)
    Assert.Equal(Refused(UnsupportedAnswer "CTX-003") |> phaseName, received (Found bytes) session |> phaseName)
    Assert.True(match received (Found bytes) session with Refused(UnsupportedAnswer "CTX-003") -> true | _ -> false)

let private answerAll (r: Response) =
    questions demo
    |> List.map snd
    |> List.fold (fun session q -> update (Chose(q.Id, fst (choices q).Head)) session) (Responding r)

let private response =
    function
    | Responding r -> r
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an incomplete response is not submitted, and says what is missing`` () =
    let untouched = loaded Import.IdentifiedGroup |> Responding |> update (SubmitRequested(Array.zeroCreate 16)) |> response
    Assert.Equal(Answering, untouched.Phase)
    Assert.Equal(Some "Answer the questions before you submit.", untouched.Refusal)

    let r =
        loaded Import.IdentifiedGroup |> Responding |> update (Chose("CTX-001", Value(Flag true))) |> update (SubmitRequested(Array.zeroCreate 16)) |> response

    Assert.Equal(Answering, r.Phase)
    Assert.Equal(Some "4 required questions still need an answer.", r.Refusal)

[<Fact>]
let ``a choice the question does not offer changes nothing`` () =
    let r = loaded Import.IdentifiedGroup
    let after = update (Chose("CTX-001", Value(Point 3))) (Responding r) |> response
    Assert.True(after.Answers.IsEmpty)
    Assert.Equal(None, choiceFor r "99-0")
    Assert.Equal(None, choiceFor r "nonsense")
    Assert.Equal(Some("CTX-002", Special Declined), choiceFor r "4-3")

let private definition mode : Import.GroupDefinition =
    { Group = group
      Mode = mode
      ExpectedCount = 1
      Template = GenericImport.shapeOf DemoSurvey DemoVersion demo
      Generic = Some demoForm }

[<Fact>]
let ``an identified submission keeps its invitation ids and imports`` () =
    let submitted = loaded Import.IdentifiedGroup |> answerAll |> update (SubmitRequested(Array.zeroCreate 16)) |> response
    Assert.Equal(Submitted, submitted.Phase)
    Assert.Equal(Identified(instance, group), submitted.Binding)
    // Sealed: a later choice changes nothing.
    Assert.Equal<Answers>(submitted.Answers, (update (Chose("CTX-001", Value(Flag true))) (Responding submitted) |> response).Answers)

    match GenericImport.evaluate (definition Import.IdentifiedGroup) (fun _ -> None) ("https://signal.example/web/survey/" + fragment submitted) with
    | Import.Accepted accepted -> Assert.Equal(5, accepted.AnsweredCount)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an anonymous submission carries a fresh id and not the instance`` () =
    let entropy = Array.init 16 (fun i -> byte (200 + i))
    let submitted = loaded Import.AnonymousGroup |> answerAll |> update (SubmitRequested entropy) |> response
    Assert.Equal(Anonymous((OpaqueId.ofBytes entropy).Value, group), submitted.Binding)
    let refused = loaded Import.AnonymousGroup |> answerAll |> update (SubmitRequested(OpaqueId.toBytes instance)) |> response
    Assert.Equal(Answering, refused.Phase)

    match GenericImport.evaluate (definition Import.AnonymousGroup) (fun _ -> None) ("https://signal.example/web/survey/" + fragment submitted) with
    | Import.Accepted _ -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a submitted link opens sealed`` () =
    let submitted = loaded Import.IdentifiedGroup |> answerAll |> update (SubmitRequested(Array.zeroCreate 16)) |> response

    match start (fragment submitted) |> received (Found demoBytes) with
    | Responding r ->
        Assert.Equal(Submitted, r.Phase)
        Assert.Equal<Answers>(submitted.Answers, r.Answers)
    | other -> failwith $"%A{other}"

// ---------------------------------------------------------------------------
// The wire: one same-origin GET, then the session.
// ---------------------------------------------------------------------------

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

[<Fact>]
let ``the page asks for one relative GET of its catalog file, bytes exact and without credentials`` () =
    let _, reply = run [ initialize (invitation Import.IdentifiedGroup DemoVersion) ]
    let fetch = effects reply |> List.exactlyOne
    Assert.Equal("Http", fetch.["kind"].GetValue<string>())
    Assert.Equal("GET", fetch.["method"].GetValue<string>())
    Assert.Equal("../../" + demoFile, fetch.["url"].GetValue<string>())
    Assert.Equal("base64", fetch.["response"].GetValue<string>())
    Assert.Equal("omit", fetch.["credentials"].GetValue<string>())
    Assert.True(flag "loading" reply)

[<Fact>]
let ``the fetched survey is shown, and answering keeps the URL equal to the response`` () =
    let _, reply = run [ initialize (invitation Import.IdentifiedGroup DemoVersion); httpResult "fetch-1" found ]
    Assert.True(flag "answering" reply)
    Assert.Equal("Signal demo survey", text "surveyTitle" reply)
    Assert.Equal("0 of 5 answered", text "progress" reply)
    let rows = (viewOf reply).["rows"].AsArray()
    // Two section headings, five questions and their choices.
    Assert.Equal(2 + 5 + 3 * 8 + 2 + 4, rows.Count)

    let state, reply = run [ initialize (invitation Import.IdentifiedGroup DemoVersion); httpResult "fetch-1" found; event "answered" "3-1" ]
    Assert.Equal("1 of 5 answered", text "progress" reply)
    let replace = effects reply |> List.exactlyOne
    Assert.Equal("Navigation", replace.["kind"].GetValue<string>())
    Assert.StartsWith("/web/survey/#r=", replace.["url"].GetValue<string>())
    Assert.Equal(Some(Value(Flag true)), (response state.Session).Answers.TryFind "CTX-001")

[<Fact>]
let ``a missing or altered file is refused on the page`` () =
    let _, missing = run [ initialize (invitation Import.IdentifiedGroup DemoVersion); httpResult "fetch-1" """{"kind":"Success","status":404,"body":""}""" ]
    Assert.True(flag "refused" missing)
    Assert.Equal(describe TemplateNotPublished, text "refusal" missing)
    let altered = Convert.ToBase64String(Array.append demoBytes [| 0x20uy |])
    let _, tampered = run [ initialize (invitation Import.IdentifiedGroup DemoVersion); httpResult "fetch-1" $"""{{"kind":"Success","status":200,"body":"{altered}"}}""" ]
    Assert.True(flag "refused" tampered)
    Assert.Contains("damaged or not in its published form", text "refusal" tampered)

[<Fact>]
let ``a result for a request the page never made is a fault, not a survey`` () =
    let sink, config = aegis ()
    let state, _ = SurveyWire.handle config SurveyWire.initial (initialize (invitation Import.IdentifiedGroup DemoVersion))
    let state, reply = SurveyWire.handle config state (httpResult "fetch-9" found)
    Assert.True(flag "hasOperationalFault" reply)
    Assert.True(flag "loading" reply)
    Assert.NotEmpty sink.Events
    // The assessment page never fetches: any Http result is a fault there too.
    let _, pilot = Wire.handle config Wire.initial (httpResult "fetch-1" found)
    Assert.True((viewOf pilot).["hasOperationalFault"].GetValue<bool>())

[<Fact>]
let ``the survey page binds only what its engine projects, sends only what it handles, and keeps connect-src to self`` () =
    let html = readRepoFile "web/survey/index.html"

    let values attribute =
        Regex.Matches(html, $"\\s{attribute}=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

    let bound =
        Regex.Matches(html, "\\sdata-bind-[a-z-]+=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq
        |> Set.union (Set.unionMany [ values "data-text"; values "data-if"; values "data-each"; values "data-key" ])

    let _, reply = run [ initialize (invitation Import.IdentifiedGroup DemoVersion); httpResult "fetch-1" found ]
    let view = viewOf reply :?> JsonObject
    let offered =
        view
        |> Seq.collect (fun pair ->
            match pair.Value with
            | :? JsonArray as items -> pair.Key :: (items |> Seq.collect (fun i -> (i :?> JsonObject) |> Seq.map _.Key) |> List.ofSeq)
            | _ -> [ pair.Key ])
        |> Set.ofSeq

    Assert.Empty(Set.difference bound offered)
    Assert.Equal<Set<string>>(SurveyWire.events, values "data-event")
    let policy = Regex.Match(html, "http-equiv=\"Content-Security-Policy\" content=\"([^\"]+)\"").Groups[1].Value
    Assert.Contains("connect-src 'self';", policy)
    Assert.Contains("default-src 'self';", policy)

[<Fact>]
let ``the console saves a published version as the site's file`` () =
    let path, bytes = publishedFile DemoSurvey DemoVersion demo
    Assert.Equal(demoFile, path)
    Assert.Equal<byte[]>(TemplateCanonical.bytes DemoSurvey DemoVersion demo, bytes)
    Assert.Contains("data-event=\"downloadSiteFile\"", readRepoFile "web/admin/index.html")
