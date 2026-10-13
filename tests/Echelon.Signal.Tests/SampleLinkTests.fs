/// Sample links an author tries on other devices and sends through email,
/// messaging or copy and paste (WI-0081, AUT-006 §§61-62): every link the
/// console gives for a published version uses only URL-safe characters and
/// fits the publication policy's URL budget on the respondent site, for
/// every demo survey the site publishes.
module Echelon.Signal.Tests.SampleLinkTests

open System
open System.Text.RegularExpressions
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Template

/// The respondent site's survey page, as a person would receive the link.
let private site = "https://signal.echelonfoundry.com/web/survey/"

let private published (surveyId: string) (content: Content) : Publication.Published =
    let midpoint: Drafts.Fixture =
        { Id = "midpoint"
          Name = "Every answer at the midpoint"
          Answers =
            questions content
            |> List.choose (fun (_, q) -> GenericSession.choices q |> List.tryHead |> Option.map (fun (state, _) -> q.Id, state))
            |> Map.ofList
          Expect = [] }

    { SurveyId = surveyId
      Version = "1"
      Hash = (GenericImport.formOf surveyId "1" content).Hash
      Content = content
      Parent = None
      PublishedAt = DateTimeOffset.UnixEpoch
      PublishedBy = "author"
      Manifest = []
      Fixtures = [ midpoint ] }

let private surveys =
    [ "DEMO", SurveyPageTests.demo
      "MIXED", SurveyPageTests.mixedEditor.Draft.Content
      "COMPOUND", SurveyPageTests.compoundEditor.Draft.Content
      "PAGED", SurveyPageTests.paged
      "BANKED", SurveyPageTests.banked
      "TIMED", SurveyPageTests.timed ]

[<Fact>]
let ``every sample link is URL-safe and fits the URL budget, on every published demo survey`` () =
    let budget = Validation.defaultPolicy.MaximumUrlCharacters
    let urlSafe = Regex("^[A-Za-z0-9_-]+$")

    for surveyId, content in surveys do
        let shown = Echelon.Signal.Admin.TestLinks.forVersion (published surveyId content)
        Assert.NotEmpty(shown.Links)

        for label, href in shown.Links do
            let payload = href.Substring(href.IndexOf "#r=" + 3)
            Assert.True(urlSafe.IsMatch payload, $"{surveyId} {label}: '{payload}' has a character a mail or chat client may change")
            Assert.True(payload.Length <= (Layout.capacity content).EncodedCharacters, $"{surveyId} {label}: longer than the template's capacity")
            let full = site + href.Substring Echelon.Signal.Admin.TestLinks.SurveyPage.Length
            Assert.True(full.Length <= budget, $"{surveyId} {label}: {full.Length} characters, over the {budget}-character budget")
