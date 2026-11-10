/// Why a section has no score, as the engine says it (signal#142 review):
/// anonymity withholding only where the engine suppressed the section; "no
/// accepted responses yet" for an empty group; otherwise not scored.
module Echelon.Signal.Tests.SectionScoreTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin
open Echelon.Signal.Tests.AdminPageTests
open Echelon.Signal.Tests.AdminLinkTests

let private pilot = Pilot.assessment
let private opaque (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed * 5 + i)))).Value
let private groupId = opaque 40
let private policy: Aggregation.Policy = { MinimumReportableCount = 5 }

/// The report state of a group whose respondents give these answers to every item.
let private stateOf mode (answers: Answer list) : ReportState.AdminReportState =
    let definition = { Group = groupId; Mode = mode; ExpectedCount = 10; Template = pilot; Generic = None }

    let imported =
        answers
        |> List.mapi (fun i answer ->
            let binding = if mode = AnonymousGroup then Anonymous(opaque i, groupId) else Identified(opaque i, groupId)
            "https://signal.example" + LiveUrl.urlFor pilot "/web/" "" { Binding = binding; Answers = pilot.Items |> List.map (fun item -> item.Id, answer) |> Map.ofList })
        |> List.fold (fun s t -> importOne s t |> fst) (empty definition)

    match Incremental.ofResults definition (imported.Results |> Map.toList |> List.map snd) with
    | Ok accumulator -> ReportState.project policy accumulator
    | Error e -> failwith $"%A{e}"

let private scores (state: ReportState.AdminReportState) =
    state.Sections |> List.map (Analysis.sectionScore policy.MinimumReportableCount state)

[<Fact>]
let ``an anonymous group below its minimum is withheld to protect anonymity`` () =
    let state = stateOf AnonymousGroup [ Rated Often; Rated Often ]
    Assert.Equal(2, state.AcceptedSurveyCount)
    Assert.All(scores state, fun s -> Assert.Equal(Analysis.Unavailable(Analysis.Suppressed(5, 2)), s))
    Assert.Equal("Withheld to protect anonymity (below 5)", Visualization.unavailableText (Analysis.Suppressed(5, 2)))

[<Fact>]
let ``an identified group whose only answers are not applicable is not scored, never withheld for anonymity`` () =
    let state = stateOf IdentifiedGroup [ Withheld NotApplicable; Withheld DontKnow ]
    Assert.Equal(2, state.AcceptedSurveyCount)
    Assert.All(state.Sections, fun (s: ReportState.SectionSummary) -> Assert.False(s.Suppressed))
    Assert.All(scores state, fun s -> Assert.Equal(Analysis.Unavailable Analysis.NotScored, s))
    let text = Visualization.unavailableText Analysis.NotScored
    Assert.Equal("Not scored: no response had enough numeric answers", text)
    Assert.DoesNotContain("anonymity", text)

[<Fact>]
let ``a group with no accepted response says so, in either mode`` () =
    for mode in [ IdentifiedGroup; AnonymousGroup ] do
        let state = stateOf mode []
        Assert.Equal(0, state.AcceptedSurveyCount)
        Assert.NotEmpty(state.Sections)
        Assert.All(scores state, fun s -> Assert.Equal(Analysis.Unavailable Analysis.NoAcceptedResponses, s))

    Assert.Equal("No accepted responses yet", Visualization.unavailableText Analysis.NoAcceptedResponses)

[<Fact>]
let ``a scored section keeps its mean`` () =
    let state = stateOf IdentifiedGroup [ Rated Often ]
    Assert.All(scores state, fun s -> Assert.Equal(Analysis.Value 75.0, s))

[<Fact>]
let ``the results chart gives the engine's reason: none yet on an empty group, anonymity below the minimum`` () =
    let _, _, page = signedIn ()
    page.Event("navigate", key = "/groups")
    page.Event("newGroupExpected", value = "4")
    page.Event("newGroupMinimum", value = "3")
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let values () = page.Items "chart" |> List.map (fun item -> item["value"].GetValue<string>())
    Assert.NotEmpty(values ())
    Assert.All(values (), fun v -> Assert.Equal("No accepted responses yet", v))

    let group = (OpaqueId.ofBytes (Convert.FromHexString key)).Value
    page.Event("importText", value = String.concat "\n" [ link 1uy group; link 2uy group ])
    page.Event("import")
    Assert.All(values (), fun v -> Assert.Equal("Withheld to protect anonymity (below 3)", v))
