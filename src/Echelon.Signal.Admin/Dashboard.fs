/// Dashboards (ADM-018, ADM-046): versioned, declarative definitions built
/// from deterministic blocks, edited by legal operations, projected to
/// desktop, tablet and mobile without separate business meaning, and read
/// back as untrusted input within complexity limits.
///
/// Pure.
module Echelon.Signal.Admin.Dashboard

open Arca
open Echelon.Signal.Admin.Codec

[<Literal>]
let DefinitionVersion = 1

/// The blocks ADM-018 names.
type BlockKind =
    | GroupStatus
    | ImportStatus
    | OverallResult
    | SectionSummary
    | Distribution
    | Trend
    | Comparison
    | Coverage
    | PrivacyWarnings
    | StorageHealth
    | Obligations

let private kinds =
    [ GroupStatus; ImportStatus; OverallResult; SectionSummary; Distribution; Trend; Comparison; Coverage; PrivacyWarnings; StorageHealth; Obligations ]

let kindName (kind: BlockKind) = $"%A{kind}"

/// The charts a block may use: its approved visualization types.
let approved (kind: BlockKind) : Visualization.ChartType list =
    match kind with
    | SectionSummary -> [ Visualization.Bar; Visualization.DotPlot; Visualization.RankedTable; Visualization.Radar ]
    | Distribution -> [ Visualization.Histogram ]
    | Trend -> [ Visualization.Line; Visualization.Sparkline ]
    | Comparison -> [ Visualization.Bar; Visualization.DotPlot ]
    | OverallResult
    | Coverage -> [ Visualization.MetricCards; Visualization.Bullet ]
    | GroupStatus
    | ImportStatus
    | PrivacyWarnings
    | StorageHealth
    | Obligations -> [ Visualization.RankedTable ]

type Block =
    { Id: string
      Kind: BlockKind
      Chart: Visualization.ChartType
      /// Measure names the block shows.
      Metrics: string list
      /// Width in columns of a four-column desktop grid, 1-4.
      Width: int }

type Definition =
    { Id: string
      Name: string
      Version: int
      Blocks: Block list }

/// Complexity limits for a definition read from storage (ADM-046).
[<Literal>]
let MaxBlocks = 24

[<Literal>]
let MaxMetricsPerBlock = 8

/// Why an edit is refused.
type EditRefusal =
    | UnknownBlock of id: string
    | DuplicateBlock of id: string
    | NotApproved of kind: string * chart: string
    | WidthOutOfRange of width: int
    | TooManyBlocks
    | TooManyMetrics of block: string

/// The problems a definition has, empty when it is valid.
let problems (definition: Definition) =
    [ if definition.Blocks.Length > MaxBlocks then TooManyBlocks
      for block in definition.Blocks do
          if not (List.contains block.Chart (approved block.Kind)) then NotApproved(kindName block.Kind, $"%A{block.Chart}")
          if block.Width < 1 || block.Width > 4 then WidthOutOfRange block.Width
          if block.Metrics.Length > MaxMetricsPerBlock then TooManyMetrics block.Id
      for id, count in definition.Blocks |> List.countBy _.Id do
          if count > 1 then DuplicateBlock id ]

/// An edit to a dashboard.
type Edit =
    | AddBlock of Block
    | RemoveBlock of id: string
    | MoveBlock of id: string * position: int
    | Resize of id: string * width: int
    | ChangeChart of id: string * Visualization.ChartType
    | SelectMetrics of id: string * metrics: string list

/// Applies an edit as a new version, or why not; the result is valid.
let edit (change: Edit) (definition: Definition) : Result<Definition, EditRefusal list> =
    let find id = definition.Blocks |> List.tryFind (fun b -> b.Id = id)
    let replace (block: Block) = definition.Blocks |> List.map (fun b -> if b.Id = block.Id then block else b)

    let next =
        match change with
        | AddBlock block when (find block.Id).IsSome -> Error [ DuplicateBlock block.Id ]
        | AddBlock block -> Ok(definition.Blocks @ [ block ])
        | RemoveBlock id when (find id).IsNone -> Error [ UnknownBlock id ]
        | RemoveBlock id -> Ok(definition.Blocks |> List.filter (fun b -> b.Id <> id))
        | MoveBlock(id, position) ->
            match find id with
            | None -> Error [ UnknownBlock id ]
            | Some block ->
                let others = definition.Blocks |> List.filter (fun b -> b.Id <> id)
                let at = max 0 (min position others.Length)
                Ok(List.take at others @ [ block ] @ List.skip at others)
        | Resize(id, width) -> find id |> Option.map (fun b -> Ok(replace { b with Width = width })) |> Option.defaultValue (Error [ UnknownBlock id ])
        | ChangeChart(id, chart) -> find id |> Option.map (fun b -> Ok(replace { b with Chart = chart })) |> Option.defaultValue (Error [ UnknownBlock id ])
        | SelectMetrics(id, metrics) -> find id |> Option.map (fun b -> Ok(replace { b with Metrics = metrics })) |> Option.defaultValue (Error [ UnknownBlock id ])

    next
    |> Result.bind (fun blocks ->
        let candidate = { definition with Blocks = blocks; Version = definition.Version + 1 }

        match problems candidate with
        | [] -> Ok candidate
        | found -> Error found)

/// A copy under a new id and name, at version 1.
let duplicate (id: string) (name: string) (definition: Definition) = { definition with Id = id; Name = name; Version = 1 }

/// The known starting dashboard for a group.
let template (id: string) =
    { Id = id
      Name = "Group overview"
      Version = 1
      Blocks =
        [ { Id = "status"; Kind = GroupStatus; Chart = Visualization.RankedTable; Metrics = [ "AcceptedCount"; "CompletionRate" ]; Width = 2 }
          { Id = "coverage"; Kind = Coverage; Chart = Visualization.MetricCards; Metrics = [ "Coverage" ]; Width = 2 }
          { Id = "sections"; Kind = SectionSummary; Chart = Visualization.Bar; Metrics = [ "Mean" ]; Width = 4 }
          { Id = "distribution"; Kind = Distribution; Chart = Visualization.Histogram; Metrics = [ "Distribution" ]; Width = 4 }
          { Id = "obligations"; Kind = Obligations; Chart = Visualization.RankedTable; Metrics = []; Width = 4 } ] }

/// Resets a dashboard to the known template, as a new version.
let reset (definition: Definition) = { template definition.Id with Version = definition.Version + 1; Name = definition.Name }

/// The screens a definition is projected to.
type Device =
    | Desktop
    | Tablet
    | Mobile

/// The same blocks, in the same order, with widths for the device's columns:
/// no device stores its own meaning.
let project (device: Device) (definition: Definition) =
    let columns =
        match device with
        | Desktop -> 4
        | Tablet -> 2
        | Mobile -> 1

    definition.Blocks |> List.map (fun b -> b.Id, min b.Width columns, columns)

// ---- Codec (untrusted on read) ---------------------------------------------------------------

let private charts =
    [ Visualization.Bar; Visualization.DotPlot; Visualization.RankedTable; Visualization.Radar; Visualization.Histogram; Visualization.Line
      Visualization.Sparkline; Visualization.MetricCards; Visualization.Bullet ]

let toJson (definition: Definition) =
    Json.objectOf
        [ "version", Json.Number(decimal DefinitionVersion)
          "id", Json.String definition.Id
          "name", Json.String definition.Name
          "revision", Json.Number(decimal definition.Version)
          "blocks",
          Json.Array(
              definition.Blocks
              |> List.map (fun b ->
                  Json.objectOf
                      [ "id", Json.String b.Id
                        "kind", Json.String(kindName b.Kind)
                        "chart", Json.String $"%A{b.Chart}"
                        "metrics", textArray b.Metrics
                        "width", Json.Number(decimal b.Width) ])
          ) ]

let ofJson (value: Json) : Decoded<Definition> =
    closed [ "blocks"; "id"; "name"; "revision"; "version" ] value
    |> Result.bind (fun () ->
        let block (b: Json) =
            closed [ "chart"; "id"; "kind"; "metrics"; "width" ] b
            |> Result.bind (fun () ->
                match text "id" b, text "kind" b, text "chart" b, texts "metrics" b, integer "width" b with
                | Ok id, Ok kind, Ok chart, Ok metrics, Ok width ->
                    match kinds |> List.tryFind (fun k -> kindName k = kind), charts |> List.tryFind (fun c -> $"%A{c}" = chart) with
                    | Some kind, Some chart -> Ok { Id = id; Kind = kind; Chart = chart; Metrics = metrics; Width = width }
                    | _ -> Error $"'{kind}' with '{chart}' is not a block this version reads"
                | Error e, _, _, _, _
                | _, Error e, _, _, _
                | _, _, Error e, _, _
                | _, _, _, Error e, _
                | _, _, _, _, Error e -> Error e)

        match integer "version" value, both (text "id" value) (text "name" value), integer "revision" value, field "blocks" value with
        | Ok v, _, _, _ when v <> DefinitionVersion -> Error $"dashboard version {v} is not {DefinitionVersion}"
        | _, _, _, Ok(Json.Array items) when items.Length > MaxBlocks -> Error $"{items.Length} blocks is over the {MaxBlocks}-block limit"
        | Ok _, Ok(id, name), Ok revision, Ok _ ->
            list "blocks" block value
            |> Result.bind (fun blocks ->
                let definition = { Id = id; Name = name; Version = revision; Blocks = blocks }

                match problems definition with
                | [] -> Ok definition
                | found -> Error $"the dashboard is not valid: %A{found.Head}")
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)
