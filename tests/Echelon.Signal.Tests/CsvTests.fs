/// Every CSV Signal writes is spreadsheet-safe (WI-0084, CSV injection): a
/// text cell starting with =, +, -, @, a tab or a carriage return gets a
/// leading single quote, numbers stay numbers, and RFC 4180 quoting applies.
module Echelon.Signal.Tests.CsvTests

open Xunit
open Echelon.Signal.Engine

[<Fact>]
let ``each formula prefix is neutralized, and kept as text`` () =
    Assert.Equal("'=1+1", Csv.field "=1+1")
    Assert.Equal("'+cmd", Csv.field "+cmd")
    Assert.Equal("'-cmd", Csv.field "-cmd")
    Assert.Equal("'@SUM(A1)", Csv.field "@SUM(A1)")
    Assert.Equal("'\tx", Csv.field "\tx")
    // A carriage return is both neutralized and quoted.
    Assert.Equal("\"'\rx\"", Csv.field "\rx")

[<Fact>]
let ``a formula smuggled in as text is neutralized and quoted`` () =
    Assert.Equal("\"'=HYPERLINK(\"\"http://x\"\",\"\"y\"\")\"", Csv.field "=HYPERLINK(\"http://x\",\"y\")")

[<Fact>]
let ``numbers stay numbers, and plain text is quoted only when it must be`` () =
    for number in [ "-1.5"; "42"; "+3"; "0"; "-0.25"; "1E3" ] do
        Assert.Equal(number, Csv.field number)

    Assert.Equal("'-Infinity", Csv.field "-Infinity")
    Assert.Equal("Delivery", Csv.field "Delivery")
    Assert.Equal("\"Plan, build\"", Csv.field "Plan, build")
    Assert.Equal("\"line\nbreak\"", Csv.field "line\nbreak")
    Assert.Equal("", Csv.field "")

[<Fact>]
let ``the report's sections CSV cannot carry a formula from a template's section title`` () =
    let csv = Csv.table [ "section"; "title" ] [ [ "D01"; "=HYPERLINK(\"http://x\",\"y\")" ]; [ "D02"; "-5 days" ] ]
    Assert.Equal("section,title\nD01,\"'=HYPERLINK(\"\"http://x\"\",\"\"y\"\")\"\nD02,'-5 days\n", csv)

[<Fact>]
let ``a report built from a template whose section title is a formula exports it neutralized`` () =
    let demo = SurveyPageTests.demo
    let formula = "=HYPERLINK(\"http://x\",\"y\")"
    let content = { demo with Sections = demo.Sections |> List.mapi (fun i s -> if i = 0 then { s with Title = formula } else s) }
    let first = content.Sections.Head.Id
    let group = GroupResult.ofEvidence "sha256:t" Import.IdentifiedGroup 3 5 3 [] [ first, [ 50.0; 60.0; 70.0 ], 0 ] { Applicable = 15; Answered = 15; Special = 0; Unanswered = 0 } [] []
    let family = ReportExport.families |> List.find (fun d -> d.Blocks |> List.contains ReportModel.SectionResults)
    let subject: Report.Subject = { SurveyId = "DEMO"; TemplateVersion = "1"; Content = content }

    match Report.build family subject 5 group [] System.DateTimeOffset.UnixEpoch with
    | Ok report ->
        let csv = ReportExport.sectionsCsv report
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\",\"\"y\"\")\"", csv)
        Assert.DoesNotContain("," + formula, csv)
        Assert.DoesNotContain(",=", csv)
    | Error problems -> failwith $"%A{problems}"
