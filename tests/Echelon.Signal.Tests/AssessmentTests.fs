/// The scoring rules of the assessment, independent of any page.
module Echelon.Signal.Tests.AssessmentTests

open System.Text.Json.Nodes
open Xunit
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Tests.Support

let private assessment = Echelon.Signal.Engine.Pilot.assessment

let private answersFor (dimensionId: string) (answers: Answer list) : Answers =
    assessment.Items
    |> List.filter (fun item -> item.DimensionId = dimensionId)
    |> List.zip answers
    |> List.map (fun (answer, item) -> item.Id, answer)
    |> Map.ofList

let private resultOf dimensionId answers =
    score assessment answers |> List.find (fun (d, _) -> d.Id = dimensionId) |> snd

[<Fact>]
let ``a dimension score is the mean numeric answer over 4, times 100`` () =
    let answers = answersFor "D01" [ Rated Never; Rated Rarely; Rated Sometimes; Rated Often; Rated AlmostAlways ]
    Assert.Equal(Scored(50.0, 5, 5), resultOf "D01" answers)
    Assert.Equal(Scored(100.0, 5, 5), resultOf "D01" (answersFor "D01" (List.replicate 5 (Rated AlmostAlways))))
    Assert.Equal(Scored(0.0, 5, 5), resultOf "D01" (answersFor "D01" (List.replicate 5 (Rated Never))))

[<Fact>]
let ``non-numeric answers are left out of the mean, never counted as zero`` () =
    let answers =
        answersFor "D02" [ Rated AlmostAlways; Rated AlmostAlways; Rated Often; Withheld DontKnow; Withheld NotApplicable ]
    // (4 + 4 + 3) / 3 / 4 * 100 = 91.666... -> 91.7
    Assert.Equal(Scored(91.7, 3, 5), resultOf "D02" answers)

[<Fact>]
let ``a dimension with fewer numeric answers than the minimum is not scored`` () =
    let answers =
        answersFor "D03" [ Rated Often; Rated Often; Withheld DontKnow; Withheld NotObserved; Withheld NotApplicable ]

    Assert.Equal(Unscored(2, 5, 3), resultOf "D03" answers)
    Assert.Equal(Unscored(0, 5, 3), resultOf "D03" Map.empty)

[<Fact>]
let ``rounding is half away from zero, to one decimal`` () =
    // (1 + 1 + 1 + 0) / 4 / 4 * 100 = 18.75 -> 18.8
    let answers = answersFor "D01" [ Rated Rarely; Rated Rarely; Rated Rarely; Rated Never; Withheld DontKnow ]
    Assert.Equal(Scored(18.8, 4, 5), resultOf "D01" answers)

[<Fact>]
let ``every answer has a code that reads back as itself, and nothing else reads`` () =
    let all =
        [ Rated Never; Rated Rarely; Rated Sometimes; Rated Often; Rated AlmostAlways
          Withheld DontKnow; Withheld NotObserved; Withheld NotApplicable ]

    for answer in all do
        Assert.Equal(Some answer, parseAnswer (answerCode answer))

    for code in [ ""; "5"; "-1"; "unknown"; "Don't know" ] do
        Assert.Equal(None, parseAnswer code)

[<Fact>]
let ``the pilot is the item bank's first three dimensions, verbatim`` () =
    let bank = JsonNode.Parse(readRepoFile "input-documents/software-delivery-reality-assessment-v1-item-bank.json")
    let str (node: JsonNode) = node.GetValue<string>()

    let bankDimensions =
        bank.["dimensions"].AsArray() |> Seq.take 3 |> Seq.map (fun d -> str d.["id"], str d.["label"]) |> Seq.toList

    let bankItems =
        bank.["coreItems"].AsArray()
        |> Seq.filter (fun i -> bankDimensions |> List.exists (fst >> (=) (str i.["dimension"])))
        |> Seq.map (fun i -> str i.["id"], str i.["dimension"], str i.["prompt"])
        |> Seq.toList

    Assert.Equal<(string * string) list>(bankDimensions, assessment.Dimensions |> List.map (fun d -> d.Id, d.Label))
    Assert.Equal<(string * string * string) list>(bankItems, assessment.Items |> List.map (fun i -> i.Id, i.DimensionId, i.Prompt))
    Assert.Equal(bank.["scoring"].["minimumNumericItemsPerDimension"].GetValue<int>(), assessment.MinimumNumericAnswers)
    Assert.Equal(str bank.["assessment"].["version"], assessment.Version)
