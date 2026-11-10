/// A group's results and scoring as the console projects them (signal#140
/// review): sections by the names their template gives them, reached by their
/// ids, and the calculated measures by display labels, never the identifiers
/// records store ("StandardDeviation").
module Echelon.Signal.Tests.ResultsProjectionTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Admin

/// A signed-in console with a group of two imported responses.
let private importedGroup () =
    let _, _, page = AdminPageTests.signedIn ()
    page.Event("navigate", key = "groups")
    page.Event("newGroupExpected", value = "2")
    page.Event("newGroupMinimum", value = "1")
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let group = (OpaqueId.ofBytes (Convert.FromHexString key)).Value
    page.Event("importText", value = String.concat "\n" [ AdminPageTests.link 1uy group; AdminPageTests.link 2uy group ])
    page.Event("import")
    page, key

let private texts (field: string) (items: Text.Json.Nodes.JsonObject list) =
    items |> List.map (fun item -> item[field].GetValue<string>())

[<Fact>]
let ``the results chart names each section as its template does, and reaches it by its id`` () =
    let page, key = importedGroup ()
    page.Event("navigate", key = $"/groups/{key}/results")
    let dimensions = Pilot.assessment.Dimensions
    let chart = page.Items "chart"
    Assert.Equal<string list>(dimensions |> List.map _.Label, texts "category" chart)
    Assert.Equal<string list>(dimensions |> List.map _.Id, texts "key" chart)
    Assert.Contains(dimensions.Head.Label, page.Text "chartDescription")
    Assert.DoesNotContain(dimensions.Head.Id, page.Text "chartDescription")
    // Drilling into a section is still by its id; its distribution is titled by name.
    page.Event("exploreSection", key = dimensions.Head.Id)
    Assert.True(page.Flag "hasDistribution")
    Assert.Equal(dimensions.Head.Label, page.Text "exploreSectionName")

[<Fact>]
let ``scoring labels its measures and sections for people, not by their identifiers`` () =
    let page, key = importedGroup ()
    page.Event("navigate", key = $"/groups/{key}/scoring")
    let dimensions = Pilot.assessment.Dimensions
    Assert.Equal<string list>(dimensions |> List.map _.Label, texts "label" (page.Items "scoringSections"))
    let measures = page.Items "scoringMeasures"
    Assert.Equal<string list>(
        [ "Mean"; "Median"; "Standard deviation"; "Interquartile range"; "95% confidence interval, lower bound"; "95% confidence interval, upper bound" ],
        texts "measure" measures |> List.distinct
    )
    Assert.Equal<string list>(dimensions |> List.map _.Label, texts "section" measures |> List.distinct)

[<Fact>]
let ``every measure has a display label, apart from the identifier records store`` () =
    let measures =
        [ Analysis.AcceptedCount; Analysis.CompletionRate; Analysis.Coverage; Analysis.Mean; Analysis.Median; Analysis.Minimum; Analysis.Maximum
          Analysis.StandardDeviation; Analysis.Percentile 90; Analysis.InterquartileRange; Analysis.MedianAbsoluteDeviation
          Analysis.ConfidenceLower95; Analysis.ConfidenceUpper95; Analysis.CeilingShare; Analysis.FloorShare; Analysis.DontKnowRate
          Analysis.NotApplicableRate; Analysis.MissingRate; Analysis.CronbachAlpha; Analysis.RecommendationFrequency ]

    for m in measures do
        let label = Analysis.measureLabel m
        Assert.False(String.IsNullOrWhiteSpace label)
        // A label is words: never a run-together identifier such as "StandardDeviation".
        Assert.DoesNotMatch(@"[a-z][A-Z]", label)

    Assert.Equal("StandardDeviation", Analysis.measureName Analysis.StandardDeviation)
    Assert.Equal("90th percentile", Analysis.measureLabel(Analysis.Percentile 90))
