/// Disclosure controls in the page (WI-0076, ADM-024, ARX-009): an anonymous
/// group releases its aggregate only when it differs from the last release by
/// at least the minimum, says how many responses it is holding back, and
/// withholds distributions that could single out respondents.
module Echelon.Signal.Tests.DisclosurePageTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Tests.AdminPageTests
open Echelon.Signal.Tests.AdminLinkTests

let private linkRated (seed: byte) (group: OpaqueId) (rating: Assessment.Frequency) =
    let answers = Pilot.assessment.Items |> List.map (fun item -> item.Id, Assessment.Rated rating) |> Map.ofList
    "https://signal.example" + LiveUrl.urlFor Pilot.assessment "/web/" "" { Binding = Anonymous((OpaqueId.ofBytes (Array.init 16 (fun i -> seed + byte i))).Value, group); Answers = answers }

let private scores (page: Page) = page.Items "sections" |> List.map (fun item -> item["score"].GetValue<string>())

[<Fact>]
let ``an anonymous group shows new responses only once they cannot be singled out by difference`` () =
    let _, _, page = signedIn ()
    page.Event("navigate", key = "/groups")
    page.Event("newGroupExpected", value = "10")
    page.Event("newGroupMinimum", value = "2")
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let group = (OpaqueId.ofBytes (Convert.FromHexString key)).Value
    let import (texts: string list) = page.Event("importText", value = String.concat "\n" texts); page.Event("import")

    import [ linkRated 1uy group Assessment.Often; linkRated 2uy group Assessment.Never ]
    Assert.Equal("2 of 10 accepted", page.Text "groupProgress")
    Assert.False(page.Flag "hasWithheld", page.ViewText)
    let released = scores page

    // One more response: showing it would reveal it by subtraction.
    import [ linkRated 3uy group Assessment.AlmostAlways ]
    Assert.Equal("3 of 10 accepted", page.Text "groupProgress")
    Assert.True(page.Flag "hasWithheld", page.ViewText)
    Assert.StartsWith("Results show 2 responses. 1 newer response(s) are held back", page.Text "withheldNotice")
    Assert.Equal<string list>(released, scores page)

    // A second: two new responses since the release, the minimum, so the state moves on.
    import [ linkRated 4uy group Assessment.Rarely ]
    Assert.False(page.Flag "hasWithheld", page.ViewText)
    Assert.NotEqual<string list>(released, scores page)

    // Four distinct scores in five bins: every occupied bin holds one respondent, so no distribution.
    page.Event("navigate", key = $"/groups/{key}/results")
    page.Event("exploreSection", key = Pilot.assessment.Dimensions.Head.Id)
    Assert.Empty(page.Items "distribution")
    Assert.Contains("Suppressed", page.Text "distributionDescription")
