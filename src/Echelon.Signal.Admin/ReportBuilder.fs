/// The report builder (WI-0075, ADM-021, ADM-063): a saved report
/// definition is started from a report family or opened from the library,
/// edited through a closed set of choices (audience, blocks and their order,
/// detail, minimum group size, rounding, highlights, template pin), checked
/// against a group, previewed with that group's current state, and saved
/// as the next version when the one in use was used for a snapshot.
///
/// The builder edits presentation only: a definition has no field that
/// could change scoring, applicability, duplicates, completion or
/// aggregation (ADM-021).
///
/// Pure.
module Echelon.Signal.Admin.ReportBuilder

open Echelon.Signal.Engine
open Echelon.Signal.Engine.ReportModel
open Echelon.Signal.Admin.ReportLibrary

/// Every block, in the order the builder offers them.
let blocks =
    [ Header; Summary; ResponseCounts; OverallResult; SectionResults; GroupDistributions; Strengths; Weaknesses
      Recommendations; CoverageAndConfidence; Comparisons; RespondentDetail; RoleBreakdown; Methodology; AuditMetadata ]

let blockName (block: Block) = $"%A{block}"

let audiences =
    [ AdministratorAudience; ExecutiveAudience; RespondentAudience; FacilitatorAudience; GroupReviewerAudience; TechnicalAudience ]

let audienceName (audience: Audience) = $"%A{audience}"

let details = [ SummaryDetail; StandardDetail; DetailedDetail; AuditDetail ]
let detailName (detail: DetailLevel) = $"%A{detail}"

/// The definition being built and its template pin.
type Builder =
    { Definition: Definition
      Pin: TemplatePin
      Unsaved: bool }

type Screen =
    { Builder: Builder option
      NewId: string
      /// The group whose current state checks and previews the definition.
      PreviewGroup: string option
      Problem: string option }

let emptyScreen =
    { Builder = None
      NewId = ""
      PreviewGroup = None
      Problem = None }

type Command = SaveDefinition of Definition * Pins

/// The page events the builder owns.
let events =
    set
        [ "newReportId"; "newReportDefinition"; "editReportDefinition"; "reportAudience"; "reportBlock"; "reportBlockUp"; "reportDetail"
          "reportMinimum"; "reportDecimals"; "reportHighlights"; "reportPin"; "reportPreviewGroup"; "saveReportDefinition" ]

let private named (names: ('a * string) list) (value: string) = names |> List.tryFind (snd >> (=) value) |> Option.map fst

let private number (low: int) (high: int) (value: string) =
    match System.Int32.TryParse value with
    | true, n when n >= low && n <= high -> Some n
    | _ -> None

/// Moves a block one place earlier.
let private earlier (block: Block) (list: Block list) =
    match List.tryFindIndex ((=) block) list with
    | Some i when i > 0 -> list |> List.mapi (fun j b -> if j = i - 1 then list[i] elif j = i then list[i - 1] else b)
    | _ -> list

/// The template a pin names, as the builder offers it: "latest:SURVEY" or an exact hash.
let pinValue (pin: TemplatePin) =
    match pin with
    | LatestTemplate survey -> "latest:" + survey
    | ExactTemplate t -> t.Hash

/// One builder event. `surveys` are the surveys the console's catalog
/// offers; a definition pins the latest published version of one of them
/// while it is built, resolved exactly when a snapshot is taken (ADM-063).
let update (library: Library) (surveys: string list) (name: string) (key: string option) (value: string) (screen: Screen) : Screen * Command list =
    let fail problem = { screen with Problem = Some problem }, []

    let edit (change: Builder -> Result<Builder, string>) =
        match screen.Builder with
        | None -> fail "Start or open a report definition first."
        | Some b ->
            match change b with
            | Ok next -> { screen with Builder = Some { next with Unsaved = true }; Problem = None }, []
            | Error problem -> fail problem

    let definition (f: Definition -> Definition) = edit (fun b -> Ok { b with Definition = f b.Definition })

    match name, key with
    | "newReportId", _ -> { screen with NewId = value.Trim().ToLowerInvariant() }, []
    | "newReportDefinition", _ when (versions library screen.NewId).Length > 0 -> fail $"'{screen.NewId}' already exists: open it to edit."
    | "newReportDefinition", _ ->
        match surveys with
        | [] -> fail "There is no survey in the catalog to report on."
        | first :: _ ->
            { screen with
                Builder = Some { Definition = { ReportExport.administratorGroup with Id = screen.NewId; Version = 1 }; Pin = LatestTemplate first; Unsaved = true }
                NewId = ""
                Problem = None },
            []
    | "editReportDefinition", Some id ->
        match latest library id with
        | Some entry -> { screen with Builder = Some { Definition = entry.Definition; Pin = entry.Pins.Template; Unsaved = false }; Problem = None }, []
        | None -> fail $"There is no report definition '{id}'."
    | "reportAudience", _ ->
        match named (audiences |> List.map (fun a -> a, audienceName a)) value with
        | Some a -> definition (fun d -> { d with Audience = a })
        | None -> fail "Choose an audience from the list."
    | "reportBlock", Some b ->
        match named (blocks |> List.map (fun x -> x, blockName x)) b with
        | Some block ->
            definition (fun d -> { d with Blocks = (if value = "" then d.Blocks |> List.filter ((<>) block) elif List.contains block d.Blocks then d.Blocks else d.Blocks @ [ block ]) })
        | None -> fail "That block is not offered."
    | "reportBlockUp", Some b ->
        match named (blocks |> List.map (fun x -> x, blockName x)) b with
        | Some block -> definition (fun d -> { d with Blocks = earlier block d.Blocks })
        | None -> fail "That block is not offered."
    | "reportDetail", _ ->
        match named (details |> List.map (fun x -> x, detailName x)) value with
        | Some detail -> definition (fun d -> { d with Detail = detail })
        | None -> fail "Choose a detail level from the list."
    | "reportMinimum", _ ->
        match number 1 1000 value with
        | Some n -> definition (fun d -> { d with MinimumGroupSize = n })
        | None -> fail "The minimum group size is a whole number from 1 to 1000."
    | "reportDecimals", _ ->
        match number 0 6 value with
        | Some n -> definition (fun d -> { d with Decimals = n })
        | None -> fail "Rounding is 0 to 6 decimal places."
    | "reportHighlights", _ ->
        match number 0 20 value with
        | Some n -> definition (fun d -> { d with HighlightCount = n })
        | None -> fail "Highlights are 0 to 20."
    | "reportPin", _ when value.StartsWith "latest:" && List.contains (value.Substring 7) surveys -> edit (fun b -> Ok { b with Pin = LatestTemplate(value.Substring 7) })
    | "reportPin", _ -> fail "Choose a survey from the list."
    | "reportPreviewGroup", _ -> { screen with PreviewGroup = (if value = "" then None else Some value) }, []
    | "saveReportDefinition", _ ->
        match screen.Builder with
        | Some b -> screen, [ SaveDefinition(b.Definition, currentPins b.Pin) ]
        | None -> fail "Start or open a report definition first."
    | _ -> screen, []

/// After a save: what is stored is what the builder holds, at its version.
let saved (entry: Entry) (screen: Screen) =
    match screen.Builder with
    | Some b when b.Definition.Id = entry.Definition.Id -> { screen with Builder = Some { b with Definition = entry.Definition; Unsaved = false } }
    | _ -> screen
