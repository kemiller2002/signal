/// Deep links through the administrator page's Limen boundary (WI-0066):
/// a fresh page opened at a link shows that view, a signed-out person goes
/// to sign-in and comes back to it across the provider's round trip, unknown
/// identifiers and bad links have their own page, Copy link writes the
/// canonical URL, and Back and Forward restore views from the URL alone
/// (SIG-LINK-001, SIG-LINK-004..008, SIG-LINK-011).
module Echelon.Signal.Tests.AdminLinkTests

open System
open Xunit
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Tests.AdminPageTests

let origin = "http://127.0.0.1:4321/web/admin/index.html"

/// A fresh page load at a fragment, against an existing browser and store.
let openAt (browser: Browser) (github: InMemoryStore) (hash: string) =
    let page = Page(browser, github, fun () -> true)
    page.Send(initialize "" hash)
    page

let callbackState (browser: Browser) =
    Uri(Seq.last browser.Left).Query.TrimStart('?').Split('&') |> Array.find (fun p -> p.StartsWith "state=") |> fun p -> p.Substring 6

let changed (page: Page) (hash: string) =
    page.Send $"""{{"kind":"LocationChanged","location":{{"path":"/web/admin/index.html","hash":"{hash}"}}}}"""

/// A group with two accepted submissions, made by a signed-in administrator.
let withGroup () =
    let browser, github, page = signedIn ()
    page.Event("navigate", key = "/groups")
    page.Event("newGroupExpected", value = "2")
    page.Event("newGroupMinimum", value = "1")
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let group = (OpaqueId.ofBytes (Convert.FromHexString key)).Value
    page.Event("importText", value = String.concat "\n" [ link 1uy group; link 2uy group ])
    page.Event("import")
    browser, github, key

[<Fact>]
let ``a signed-out deep link goes to sign-in and returns to the same view after the provider (SIG-LINK-006)`` () =
    let browser, github, key = withGroup ()
    let first = Pilot.assessment.Dimensions.Head.Id
    let target = $"/groups/{key}/results?section={first}&display=percent"

    // A new page, memory-only retention: signed out, at the deep link.
    let cold = openAt browser github $"#{target}"
    Assert.True(cold.Flag "screenSignIn", cold.ViewText)
    let signInHash = "#/sign-in?returnTo=" + Uri.EscapeDataString target
    Assert.Equal(signInHash, browser.Hash)
    Assert.Equal($"replace {signInHash}", Seq.last browser.Navigations)

    // Sign in: the target is kept in this tab, never in the provider's URL.
    cold.Event("signIn")
    Assert.Equal(target, browser.Tab["signal.admin.returnTo"])
    Assert.DoesNotContain("groups", Seq.last browser.Left)

    // The provider sends the browser back without the fragment.
    let back = Page(browser, github, fun () -> true)
    back.Send(initialize $"?code=good-code&state={callbackState browser}" "")
    Assert.Equal($"#{target}", browser.Hash)
    Assert.Equal($"replace #{target}", Seq.last browser.Navigations)
    Assert.True(back.Flag "screenDataset", back.ViewText)
    Assert.True(back.Flag "viewResults")
    Assert.True(back.Flag "hasDistribution")
    Assert.True(back.Flag "explorePercent")
    Assert.False(browser.Tab.ContainsKey "signal.admin.returnTo")

    // The share link is the view, without the callback or a return target.
    back.Event("copyLink")
    Assert.Equal($"{origin}#{target}", Seq.last browser.Copied)
    Assert.Equal("SIGNAL.LINK.COPIED", back.Text "noticeCode")

[<Fact>]
let ``an unknown group, an unknown route and a bad parameter each have their own page (SIG-LINK-007)`` () =
    let browser, _, page = signedIn ()

    changed page "#/groups/ffffffffffffffffffffffffffffffff/results"
    Assert.True(page.Flag "hasProblem")
    Assert.Equal("Not found", page.Text "problemTitle")
    Assert.Contains("ffffffffffffffffffffffffffffffff", page.Text "problemMessage")
    Assert.False(page.Flag "viewResults")
    // The URL is left as it was opened: no navigation was requested.
    Assert.False(browser.Navigations |> Seq.exists (fun n -> n.Contains "ffffffff"))

    changed page "#/reports/2026"
    Assert.True(page.Flag "screenPublic")
    Assert.Equal("Not found", page.Text "problemTitle")

    changed page "#/groups/g1/results?sort=sideways"
    Assert.Equal("This link is not valid", page.Text "problemTitle")
    Assert.Contains("'sideways' is not a valid sort", page.Text "problemMessage")

[<Fact>]
let ``assessments open cold without sign-in, down to the question, and unknown ids are not found`` () =
    let browser = Browser(Some local)
    let github = InMemoryStore()
    let item = Pilot.assessment.Items[6]
    let page = openAt browser github $"#/assessments/SDRA/versions/0.1.0-draft/sections/{item.DimensionId}/questions/{item.Id}"

    Assert.True(page.Flag "screenPublic", page.ViewText)
    Assert.True(page.Flag "viewAssessments")
    Assert.Equal(item.Prompt, page.Text "questionPrompt")
    Assert.Equal("Software Delivery Reality Assessment 0.1.0-draft", page.Text "assessmentTitle")
    let current = page.Items "sectionQuestions" |> List.filter (fun q -> q["current"].GetValue<string>() = "page")
    Assert.Equal(item.Id, current.Head["id"].GetValue<string>())

    changed page "#/assessments/SDRA/versions/9.9.9/sections/D01"
    Assert.Contains("No assessment SDRA at version 9.9.9", page.Text "problemMessage")
    changed page "#/assessments/SDRA/versions/0.1.0-draft/sections/D01/questions/CORE-999"
    Assert.Contains("no question CORE-999 in section D01", page.Text "problemMessage")

[<Fact>]
let ``Back and Forward are adopted, never pushed, and filters replace (SIG-LINK-004)`` () =
    let browser, _, page = signedIn ()
    page.Event("navigate", key = "/groups")
    let pushes = browser.Navigations.Count
    Assert.Equal("push #/groups", Seq.last browser.Navigations)

    page.Event("filterStatus", key = "sealed", value = "sealed")
    page.Event("filterStatus", key = "collecting", value = "collecting")
    page.Event("filterMode", key = "anonymous", value = "anonymous")
    Assert.Equal("#/groups?status=collecting,sealed&mode=anonymous", browser.Hash)
    Assert.Equal("replace #/groups?status=collecting,sealed&mode=anonymous", Seq.last browser.Navigations)
    page.Event("filterStatus", key = "sealed", value = "")
    Assert.Equal("#/groups?status=collecting&mode=anonymous", browser.Hash)

    // A history move is adopted: a canonical location gets no effect at all.
    let before = browser.Navigations.Count
    changed page "#/administrators"
    Assert.Equal(before, browser.Navigations.Count)
    Assert.True(page.Flag "viewAdministrators")
    changed page "#/groups?status=collecting&mode=anonymous"
    Assert.True(page.Flag "viewGroups")
    let filters = page.Items "statusFilters" |> List.filter (fun f -> f["checked"].GetValue<bool>()) |> List.map (fun f -> f["key"].GetValue<string>())
    Assert.Equal<string list>([ "collecting" ], filters)
    Assert.True(browser.Navigations.Count >= pushes)

[<Fact>]
let ``old links redirect, and a non-canonical link is replaced by its canonical form (SIG-LINK-002, SIG-LINK-011)`` () =
    let browser, _, page = signedIn ()
    changed page "#/groups/g1/explore/D01/value/percent"
    Assert.Equal("replace #/groups/g1/results", Seq.last browser.Navigations)

    changed page "#/compare?section=D01&groups=b,a,b&utm=mail"
    Assert.Equal("replace #/compare?groups=a,b&section=D01", Seq.last browser.Navigations)
    Assert.True(page.Flag "viewCompare")

    changed page "#/overview"
    Assert.Equal("replace #/", Seq.last browser.Navigations)

[<Fact>]
let ``no link the page renders or copies carries answers or respondent data (SIG-LINK-008)`` () =
    let browser, github, key = withGroup ()
    let page = openAt browser github ""
    let back = Page(browser, github, fun () -> true)
    page.Event("signIn")
    back.Send(initialize $"?code=good-code&state={callbackState browser}" "")
    changed back $"#/groups/{key}/imports?outcome=accepted"
    Assert.Equal(2, back.Items("importedSurveys").Length)
    back.Event("copyLink")

    let links =
        [ yield! browser.Copied
          yield! browser.Navigations
          for name in [ "areas"; "groups"; "scoringSections"; "catalogAssessments" ] do
              for item in back.Items name do
                  if item.ContainsKey "href" then yield item["href"].GetValue<string>() ]

    Assert.NotEmpty links

    for l in links do
        Assert.DoesNotContain("#r=", l)
        Assert.DoesNotContain("code=", l)
        Assert.DoesNotContain("octocat", l)
        Assert.DoesNotContain("583231", l)

[<Fact>]
let ``a fresh page opens a dataset that already has groups within the sign-in step`` () =
    // Regression: the groups were opened on the thread pool, so the dataset
    // appeared only after some later message (WI-0066).
    let browser, github, _ = withGroup ()
    let cold = openAt browser github ""
    cold.Event("signIn")
    let back = Page(browser, github, fun () -> true)
    back.Send(initialize $"?code=good-code&state={callbackState browser}" "")
    Assert.True(back.Flag "screenDataset", back.ViewText)
    Assert.Equal(1, back.Items("groups").Length)

/// Every name a view offers: its keys and the fields of its list items.
let private namesOf (viewText: string) =
    let view = Text.Json.Nodes.JsonNode.Parse(viewText).AsObject()

    view
    |> Seq.collect (fun pair ->
        match pair.Value with
        | :? Text.Json.Nodes.JsonArray as items -> pair.Key :: (items |> Seq.collect (fun item -> item.AsObject() |> Seq.map _.Key) |> List.ofSeq)
        | _ -> [ pair.Key ])
    |> Set.ofSeq

[<Fact>]
let ``the administrator page binds only what its engine projects, and sends only events it handles`` () =
    let html = Support.readRepoFile "web/admin/index.html"
    let browser, github, key = withGroup ()
    let page = openAt browser github ""
    page.Event("signIn")
    let back = Page(browser, github, fun () -> true)
    back.Send(initialize $"?code=good-code&state={callbackState browser}" "")
    // A second comparable group, so comparison rows are projected.
    changed back "#/groups"
    back.Event("newGroupExpected", value = "2")
    back.Event("newGroupMinimum", value = "1")
    back.Event("createGroup")
    let other = back.Text "groupKey"

    let seen =
        [ $"#/compare?groups={key},{other}"; $"#/groups/{key}"; $"#/groups/{key}/results?section=D01"; $"#/groups/{key}/scoring"; $"#/groups/{key}/imports"
          $"#/compare?groups={key}"; $"#/groups/{key}/report?family=audit&locale=ar-EG"; $"#/groups/{key}/report"; "#/groups"; "#/assessments/SDRA/versions/0.1.0-draft/sections/D01/questions/CORE-001"; "#/templates/PULSE/draft"; "#/nowhere" ]
        |> List.map (fun hash ->
            changed back hash
            namesOf back.ViewText)
        |> Set.unionMany

    let attribute (name: string) =
        Text.RegularExpressions.Regex.Matches(html, $"\\s{name}=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

    let bound =
        Set.unionMany
            [ attribute "data-text"; attribute "data-if"; attribute "data-each"; attribute "data-key"
              Text.RegularExpressions.Regex.Matches(html, "\\sdata-bind-[a-z-]+=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq ]

    // Fields of lists these flows leave empty: obligations (code, text) need
    // unreconciled work; report recommendations (title, priority, frequency),
    // comparisons (label, baseline, delta) and roles (role, count) need a
    // template or group that yields them. AdminView and AdminReportView
    // project each of them.
    // The authoring lists (WI-0073) are empty without a stored template or
    // draft: published versions (canDerive, canHide), a draft's sections
    // (questionCount), fixtures (name) and findings (severity, message);
    // AuthoringView projects each and AuthoringScreenTests fill them.
    let emptyListFields =
        set [ "code"; "text"; "title"; "priority"; "frequency"; "label"; "baseline"; "delta"; "role"; "count"; "canDerive"; "canHide"; "questionCount"; "name"; "severity"; "message" ]
    Assert.Empty(Set.difference bound (Set.union seen emptyListFields))

    // Every event the page sends is one the engine handles (an unknown one fails loudly).
    for name in attribute "data-event" do
        let probe = Page(browser, github, fun () -> true)
        probe.Send(initialize "" "#/assessments")
        probe.Event(name, key = "x", value = "")
