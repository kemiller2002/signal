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
let ``every stored template starts groups through the generic pipeline, and its links import (WI-0078)`` () =
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

    let yesNoPublished = publish opened "YESNO" yesNo [ yes ]
    page.Event("openDataset", key = "ds_engagement")

    let catalog = page.Items "catalog" |> List.map (fun i -> i["label"].GetValue<string>())
    Assert.Contains("Team working agreement (TEAM 1)", catalog)
    Assert.Contains("Yes or no (YESNO 1)", catalog)

    page.Event("navigate", key = "/assessments")
    Assert.All(page.Items "templateVersions", fun i -> Assert.Equal("Can start groups.", i["groups"].GetValue<string>()))

    // A group from the yes/no template, and a generic submission link for it is accepted.
    let form = GenericImport.formOf "YESNO" yesNoPublished.Version yesNoPublished.Content
    let entry = page.Items "catalog" |> List.find (fun i -> i["label"].GetValue<string>().Contains "YESNO")
    Assert.Equal(form.Hash, entry["hash"].GetValue<string>())
    page.Event("navigate", key = "/groups")
    page.Event("newGroupTemplate", value = form.Hash)
    page.Event("newGroupMinimum", value = "1")
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let group = (OpaqueId.ofBytes (Convert.FromHexString key)).Value

    let submission seed =
        let answers = team.Items |> List.map (fun i -> i.Id, Responses.Value(Responses.Flag true)) |> Map.ofList
        "https://signal.example" + GenericImport.link form "/web/" { Binding = Anonymous((OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value, group); Answers = answers }

    // Invitations (VER-003): links for this group with a language and a last day, nothing stored.
    page.Event("inviteCount", value = "0")
    page.Event("issueInvitations")
    Assert.Equal("SIGNAL.INVITE.INVALID", page.Text "noticeCode")
    page.Event("inviteCount", value = "3")
    page.Event("inviteLocale", value = "fr-CA")
    page.Event("inviteExpires", value = "2100-01-01")
    page.Event("issueInvitations")
    let issued = page.Items "invitationLinks" |> List.map (fun i -> i["link"].GetValue<string>())
    Assert.Equal(3, issued.Length)
    // (Each link's instance id is fresh entropy; this fixture's entropy is fixed.)

    for link in issued do
        Assert.Contains("/web/survey/#r=", link)

        match GenericEnvelope.decodeWithTerms form.Content form.Reference (link.Split("#r=")[1]) with
        | Ok(envelope, terms) ->
            Assert.Equal({ GenericEnvelope.Terms.Locale = Some "fr-CA"; GenericEnvelope.Terms.ExpiresOn = Some(DateOnly(2100, 1, 1)) }, terms)
            Assert.True(match envelope.Binding with AnonymousInvitation(_, g) -> g = group | _ -> false)
        | Error e -> failwith $"%A{e}"

    page.Event("importText", value = String.concat "\n" [ submission 1; submission 40; "https://signal.example/web/#r=AAAA" ])
    page.Event("import")
    Assert.Equal("2 of 10 accepted", page.Text "groupProgress")
    // An envelope of an encoding this version cannot read is held, not retried forever in the batch.
    Assert.Equal((2, 1), (page.Items("importAccepted").Length, page.Items("importBlockedEncoding").Length))

    // The report describes the generic template's own content.
    page.Send $"""{{"kind":"LocationChanged","location":{{"path":"/web/admin/index.html","hash":"#/groups/{key}/report"}}}}"""
    Assert.Contains(page.Items "reportSections", fun i -> i["section"].GetValue<string>().Contains "Plan Commitment")

    // Hidden: still listed, no longer offered for new groups; its group still opens.
    TemplateStore.hide (actor octocat) at opened "TEAM" runnable.Version |> run |> ok
    page.Event("openDataset", key = "ds_engagement")
    Assert.DoesNotContain(page.Items "catalog", fun i -> i["label"].GetValue<string>().Contains "TEAM")
    page.Event("navigate", key = "/assessments")
    Assert.Contains(page.Items "templateVersions", fun i -> i["status"].GetValue<string>() = "Hidden from new groups")
