/// Issuing respondent links for a group (VER-003): parsing the request,
/// building generic and pilot links, refusing what cannot be issued.
module Echelon.Signal.Tests.InvitationTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Admin

let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private demo = SurveyPageTests.demo
let private form = GenericImport.formOf SurveyPageTests.DemoSurvey SurveyPageTests.DemoVersion demo
let private today = DateOnly(2026, 10, 9)

let private definition mode generic : Import.GroupDefinition =
    { Group = oid 40
      Mode = mode
      ExpectedCount = 5
      Template = Pilot.assessment
      Generic = generic }

[<Fact>]
let ``a request is a count between 1 and 500, an optional language and an optional last day`` () =
    let parse count locale expires = Invitations.parse { Count = count; Locale = locale; Expires = expires }
    Assert.Equal(Ok(3, GenericEnvelope.noTerms), parse "3" "" "")
    Assert.Equal(Ok(1, { GenericEnvelope.Terms.Locale = Some "fr-CA"; GenericEnvelope.Terms.ExpiresOn = Some(DateOnly(2026, 12, 31)) }), parse "1" " fr-CA " "2026-12-31")
    Assert.True(parse "0" "" "" |> Result.isError)
    Assert.True(parse "501" "" "" |> Result.isError)
    Assert.True(parse "two" "" "" |> Result.isError)
    Assert.True(parse "1" "not a tag" "" |> Result.isError)
    Assert.True(parse "1" "" "31/12/2026" |> Result.isError)

[<Fact>]
let ``generic links carry the group, a fresh instance each, and the terms`` () =
    let terms: GenericEnvelope.Terms = { Locale = Some "de"; ExpiresOn = Some today }

    match Invitations.links (definition Import.IdentifiedGroup (Some form)) terms today [ oid 1; oid 2 ] with
    | Ok [ first; second ] ->
        for link, instance in [ first, oid 1; second, oid 2 ] do
            Assert.StartsWith("../survey/#r=", link)

            match GenericEnvelope.decodeWithTerms form.Content form.Reference (link.Split("#r=")[1]) with
            | Ok(envelope, read) ->
                Assert.Equal(terms, read)
                Assert.Equal(IdentifiedInvitation(instance, oid 40), envelope.Binding)
                Assert.True(envelope.Answers.IsEmpty)
            | Error e -> failwith $"%A{e}"
    | other -> failwith $"%A{other}"

[<Fact>]
let ``pilot links carry no terms, and a past last day is refused`` () =
    match Invitations.links (definition Import.AnonymousGroup None) GenericEnvelope.noTerms today [ oid 1 ] with
    | Ok [ link ] ->
        Assert.StartsWith("../#r=", link)

        match LiveUrl.read Pilot.assessment (link.Substring 3) with
        | LiveUrl.Saved envelope -> Assert.Equal(AnonymousInvitation(oid 1, oid 40), envelope.Binding)
        | other -> failwith $"%A{other}"
    | other -> failwith $"%A{other}"

    Assert.True(Invitations.links (definition Import.AnonymousGroup None) { GenericEnvelope.noTerms with Locale = Some "fr" } today [ oid 1 ] |> Result.isError)
    Assert.True(Invitations.links (definition Import.AnonymousGroup (Some form)) { GenericEnvelope.noTerms with ExpiresOn = Some(today.AddDays -1) } today [ oid 1 ] |> Result.isError)

[<Fact>]
let ``the saved file numbers the links and holds nothing else`` () =
    Assert.Equal("invitation,link\n1,a\n2,b\n", Invitations.csv [ "a"; "b" ])
