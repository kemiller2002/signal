/// Limen's routing vectors (tests/limen-routing, copied from
/// kemiller2002/limen conformance/routing at v0.9.0) as F# values: the
/// neutral JSON tables translated into Limen.Routing routes, and results
/// rendered back into the neutral JSON shape so they compare structurally.
/// Adapted from Limen's own F# conformance runner, without its mutable
/// tallies (WI-0066, DF-SIGNAL-2026-0003).
module Echelon.Signal.Tests.LimenRoutingVectors

open System
open System.Text.Json
open System.Collections.Generic
open System.Text.Json.Nodes
open Limen.Routing
open Echelon.Signal.Tests.Support

let private load name = JsonNode.Parse(readRepoFile $"tests/limen-routing/{name}").AsObject()

/// Both vector files: the LCP-005 vectors and the URL-state vectors.
let files = [ load "routing.vectors.json"; load "url-state.vectors.json" ]

let text (node: JsonNode) = node.GetValue<string>()

let items (node: JsonNode) =
    match node with
    | null -> []
    | node -> node.AsArray() |> List.ofSeq

let private kindOf (node: JsonNode) = node.GetValueKind()
let isString (node: JsonNode) = not (isNull node) && kindOf node = JsonValueKind.String

let private paramType (q: JsonNode) =
    let values () = items q["values"] |> List.map text

    match text q["type"] with
    | "int" -> ParamType.Int
    | "bool" -> ParamType.Bool
    | "date" -> ParamType.Date
    | "month" -> ParamType.Month
    | "enum" -> ParamType.Enum(values ())
    | "set" -> ParamType.Set(values ())
    | _ -> ParamType.String

let private month (text: string) = Value.Month(int (text.Substring(0, 4)), int (text.Substring(5, 2)))

/// A JSON value with no type context: the vectors' input encoding.
let toValue (node: JsonNode) =
    match kindOf node with
    | JsonValueKind.Number -> Value.Integer(node.GetValue<int64>())
    | JsonValueKind.True -> Value.Boolean true
    | JsonValueKind.False -> Value.Boolean false
    | JsonValueKind.Array -> Value.Members(items node |> List.map text)
    | JsonValueKind.Object when not (isNull node["date"]) ->
        match DateOnly.TryParseExact(text node["date"], "yyyy-MM-dd") with
        | true, date -> Value.Date date
        | _ -> Value.Text(text node["date"])
    | JsonValueKind.Object -> month (text node["month"])
    | _ -> Value.Text(text node)

/// A default in a table: typed by its parameter.
let private defaultValue (kind: ParamType) (node: JsonNode) =
    match kind, kindOf node with
    | ParamType.Date, JsonValueKind.String ->
        match DateOnly.TryParseExact(text node, "yyyy-MM-dd") with
        | true, date -> Value.Date date
        | _ -> Value.Text(text node)
    | ParamType.Month, JsonValueKind.String when (text node).Length = 7 -> month (text node)
    | _ -> toValue node

let private template (value: string) =
    if value.StartsWith "{" && value.EndsWith "}" then
        Template.FromParam(value.Substring(1, value.Length - 2))
    else
        Template.Literal value

let private templates (node: JsonNode) =
    match node with
    | null -> []
    | node -> node.AsObject() |> Seq.map (fun pair -> pair.Key, template (text pair.Value)) |> List.ofSeq

let rec toRoute (node: JsonNode) : Result<Route, DefinitionError> =
    let o = node.AsObject()
    let children = items o["children"] |> List.map toRoute
    let optional (node: JsonNode) = if isNull node then None else Some(text node)

    match Route.define (text o["name"]) (text o["path"]), children |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
    | Error e, _
    | _, Some e -> Error e
    | Ok route, None ->
        let query (q: JsonNode) =
            let kind = paramType q

            { Name = text q["name"]
              Type = kind
              Required = q["required"].GetValue<bool>()
              Default = (if isNull q["default"] then None else Some(defaultValue kind q["default"])) }

        Ok
            { route with
                Query = items o["query"] |> List.map query
                Children = children |> List.choose Result.toOption
                Redirect =
                    match o["redirect"] with
                    | null -> None
                    | r -> Some(text r["to"], templates r["params"])
                Guard = optional o["guard"]
                Requires = items o["requires"] |> List.map text
                ReturnTarget = (if isNull o["returnTarget"] then true else o["returnTarget"].GetValue<bool>()) }

let routesOf (node: JsonNode) = items node |> List.map toRoute

let legacyOf (node: JsonNode) =
    items node |> List.map (fun l -> { Path = text l["path"]; To = text l["to"]; Params = templates l["params"] })

let rolesOf (node: JsonNode) =
    let optional (name: string) = if isNull node[name] then None else Some(text node[name])

    { Home = text node["home"]
      SignIn = optional "signIn"
      NotFound = optional "notFound" }

let private ok results =
    results |> List.map (function Ok r -> r | Error e -> failwith $"table does not parse: {e}")

let private pairsOf (node: JsonNode) =
    match node with
    | null -> []
    | node -> node.AsObject() |> Seq.map (fun pair -> pair.Key, pair.Value) |> List.ofSeq

let rawTables = files |> List.collect (fun f -> pairsOf f["tables"]) |> Map.ofList

/// The tables the vectors define with legacy entries and roles.
let definedTables =
    files
    |> List.collect (fun f -> pairsOf f["definitions"])
    |> List.map (fun (name, definition) ->
        match RouteTable.define (ok (routesOf rawTables[name])) (legacyOf definition["legacy"]) (rolesOf definition["roles"]) with
        | Ok table -> name, table
        | Error errors -> failwith $"table {name} does not define: {errors}")
    |> Map.ofList

/// A table's routes as matched: a defined table's, with its legacy entries.
let routeList name =
    match Map.tryFind name definedTables with
    | Some table -> RouteTable.routes table
    | None -> ok (routesOf rawTables[name])

let toValues (node: JsonNode) =
    pairsOf node |> List.map (fun (key, value) -> key, toValue value) |> Map.ofList

let private node (value: 'T) : JsonNode = JsonValue.Create value
let private obj (pairs: (string * JsonNode) list) : JsonNode = JsonObject(pairs |> List.map KeyValuePair.Create)
let strings (values: string list) : JsonNode = JsonArray(values |> List.map node |> Array.ofList)

let private valueJson (value: Value) : JsonNode =
    match value with
    | Value.Text t -> node t
    | Value.Integer i -> node i
    | Value.Boolean b -> node b
    | Value.Date d -> obj [ "date", node (d.ToString "yyyy-MM-dd") ]
    | Value.Month(y, m) -> obj [ "month", node $"%04d{y}-%02d{m}" ]
    | Value.Members ms -> strings ms

let private mapJson (values: Map<string, Value>) = obj (values |> Map.toList |> List.map (fun (key, value) -> key, valueJson value))

let resolutionJson (resolution: Resolution) : JsonNode =
    match resolution with
    | Resolution.Matched m ->
        obj
            [ "kind", node "Matched"
              "route", node m.Route
              "chain", JsonArray(m.Chain |> List.map (fun level -> obj [ "route", node level.Route; "params", mapJson level.Params ]) |> Array.ofList)
              "query", mapJson m.Query
              "requires", strings m.Requires
              "redirectedFrom", strings m.RedirectedFrom ]
    | Resolution.NotFound -> obj [ "kind", node "NotFound" ]
    | Resolution.MalformedPath -> obj [ "kind", node "MalformedPath" ]
    | Resolution.MalformedQuery -> obj [ "kind", node "MalformedQuery" ]
    | Resolution.TooLong -> obj [ "kind", node "TooLong" ]
    | Resolution.Invalid(route, parameter, value, expected) ->
        obj [ "kind", node "Invalid"; "route", node route; "parameter", node parameter; "value", node value; "expected", node expected ]
    | Resolution.RedirectLoop chain -> obj [ "kind", node "RedirectLoop"; "chain", strings chain ]
    | Resolution.Denied route -> obj [ "kind", node "Denied"; "route", node route ]

/// The vectors' guard decisions: absent is allow, "deny", or a redirect.
let guardsFrom (decisions: JsonNode) : string -> Match -> GuardDecision =
    let byName = pairsOf decisions |> Map.ofList

    fun name _ ->
        match Map.tryFind name byName with
        | None -> GuardDecision.Allow
        | Some decision when isString decision -> if text decision = "deny" then GuardDecision.Deny else GuardDecision.Allow
        | Some decision ->
            let r = decision["redirect"]
            GuardDecision.Redirect(text r["to"], toValues r["params"], toValues r["query"])

let buildJson (result: Result<string, BuildError>) : JsonNode =
    match result with
    | Ok location -> obj [ "ok", node location ]
    | Error error ->
        let kind, parameter =
            match error with
            | BuildError.UnknownRoute -> "UnknownRoute", ""
            | BuildError.MissingParameter p -> "MissingParameter", p
            | BuildError.InvalidParameter p -> "InvalidParameter", p

        obj [ "error", node kind; "parameter", node parameter ]

let effectJson (effect: NavigationEffect option) : JsonNode =
    match effect with
    | None -> null
    | Some(NavigationEffect.Push url) -> obj [ "push", node url ]
    | Some(NavigationEffect.Replace url) -> obj [ "replace", node url ]

let definitionErrorJson (error: DefinitionError) : JsonNode =
    let pairs =
        match error with
        | DefinitionError.InvalidSegment(route, segment) -> [ "kind", "InvalidSegment"; "route", route; "segment", segment ]
        | DefinitionError.DuplicateName route -> [ "kind", "DuplicateName"; "route", route ]
        | DefinitionError.DuplicateParameter(route, p) -> [ "kind", "DuplicateParameter"; "route", route; "parameter", p ]
        | DefinitionError.ReservedName(route, p) -> [ "kind", "ReservedName"; "route", route; "parameter", p ]
        | DefinitionError.InvalidValues(route, p) -> [ "kind", "InvalidValues"; "route", route; "parameter", p ]
        | DefinitionError.InvalidDefault(route, p) -> [ "kind", "InvalidDefault"; "route", route; "parameter", p ]
        | DefinitionError.RequiredWithDefault(route, p) -> [ "kind", "RequiredWithDefault"; "route", route; "parameter", p ]
        | DefinitionError.UnknownTarget(route, target) -> [ "kind", "UnknownTarget"; "route", route; "target", target ]
        | DefinitionError.UnknownParameter(route, p) -> [ "kind", "UnknownParameter"; "route", route; "parameter", p ]
        | DefinitionError.UnknownRole(role, route) -> [ "kind", "UnknownRole"; "role", role; "route", route ]

    obj (pairs |> List.map (fun (k, v) -> k, node v))

let outcomeJson (outcome: Result<Match, RouteError>) : JsonNode =
    let pairs =
        match outcome with
        | Ok m -> [ "ok", m.Route ]
        | Error RouteError.NotFound -> [ "error", "NotFound" ]
        | Error(RouteError.NotPermitted route) -> [ "error", "NotPermitted"; "route", route ]
        | Error(RouteError.Invalid(route, p, v, e)) -> [ "error", "Invalid"; "route", route; "parameter", p; "value", v; "expected", e ]
        | Error(RouteError.Malformed part) -> [ "error", "Malformed"; "part", part ]
        | Error(RouteError.RedirectLoop _) -> [ "error", "RedirectLoop" ]
        | Error(RouteError.Unmapped(route, problem)) -> [ "error", "Unmapped"; "route", route; "problem", problem ]

    obj (pairs |> List.map (fun (k, v) -> k, node v))

/// A text node, or JSON null for None.
let optionalText (value: string option) : JsonNode =
    match value with
    | Some v -> node v
    | None -> null

/// A text node.
let textNode (value: string) : JsonNode = node value

/// An object of named nodes.
let objectOf (pairs: (string * JsonNode) list) : JsonNode = obj pairs
