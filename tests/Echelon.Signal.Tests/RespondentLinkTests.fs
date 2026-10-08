/// The respondent's answer link is an accepted, scoped exception to "no
/// answers in URLs" (DF-SIGNAL-2026-0003 decision 4, WI-0070): it lives
/// only on the respondent page, in the fragment, says plainly that it holds
/// the answers, is never logged, is never a route and is never what an
/// administrator view's Copy link produces.
module Echelon.Signal.Tests.RespondentLinkTests

open System
open System.Text.RegularExpressions
open Xunit
open Aegis
open Limen.Routing
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Application
open Echelon.Signal.Admin.Routes
open Echelon.Signal.Tests.Support

/// A real answer link's fragment: every pilot item answered "Often".
let private fragment =
    let answers = Pilot.assessment.Items |> List.map (fun i -> i.Id, Assessment.Rated Assessment.Often) |> Map.ofList
    LiveUrl.fragment Pilot.assessment { Binding = Unbound; Answers = answers }

[<Fact>]
let ``the respondent page says plainly that its address and the submission link contain the answers`` () =
    let html = readRepoFile "web/index.html"
    let text (id: string) = Regex.Match(html, $"id=\"{id}\"[^>]*>(.*?)</p>", RegexOptions.Singleline).Groups[1].Value
    Assert.Contains("This page's address contains your answers", text "link-disclosure")
    Assert.Contains("never sent to a server", text "link-disclosure")
    Assert.Contains("This link contains your answers", text "submission-disclosure")
    // The disclosure is shown while answering, beside the questions.
    Assert.True(html.IndexOf "id=\"link-disclosure\"" > html.IndexOf "data-if=\"responding\"")

[<Fact>]
let ``the answer document is never a route of the administrator application`` () =
    let inventory = readRepoFile ".echelon/routes.json"
    Assert.DoesNotContain("\"name\": \"r\"", inventory)
    Assert.DoesNotContain("/web/", inventory)

    // Opened in the administrator page, an answer fragment names no view.
    let page = { Origin = "https://signal.echelonfoundry.com"; Path = "/web/admin/"; Query = ""; Hash = fragment }
    Assert.Equal(Error RouteError.NotFound, RouteCodec.parse codec Router.allowAll (Location.ofBrowser mode page))

[<Fact>]
let ``an administrator view's Copy link is a route, never an answer document`` () =
    let page = { Origin = "https://signal.echelonfoundry.com"; Path = "/web/admin/"; Query = ""; Hash = fragment }

    let routes =
        [ Overview; Assessments; Question("SDRA", "0.1.0-draft", "D01", "CORE-001"); Groups noFilter; Group "g1"; Results("g1", defaultResults)
          Scoring("g1", None); Imports("g1", [ "accepted" ]); Report("g1", defaultFamily, defaultLocale); Compare([ "g1"; "g2" ], None); Administrators; Storage ]

    for route in routes do
        let place: Echelon.Signal.Admin.AdminNavigation.Place = { Echelon.Signal.Admin.AdminNavigation.initial with View = Ok route; Page = page }
        let link = Echelon.Signal.Admin.AdminNavigation.shareLink place
        Assert.StartsWith("https://signal.echelonfoundry.com/web/admin/#/", link)
        Assert.DoesNotContain("#r=", link)

[<Fact>]
let ``a fault on a page holding answers never records the answer document`` () =
    let sink = Sinks.Collector()
    let aegis = { Boundary.configure [ sink.Sink() ] with Persistence = Blocking }
    let encoded = fragment.Substring 3

    let initialize =
        $"""{{"kind":"Initialize","protocolVersion":1,"capabilities":["Navigation","Clipboard"],
            "location":{{"origin":"http://localhost","path":"/web/","query":"","hash":"{fragment}"}},
            "handshake":{{"protocol":{{"major":1,"minor":4}},"contract":{{"unit":"limen.core","version":1,"fingerprint":"{Limen.core.Fingerprint}"}},"capabilities":[]}}}}"""

    let state, _ = Wire.handle aegis Wire.initial initialize
    // Malformed messages: an unknown answer code and a broken shape, each an Aegis fault.
    let state, _ = Wire.handle aegis state """{"kind":"Event","event":{"name":"answered","key":"CORE-001","value":"seven"}}"""
    let _, _ = Wire.handle aegis state $"""{{"kind":"LocationChanged","location":{{"hash":"{fragment}"}}}}"""

    Assert.NotEmpty sink.Events

    for event in sink.Events do
        Assert.DoesNotContain(encoded, event)
        Assert.DoesNotContain("#r=", event)
