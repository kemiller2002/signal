// Limen.Routing (interim copy in Signal): the echelon.routes/v1 route inventory
//
// Copied from kemiller2002/limen libraries/fsharp/Limen.Routing/Routing.fs at
// e935da7 (PR #101, WI-0168), the F# reference library Limen 0.9.0 ships as
// EchelonFoundry.Limen.Routing. It is split into files below Ordo's
// structural-review size, and the cross-file `private` members are
// `internal` (with ModuleSuffix where a module now sits in another file
// from its type, the name the compiler gave it); nothing else differs. When
// Limen 0.9.0 is released, delete this project and reference the package:
// the namespace, modules and signatures are the package's own
// (DF-SIGNAL-2026-0003, WI-0066).
namespace Limen.Routing

open System
open System.Globalization
open System.Text

/// The route inventory (LCP-107): echelon.routes/v1, for .echelon/routes.json.
module Inventory =
    type private Json =
        | JNull
        | JBool of bool
        | JNumber of int64
        | JString of string
        | JArray of Json list
        | JObject of (string * Json) list

    let private escape (text: string) =
        let builder = StringBuilder("\"")

        for c in text do
            match c with
            | '"' -> builder.Append "\\\"" |> ignore
            | '\\' -> builder.Append "\\\\" |> ignore
            | '\b' -> builder.Append "\\b" |> ignore
            | '\f' -> builder.Append "\\f" |> ignore
            | '\n' -> builder.Append "\\n" |> ignore
            | '\r' -> builder.Append "\\r" |> ignore
            | '\t' -> builder.Append "\\t" |> ignore
            | c when c < ' ' -> builder.Append($"\\u{int c:x4}") |> ignore
            | c -> builder.Append c |> ignore

        builder.Append('"').ToString()

    /// JSON.stringify(value, null, 2) with keys sorted by code unit.
    let rec private write (indent: string) (json: Json) =
        let inner = indent + "  "
        match json with
        | JNull -> "null"
        | JBool flag -> if flag then "true" else "false"
        | JNumber number -> number.ToString(CultureInfo.InvariantCulture)
        | JString text -> escape text
        | JArray [] -> "[]"
        | JArray items -> "[\n" + (items |> List.map (fun item -> inner + write inner item) |> String.concat ",\n") + "\n" + indent + "]"
        | JObject [] -> "{}"
        | JObject fields ->
            let sorted = fields |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
            "{\n" + (sorted |> List.map (fun (key, value) -> inner + escape key + ": " + write inner value) |> String.concat ",\n") + "\n" + indent + "}"

    let private optionalString = Option.map JString >> Option.defaultValue JNull

    let private valueJson (value: Value) =
        match value with
        | Value.Text text -> JString text
        | Value.Integer number -> JNumber number
        | Value.Boolean flag -> JBool flag
        | Value.Date date -> JString(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        | Value.Month(year, month) -> JString $"%04d{year}-%02d{month}"
        | Value.Members items -> JArray(items |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.map JString)

    let private values kind =
        match kind with
        | ParamType.Enum items -> JArray(List.map JString items)
        | ParamType.Set [] -> JNull
        | ParamType.Set items -> JArray(List.map JString items)
        | _ -> JNull

    let private segmentText (segment: Segment) =
        match segment with
        | Segment.Literal literal -> literal
        | Segment.Param(name, kind) -> $"{{{name}:{Values.typeName kind}}}"
        | Segment.Wildcard name -> $"{{*{name}}}"

    let private pattern (segments: Segment list) = "/" + (segments |> List.map segmentText |> String.concat "/")

    let private parameter name place (kind: ParamType) required (fallback: Value option) =
        JObject
            [ "name", JString name
              "in", JString place
              "type", JString(Values.typeName kind)
              "required", JBool required
              "default", fallback |> Option.map valueJson |> Option.defaultValue JNull
              "values", values kind ]

    let private chainParams (chain: (string * Route) list) =
        let path =
            chain
            |> List.collect (fun (_, route) -> route.Path)
            |> List.choose (fun segment ->
                match segment with
                | Segment.Param(name, kind) -> Some(parameter name "path" kind true None)
                | Segment.Wildcard name -> Some(parameter name "path" ParamType.String false None)
                | Segment.Literal _ -> None)

        let query =
            chain
            |> List.collect (fun (_, route) -> route.Query)
            |> List.map (fun declared -> parameter declared.Name "query" declared.Type declared.Required declared.Default)

        path @ query

    let private templateJson (template: Template) =
        match template with
        | Template.FromParam source -> JString $"{{{source}}}"
        | Template.Literal literal -> JString literal

    /// The inventory document: deterministic, byte-identical in every
    /// conforming library (sorted keys, two-space indentation, final newline).
    let render (mode: LocationMode) (table: RouteTable) : string =
        let roles = RouteTable.roles table
        let all = RouteTable.destinations table
        let isRedirect (_, route: Route) = route.Redirect.IsSome

        let routeJson (name: string, _) =
            let chain = Router.chainOf (RouteTable.routes table) name |> Option.defaultValue []
            let destination = chain |> List.last |> snd

            JObject
                [ "name", JString name
                  "pattern", JString(pattern (chain |> List.collect (fun (_, route) -> route.Path)))
                  "params", JArray(chainParams chain)
                  "guards", JArray(chain |> List.choose (fun (_, route) -> route.Guard) |> List.map JString)
                  "requires", JArray(chain |> List.collect (fun (_, route) -> route.Requires) |> List.distinct |> List.map JString)
                  "returnTarget", JBool(destination.ReturnTarget && Some name <> roles.SignIn && Some name <> roles.NotFound) ]

        let legacyJson (name: string, route: Route) =
            let chain = Router.chainOf (RouteTable.routes table) name |> Option.defaultValue []
            let target, templates = route.Redirect |> Option.defaultValue ("", [])

            JObject
                [ "name", JString name
                  "pattern", JString(pattern (chain |> List.collect (fun (_, r) -> r.Path)))
                  "to", JString target
                  "params", JObject(templates |> List.map (fun (key, template) -> key, templateJson template)) ]

        let document =
            JObject
                [ "schema", JString "echelon.routes/v1"
                  "mode", JString(match mode with LocationMode.Hash -> "hash" | LocationMode.Path -> "path")
                  "home", JString roles.Home
                  "signIn", optionalString roles.SignIn
                  "notFound", optionalString roles.NotFound
                  "routes", JArray(all |> List.filter (isRedirect >> not) |> List.map routeJson)
                  "legacy", JArray(all |> List.filter isRedirect |> List.map legacyJson) ]

        write "" document + "\n"
