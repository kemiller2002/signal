/// `.echelon/routes.json` against the schema Limen 0.9.0 ships,
/// `@echelon-foundry/limen/contract/routes.schema.json` (LCP-108, SIG-LINK-009,
/// WI-0071). Like Limen's own test, this is a small JSON Schema 2020-12
/// evaluator for the keywords that schema uses, with no dependency added; a
/// keyword it does not evaluate fails the first test rather than being
/// silently ignored.
module Echelon.Signal.Tests.RouteSchemaTests

open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open Echelon.Signal.Tests.Support

let private schema = JsonNode.Parse(readRepoFile "node_modules/@echelon-foundry/limen/contract/routes.schema.json")

/// Keywords the evaluator applies, and annotations it may ignore.
let private evaluated = set [ "$ref"; "type"; "const"; "enum"; "minLength"; "pattern"; "oneOf"; "required"; "properties"; "additionalProperties"; "items" ]
let private annotations = set [ "$schema"; "$id"; "$defs"; "title"; "description"; "examples"; "$comment" ]

/// Every keyword the schema uses, at any depth (property names are not keywords).
let rec private keywords (node: JsonNode) : string list =
    match node with
    | :? JsonObject as o ->
        o
        |> Seq.collect (fun pair ->
            let inside =
                match pair.Key with
                | "properties"
                | "$defs" -> (pair.Value.AsObject() |> Seq.collect (fun p -> keywords p.Value) |> List.ofSeq)
                | _ -> keywords pair.Value

            pair.Key :: inside)
        |> List.ofSeq
    | :? JsonArray as a -> a |> Seq.collect keywords |> List.ofSeq
    | _ -> []

let private kindOf (node: JsonNode) = if isNull node then JsonValueKind.Null else node.GetValueKind()

let private typeOk (kind: string) (value: JsonNode) =
    match kind, kindOf value with
    | "null", JsonValueKind.Null -> true
    | "array", JsonValueKind.Array -> true
    | "object", JsonValueKind.Object -> true
    | "string", JsonValueKind.String -> true
    | "boolean", (JsonValueKind.True | JsonValueKind.False) -> true
    | "number", JsonValueKind.Number -> true
    | "integer", JsonValueKind.Number -> value.GetValue<double>() = floor (value.GetValue<double>())
    | _ -> false

let private show (node: JsonNode) = if isNull node then "null" else node.ToJsonString()

let rec private validate (s: JsonObject) (value: JsonNode) (path: string) : string list =
    match s["$ref"] with
    | null ->
        let field (name: string) = s[name]

        match field "type" with
        | :? JsonValue as t when not (typeOk (t.GetValue<string>()) value) -> [ $"{path}: not {t}" ]
        | _ ->
            [ match field "const" with
              | null when not (s.ContainsKey "const") -> ()
              | c -> if show c <> show value then $"{path}: not the constant"
              match field "enum" with
              | :? JsonArray as options -> if not (options |> Seq.exists (fun o -> show o = show value)) then $"{path}: not in the enum"
              | _ -> ()
              match field "minLength", kindOf value with
              | null, _ -> ()
              | n, JsonValueKind.String -> if value.GetValue<string>().Length < n.GetValue<int>() then $"{path}: too short"
              | _ -> ()
              match field "pattern", kindOf value with
              | null, _ -> ()
              | p, JsonValueKind.String -> if not (Regex.IsMatch(value.GetValue<string>(), p.GetValue<string>())) then $"{path}: does not match {p}"
              | _ -> ()
              match field "oneOf" with
              | :? JsonArray as options ->
                  let passing = options |> Seq.filter (fun o -> validate (o.AsObject()) value path |> List.isEmpty) |> Seq.length
                  if passing <> 1 then $"{path}: matches {passing} of oneOf"
              | _ -> ()
              match value with
              | :? JsonObject as o ->
                  match field "required" with
                  | :? JsonArray as names ->
                      for name in names do
                          if not (o.ContainsKey(name.GetValue<string>())) then $"{path}: missing {name}"
                  | _ -> ()

                  let properties = match field "properties" with :? JsonObject as p -> p | _ -> JsonObject()

                  for pair in o do
                      let property = if properties.ContainsKey pair.Key then properties[pair.Key] else field "additionalProperties"

                      match property with
                      | :? JsonObject as p -> yield! validate p pair.Value $"{path}.{pair.Key}"
                      | :? JsonValue as v when v.GetValueKind() = JsonValueKind.False -> $"{path}.{pair.Key}: not allowed"
                      | _ -> ()
              | :? JsonArray as items ->
                  match field "items" with
                  | :? JsonObject as item ->
                      for i, element in Seq.indexed items do
                          yield! validate item element $"{path}[{i}]"
                  | _ -> ()
              | _ -> () ]
    | reference ->
        let target =
            reference.GetValue<string>().TrimStart('#', '/').Split('/')
            |> Array.fold (fun (node: JsonNode) key -> node[key]) schema

        validate (target.AsObject()) value path

let private inventory () = JsonNode.Parse(readRepoFile ".echelon/routes.json")

[<Fact>]
let ``the evaluator covers every keyword the shipped schema uses`` () =
    let used = keywords schema |> Set.ofList
    Assert.Empty(Set.difference used (Set.union evaluated annotations))

[<Fact>]
let ``.echelon/routes.json validates against Limen's routes.schema.json`` () =
    Assert.Equal<string list>([], validate (schema.AsObject()) (inventory ()) "$")

[<Fact>]
let ``the schema refuses documents that are not an inventory`` () =
    let changed (edit: JsonObject -> unit) =
        let document = inventory().AsObject()
        edit document
        validate (schema.AsObject()) document "$"

    Assert.NotEmpty(changed (fun d -> d["schema"] <- JsonValue.Create "echelon.routes/v2"))
    Assert.NotEmpty(changed (fun d -> d["mode"] <- JsonValue.Create "query"))
    Assert.NotEmpty(changed (fun d -> d.Remove "routes" |> ignore))
