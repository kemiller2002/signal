/// What the report builder adds to the page (WI-0075, ADM-021): the saved
/// definitions with their versions, the definition being built, its checks
/// against a group and a preview with that group's current state.
///
/// Pure.
module Echelon.Signal.Admin.ReportBuilderView

open Echelon.Signal.Engine
open Echelon.Signal.Engine.View
open Echelon.Signal.Engine.ReportModel
open Echelon.Signal.Admin.Routes
open Echelon.Signal.Admin.AdminApp
open Echelon.Signal.Admin.ReportBuilder

let private text (value: string) = Value(Text value)
let private flag (value: bool) = Value(Flag value)

let project (model: Model) : (string * ViewValue) list =
    let can = capabilities model
    let builder = model.Builder.Builder
    let definition = builder |> Option.map _.Definition
    let groups = model.Dataset |> Option.map _.Groups |> Option.defaultValue []
    let preview = model.Builder.PreviewGroup |> Option.bind (fun key -> groups |> List.tryFind (fun g -> g.Key = key))

    let checks =
        match definition, preview with
        | Some d, Some g -> Report.check d g.Mode g.MinimumReportable |> List.map (fun p -> $"%A{p}")
        | Some d, None -> Report.check d Echelon.Signal.Engine.Import.IdentifiedGroup 0 |> List.map (fun p -> $"%A{p}")
        | None, _ -> []

    let previewLines =
        match definition, preview with
        | Some d, Some g ->
            match AdminReportView.buildWith model g d with
            | Ok data ->
                [ $"{data.Title}: {data.Counts.Accepted} of {data.Counts.Expected} accepted."
                  yield!
                      data.Sections
                      |> List.map (fun s ->
                          let mean = AdminReportView.valueText "en-US" d.Decimals s.Mean
                          $"{s.Section} {s.Title}: mean {mean}") ]
            | Error reasons -> reasons |> List.map (fun r -> "Withheld: " + r)
        | _ -> []

    let pinOptions =
        model.Catalog |> List.map _.SurveyIdentifier |> List.distinct |> List.map (fun s -> "latest:" + s, $"Latest published {s}")

    [ "viewReports", flag (match model.Place.View with Ok Reports -> true | _ -> false)
      "canBuildReports", flag (can.Contains AdminState.CanBuildReport)
      "reportsHref", text (href Reports)
      "newReportId", text model.Builder.NewId
      "hasBuilderProblem", flag model.Builder.Problem.IsSome
      "builderProblem", text (model.Builder.Problem |> Option.defaultValue "")
      "savedDefinitions",
      Items(
          model.Library
          |> Map.toList
          |> List.map (fun (id, versions) ->
              let latest = List.last versions
              [ "key", Text id
                "label", Text $"""{id} v{latest.Definition.Version} ({if latest.Used then "used for a snapshot: edits make a new version" else "not used yet"})"""
                "pin", Text(pinValue latest.Pins.Template) ])
      )
      "hasBuilder", flag builder.IsSome
      "builderHeading", text (definition |> Option.map (fun d -> $"{d.Id} (version {d.Version})") |> Option.defaultValue "")
      "builderUnsaved", flag (builder |> Option.exists _.Unsaved)
      "builderAudiences", Items(audiences |> List.map (fun a -> [ "value", Text(audienceName a); "label", Text(audienceName a); "selected", Flag(definition |> Option.exists (fun d -> d.Audience = a)) ]))
      "builderDetails", Items(details |> List.map (fun x -> [ "value", Text(detailName x); "label", Text(detailName x); "selected", Flag(definition |> Option.exists (fun d -> d.Detail = x)) ]))
      "builderBlocks",
      Items(
          let chosen = definition |> Option.map _.Blocks |> Option.defaultValue []
          (chosen @ (blocks |> List.filter (fun b -> not (List.contains b chosen))))
          |> List.map (fun b -> [ "key", Text(blockName b); "label", Text(blockName b); "checked", Flag(List.contains b chosen) ])
      )
      "builderMinimum", text (definition |> Option.map (fun d -> string d.MinimumGroupSize) |> Option.defaultValue "")
      "builderDecimals", text (definition |> Option.map (fun d -> string d.Decimals) |> Option.defaultValue "")
      "builderHighlights", text (definition |> Option.map (fun d -> string d.HighlightCount) |> Option.defaultValue "")
      "builderPins", Items(pinOptions |> List.map (fun (v, l) -> [ "value", Text v; "label", Text l; "selected", Flag(builder |> Option.exists (fun b -> pinValue b.Pin = v)) ]))
      "builderGroups",
      Items(([ "", "No group" ] @ (groups |> List.map (fun g -> g.Key, $"{g.SurveyIdentifier} group {g.Key}"))) |> List.map (fun (v, l) -> [ "value", Text v; "label", Text l; "selected", Flag(model.Builder.PreviewGroup = (if v = "" then None else Some v)) ]))
      "builderChecks", Items(checks |> List.mapi (fun i c -> [ "key", Text(string i); "text", Text c ]))
      "builderPreview", Items(previewLines |> List.mapi (fun i l -> [ "key", Text(string i); "text", Text l ])) ]
