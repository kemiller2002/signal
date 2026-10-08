// Limen.Routing (interim copy in Signal): tables, outcomes, navigation and the typed codec
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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module RouteTable =
    let private reserved =
        set [ "token"; "accesstoken"; "idtoken"; "refreshtoken"; "password"; "passwd"; "secret"; "clientsecret"; "apikey"; "key"; "session"
              "sessionid"; "auth"; "authorization"; "code"; "credential"; "credentials" ]

    /// Whether a parameter name is reserved for credentials (LCP-109).
    let isReserved (name: string) =
        reserved.Contains(name.ToLowerInvariant().Replace("-", "").Replace("_", ""))

    let private full (prefix: string) (name: string) = if prefix = "" then name else $"{prefix}.{name}"

    let private pathParams (route: Route) =
        route.Path
        |> List.choose (function
            | Segment.Param(name, kind) -> Some(name, Some kind)
            | Segment.Wildcard name -> Some(name, None)
            | Segment.Literal _ -> None)

    let private valuesProblem kind =
        match kind with
        | ParamType.Enum values -> values.IsEmpty || values |> List.exists ((=) "") || List.distinct values <> values
        | ParamType.Set values -> values |> List.exists ((=) "") || List.distinct values <> values
        | _ -> false

    let rec private destinationsOf (prefix: string) (routes: Route list) =
        routes
        |> List.collect (fun route ->
            let name = full prefix route.Name
            if route.Children.IsEmpty then [ name, route ] else destinationsOf name route.Children)

    let rec private check (table: Route list) (prefix: string) (inherited: string list) (routes: Route list) : DefinitionError list =
        let duplicates =
            routes
            |> List.countBy (fun route -> route.Name)
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map (fun (name, _) -> DefinitionError.DuplicateName(full prefix name))

        let perRoute (route: Route) =
            let name = full prefix route.Name
            let last = route.Path.Length - 1

            let segmentErrors =
                route.Path
                |> List.indexed
                |> List.choose (fun (index, segment) ->
                    match segment with
                    | Segment.Wildcard wildcard when index <> last || not route.Children.IsEmpty -> Some(DefinitionError.InvalidSegment(name, $"{{*{wildcard}}}"))
                    | _ -> None)
            let local = (pathParams route |> List.map fst) @ (route.Query |> List.map (fun parameter -> parameter.Name))

            let duplicateParams =
                local
                |> List.indexed
                |> List.choose (fun (index, parameter) ->
                    if List.contains parameter inherited || List.contains parameter (List.take index local) then Some(DefinitionError.DuplicateParameter(name, parameter)) else None)

            let reservedNames = local |> List.filter isReserved |> List.map (fun parameter -> DefinitionError.ReservedName(name, parameter))

            let pathTypes =
                pathParams route
                |> List.choose (fun (parameter, kind) ->
                    match kind with
                    | Some(ParamType.Bool | ParamType.Set _) -> Some(DefinitionError.InvalidValues(name, parameter))
                    | Some kind when valuesProblem kind -> Some(DefinitionError.InvalidValues(name, parameter))
                    | _ -> None)

            let queryErrors =
                route.Query
                |> List.collect (fun parameter ->
                    [ if valuesProblem parameter.Type then DefinitionError.InvalidValues(name, parameter.Name)
                      match parameter.Default with
                      | Some _ when parameter.Required -> DefinitionError.RequiredWithDefault(name, parameter.Name)
                      | Some value when (Values.render parameter.Type value).IsNone -> DefinitionError.InvalidDefault(name, parameter.Name)
                      | _ -> () ])

            let redirectErrors =
                match route.Redirect with
                | None -> []
                | Some(target, templates) ->
                    let sources = pathParams route |> List.map fst
                    [ if (Router.chainOf table target).IsNone then DefinitionError.UnknownTarget(name, target)
                      for (_, template) in templates do
                          match template with
                          | Template.FromParam source when not (List.contains source sources) -> DefinitionError.UnknownParameter(name, source)
                          | _ -> () ]

            segmentErrors @ duplicateParams @ reservedNames @ pathTypes @ queryErrors @ redirectErrors @ check table name (inherited @ local) route.Children

        duplicates @ (routes |> List.collect perRoute)

    let private legacyRoute (index: int) (legacy: LegacyRoute) =
        let name = $"legacy-{index + 1}"
        Route.define name legacy.Path
        |> Result.map (fun route -> { route with Redirect = Some(legacy.To, legacy.Params); ReturnTarget = false })

    /// Legacy entries are matched after every current route and before the
    /// first top-level wildcard, so a current route always wins.
    let private withLegacy (routes: Route list) (legacy: Route list) =
        let isWildcard (route: Route) = match route.Path with Segment.Wildcard _ :: _ -> true | _ -> false
        let before = routes |> List.takeWhile (isWildcard >> not)
        let after = routes |> List.skipWhile (isWildcard >> not)
        before @ legacy @ after

    /// A validated table, or every problem found (LCP-090).
    let define (routes: Route list) (legacy: LegacyRoute list) (roles: Roles) : Result<RouteTable, DefinitionError list> =
        let legacyRoutes = legacy |> List.mapi legacyRoute
        let legacyErrors = legacyRoutes |> List.choose (function Error e -> Some e | Ok _ -> None)
        let parsed = legacyRoutes |> List.choose (function Ok r -> Some r | Error _ -> None)
        let matching = withLegacy routes parsed
        let isDestination name = (Router.chainOf routes name |> Option.map (fun chain -> (chain |> List.last |> snd).Redirect.IsNone)) = Some true

        let roleErrors =
            [ if not (isDestination roles.Home) then DefinitionError.UnknownRole("home", roles.Home)
              match roles.SignIn with
              | Some signIn when not (isDestination signIn) -> DefinitionError.UnknownRole("signIn", signIn)
              | _ -> ()
              match roles.NotFound with
              | Some notFound when not (isDestination notFound) -> DefinitionError.UnknownRole("notFound", notFound)
              | _ -> () ]

        let errors = check matching "" [] matching @ legacyErrors @ roleErrors
        if errors.IsEmpty then Ok { routes = routes; legacy = legacy; roles = roles; matching = matching } else Error errors

    /// The routes as matched: the table's own, with the legacy entries.
    let routes (table: RouteTable) = table.matching
    let declared (table: RouteTable) = table.routes
    let legacy (table: RouteTable) = table.legacy
    let roles (table: RouteTable) = table.roles

    /// Every destination's full name with its chain, in table order.
    let destinations (table: RouteTable) = destinationsOf "" table.matching

/// What an application route maps to: a destination and its typed values.
type Target =
    { Route: string
      Params: Map<string, Value>
      Query: Map<string, Value> }

/// Why a location is not one of the application's routes (LCP-098). Every
/// case has its own view; none is a blank page or another route's view.
[<RequireQualifiedAccess>]
type RouteError =
    | NotFound
    | NotPermitted of route: string
    | Invalid of route: string * parameter: string * value: string * expected: string
    /// "path", "query" or "length".
    | Malformed of part: string
    | RedirectLoop of chain: string list
    /// The application's mapping refused a match.
    | Unmapped of route: string * problem: string

module RouteError =
    /// A resolution as a match or a route error. A match of the table's
    /// not-found route is NotFound.
    let ofResolution (table: RouteTable) (resolution: Resolution) : Result<Match, RouteError> =
        match resolution with
        | Resolution.Matched matched when Some matched.Route = (RouteTable.roles table).NotFound -> Error RouteError.NotFound
        | Resolution.Matched matched -> Ok matched
        | Resolution.NotFound -> Error RouteError.NotFound
        | Resolution.MalformedPath -> Error(RouteError.Malformed "path")
        | Resolution.MalformedQuery -> Error(RouteError.Malformed "query")
        | Resolution.TooLong -> Error(RouteError.Malformed "length")
        | Resolution.Invalid(route, parameter, value, expected) -> Error(RouteError.Invalid(route, parameter, value, expected))
        | Resolution.RedirectLoop chain -> Error(RouteError.RedirectLoop chain)
        | Resolution.Denied route -> Error(RouteError.NotPermitted route)

/// The engine's navigation state: the location it last adopted or pushed.
type RouterState = { Current: string option }

module Navigation =
    let initial = { Current = None }

    /// A location the browser reported (a deep link in Initialize, or Back and
    /// Forward in LocationChanged). Never answered with a push: at most a
    /// replace that corrects the entry to its canonical form.
    let adopt (table: Route list) guard (state: RouterState) (location: string) =
        let resolution = Router.resolveLocation table guard location

        match resolution with
        | Resolution.Matched matched ->
            match Router.canonical table matched with
            | Ok canonical when canonical <> location -> { Current = Some canonical }, resolution, Some(NavigationEffect.Replace canonical)
            | Ok canonical -> { Current = Some canonical }, resolution, None
            | Error _ -> { state with Current = Some location }, resolution, None
        | _ -> { Current = Some location }, resolution, None

    /// An in-app navigation to another place: push the built location unless
    /// it is already current (LCP-096).
    let navigate (table: Route list) (state: RouterState) route parameters query =
        Router.build table route parameters query
        |> Result.map (fun location ->
            if state.Current = Some location then state, None
            else { Current = Some location }, Some(NavigationEffect.Push location))

    /// Replace the current entry with a location the engine decided on, such
    /// as ReturnTo.resume after sign-in; nothing when it is already current.
    let replace (state: RouterState) (location: string) =
        if state.Current = Some location then state, None
        else { Current = Some location }, Some(NavigationEffect.Replace location)

    /// An in-place refinement of the current view (a filter, sort, page, tab
    /// or date): replace, so Back steps to the previous place (LCP-096).
    let refine (table: Route list) (state: RouterState) route parameters query =
        Router.build table route parameters query
        |> Result.map (fun location ->
            if state.Current = Some location then state, None
            else { Current = Some location }, Some(NavigationEffect.Replace location))

/// A typed codec: the table mapped onto the application's own route type.
[<NoEquality; NoComparison>]
type RouteCodec<'Route> =
    { Table: RouteTable
      ToTarget: 'Route -> Target
      OfMatch: Match -> Result<'Route, string> }

module RouteCodec =
    let create table (toTarget: 'Route -> Target) (ofMatch: Match -> Result<'Route, string>) =
        { Table = table; ToTarget = toTarget; OfMatch = ofMatch }

    let private typed (codec: RouteCodec<'Route>) resolution =
        RouteError.ofResolution codec.Table resolution
        |> Result.bind (fun matched -> codec.OfMatch matched |> Result.mapError (fun problem -> RouteError.Unmapped(matched.Route, problem)))

    /// A routed location ("/path?query") → the application's route.
    let parse (codec: RouteCodec<'Route>) guard (location: string) : Result<'Route, RouteError> =
        Router.resolveLocation (RouteTable.routes codec.Table) guard location |> typed codec

    /// The application's route → its canonical location.
    let format (codec: RouteCodec<'Route>) (route: 'Route) : Result<string, BuildError> =
        let target = codec.ToTarget route
        Router.build (RouteTable.routes codec.Table) target.Route target.Params target.Query

    /// Navigation.adopt, typed.
    let adopt (codec: RouteCodec<'Route>) guard (state: RouterState) (location: string) =
        let next, resolution, effect = Navigation.adopt (RouteTable.routes codec.Table) guard state location
        next, typed codec resolution, effect

    let private move operation (codec: RouteCodec<'Route>) (state: RouterState) (route: 'Route) =
        let target = codec.ToTarget route
        operation (RouteTable.routes codec.Table) state target.Route target.Params target.Query

    /// Navigation.navigate, typed: a push.
    let navigate codec state route = move Navigation.navigate codec state route

    /// Navigation.refine, typed: a replace.
    let refine codec state route = move Navigation.refine codec state route
