/// Signal's vendored Limen.Routing (vendor/limen-routing) against Limen's
/// own language-neutral vectors: every resolve, build, session, definition,
/// return-target, location, outcome and inventory vector of
/// conformance/routing at limen e935da7 (LCP-005, LCP-088..112). They pass
/// unchanged when the vendored copy is replaced by the Limen 0.9.0 package,
/// which is what makes that switch mechanical (WI-0066, DF-SIGNAL-2026-0003).
module Echelon.Signal.Tests.LimenRoutingConformanceTests

open System
open System.Text.Json.Nodes
open Xunit
open Limen.Routing
open Echelon.Signal.Tests.LimenRoutingVectors
open Echelon.Signal.Tests.Support

/// The vendored router is Limen's file byte for byte (limen e935da7): any
/// edit, or a different upstream file, changes the digest and fails here.
[<Fact>]
let ``the vendored router is Limen's Routing.fs at e935da7, unmodified`` () =
    let bytes = System.IO.File.ReadAllBytes(repoFile "vendor/limen-routing/Routing.fs")
    let digest = System.Security.Cryptography.SHA256.HashData bytes |> Convert.ToHexStringLower
    Assert.Equal("c7fc955063b2b4490d1fbcc29aa7302e2f16f0310609d45dd0679c32ba664338", digest)

    let vectors name = System.IO.File.ReadAllBytes(repoFile $"tests/limen-routing/{name}") |> System.Security.Cryptography.SHA256.HashData |> Convert.ToHexStringLower
    Assert.Equal("27ded668bde4646653d8bb8eb2e472f88d683acf49dc91239625d74efc47cf70", vectors "routing.vectors.json")
    Assert.Equal("8c3bf1c5bc06b790d7e4eeb201c89150fd397539c85b55258b622454c677bd9b", vectors "url-state.vectors.json")

/// One vector: its name, the expected result and the library's.
[<NoComparison>]
type private Check = { Name: string; Expected: JsonNode; Actual: JsonNode }

let private show (node: JsonNode) = if isNull node then "null" else node.ToJsonString()

let private disagreements (checks: Check list) =
    checks
    |> List.filter (fun c -> not (JsonNode.DeepEquals(c.Expected, c.Actual)))
    |> List.map (fun c -> $"{c.Name}\n    expected {show c.Expected}\n    actual   {show c.Actual}")

let private assertAgree (checks: Check list) =
    Assert.NotEmpty checks
    Assert.Equal<string list>([], disagreements checks)

let private nameOf (case: JsonNode) = text case["name"]

let private cases (group: string) = files |> List.collect (fun file -> items file[group])

/// A vector's text field, or its `<field>Repeat` expansion (prefix + text * times).
let private repeated (plain: string) (case: JsonNode) =
    match case[plain + "Repeat"] with
    | null -> text case[plain]
    | r -> text r["prefix"] + String.replicate (r["times"].GetValue<int>()) (text r["text"])

[<Fact>]
let ``every resolve vector agrees`` () =
    cases "resolve"
    |> List.map (fun case ->
        let actual = Router.resolve (routeList (text case["table"])) (guardsFrom case["guards"]) (repeated "path" case) (text case["query"])
        { Name = $"resolve: {nameOf case}"; Expected = case["expect"]; Actual = resolutionJson actual })
    |> assertAgree

[<Fact>]
let ``every build vector agrees`` () =
    cases "build"
    |> List.map (fun case ->
        let actual = Router.build (routeList (text case["table"])) (text case["route"]) (toValues case["params"]) (toValues case["query"])
        { Name = $"build: {nameOf case}"; Expected = case["expect"]; Actual = buildJson actual })
    |> assertAgree

/// The browser's session history, as far as the vectors need it.
type private History = { Entries: string list; Index: int }

let private applyEffect (history: History) (effect: NavigationEffect option) =
    match effect with
    | None -> history
    | Some(NavigationEffect.Push url) -> { Entries = List.take (history.Index + 1) history.Entries @ [ url ]; Index = history.Index + 1 }
    | Some(NavigationEffect.Replace url) -> { history with Entries = history.Entries |> List.mapi (fun i e -> if i = history.Index then url else e) }

let private routeOf (resolution: Resolution) : JsonNode =
    match resolution with
    | Resolution.Matched m -> textNode m.Route
    | _ -> null

/// One session step: the next router state and history, and what it showed.
let private sessionStep (tableName: string) (state: RouterState, history: History) (step: JsonNode) =
    let table = routeList tableName
    let guard = guardsFrom step["guards"]

    let adoptAt (history: History) location =
        let next, resolution, effect = Navigation.adopt table guard state location
        (next, applyEffect history effect), [ "route", routeOf resolution; "effect", effectJson effect ]

    let moved delta =
        let target = history.Index + delta

        if target < 0 || target >= history.Entries.Length then
            (state, history), [ "left", JsonValue.Create true :> JsonNode ]
        else
            let location = history.Entries[target]
            let next, shown = adoptAt { history with Index = target } location
            next, ("location", textNode location) :: shown

    if not (isNull step["adopt"]) then
        let location = text step["adopt"]
        let history = if history.Entries.IsEmpty then { Entries = [ location ]; Index = 0 } else applyEffect history (Some(NavigationEffect.Push location))
        adoptAt history location
    elif not (isNull step["navigate"]) || not (isNull step["refine"]) then
        let n, operation = if isNull step["navigate"] then step["refine"], Navigation.refine else step["navigate"], Navigation.navigate

        match operation table state (text n["route"]) (toValues n["params"]) (toValues n["query"]) with
        | Ok(next, effect) -> (next, applyEffect history effect), [ "route", textNode (text n["route"]); "effect", effectJson effect ]
        | Error error -> failwith $"{tableName}: {error}"
    elif not (isNull step["back"]) then
        moved -1
    elif not (isNull step["forward"]) then
        moved 1
    else
        let target = ReturnTo.resume definedTables[tableName] guard (Some(text step["resume"]))
        let next, effect = Navigation.replace state target
        (next, applyEffect history effect), [ "route", routeOf (Router.resolveLocation table guard target); "effect", effectJson effect ]

[<Fact>]
let ``every session vector agrees, step by step`` () =
    cases "sessions"
    |> List.collect (fun session ->
        let tableName = text session["table"]
        let sessionName = text session["name"]

        items session["steps"]
        |> List.mapi (fun i step -> i + 1, step)
        |> List.mapFold
            (fun position (index, step) ->
                let next, shown = sessionStep tableName position step
                { Name = $"session {sessionName} step {index}"; Expected = step["expect"]; Actual = objectOf shown }, next)
            (Navigation.initial, { Entries = []; Index = -1 })
        |> fst)
    |> assertAgree

[<Fact>]
let ``every definition vector agrees`` () =
    cases "definitionCases"
    |> List.map (fun case ->
        let routes = if isString case["routes"] then routesOf rawTables[text case["routes"]] else routesOf case["routes"]
        let parseErrors = routes |> List.choose (function Error e -> Some e | Ok _ -> None)

        let result =
            if parseErrors.IsEmpty then
                RouteTable.define (routes |> List.choose Result.toOption) (legacyOf case["legacy"]) (rolesOf case["roles"]) |> Result.map ignore
            else
                Error parseErrors

        let actual =
            match result with
            | Ok() -> objectOf [ "ok", JsonValue.Create true ]
            | Error errors -> objectOf [ "errors", JsonArray(errors |> List.map definitionErrorJson |> Array.ofList) ]

        { Name = $"definition: {nameOf case}"; Expected = case["expect"]; Actual = actual })
    |> assertAgree

[<Fact>]
let ``every return-target vector agrees`` () =
    cases "returnTo"
    |> List.map (fun case ->
        let table = definedTables[text case["table"]]
        let optional (node: JsonNode) = if isNull node then None else Some(text node)
        let has key = case.AsObject().ContainsKey key

        let actual =
            if has "capture" then
                optionalText (ReturnTo.capture table (text case["capture"]))
            elif has "resume" then
                textNode (ReturnTo.resume table (guardsFrom case["guards"]) (optional case["resume"]))
            else
                match ReturnTo.signIn table (optional case["signIn"]) with
                | Ok location -> textNode location
                | Error e -> textNode $"error {e}"

        { Name = $"returnTo: {nameOf case}"; Expected = case["expect"]; Actual = actual })
    |> assertAgree

[<Fact>]
let ``every location and share-link vector agrees`` () =
    cases "locations"
    |> List.map (fun case ->
        let mode = if text case["mode"] = "hash" then LocationMode.Hash else LocationMode.Path

        let page () =
            let l = case["location"]
            { Origin = text l["origin"]; Path = text l["path"]; Query = text l["query"]; Hash = text l["hash"] }

        let actual =
            if not (isNull case["href"]) then Location.href mode (text case["href"])
            elif not (isNull case["share"]) then Link.share mode (page ()) (text case["share"])
            else Location.ofBrowser mode (page ())

        { Name = $"location: {nameOf case}"; Expected = case["expect"]; Actual = textNode actual })
    |> assertAgree

[<Fact>]
let ``every outcome vector agrees`` () =
    cases "outcomes"
    |> List.map (fun case ->
        let table = definedTables[text case["table"]]
        let location = repeated "location" case
        let outcome = Router.resolveLocation (RouteTable.routes table) (guardsFrom case["guards"]) location |> RouteError.ofResolution table
        { Name = $"outcome: {nameOf case}"; Expected = case["expect"]; Actual = outcomeJson outcome })
    |> assertAgree

[<Fact>]
let ``every inventory vector renders byte for byte`` () =
    cases "inventory"
    |> List.map (fun case ->
        let mode = if text case["mode"] = "hash" then LocationMode.Hash else LocationMode.Path
        { Name = $"inventory: {nameOf case}"; Expected = case["expect"]; Actual = textNode (Inventory.render mode definedTables[text case["table"]]) })
    |> assertAgree
