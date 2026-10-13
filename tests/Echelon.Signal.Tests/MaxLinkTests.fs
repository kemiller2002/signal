/// The longest respondent link a published template can have (WI-0081,
/// AUT-006 §62, CAN-004 §26): a template filled with optional ranking
/// questions until its largest response is as close to the URL budget as
/// publication allows. The cross-browser workflow opens it on WebKit and
/// Firefox (tests/browser/survey-max-link.spec.js).
module Echelon.Signal.Tests.MaxLinkTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Template
open Echelon.Signal.Tests.Support

let private policy = Validation.defaultPolicy

/// The payload budget: the URL budget less the allowance for origin and path.
let private budget = policy.MaximumUrlCharacters - policy.BaseUrlAllowance

let private ranking (i: int) : Question =
    { Id = $"R{i:D3}"
      Prompt = $"Put these in order ({i})."
      HelpText = None
      Answer = Ranking { Options = [ for o in 1..8 -> { Id = $"o{o}"; Label = $"Option {o}"; Score = None } ]; Positions = None }
      Selector = { Preset = Selectors.RankingList; Labels = []; Accessibility = Selectors.noAccessibility }
      SpecialStates = []
      Required = false
      Tags = [] }

let private withQuestions (n: int) : Content =
    let demo = SurveyPageTests.demo

    { demo with
        Metadata = { demo.Metadata with Title = "The longest link" }
        Presentation = { demo.Presentation with ItemsPerPage = Some 40 }
        Sections =
            [ { demo.Sections[1] with
                  Id = "RANKS"
                  Title = "Rankings"
                  Required = false
                  Scoring = None
                  Questions = [ for i in 1..n -> ranking i ] } ] }

/// As many ranking questions as fit the budget.
let content =
    Seq.initInfinite id
    |> Seq.skip 1
    |> Seq.takeWhile (fun n -> (Layout.capacity (withQuestions n)).EncodedCharacters <= budget)
    |> Seq.last
    |> withQuestions

let private instance = (UrlState.OpaqueId.ofBytes (Array.init 16 (fun i -> byte (1 + i)))).Value
let private group = (UrlState.OpaqueId.ofBytes (Array.init 16 (fun i -> byte (40 + i)))).Value
let private payload = GenericEnvelope.invitation "MAXLINK" "1" content Import.AnonymousGroup instance group

[<Fact>]
let ``the longest link fits the budget, and one more question would not`` () =
    let size = Layout.capacity content
    Assert.True(size.EncodedCharacters <= budget)
    Assert.True((Layout.capacity (withQuestions (content.Sections.Head.Questions.Length + 1))).EncodedCharacters > budget)
    Assert.True(payload.Length <= size.EncodedCharacters)
    // Within a few characters of the payload budget: the terms a link may add are the only slack.
    Assert.True(budget - payload.Length < Layout.TermsOverheadBytes * 2, $"{payload.Length} of {budget}")
    let report = Validation.validate policy { SurveyId = "MAXLINK"; Parent = None; Content = content; Fixtures = [] }
    Assert.DoesNotContain("ENCODING-URL-BUDGET", report.Findings |> List.map _.Code)

let private links =
    [ "maxLink", "web/survey/#r=" + payload
      "maxLinkFile", fst (GenericSession.publishedFile "MAXLINK" "1" content) ]

let private linksJson () =
    let o = Text.Json.Nodes.JsonObject()
    links |> List.iter (fun (name, value) -> o[name] <- Text.Json.Nodes.JsonValue.Create value)
    o.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)) + "\n"

/// SIGNAL_WRITE_FIXTURES=1 rewrites the committed files from the code.
[<Fact>]
let ``the longest link's survey file and browser link are what the code produces`` () =
    let path, bytes = GenericSession.publishedFile "MAXLINK" "1" content

    if Environment.GetEnvironmentVariable "SIGNAL_WRITE_FIXTURES" = "1" then
        IO.File.WriteAllBytes(repoFile path, bytes)
        IO.File.WriteAllText(repoFile "tests/browser/fixtures/max-link.json", linksJson ())

    Assert.Equal<byte[]>(bytes, IO.File.ReadAllBytes(repoFile path))
    Assert.Equal(linksJson (), readRepoFile "tests/browser/fixtures/max-link.json")
