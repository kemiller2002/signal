/// The console's template catalog from the dataset (WI-0073): stored,
/// published templates join the built-in pilot, the ones with the group
/// pipeline's shape start groups, hidden ones are listed but not offered, and
/// the rest say why they cannot start groups.
module Echelon.Signal.Tests.CatalogPageTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin
open Echelon.Signal.Application
open Echelon.Signal.Tests.AdminPageTests
open Echelon.Signal.Tests.StoreFixture

/// A five-point frequency survey the group pipeline can run, under another identity.
let private team: Assessment.Assessment =
    { Pilot.assessment with
        Id = "TEAM"
        Title = "Team working agreement"
        Dimensions = Pilot.assessment.Dimensions |> List.take 1
        Items = Pilot.assessment.Items |> List.filter (fun i -> i.DimensionId = "D01") }

let private fixture (assessment: Assessment.Assessment) : Drafts.Fixture =
    { Id = "all-often"
      Name = "Every answer is often"
      Answers = assessment.Items |> List.map (fun i -> i.Id, Responses.Value(Responses.Point 3)) |> Map.ofList
      Expect = [ Drafts.ExpectComplete true ] }

let private publish (opened: Store.Opened) (surveyId: string) (content: Template.Content) (fixtures: Drafts.Fixture list) =
    let draft: Drafts.Draft = { SurveyId = surveyId; Parent = None; Content = content; Fixtures = fixtures }
    let warnings = (Validation.validate Validation.defaultPolicy draft).Warnings |> List.map _.Code |> Set.ofList
    TemplateStore.publish (actor octocat) Validation.defaultPolicy warnings at opened draft |> run |> ok

[<Fact>]
let ``the pilot round-trips through its generic form, and other shapes are refused with a reason`` () =
    Assert.Equal(Ok Pilot.assessment, Pilot.assessmentOf Pilot.assessment.Id Pilot.assessment.Version Pilot.content)
    let described = { Pilot.content with Metadata = { Pilot.content.Metadata with Description = Some "About delivery." } }
    Assert.Equal(Ok Pilot.assessment, Pilot.assessmentOf Pilot.assessment.Id Pilot.assessment.Version described)

    let boolean =
        { Pilot.content with
            Sections = Pilot.content.Sections |> List.map (fun s -> { s with Questions = s.Questions |> List.map (fun q -> { q with Answer = Primitives.Boolean }) }) }

    Assert.True(Pilot.assessmentOf "SDRA" "2" boolean |> Result.isError)
    Assert.True(Pilot.assessmentOf "SDRA" "2" { Pilot.content with Sections = [] } |> Result.isError)

[<Fact>]
let ``stored templates join the catalog; a runnable one starts a group that imports its links`` () =
    let _, github, page = signedIn ()
    let config = Deployment.parse configured |> ok
    let opened = Store.openDataset (backend github) config (actor octocat) None "signal-test" "ds_engagement" at |> run |> ok

    let runnable = publish opened "TEAM" (Pilot.contentOf team) [ fixture team ]
    let other = { Pilot.contentOf team with Metadata = { (Pilot.contentOf team).Metadata with Title = "Yes or no" } }
    let yesNo =
        { other with
            Sections = other.Sections |> List.map (fun s -> { s with Scoring = None; Questions = s.Questions |> List.map (fun q -> { q with Answer = Primitives.Boolean; Selector = { Preset = Selectors.YesNo; Labels = [ "No"; "Yes" ] }; SpecialStates = [] }) }) }

    let yes: Drafts.Fixture =
        { Id = "all-yes"
          Name = "Every answer is yes"
          Answers = team.Items |> List.map (fun i -> i.Id, Responses.Value(Responses.Flag true)) |> Map.ofList
          Expect = [ Drafts.ExpectComplete true ] }

    publish opened "YESNO" yesNo [ yes ] |> ignore
    page.Event("openDataset", key = "ds_engagement")

    let catalog = page.Items "catalog" |> List.map (fun i -> i["label"].GetValue<string>())
    Assert.Contains("Team working agreement (TEAM 1)", catalog)
    Assert.DoesNotContain(catalog, fun label -> label.Contains "YESNO")

    page.Event("navigate", key = "/assessments")
    let versions = page.Items "templateVersions" |> List.map (fun i -> i["label"].GetValue<string>(), i["groups"].GetValue<string>())
    Assert.Contains(("Team working agreement (TEAM 1)", "Can start groups."), versions)
    Assert.Contains(versions, fun (label, groups) -> label.Contains "YESNO" && groups.StartsWith "Cannot start groups")

    // A group from the stored template, and a link for it is accepted.
    let entry = page.Items "catalog" |> List.find (fun i -> i["label"].GetValue<string>().Contains "TEAM")
    page.Event("navigate", key = "/groups")
    page.Event("newGroupTemplate", value = entry["hash"].GetValue<string>())
    page.Event("newGroupMinimum", value = "1")
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let group = (OpaqueId.ofBytes (Convert.FromHexString key)).Value
    let assessment = Pilot.assessmentOf "TEAM" runnable.Version runnable.Content |> ok
    let answers = assessment.Items |> List.map (fun i -> i.Id, Assessment.Rated Assessment.Often) |> Map.ofList
    let url = "https://signal.example" + LiveUrl.urlFor assessment "/web/" "" { Binding = Anonymous((OpaqueId.ofBytes (Array.init 16 byte)).Value, group); Answers = answers }
    page.Event("importText", value = url)
    page.Event("import")
    Assert.Equal("1 of 10 accepted", page.Text "groupProgress")

    // Hidden: still listed, no longer offered for new groups.
    TemplateStore.hide (actor octocat) at opened "TEAM" runnable.Version |> run |> ok
    page.Event("openDataset", key = "ds_engagement")
    Assert.DoesNotContain(page.Items "catalog", fun i -> i["label"].GetValue<string>().Contains "TEAM")
    page.Event("navigate", key = "/assessments")
    Assert.Contains(page.Items "templateVersions", fun i -> i["status"].GetValue<string>() = "Hidden from new groups")
