/// What the routed views add to the administrator page (SIG-LINK-001,
/// SIG-LINK-005, SIG-LINK-007): links from the route codec, the view the URL
/// names, Copy link, a page for every location that names nothing, and the
/// assessment, comparison and imported-survey views with their typed
/// filters. Every link is relative (`#/…`) and canonical.
///
/// Pure.
module Echelon.Signal.Admin.AdminRouteView

open System.Globalization
open Limen.Routing
open Echelon.Signal.Engine.View
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin.Routes
open Echelon.Signal.Admin.AdminApp

let private text (value: string) = Value(Text value)
let private flag (value: bool) = Value(Flag value)

/// Why the view the URL names cannot be shown: a title and a sentence.
type Problem = { Title: string; Message: string }

let private notFound (message: string) = Some { Title = "Not found"; Message = message }

/// The catalog entry an assessment route names.
let private entryOf (model: Model) (assessment: string) (version: string) =
    model.Catalog |> List.tryFind (fun e -> e.Content.Id = assessment && e.Content.Version = version)

/// The assessment views' subject, or what is missing.
let private assessmentPlace (model: Model) =
    let find a v = entryOf model a v |> Option.map (fun e -> e.Content)

    match model.Place.View with
    | Ok(Assessment(a, v)) ->
        match find a v with
        | Some c -> Ok(c, None, None)
        | None -> Error $"No assessment {a} at version {v} is in the catalog."
    | Ok(Section(a, v, s)) ->
        match find a v with
        | None -> Error $"No assessment {a} at version {v} is in the catalog."
        | Some c when c.Dimensions |> List.exists (fun d -> d.Id = s) -> Ok(c, Some s, None)
        | Some c -> Error $"{c.Title} {v} has no section {s}."
    | Ok(Question(a, v, s, q)) ->
        match find a v with
        | None -> Error $"No assessment {a} at version {v} is in the catalog."
        | Some c when c.Items |> List.exists (fun i -> i.Id = q && i.DimensionId = s) -> Ok(c, Some s, Some q)
        | Some c -> Error $"{c.Title} {v} has no question {q} in section {s}."
    | _ -> Error ""

/// The problem with the current location, if any: a route error, or an
/// identifier that names nothing (SIG-LINK-007).
let problem (model: Model) =
    match model.Place.View with
    | Error RouteError.NotFound
    | Ok NotFoundPage -> notFound "Nothing in Signal has this address. Check the link, or start from the overview."
    | Error(RouteError.Invalid(_, parameter, value, expected)) ->
        Some { Title = "This link is not valid"; Message = $"'{value}' is not a valid {parameter}: expected {expected}." }
    | Error(RouteError.Malformed _) -> Some { Title = "This link is damaged"; Message = "The address cannot be read. Ask for the link again." }
    | Error(RouteError.NotPermitted _) -> Some { Title = "Not available"; Message = "This view is not available to you." }
    | Error(RouteError.RedirectLoop _ | RouteError.Unmapped _) -> notFound "Nothing in Signal has this address."
    | Ok(Assessment _ | Section _ | Question _) ->
        match assessmentPlace model with
        | Error message -> notFound message
        | Ok _ -> None
    | Ok _ ->
        match groupKey model, model.Dataset with
        | Some key, Some dataset when not (dataset.Groups |> List.exists (fun g -> g.Key = key)) ->
            notFound $"No group with the identifier {key} is in {dataset.Label}."
        | _ -> None

/// Views that need neither sign-in nor a dataset: assessments and problems.
let isPublic (model: Model) =
    match model.Place.View with
    | Ok(Assessments | Assessment _ | Section _ | Question _ | NotFoundPage)
    | Error _ -> true
    | _ -> false

let private viewIs (model: Model) (test: AdminRoute -> bool) =
    match model.Place.View with
    | Ok route -> test route
    | Error _ -> false

let private areas (model: Model) =
    let current (route: AdminRoute) =
        match model.Place.View, route with
        | Ok(Groups _ | Group _ | Results _ | Scoring _ | Imports _ | Report _), Groups _ -> true
        | Ok(Assessments | Assessment _ | Section _ | Question _ | Draft _), Assessments -> true
        | Ok r, _ -> r = route
        | _ -> false

    [ Overview, "Overview"; Groups noFilter, "Groups"; Compare([], None), "Compare"; Assessments, "Assessments"; Reports, "Reports"; Administrators, "Administrators"; Storage, "Storage" ]
    |> List.map (fun (route, label) -> [ "label", Text label; "href", Text(href route); "current", Text(if current route then "page" else "false") ])

let private assessments (model: Model) : View =
    let place = assessmentPlace model
    let content, section, question =
        match place with
        | Ok(c, s, q) -> Some c, s, q
        | Error _ -> None, None, None

    let a, v = content |> Option.map (fun c -> c.Id, c.Version) |> Option.defaultValue ("", "")

    let questions =
        content
        |> Option.map (fun c -> c.Items |> List.indexed |> List.filter (fun (_, i) -> Some i.DimensionId = section))
        |> Option.defaultValue []

    let chosen = content |> Option.bind (fun c -> c.Items |> List.tryFind (fun i -> Some i.Id = question))

    [ "catalogAssessments", Items(model.Catalog |> List.map (fun e -> [ "label", Text $"{e.Title} ({e.SurveyIdentifier} {e.Version})"; "href", Text(href (Assessment(e.Content.Id, e.Content.Version))) ]))
      // The dataset's stored catalog (WI-0073): every published version and draft.
      "hasStoredTemplates", flag (not model.Templates.Published.IsEmpty || not model.Templates.Drafts.IsEmpty)
      "templateVersions",
      Items(
          model.Templates.Published
          |> List.map (fun r ->
              [ "key", Text r.Hash
                "label", Text $"{r.Title} ({r.SurveyId} {r.Version})"
                "status", Text(if r.Hidden then "Hidden from new groups" elif r.Superseded then "Superseded" else "Listed")
                "groups", Text(r.NotForGroups |> Option.map (fun reason -> $"Cannot start groups: {reason}.") |> Option.defaultValue "Can start groups.") ])
      )
      "templateDrafts", Items(model.Templates.Drafts |> List.map (fun (surveyId, title) -> [ "key", Text surveyId; "label", Text $"{title} ({surveyId}, draft)" ]))
      "hasAssessment", flag content.IsSome
      "assessmentTitle", text (content |> Option.map (fun c -> $"{c.Title} {c.Version}") |> Option.defaultValue "")
      "assessmentHref", text (if content.IsSome then href (Assessment(a, v)) else "")
      "assessmentSections",
      Items(
          content
          |> Option.map (fun c -> c.Dimensions |> List.map (fun d -> [ "id", Text d.Id; "label", Text d.Label; "href", Text(href (Section(a, v, d.Id))); "current", Text(if Some d.Id = section then "page" else "false") ]))
          |> Option.defaultValue []
      )
      "hasSection", flag section.IsSome
      "sectionLabel", text (content |> Option.bind (fun c -> c.Dimensions |> List.tryFind (fun d -> Some d.Id = section)) |> Option.map (fun d -> $"{d.Id} {d.Label}") |> Option.defaultValue "")
      "sectionQuestions",
      Items(questions |> List.map (fun (n, i) -> [ "id", Text i.Id; "number", Text(string (n + 1)); "prompt", Text i.Prompt; "href", Text(href (Question(a, v, i.DimensionId, i.Id))); "current", Text(if Some i.Id = question then "page" else "false") ]))
      "hasQuestion", flag chosen.IsSome
      "questionId", text (chosen |> Option.map _.Id |> Option.defaultValue "")
      "questionPrompt", text (chosen |> Option.map _.Prompt |> Option.defaultValue "") ]

let private outcomeOf (code: string) =
    if code = "accepted" then "accepted"
    elif code = "pending" then "pending"
    elif code = "duplicate" || code = "rejected:duplicate-instance" then "duplicate"
    elif code = "reconciliation-required" then "reconciliation"
    elif code.StartsWith "blocked:" then "blocked"
    else "rejected"

let private imports (model: Model) (group: GroupSummary option) : View =
    let outcomes = match model.Place.View with Ok(Imports(_, o)) -> o | _ -> []

    [ "outcomeFilters", Items(outcomeValues |> List.map (fun o -> [ "key", Text o; "label", Text o; "checked", Flag(List.contains o outcomes) ]))
      "importedSurveys",
      Items(
          group
          |> Option.map (fun g ->
              g.Items
              |> List.filter (fun (_, code) -> outcomes.IsEmpty || List.contains (outcomeOf code) outcomes)
              |> List.map (fun (hash, code) -> [ "artifact", Text(hash.Substring(0, min 19 hash.Length)); "outcome", Text code ]))
          |> Option.defaultValue []
      ) ]

let private score (value: float option) =
    value |> Option.map (fun v -> v.ToString("0.0", CultureInfo.InvariantCulture)) |> Option.defaultValue "suppressed"

/// Groups compare only with the same survey and template version, identity
/// mode and privacy minimum (ADM-014); anything else says why not.
let private comparison (model: Model) : View =
    let selected, section = match model.Place.View with Ok(Compare(g, s)) -> g, s | _ -> [], None
    let groups = model.Dataset |> Option.map _.Groups |> Option.defaultValue []
    let chosen = groups |> List.filter (fun g -> List.contains g.Key selected)

    let reasons =
        match chosen with
        | [] | [ _ ] -> [ "Choose at least two groups." ]
        | first :: rest ->
            [ if rest |> List.exists (fun g -> g.SurveyIdentifier <> first.SurveyIdentifier || g.TemplateVersion <> first.TemplateVersion) then "they use different surveys or template versions"
              if rest |> List.exists (fun g -> g.Mode <> first.Mode) then "their identity modes differ"
              if rest |> List.exists (fun g -> g.MinimumReportable <> first.MinimumReportable) then "their privacy minimums differ" ]

    let comparable = reasons.IsEmpty
    let sections = chosen |> List.collect (fun g -> g.Sections |> List.map fst) |> List.distinct

    [ "compareGroups", Items(groups |> List.map (fun g -> [ "key", Text g.Key; "survey", Text $"{g.SurveyIdentifier} {g.TemplateVersion}"; "checked", Flag(List.contains g.Key selected) ]))
      "compareSections", Items(sections |> List.map (fun s -> [ "id", Text s; "selected", Flag(Some s = section) ]))
      "compareComparable", flag comparable
      "compareReason", text (if comparable then "Comparable: same survey and template version, identity mode and privacy minimum." elif chosen.Length < 2 then reasons.Head else "Not comparable: " + String.concat "; " reasons + ".")
      "compareRows",
      Items(
          if not comparable then []
          else
              [ for s in sections |> List.filter (fun s -> section |> Option.forall ((=) s)) do
                    for g in chosen do
                        [ "key", Text $"{s}/{g.Key}"; "section", Text s; "group", Text g.Key; "score", Text(g.Sections |> List.tryFind (fst >> (=) s) |> Option.bind snd |> score) ] ]
      ) ]

/// The routed part of the page's view.
let project (model: Model) (group: GroupSummary option) : View =
    let issue = problem model
    let is test = issue.IsNone && viewIs model test
    let key = groupKey model |> Option.defaultValue ""
    let filter = match model.Place.View with Ok(Groups f) -> f | _ -> noFilter
    let scoringSection = match model.Place.View with Ok(Scoring(_, s)) -> s | _ -> None

    [ "areas", Items(areas model)
      "shareLink", text (AdminNavigation.shareLink model.Place)
      "hasProblem", flag issue.IsSome
      "problemTitle", text (issue |> Option.map _.Title |> Option.defaultValue "")
      "problemMessage", text (issue |> Option.map _.Message |> Option.defaultValue "")
      "homeHref", text (href Overview)
      "viewOverview", flag (is ((=) Overview))
      "viewGroups", flag (is (function Groups _ -> true | _ -> false))
      "viewGroup", flag (is (function Group _ -> true | _ -> false))
      "viewResults", flag (is (function Results _ -> true | _ -> false))
      "viewScoring", flag (is (function Scoring _ -> true | _ -> false))
      "viewImports", flag (is (function Imports _ -> true | _ -> false))
      "viewReport", flag (is (function Report _ -> true | _ -> false))
      "viewGroupTabs", flag (is (function Group _ | Results _ | Scoring _ | Imports _ | Report _ -> true | _ -> false))
      "viewCompare", flag (is (function Compare _ -> true | _ -> false))
      "viewAssessments", flag (is (function Assessments | Assessment _ | Section _ | Question _ -> true | _ -> false))
      "viewAdministrators", flag (is ((=) Administrators))
      "viewStorage", flag (is ((=) Storage))
      "groupHref", text (href (Group key))
      "resultsHref", text (href (Results(key, defaultResults)))
      "scoringHref", text (href (Scoring(key, None)))
      "importsHref", text (href (Imports(key, [])))
      "reportHref", text (href (Report(key, defaultFamily, defaultLocale)))
      "statusFilters", Items(statusValues |> List.map (fun s -> [ "key", Text s; "label", Text s; "checked", Flag(List.contains s filter.Status) ]))
      "modeFilters", Items(modeValues |> List.map (fun m -> [ "key", Text m; "label", Text m; "checked", Flag(List.contains m filter.Mode) ]))
      "surveyFilters",
      Items(([ "" ] @ (model.Catalog |> List.map _.SurveyIdentifier |> List.distinct)) |> List.map (fun s -> [ "value", Text s; "label", Text(if s = "" then "Any survey" else s); "selected", Flag(filter.Survey = (if s = "" then None else Some s)) ]))
      "scoringSections",
      Items(group |> Option.map (fun g -> g.Sections |> List.map (fun (id, _) -> [ "id", Text id; "href", Text(href (Scoring(key, Some id))); "current", Text(if Some id = scoringSection then "page" else "false") ])) |> Option.defaultValue [])
      "scoringMeasures",
      Items(
          group
          |> Option.map (fun g -> g.Analysis |> List.filter (fun (s, _, _) -> scoringSection |> Option.forall ((=) s)) |> List.map (fun (s, m, v) -> [ "key", Text $"{s}/{m}"; "section", Text s; "measure", Text m; "value", Text v ]))
          |> Option.defaultValue []
      )
      yield! assessments model
      yield! imports model group
      yield! comparison model ]
