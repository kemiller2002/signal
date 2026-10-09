/// The administrator application's URL space (SIG-LINK-001..011,
/// DF-SIGNAL-2026-0003): the route table, Signal's typed route, and the codec
/// between them, on Limen's routing semantics (`Limen.Routing`).
///
/// Every view has one canonical location in the fragment (`#/…`). Identifiers
/// are opaque (group keys, instrument, section and question ids); view
/// parameters are typed. No route carries an answer, a respondent's data or
/// free text, and credential-like parameter names are refused by the table
/// (SIG-LINK-008).
///
/// Pure and total: a location that is not a route is a `RouteError` value.
module Echelon.Signal.Admin.Routes

open Limen.Routing

/// How a group's results are sorted.
type Sort =
    | ByOrder
    | ByValue

/// Whether distributions show counts or percentages.
type Display =
    | Counts
    | Percentages

/// How a group's results are explored (ADM-019): the section drilled into,
/// the sort and the display.
type ResultsView =
    { Section: string option
      Sort: Sort
      Display: Display }

let defaultResults =
    { Section = None
      Sort = ByOrder
      Display = Counts }

/// The group list's filters: typed values only, never free text.
type GroupFilter =
    { Status: string list
      Mode: string list
      Survey: string option }

let noFilter = { Status = []; Mode = []; Survey = None }

/// Every view of the administrator application.
type AdminRoute =
    | Overview
    | SignIn of returnTo: string option
    | NotFoundPage
    | Assessments
    | Assessment of assessment: string * version: string
    | Section of assessment: string * version: string * section: string
    | Question of assessment: string * version: string * section: string * question: string
    | Groups of GroupFilter
    | Group of group: string
    | Results of group: string * ResultsView
    | Scoring of group: string * section: string option
    | Imports of group: string * outcomes: string list
    /// A group's report in one family and locale (WI-0062).
    | Report of group: string * family: string * locale: string
    | Compare of groups: string list * section: string option
    /// A survey's draft in the authoring screens (WI-0073).
    | Draft of survey: string
    | Administrators
    | Storage

/// Group statuses as the filter writes them (GroupLifecycle.statusName, kebab-case).
let statusValues = [ "collecting"; "closed-incomplete"; "finalized"; "finalized-incomplete"; "sealed"; "superseded" ]

let modeValues = [ "anonymous"; "identified" ]

/// Import outcomes as the imported-surveys filter writes them (ADM-008's views).
let outcomeValues = [ "accepted"; "blocked"; "duplicate"; "pending"; "reconciliation"; "rejected" ]

/// Report families and locales a report route may name; the defaults are
/// the administrator's group report in English.
let familyValues = Echelon.Signal.Engine.ReportExport.families |> List.map _.Id
let defaultFamily = Echelon.Signal.Engine.ReportExport.administratorGroup.Id
let localeValues = Locale.supported |> List.map _.Tag
let defaultLocale = Locale.english.Tag

/// The guard every route that shows dataset content carries.
[<Literal>]
let SignedIn = "signedIn"

let private guarded (route: Route) =
    { route with
        Guard = Some SignedIn
        Requires = [ "dataset" ] }

let private withQuery (query: QueryParam list) (route: Route) = { route with Query = query }

let private section = QueryParam.optional "section" ParamType.String

let private routes =
    [ Route.create "overview" "" |> guarded
      Route.create "sign-in" "sign-in" |> withQuery [ QueryParam.optional ReturnTo.parameter ParamType.String ] |> fun r -> { r with ReturnTarget = false }
      { Route.create "not-found" "not-found" with ReturnTarget = false }
      { Route.create "assessments" "assessments" with Requires = [ "catalog" ] }
      { Route.create "assessment" "assessments/{assessment}/versions/{version}" with Requires = [ "catalog" ] }
      { Route.create "section" "assessments/{assessment}/versions/{version}/sections/{section}" with Requires = [ "catalog" ] }
      { Route.create "question" "assessments/{assessment}/versions/{version}/sections/{section}/questions/{question}" with Requires = [ "catalog" ] }
      Route.create "groups" "groups"
      |> withQuery
          [ QueryParam.optional "status" (ParamType.Set statusValues)
            QueryParam.optional "mode" (ParamType.Set modeValues)
            QueryParam.optional "survey" ParamType.String ]
      |> guarded
      Route.create "group" "groups/{group}" |> guarded
      Route.create "results" "groups/{group}/results"
      |> withQuery
          [ section
            QueryParam.optional "sort" (ParamType.Enum [ "order"; "value" ]) |> QueryParam.withDefault (Value.Text "order")
            QueryParam.optional "display" (ParamType.Enum [ "count"; "percent" ]) |> QueryParam.withDefault (Value.Text "count") ]
      |> guarded
      Route.create "scoring" "groups/{group}/scoring" |> withQuery [ section ] |> guarded
      Route.create "imports" "groups/{group}/imports" |> withQuery [ QueryParam.optional "outcome" (ParamType.Set outcomeValues) ] |> guarded
      Route.create "report" "groups/{group}/report"
      |> withQuery
          [ QueryParam.optional "family" (ParamType.Enum familyValues) |> QueryParam.withDefault (Value.Text defaultFamily)
            QueryParam.optional "locale" (ParamType.Enum localeValues) |> QueryParam.withDefault (Value.Text defaultLocale) ]
      |> guarded
      Route.create "compare" "compare" |> withQuery [ QueryParam.optional "groups" (ParamType.Set []); section ] |> guarded
      Route.create "draft" "templates/{survey}/draft" |> guarded
      Route.create "administrators" "administrators" |> guarded
      Route.create "storage" "storage" |> guarded ]

/// Administrator links published before SIG-LINK (SIG-LINK-011).
let private legacy =
    [ { Path = "overview"; To = "overview"; Params = [] }
      { Path = "groups/{group}/explore/{*view}"
        To = "results"
        Params = [ "group", Template.FromParam "group" ] } ]

let private roles =
    { Home = "overview"
      SignIn = Some "sign-in"
      NotFound = Some "not-found" }

/// The table, or every problem with it.
let definition = RouteTable.define routes legacy roles

/// The table. A table that does not define is a programming defect, and
/// fails loudly at start-up (and in the tests).
let table =
    match definition with
    | Ok table -> table
    | Error problems -> invalidOp $"The administrator route table does not define: %A{problems}"

let private text (value: string) = Value.Text value
let private members (values: string list) = Value.Members values
let private optionalText name = Option.map (fun (v: string) -> name, text v) >> Option.toList

let private target (route: string) (parameters: (string * Value) list) (query: (string * Value) list) : Target =
    { Route = route
      Params = Map parameters
      Query = Map query }

/// A typed route as the table's destination and values.
let toTarget (route: AdminRoute) : Target =
    match route with
    | Overview -> target "overview" [] []
    | SignIn returnTo -> target "sign-in" [] (optionalText ReturnTo.parameter returnTo)
    | NotFoundPage -> target "not-found" [] []
    | Assessments -> target "assessments" [] []
    | Assessment(a, v) -> target "assessment" [ "assessment", text a; "version", text v ] []
    | Section(a, v, s) -> target "section" [ "assessment", text a; "version", text v; "section", text s ] []
    | Question(a, v, s, q) -> target "question" [ "assessment", text a; "version", text v; "section", text s; "question", text q ] []
    | Groups filter -> target "groups" [] ([ "status", members filter.Status; "mode", members filter.Mode ] @ optionalText "survey" filter.Survey)
    | Group g -> target "group" [ "group", text g ] []
    | Results(g, view) ->
        target
            "results"
            [ "group", text g ]
            (optionalText "section" view.Section
             @ [ "sort", text (if view.Sort = ByValue then "value" else "order")
                 "display", text (if view.Display = Percentages then "percent" else "count") ])
    | Scoring(g, s) -> target "scoring" [ "group", text g ] (optionalText "section" s)
    | Imports(g, outcomes) -> target "imports" [ "group", text g ] [ "outcome", members outcomes ]
    | Report(g, family, locale) -> target "report" [ "group", text g ] [ "family", text family; "locale", text locale ]
    | Compare(groups, s) -> target "compare" [] ([ "groups", members groups ] @ optionalText "section" s)
    | Draft survey -> target "draft" [ "survey", text survey ] []
    | Administrators -> target "administrators" [] []
    | Storage -> target "storage" [] []

/// A match as a typed route; the table's types make every lookup succeed,
/// and a mismatch is a value, not an exception.
let ofMatch (m: Match) : Result<AdminRoute, string> =
    let values = m.Chain |> List.collect (fun level -> Map.toList level.Params) |> Map.ofList
    let path name = match values.TryFind name with Some(Value.Text v) -> Some v | _ -> None
    let query name = match m.Query.TryFind name with Some(Value.Text v) -> Some v | _ -> None
    let set name = match m.Query.TryFind name with Some(Value.Members v) -> v | _ -> []
    let need (value: 'T option) =
        match value with
        | Some v -> Ok v
        | None -> Error $"route {m.Route} without its parameters"

    match m.Route with
    | "overview" -> Ok Overview
    | "sign-in" -> Ok(SignIn(query ReturnTo.parameter))
    | "not-found" -> Ok NotFoundPage
    | "assessments" -> Ok Assessments
    | "assessment" -> Option.map2 (fun a v -> Assessment(a, v)) (path "assessment") (path "version") |> need
    | "section" ->
        match path "assessment", path "version", path "section" with
        | Some a, Some v, Some s -> Ok(Section(a, v, s))
        | _ -> need None
    | "question" ->
        match path "assessment", path "version", path "section", path "question" with
        | Some a, Some v, Some s, Some q -> Ok(Question(a, v, s, q))
        | _ -> need None
    | "groups" ->
        Ok(
            Groups
                { Status = set "status"
                  Mode = set "mode"
                  Survey = query "survey" }
        )
    | "group" -> path "group" |> Option.map Group |> need
    | "results" ->
        path "group"
        |> Option.map (fun g ->
            Results(
                g,
                { Section = query "section"
                  Sort = (if query "sort" = Some "value" then ByValue else ByOrder)
                  Display = (if query "display" = Some "percent" then Percentages else Counts) }
            ))
        |> need
    | "scoring" -> path "group" |> Option.map (fun g -> Scoring(g, query "section")) |> need
    | "imports" -> path "group" |> Option.map (fun g -> Imports(g, set "outcome")) |> need
    | "report" -> path "group" |> Option.map (fun g -> Report(g, defaultArg (query "family") defaultFamily, defaultArg (query "locale") defaultLocale)) |> need
    | "compare" -> Ok(Compare(set "groups", query "section"))
    | "draft" -> path "survey" |> Option.map Draft |> need
    | "administrators" -> Ok Administrators
    | "storage" -> Ok Storage
    | other -> Error $"no view for {other}"

/// The codec: typed routes to canonical locations and back (SIG-LINK-003).
let codec = RouteCodec.create table toTarget ofMatch

/// Where the routed location lives: the fragment (SIG-LINK-002).
let mode = LocationMode.Hash

/// Whether the person may see dataset views now, as the guard reads it.
type Access =
    /// Signed in, or still finding out, or no sign-in is configured: allow.
    | Open
    /// Sign-in is configured and the person is signed out.
    | SignInRequired

/// The guards' decisions (interface policy, never an authorization
/// boundary: the store refuses what the person may not do). A signed-out
/// person is sent to sign-in with the view as the return target.
let guard (access: Access) : string -> Match -> GuardDecision =
    fun name matched ->
        match access, name with
        | SignInRequired, SignedIn ->
            let returnTo =
                Router.canonical (RouteTable.routes table) matched
                |> Result.toOption
                |> Option.bind (ReturnTo.capture table)
                |> Option.map (fun target -> Map [ ReturnTo.parameter, Value.Text target ])
                |> Option.defaultValue Map.empty

            GuardDecision.Redirect("sign-in", Map.empty, returnTo)
        | _ -> GuardDecision.Allow

/// A route's canonical location ("/path?query").
let location (route: AdminRoute) =
    RouteCodec.format codec route |> Result.defaultValue "/"

/// A route's relative link for the markup ("#/path?query").
let href (route: AdminRoute) = Location.href mode (location route)

/// The route inventory, as `.echelon/routes.json` holds it (SIG-LINK-009).
let inventory () = Inventory.render mode table
