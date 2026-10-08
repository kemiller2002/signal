// Limen.Routing (interim copy in Signal): resolving and building locations
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

module Router =
    let private full (prefix: string) (name: string) = if prefix = "" then name else $"{prefix}.{name}"

    /// Locations longer than this are refused before decoding (LCP-095).
    let maxLength = 8192

    // ------------------------------------------------------------------
    // Structural matching: literals and segment counts only; types later.
    // ------------------------------------------------------------------

    type private Binding = { Level: string; Name: string; Type: ParamType; Raw: string }

    let rec private consume (level: string) (pattern: Segment list) (segments: string list) (bound: Binding list) =
        match pattern, segments with
        | [], rest -> Some(List.rev bound, rest)
        | [ Segment.Wildcard name ], rest -> Some(List.rev ({ Level = level; Name = name; Type = ParamType.String; Raw = String.concat "/" rest } :: bound), [])
        | Segment.Literal literal :: pattern, segment :: rest when segment = literal -> consume level pattern rest bound
        | Segment.Param(name, kind) :: pattern, segment :: rest -> consume level pattern rest ({ Level = level; Name = name; Type = kind; Raw = segment } :: bound)
        | _ -> None

    let rec private matchRoute (prefix: string) (route: Route) (segments: string list) : ((string * Route) list * Binding list) option =
        let name = full prefix route.Name

        consume name route.Path segments []
        |> Option.bind (fun (bound, rest) ->
            if List.isEmpty route.Children then
                if List.isEmpty rest then Some([ name, route ], bound) else None
            else
                route.Children
                |> List.tryPick (fun child -> matchRoute name child rest)
                |> Option.map (fun (chain, childBound) -> (name, route) :: chain, bound @ childBound))

    let private matchTable (table: Route list) segments =
        table |> List.tryPick (fun route -> matchRoute "" route segments)

    /// The chain of a destination's full name: (full name, route) per level.
    let chainOf (table: Route list) (fullName: string) =
        let rec walk (routes: Route list) (names: string list) (prefix: string) acc =
            match names with
            | [] -> None
            | name :: rest ->
                routes
                |> List.tryFind (fun route -> route.Name = name)
                |> Option.bind (fun route ->
                    let chain = acc @ [ full prefix name, route ]
                    if List.isEmpty rest then (if List.isEmpty route.Children then Some chain else None)
                    else walk route.Children rest (full prefix name) chain)

        walk table (List.ofArray (fullName.Split '.')) "" []

    // ------------------------------------------------------------------
    // Building
    // ------------------------------------------------------------------

    let private sequence (results: Result<'a, 'e> list) =
        List.foldBack (fun item acc -> Result.bind (fun items -> Result.map (fun value -> value :: items) item) acc) results (Ok [])

    let build (table: Route list) (fullName: string) (parameters: Map<string, Value>) (query: Map<string, Value>) : Result<string, BuildError> =
        match chainOf table fullName with
        | None -> Error BuildError.UnknownRoute
        | Some chain ->
            let routes = chain |> List.map snd

            let segment piece =
                match piece with
                | Segment.Literal literal -> Text.tryEncode literal |> Option.map Ok |> Option.defaultValue (Error(BuildError.InvalidParameter literal))
                | Segment.Param(name, kind) ->
                    match Map.tryFind name parameters with
                    | None -> Error(BuildError.MissingParameter name)
                    | Some value -> Values.render kind value |> Option.map Ok |> Option.defaultValue (Error(BuildError.InvalidParameter name))
                | Segment.Wildcard name ->
                    match Map.tryFind name parameters with
                    | Some(Value.Text text) ->
                        let pieces = text.Split('/', StringSplitOptions.RemoveEmptyEntries) |> Array.map Text.tryEncode
                        if pieces |> Array.forall Option.isSome then Ok(pieces |> Array.choose id |> String.concat "/") else Error(BuildError.InvalidParameter name)
                    | Some _ -> Error(BuildError.InvalidParameter name)
                    | None -> Ok ""

            let pair (declared: QueryParam) =
                let absent () = if declared.Required then Some(Error(BuildError.MissingParameter declared.Name)) else None

                match Map.tryFind declared.Name query with
                | None -> absent ()
                | Some value ->
                    match Values.render declared.Type value, Text.tryEncode declared.Name with
                    | None, _
                    | _, None -> Some(Error(BuildError.InvalidParameter declared.Name))
                    | Some "", _ when (match declared.Type with ParamType.Set _ -> true | _ -> false) -> absent ()
                    | Some text, Some key ->
                        match declared.Default with
                        | Some fallback when not declared.Required && Values.same declared.Type fallback value -> None
                        | _ -> Some(Ok $"{key}={text}")

            let path = routes |> List.collect (fun route -> route.Path) |> List.map segment |> sequence
            let pairs = routes |> List.collect (fun route -> route.Query) |> List.choose pair |> sequence

            match path, pairs with
            | Error error, _
            | _, Error error -> Error error
            | Ok segments, Ok pairs ->
                let joined = "/" + (segments |> List.filter (fun piece -> piece <> "") |> String.concat "/")
                Ok(if List.isEmpty pairs then joined else joined + "?" + String.concat "&" pairs)

    // ------------------------------------------------------------------
    // Resolving
    // ------------------------------------------------------------------

    let private decodePath (path: string) =
        let pieces = path.Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray |> List.map (Text.decode false)
        if pieces |> List.forall Option.isSome then Some(pieces |> List.choose id) else None

    type private Pair = { Key: string; Raw: string; Decoded: string }

    let private decodeQuery (query: string) =
        let raw = if query.StartsWith "?" then query.Substring 1 else query

        let pieces =
            raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray
            |> List.map (fun part ->
                let index = part.IndexOf '='
                let key, value = if index < 0 then part, "" else part.Substring(0, index), part.Substring(index + 1)
                match Text.decode true key, Text.decode true value with
                | Some key, Some decoded -> Some { Key = key; Raw = value; Decoded = decoded }
                | _ -> None)

        if pieces |> List.forall Option.isSome then Some(pieces |> List.choose id) else None

    let splitLocation (location: string) =
        let index = location.IndexOf '?'
        if index < 0 then location, "" else location.Substring(0, index), location.Substring index

    /// Step 4: typed path parameters, per chain level.
    let private typedLevels (chain: (string * Route) list) (bindings: Binding list) =
        let converted =
            bindings
            |> List.map (fun binding ->
                match Values.convert binding.Type binding.Raw with
                | Some value -> Ok(binding.Level, binding.Name, value)
                | None -> Error(Resolution.Invalid(binding.Level, binding.Name, binding.Raw, Values.expected binding.Type)))

        match converted |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
        | Some invalid -> Error invalid
        | None ->
            let values = converted |> List.choose (function Ok v -> Some v | Error _ -> None)
            Ok(chain |> List.map (fun (name, route) -> { Route = route.Name; Params = values |> List.filter (fun (level, _, _) -> level = name) |> List.map (fun (_, key, value) -> key, value) |> Map.ofList }), values)

    /// Step 6: declared query parameters along the chain, parent first.
    let private typedQuery (chain: (string * Route) list) (pairs: Pair list) =
        let declared = chain |> List.collect (fun (name, route) -> route.Query |> List.map (fun parameter -> name, parameter))

        let check (level, parameter: QueryParam) =
            match pairs |> List.filter (fun pair -> pair.Key = parameter.Name) with
            | [] when parameter.Required -> Error(Resolution.Invalid(level, parameter.Name, "", $"{Values.expected parameter.Type} (required)"))
            | [] -> Ok(parameter.Default |> Option.map (fun value -> parameter.Name, value))
            | [ pair ] ->
                let converted =
                    match parameter.Type with
                    | ParamType.Set values -> Values.convertSet values pair.Raw
                    | kind -> Values.convert kind pair.Decoded

                match converted with
                | Some value -> Ok(Some(parameter.Name, value))
                | None -> Error(Resolution.Invalid(level, parameter.Name, pair.Decoded, Values.expected parameter.Type))
            | many -> Error(Resolution.Invalid(level, parameter.Name, many |> List.map (fun pair -> pair.Decoded) |> String.concat ",", "a single value"))

        declared |> List.map check |> sequence |> Result.map (List.choose id >> Map.ofList)

    let rec private resolveFrom (table: Route list) (guard: string -> Match -> GuardDecision) (visited: string list) (path: string) (query: string) =
        match decodePath path, decodeQuery query with
        | None, _ -> Resolution.MalformedPath
        | _, None -> Resolution.MalformedQuery
        | Some segments, Some pairs ->
            match matchTable table segments with
            | None -> Resolution.NotFound
            | Some(chain, bindings) ->
                match typedLevels chain bindings with
                | Error invalid -> invalid
                | Ok(levels, values) ->
                    let destination = chain |> List.last |> fst
                    let visited = visited @ [ destination ]

                    if List.contains destination (List.take (visited.Length - 1) visited) then
                        Resolution.RedirectLoop visited
                    else
                        let follow target (parameters: Map<string, Value>) (targetQuery: string) =
                            match build table target parameters Map.empty with
                            | Error _ -> Resolution.Invalid(destination, target, "", "a buildable redirect target")
                            | Ok location -> resolveFrom table guard visited (fst (splitLocation location)) targetQuery

                        match chain |> List.last |> snd |> fun route -> route.Redirect with
                        | Some(target, templates) ->
                            let lookup = values |> List.map (fun (_, key, value) -> key, value) |> Map.ofList

                            let parameters =
                                templates
                                |> List.choose (fun (key, template) ->
                                    match template with
                                    | Template.FromParam source -> Map.tryFind source lookup |> Option.map (fun value -> key, value)
                                    | Template.Literal literal -> Some(key, Value.Text literal))
                                |> Map.ofList

                            follow target parameters query
                        | None ->
                            match typedQuery chain pairs with
                            | Error invalid -> invalid
                            | Ok typed ->
                                let candidate =
                                    { Route = destination
                                      Chain = levels
                                      Query = typed
                                      Requires = chain |> List.collect (fun (_, route) -> route.Requires) |> List.distinct
                                      RedirectedFrom = List.take (visited.Length - 1) visited }

                                let decision =
                                    chain
                                    |> List.tryPick (fun (name, route) ->
                                        route.Guard
                                        |> Option.bind (fun guardName ->
                                            match guard guardName candidate with
                                            | GuardDecision.Allow -> None
                                            | other -> Some(name, other)))

                                match decision with
                                | None -> Resolution.Matched candidate
                                | Some(name, GuardDecision.Deny) -> Resolution.Denied name
                                | Some(_, GuardDecision.Redirect(target, parameters, guardQuery)) ->
                                    match build table target parameters guardQuery with
                                    | Error _ -> Resolution.Invalid(destination, target, "", "a buildable guard redirect target")
                                    | Ok location ->
                                        let path, query = splitLocation location
                                        resolveFrom table guard visited path query
                                | Some(_, GuardDecision.Allow) -> Resolution.Matched candidate

    /// Resolves a reported location. `guard` is the engine's decision for a
    /// named guard; it is interface policy, never an authorization boundary.
    let resolve (table: Route list) (guard: string -> Match -> GuardDecision) (path: string) (query: string) =
        if path.Length + query.Length > maxLength then Resolution.TooLong
        else resolveFrom table guard [] path query

    /// Resolves "path?query" as one string.
    let resolveLocation (table: Route list) guard (location: string) =
        let path, query = splitLocation location
        resolve table guard path query

    /// The canonical location of a match.
    let canonical (table: Route list) (matched: Match) =
        let parameters = matched.Chain |> List.collect (fun level -> Map.toList level.Params) |> Map.ofList
        build table matched.Route parameters matched.Query

    /// Allows every guard: for checks that must not depend on who is signed in.
    let allowAll : string -> Match -> GuardDecision = fun _ _ -> GuardDecision.Allow
