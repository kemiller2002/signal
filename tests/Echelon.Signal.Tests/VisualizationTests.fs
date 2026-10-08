/// The visualization grammar, dashboards, localization and semantic
/// regression verification (WI-0049): ADM-015..018, ADM-046 (dashboard
/// limits), ADM-050, ADM-069.
module Echelon.Signal.Tests.VisualizationTests

open System
open Xunit
open Arca
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Analysis
open Echelon.Signal.Admin.Visualization

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private datum category value order : Datum = { Category = category; Value = value; Order = order }
let private sections = [ datum "Flow" (Value 62.5) 0; datum "Quality" (Value 40.0) 1; datum "Safety" (Unavailable(Suppressed(5, 3))) 2 ]
let private refusals result = match result with Error found -> found | Ok _ -> []

// ---- ADM-015 / ADM-016: legal shapes and suitability -----------------------------------------

[<Fact>]
let ``every chart type declares its legal shapes, and invalid combinations fail`` () =
    let bar = defaultSpec Bar "Section means"
    Assert.True(compile bar sections |> Result.isOk)
    Assert.Contains(IllegalShape("Line", "a line needs an ordered or temporal dimension"), compile { bar with Chart = Line } sections |> refusals)
    Assert.NotEmpty(compile { bar with Chart = Histogram } sections |> refusals)
    Assert.Contains(MissingDenominator, compile { bar with Chart = Stacked100 } sections |> refusals)
    Assert.Contains(MissingJustification, compile { bar with Chart = DualAxis } sections |> refusals)
    Assert.Contains(MissingTitle, compile { bar with Title = " " } sections |> refusals)
    Assert.Contains(TextTooSmall 9, compile { bar with TextSize = 9 } sections |> refusals)
    Assert.NotEmpty(compile { bar with Chart = Pie } [ for i in 1..6 -> datum $"c{i}" (Value 1.0) i ] |> refusals)
    Assert.True(compile { bar with Chart = Line; Dimension = Ordinal; Sort = ByOrder } sections |> Result.isOk)

[<Fact>]
let ``missing values stay explicit, never zero, and totals cannot reveal suppressed cells`` () =
    let compiled = compile { defaultSpec Bar "Section means" with ShowTotal = true } sections |> ok
    let safety = compiled.Marks |> List.find (fun m -> m.Category = "Safety")

    Assert.Equal(None, safety.Position)
    Assert.Equal("suppressed below 5", safety.Text)
    Assert.Equal(None, compiled.Total)
    Assert.Contains(TotalWithheldForSuppression, compiled.Warnings)
    Assert.Contains(MissingValues 1, compiled.Warnings)

[<Fact>]
let ``sorting is deterministic and a truncated bar axis is warned`` () =
    let spec = { defaultSpec Bar "Section means" with Sort = ByValueDescending }
    let once = compile spec sections |> ok
    let again = compile spec (List.rev sections) |> ok
    Assert.Equal<string list>(once.Marks |> List.map _.Category, again.Marks |> List.map _.Category)
    Assert.Equal<string list>([ "Flow"; "Quality"; "Safety" ], once.Marks |> List.map _.Category)
    // A category keeps its encoding whatever the sort (stable legend).
    let byName = compile (defaultSpec Bar "Section means") sections |> ok
    let encoding (c: Compiled) = c.Marks |> List.map (fun m -> m.Category, (m.Colour, m.Pattern, m.Symbol)) |> Map.ofList
    let worst = compile { spec with Sort = ByValueDescending } [ datum "A" (Value 1.0) 0; datum "B" (Value 9.0) 1 ] |> ok
    Assert.Equal<Map<string, string * string * string>>(encoding byName, encoding once)
    Assert.Equal("B", worst.Marks.Head.Category)
    Assert.Equal(palette[1], (worst.Marks.Head.Colour, worst.Marks.Head.Pattern, worst.Marks.Head.Symbol))

    let truncated = compile { spec with Scale = { Minimum = 30.0; Maximum = 70.0 } } sections |> ok
    Assert.Contains(TruncatedAxis, truncated.Warnings)
    Assert.Contains(TooManyCategories 13, (compile spec [ for i in 1..13 -> datum $"c{i:D2}" (Value 1.0) i ] |> ok).Warnings)

// ---- ADM-017: accessibility ---------------------------------------------------------------------

[<Fact>]
let ``every chart has a description, a table and non-colour encodings with enough contrast`` () =
    let compiled = compile (defaultSpec Bar "Section means") sections |> ok

    Assert.Contains("highest Flow (62.5), lowest Quality (40.0)", compiled.Description)
    Assert.Equal<(string * string) list>(compiled.Marks |> List.map (fun m -> m.Category, m.Text), compiled.Table)
    Assert.Empty(accessibility compiled "#FFFFFF")
    Assert.All(palette, fun (colour, _, _) -> Assert.True(contrast colour "#FFFFFF" >= 3.0, colour))
    Assert.Equal(palette.Length, palette |> List.map (fun (_, p, s) -> p, s) |> List.distinct |> List.length)
    // A light colour on white would be flagged.
    Assert.True(contrast "#E69F00" "#FFFFFF" < 3.0)

// ---- ADM-018 / ADM-046: dashboards ------------------------------------------------------------------

[<Fact>]
let ``dashboards are versioned definitions edited by legal operations`` () =
    let start = Dashboard.template "overview"
    Assert.Empty(Dashboard.problems start)

    let moved = Dashboard.edit (Dashboard.MoveBlock("distribution", 0)) start |> ok
    Assert.Equal("distribution", moved.Blocks.Head.Id)
    Assert.Equal(2, moved.Version)

    Assert.True(Dashboard.edit (Dashboard.ChangeChart("sections", DotPlot)) moved |> Result.isOk)
    Assert.Equal<Dashboard.EditRefusal list>([ Dashboard.NotApproved("Distribution", "Pie") ], Dashboard.edit (Dashboard.ChangeChart("distribution", Pie)) moved |> refusals)
    Assert.Equal<Dashboard.EditRefusal list>([ Dashboard.WidthOutOfRange 5 ], Dashboard.edit (Dashboard.Resize("sections", 5)) moved |> refusals)
    Assert.Equal<Dashboard.EditRefusal list>([ Dashboard.UnknownBlock "nope" ], Dashboard.edit (Dashboard.RemoveBlock "nope") moved |> refusals)

    let reset = Dashboard.reset moved
    Assert.Equal<string list>(start.Blocks |> List.map _.Id, reset.Blocks |> List.map _.Id)
    Assert.Equal(moved.Version + 1, reset.Version)
    Assert.Equal(1, (Dashboard.duplicate "copy" "Copy" moved).Version)

[<Fact>]
let ``one definition projects to every device in the same order`` () =
    let definition = Dashboard.template "overview"
    let order device = Dashboard.project device definition |> List.map (fun (id, _, _) -> id)

    Assert.Equal<string list>(order Dashboard.Desktop, order Dashboard.Mobile)
    Assert.All(Dashboard.project Dashboard.Mobile definition, fun (_, width, columns) -> Assert.Equal((1, 1), (width, columns)))
    Assert.Contains(("sections", 2, 2), Dashboard.project Dashboard.Tablet definition)

[<Fact>]
let ``a stored dashboard is read as untrusted input within limits`` () =
    let definition = Dashboard.template "overview"
    Assert.Equal(definition, Dashboard.ofJson (Dashboard.toJson definition) |> ok)

    let huge = { definition with Blocks = [ for i in 1..30 -> { definition.Blocks.Head with Id = $"b{i}" } ] }
    Assert.True(Dashboard.ofJson (Dashboard.toJson huge) |> Result.isError)

    let wrongChart = Json.canonicalText (Dashboard.toJson definition) |> fun t -> t.Replace("\"Histogram\"", "\"Pie\"")
    Assert.True(Json.parse wrongChart |> ok |> Dashboard.ofJson |> Result.isError)

// ---- ADM-069: localization is presentation only -----------------------------------------------------

[<Fact>]
let ``locales change presentation, never the canonical values`` () =
    Assert.Equal("1,234.5", Locale.number Locale.english 1 1234.5)
    Assert.Equal("1.234,5", Locale.number Locale.german 1 1234.5)
    Assert.Equal("1 234,5", Locale.number Locale.french 1 1234.5)
    Assert.Equal("1٬234٫5", Locale.number Locale.arabic 1 1234.5)
    Assert.Equal("62,5 %", Locale.percent Locale.german 1 0.625)
    Assert.Equal("10/08/2026", Locale.date Locale.english (DateOnly(2026, 10, 8)))
    Assert.Equal("08.10.2026", Locale.date Locale.german (DateOnly(2026, 10, 8)))

    // Right-to-left: mirrored layout, the same data order.
    Assert.Equal("rtl", Locale.dir Locale.arabic)
    Assert.Equal<string list>([ "c"; "b"; "a" ], Locale.layout Locale.arabic [ "a"; "b"; "c" ])
    Assert.Equal<Locale.PluralCategory list>([ Locale.Zero; Locale.One; Locale.Two; Locale.Few; Locale.Many; Locale.Other ], [ 0; 1; 2; 5; 11; 100 ] |> List.map Locale.arabic.Plural)
    Assert.Equal("⁨abc⁩", Locale.isolate "abc")

    // The canonical value a chart compiles from does not depend on a locale.
    let compiled = compile (defaultSpec Bar "Section means") sections |> ok
    Assert.Equal("62.5", (compiled.Marks |> List.find (fun m -> m.Category = "Flow")).Text)

// ---- ADM-050: semantic regression verification -----------------------------------------------------------

[<Fact>]
let ``regression checks meaning and accessibility, and show what a change broke`` () =
    let expectation: Regression.Expectation = { Required = [ "Flow"; "Quality" ]; Forbidden = [ "Secret" ]; Unit = Score0To100 }
    let good = compile (defaultSpec Bar "Section means") sections |> ok
    let before = Regression.assertions expectation sections good
    Assert.All(before, fun a -> Assert.True(a.Holds, a.Name))

    // A changed definition that drops a required metric and changes the unit.
    let dropped = sections |> List.filter (fun d -> d.Category <> "Quality")
    let changedChart = compile { defaultSpec Bar "Section means" with Unit = Percent0To100 } dropped |> ok
    let after = Regression.assertions expectation dropped changedChart
    let broken = Regression.changed before after |> List.map _.Name
    Assert.Equal<string list>([ "every required metric is represented"; "the unit is correct" ], broken)
