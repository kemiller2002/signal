/// The administrator application's routes (WI-0066): the canonical form, the
/// codec's round trip and totality, guards and return targets through
/// sign-in, legacy links, the inventory, and the privacy rules
/// (SIG-LINK-002..004, SIG-LINK-006..011).
module Echelon.Signal.Tests.RoutesTests

open System
open Xunit
open Limen.Routing
open Echelon.Signal.Admin.Routes
open Echelon.Signal.Tests.Support

let private parse location = RouteCodec.parse codec Router.allowAll location
let private adopt access location = RouteCodec.adopt codec (guard access) Navigation.initial location

[<Fact>]
let ``the route table defines without a problem`` () =
    match definition with
    | Ok _ -> ()
    | Error problems -> failwith $"%A{problems}"

[<Fact>]
let ``every view has one canonical location (SIG-LINK-002)`` () =
    let cases =
        [ Overview, "/"
          SignIn(Some "/groups/g1"), "/sign-in?returnTo=%2Fgroups%2Fg1"
          Assessments, "/assessments"
          Assessment("SDRA", "0.1.0-draft"), "/assessments/SDRA/versions/0.1.0-draft"
          Question("SDRA", "0.1.0-draft", "D01", "CORE-001"), "/assessments/SDRA/versions/0.1.0-draft/sections/D01/questions/CORE-001"
          // %20 for a space and upper-case hex for every other byte.
          Section("a b", "é", "x/y"), "/assessments/a%20b/versions/%C3%A9/sections/x%2Fy"
          // Sets sorted and de-duplicated; empty filters omitted.
          Groups { Status = [ "sealed"; "collecting"; "sealed" ]; Mode = []; Survey = Some "SDRA" }, "/groups?status=collecting,sealed&survey=SDRA"
          Groups noFilter, "/groups"
          // Defaults omitted; declared order (section, sort, display).
          Results("g1", defaultResults), "/groups/g1/results"
          Results("g1", { Section = Some "D02"; Sort = ByValue; Display = Percentages }), "/groups/g1/results?section=D02&sort=value&display=percent"
          Imports("g1", [ "rejected"; "accepted" ]), "/groups/g1/imports?outcome=accepted,rejected"
          Compare([ "g2"; "g1" ], Some "D01"), "/compare?groups=g1,g2&section=D01" ]

    for route, expected in cases do
        Assert.Equal(Ok expected, RouteCodec.format codec route)

    Assert.Equal("#/groups/g1/results", href (Results("g1", defaultResults)))

[<Fact>]
let ``a non-canonical location opens the same view and is replaced by the canonical one`` () =
    let _, route, effect = adopt Open "/groups/?survey=SDRA&utm=mail&status=sealed,collecting,sealed"
    Assert.Equal(Ok(Groups { Status = [ "collecting"; "sealed" ]; Mode = []; Survey = Some "SDRA" }), route)
    Assert.Equal(Some(NavigationEffect.Replace "/groups?status=collecting,sealed&survey=SDRA"), effect)

    let _, again, none = adopt Open "/groups?status=collecting,sealed&survey=SDRA"
    Assert.Equal(route, again)
    Assert.Equal(None, none)

    // A default spelled out is dropped.
    let _, _, shorter = adopt Open "/groups/g1/results?sort=order&display=count"
    Assert.Equal(Some(NavigationEffect.Replace "/groups/g1/results"), shorter)

[<Fact>]
let ``navigating pushes, refining replaces, and adopting never pushes (SIG-LINK-004)`` () =
    let state, _, _ = adopt Open "/groups/g1/results"
    let pushed = RouteCodec.navigate codec state (Scoring("g1", None))
    Assert.Equal(Ok(Some(NavigationEffect.Push "/groups/g1/scoring")), pushed |> Result.map snd)

    let refined = RouteCodec.refine codec state (Results("g1", { defaultResults with Sort = ByValue }))
    Assert.Equal(Ok(Some(NavigationEffect.Replace "/groups/g1/results?sort=value")), refined |> Result.map snd)
    Assert.Equal(Ok None, RouteCodec.navigate codec state (Results("g1", defaultResults)) |> Result.map snd)

[<Fact>]
let ``what is not a view is a route error, never an exception (SIG-LINK-007)`` () =
    Assert.Equal(Error RouteError.NotFound, parse "/nowhere/at/all")
    Assert.Equal(Error RouteError.NotFound, parse "/not-found")
    Assert.Equal(Error(RouteError.Invalid("results", "sort", "sideways", "one of order|value")), parse "/groups/g1/results?sort=sideways")
    Assert.Equal(Error(RouteError.Invalid("groups", "status", "open", "a set of collecting|closed-incomplete|finalized|finalized-incomplete|sealed|superseded")), parse "/groups?status=open")
    Assert.Equal(Error(RouteError.Malformed "path"), parse "/groups/%zz")
    Assert.Equal(Error(RouteError.Malformed "length"), parse ("/groups/" + String('a', 9000)))

[<Fact>]
let ``a signed-out person is sent to sign-in with the view as the return target (SIG-LINK-006)`` () =
    let _, route, effect = adopt SignInRequired "/groups/g1/results?section=D01&sort=value"
    let target = "/groups/g1/results?section=D01&sort=value"
    Assert.Equal(Ok(SignIn(Some target)), route)
    Assert.Equal(Some(NavigationEffect.Replace "/sign-in?returnTo=%2Fgroups%2Fg1%2Fresults%3Fsection%3DD01%26sort%3Dvalue"), effect)

    // After sign-in the target resumes; for anything not a target, home.
    Assert.Equal(target, ReturnTo.resume table (guard Open) (Some target))
    Assert.Equal("/", ReturnTo.resume table (guard Open) (Some "https://evil.example/"))
    Assert.Equal("/", ReturnTo.resume table (guard Open) (Some "//evil.example/groups"))
    Assert.Equal("/", ReturnTo.resume table (guard Open) (Some "/sign-in?returnTo=%2F"))
    Assert.Equal("/", ReturnTo.resume table (guard SignInRequired) (Some target))
    Assert.Equal(None, ReturnTo.capture table "/not-found")

    // Assessments are public: no sign-in, so no redirect.
    let _, public', none = adopt SignInRequired "/assessments/SDRA/versions/0.1.0-draft"
    Assert.Equal(Ok(Assessment("SDRA", "0.1.0-draft")), public')
    Assert.Equal(None, none)

[<Fact>]
let ``links published before deep linking redirect to their current routes (SIG-LINK-011)`` () =
    let _, overview, toHome = adopt Open "/overview"
    Assert.Equal(Ok Overview, overview)
    Assert.Equal(Some(NavigationEffect.Replace "/"), toHome)

    let _, explored, toResults = adopt Open "/groups/g1/explore/D01/value/percent"
    Assert.Equal(Ok(Results("g1", defaultResults)), explored)
    Assert.Equal(Some(NavigationEffect.Replace "/groups/g1/results"), toResults)

[<Fact>]
let ``share links are the canonical location in the page's own document`` () =
    let page = { Origin = "https://signal.echelonfoundry.com"; Path = "/web/admin/"; Query = ""; Hash = "#/groups?status=sealed" }
    Assert.Equal("/groups?status=sealed", Location.ofBrowser mode page)
    Assert.Equal("https://signal.echelonfoundry.com/web/admin/#/compare?groups=g1,g2", Link.share mode page (location (Compare([ "g2"; "g1" ], None))))

[<Fact>]
let ``the inventory file is the table's inventory, byte for byte (SIG-LINK-009)`` () =
    Assert.Equal(inventory (), readRepoFile ".echelon/routes.json")
    Assert.StartsWith("{\n  \"home\": \"overview\",", inventory ())
    Assert.EndsWith("}\n", inventory ())

/// Every parameter name a route may carry. Adding one is a privacy review
/// (SIG-LINK-008): it must be an opaque identifier or a typed view parameter.
let private allowedParameters =
    set [ "assessment"; "version"; "section"; "question"; "group"; "groups"; "status"; "mode"; "survey"; "sort"; "display"; "outcome"; "returnTo"; "view" ]

[<Fact>]
let ``routes carry only identifiers and typed view parameters, never answers (SIG-LINK-008)`` () =
    let names =
        RouteTable.destinations table
        |> List.collect (fun (_, route) -> (route.Path |> List.choose (function Segment.Param(n, _) | Segment.Wildcard n -> Some n | _ -> None)) @ (route.Query |> List.map _.Name))
        |> Set.ofList

    Assert.Empty(Set.difference names allowedParameters)
    Assert.DoesNotContain("answer", (inventory ()).ToLowerInvariant())

    // A credential-like name is refused when the table is defined.
    let withToken = [ Route.create "x" "x" |> fun r -> { r with Query = [ QueryParam.optional "session_id" ParamType.String ] } ]
    let roles = { Home = "x"; SignIn = None; NotFound = None }
    Assert.Equal(Error [ DefinitionError.ReservedName("x", "session_id") ], RouteTable.define withToken [] roles |> Result.map ignore)

// ---- Properties: a seeded generator, so a failure always reproduces ----------------------------

let private alphabet = [ "a"; "Z"; "0"; " "; ","; "&"; "="; "?"; "#"; "/"; "%"; "+"; "~"; "é"; "日本"; "😀"; "-"; "_"; "."; "'" ]

let private generate (seed: int) =
    let random = Random seed
    let pick (xs: 'a list) = xs[random.Next xs.Length]
    let word () = String.concat "" [ for _ in 1 .. random.Next(1, 6) -> pick alphabet ]
    let maybe f = if random.Next 2 = 0 then None else Some(f ())
    let subset values = values |> List.filter (fun _ -> random.Next 2 = 0)
    let sorted (xs: string list) = xs |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
    let id () = word () |> fun w -> if w.Trim('/') = "" then "g" + w else w

    match random.Next 13 with
    | 0 -> Overview
    | 1 -> SignIn(maybe (fun () -> "/groups/" + Uri.EscapeDataString(id ())))
    | 2 -> Assessments
    | 3 -> Assessment(id (), id ())
    | 4 -> Section(id (), id (), id ())
    | 5 -> Question(id (), id (), id (), id ())
    | 6 -> Groups { Status = sorted (subset statusValues); Mode = sorted (subset modeValues); Survey = maybe word }
    | 7 -> Group(id ())
    | 8 -> Results(id (), { Section = maybe word; Sort = pick [ ByOrder; ByValue ]; Display = pick [ Counts; Percentages ] })
    | 9 -> Scoring(id (), maybe word)
    | 10 -> Imports(id (), sorted (subset outcomeValues))
    | 11 -> Compare(sorted [ for _ in 1 .. random.Next 4 -> id () ], maybe word)
    | _ -> pick [ Administrators; Storage ]

[<Fact>]
let ``parse (format r) = r for every route (SIG-LINK-003)`` () =
    let failures =
        [ 1..3000 ]
        |> List.map generate
        |> List.choose (fun route ->
            match RouteCodec.format codec route with
            | Error e -> Some $"format {route}: {e}"
            | Ok location when parse location = Ok route -> None
            | Ok location -> Some $"{route} via {location}: {parse location}")

    Assert.Equal<string list>([], failures |> List.truncate 5)

[<Fact>]
let ``every string resolves, parses and resumes without an exception`` () =
    let random = Random 20261008

    let noise () =
        String(Array.init (random.Next 60) (fun _ -> char (List.item (random.Next 4) [ random.Next(0, 128); random.Next(0xD800, 0xE000); int '%'; int '/' ])))

    let failures =
        [ 1..3000 ]
        |> List.map (fun i -> if i % 2 = 0 then "/" + noise () else noise ())
        |> List.choose (fun candidate ->
            try
                parse candidate |> ignore
                adopt SignInRequired candidate |> ignore
                let resumed = ReturnTo.resume table (guard Open) (Some candidate)
                if ReturnTo.isRelative resumed then None else Some $"resume gave {resumed}"
            with error ->
                Some $"threw on {candidate}: {error.Message}")

    Assert.Equal<string list>([], failures |> List.truncate 5)
