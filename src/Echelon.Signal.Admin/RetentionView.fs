/// What the dataset lifecycle and retention screens show (WI-0077,
/// `RetentionScreen`): the dataset's state and the states it may move to,
/// and for the open group whether its accepted results are held or retired,
/// with what reconstruction remains.
///
/// Pure.
module Echelon.Signal.Admin.RetentionView

open Echelon.Signal.Engine.View
open Echelon.Signal.Admin.AdminTypes
open Echelon.Signal.Admin.AdminApp

let private text (value: string) = Value(Text value)
let private flag (value: bool) = Value(Flag value)

let project (model: Model) (selected: GroupSummary option) : (string * ViewValue) list =
    let can = capabilities model
    let dataset = model.Dataset
    let manage = can.Contains AdminState.CanManageLifecycle
    let retired = selected |> Option.bind _.SourcesRetired
    let confirming = selected |> Option.exists (fun g -> model.RetireConfirm = Some g.Key)

    [ // The dataset's lifecycle (ADM-045).
      "datasetLifecycle", text (dataset |> Option.map (_.Lifecycle >> Retention.stateId) |> Option.defaultValue "")
      "datasetLifecycleText", text (dataset |> Option.map (_.Lifecycle >> RetentionScreen.stateLabel) |> Option.defaultValue "")
      "canManageLifecycle", flag manage
      "lifecycleTargets",
      Items(
          dataset
          |> Option.filter (fun _ -> manage)
          |> Option.map (fun d ->
              RetentionScreen.offered d.Lifecycle
              |> List.map (fun target ->
                  [ "target", Text(Retention.stateId target)
                    "targetLabel", Text(RetentionScreen.targetLabel target)
                    "targetMeaning", Text(RetentionScreen.stateLabel target) ]))
          |> Option.defaultValue []
      )
      // The open group's accepted results (ADM-045, ADM-064).
      "sourcesRetired", flag retired.IsSome
      "sourcesRetiredText", text (retired |> Option.map RetentionScreen.reconstructionLabel |> Option.defaultValue "")
      "canRetireSources", flag (retired.IsNone && not confirming && can.Contains AdminState.CanRetireSources)
      "confirmingRetire", flag (retired.IsNone && confirming && can.Contains AdminState.CanRetireSources)
      "retireWarning", text RetentionScreen.retireWarning ]
