/// A group's report as the page renders it through Folio (WI-0062,
/// RPT-001..006, SRPP): ReportData built by `Report.build` from the group
/// result, never recomputed here; the definition's blocks in reading order
/// with a table of contents and a title page; every number, date and label
/// localized by `Locale` and `ReportLabels`, with the report's own direction.
///
/// The family and locale are the route's (`#/groups/{g}/report?family=…&locale=…`),
/// so a report link opens the same report. A family whose privacy conditions
/// fail for the group is withheld, with its reasons.
///
/// Pure.
module Echelon.Signal.Admin.AdminReportView

open System
open Echelon.Signal.Engine
open Echelon.Signal.Engine.View
open Echelon.Signal.Engine.ReportModel
open Echelon.Signal.Admin.Routes
open Echelon.Signal.Admin.AdminApp

let private text (value: string) = Value(Text value)
let private flag (value: bool) = Value(Flag value)

let private familyLabels =
    [ "administrator-group", "Administrator group report"
      "executive-summary", "Executive summary"
      "detailed-section", "Detailed sections"
      "anonymous-aggregate", "Anonymous aggregate"
      "identified-detail", "Identified detail"
      "audit", "Audit" ]
    |> Map.ofList

let private localeLabels = [ "en-US", "English"; "de-DE", "Deutsch"; "fr-FR", "Français"; "ar-EG", "العربية" ] |> Map.ofList

let private blockKey (block: Block) = sprintf "%A" block

let private problemText =
    function
    | Report.BlockNotAllowed(block, _) -> $"{blockKey block} is shown only for identified groups."
    | Report.AuditBlockOutsideAuditDetail -> "Audit metadata needs the audit detail level."
    | Report.NoBlocks -> "The report has no blocks."
    | Report.DuplicateBlock block -> $"{blockKey block} appears twice."
    | Report.MinimumBelowGroupPolicy(report, group) -> $"Its minimum group size ({report}) is below this group's ({group})."
    | Report.InvalidDecimals -> "Its rounding is invalid."

/// A report family, or a saved definition's latest version (WI-0075).
let definitionOf (model: Model) (family: string) =
    ReportExport.families
    |> List.tryFind (fun d -> d.Id = family)
    |> Option.orElse (ReportLibrary.latest model.Library family |> Option.map _.Definition)

/// A definition's report for a group, or why it is withheld.
let buildWith (model: Model) (group: GroupSummary) (definition: Definition) : Result<ReportData, string list> =
    let subject: Report.Subject =
        { SurveyId = group.SurveyIdentifier
          TemplateVersion = group.TemplateVersion
          Content = Pilot.contentOf group.Template }

    // The time the data was last verified, not a clock read: the same data renders the same report.
    let at = model.LastVerified |> Option.defaultValue DateTimeOffset.UnixEpoch

    Report.build definition subject group.MinimumReportable group.Report [] at
    |> Result.mapError (List.map problemText)

/// The report the route names for a group, or why it is withheld.
let build (model: Model) (group: GroupSummary) (family: string) : Result<ReportData, string list> =
    match definitionOf model family with
    | None -> Error [ $"There is no report family or saved definition {family}." ]
    | Some definition -> buildWith model group definition

/// A reported value as text in a locale; never zero by implication.
let valueText (tag: string) (decimals: int) (value: Value) =
    let l = ReportLabels.label tag
    let locale = Locale.find tag

    match value with
    | Shown v -> Locale.number locale decimals v
    | NotAvailable -> l "notAvailable"
    | NotApplicableValue -> l "notApplicable"
    | InsufficientResponses(have, need) -> $"""{l "insufficientResponses"} ({have}/{need})"""
    | NotComparable reason -> $"""{l "notComparable"}: {reason}"""
    | SuppressedValue _ -> l "suppressed"

let private range (tag: string) (decimals: int) (row: SectionRow) =
    match row.Minimum, row.Maximum with
    | Shown a, Shown b -> $"{valueText tag decimals (Shown a)} – {valueText tag decimals (Shown b)}"
    | other, _ -> valueText tag decimals other

/// The report part of the page's view.
let project (model: Model) (group: GroupSummary option) : View =
    let family, tag =
        match model.Place.View with
        | Ok(Report(_, f, t)) -> f, t
        | _ -> defaultFamily, defaultLocale

    let key = group |> Option.map _.Key |> Option.defaultValue ""
    let l = ReportLabels.label tag
    let locale = Locale.find tag
    let report = group |> Option.map (fun g -> build model g family)
    let data = report |> Option.bind Result.toOption
    let decimals = 1
    let n (count: int) = Locale.number locale 0 (float count)
    let has block = data |> Option.exists (fun r -> List.contains block r.Blocks)
    let available = group |> Option.map (fun g -> ReportExport.availableFamilies g.Mode g.MinimumReportable |> List.map _.Id) |> Option.defaultValue []

    let status =
        match data |> Option.map _.Status with
        | Some Complete -> l "complete"
        | Some(Partial(accepted, expected)) -> $"""{l "partial"} ({n accepted}/{n expected})"""
        | Some InsufficientData -> l "insufficient"
        | None -> ""

    let items (rows: (string * Scalar) list list) = Items rows

    [ "reportFamilies",
      items (
          (familyValues @ (model.Library |> Map.keys |> List.ofSeq))
          |> List.map (fun f ->
              [ "id", Text f
                "label", Text(familyLabels.TryFind f |> Option.defaultValue $"Saved: {f}")
                "href", Text(href (Report(key, f, tag)))
                "current", Text(if f = family then "page" else "false")
                "available", Flag(List.contains f available) ])
      )
      "reportLocales",
      items (localeValues |> List.map (fun t -> [ "id", Text t; "label", Text(localeLabels.TryFind t |> Option.defaultValue t); "href", Text(href (Report(key, family, t))); "current", Text(if t = tag then "page" else "false") ]))
      "hasReport", flag data.IsSome
      // Formal snapshots of this group (WI-0075, ADM-022, ADM-023).
      "canTakeSnapshot", flag (data.IsSome && (capabilities model).Contains AdminState.CanCreateSnapshot)
      "reportSnapshots",
      Items(
          model.Snapshots
          |> List.filter (fun s -> s.GroupKey = key)
          |> List.map (fun s ->
              [ "key", Text s.SnapshotId
                "label", Text $"{s.SnapshotId} · {s.DefinitionId} v{s.DefinitionVersion} · {s.Accepted} responses · {s.Locale} · {s.TakenAt}"
                "intact", Text(if s.Intact then "Verified" else "Altered: not exportable")
                "canExport", Flag(s.Intact && (capabilities model).Contains AdminState.CanExport) ])
      )
      "reportWithheld", flag (match report with Some(Error _) -> true | _ -> false)
      "reportWithheldReason", text (match report with Some(Error reasons) -> String.concat " " reasons | _ -> "")
      "reportLang", text tag
      "reportDir", text (Locale.dir locale)
      "reportTitle", text (data |> Option.map _.Title |> Option.defaultValue "")
      "reportFamily", text (familyLabels.TryFind family |> Option.defaultValue family)
      "reportGroupLabel", text (l "group")
      "reportGroup", text key
      "reportGeneratedLabel", text (l "generated")
      "reportGenerated", text (data |> Option.map (fun r -> Locale.date locale (DateOnly.FromDateTime r.GeneratedAt.UtcDateTime)) |> Option.defaultValue "")
      "reportStatus", text status
      "reportContentsLabel", text (l "contents")
      // Reading order is the definition's; the contents list it, without in-page anchors (the fragment is the route).
      "reportContents",
      items (data |> Option.map (fun r -> r.Blocks |> List.filter ((<>) Header) |> List.mapi (fun i b -> [ "key", Text(blockKey b); "number", Text(n (i + 1)); "title", Text(l (blockKey b)) ])) |> Option.defaultValue [])
      for block in [ Summary; ResponseCounts; OverallResult; SectionResults; GroupDistributions; Strengths; Weaknesses; Recommendations; CoverageAndConfidence; Comparisons; RespondentDetail; RoleBreakdown; Methodology; AuditMetadata ] do
          $"reportHas{blockKey block}", flag (has block)
          $"reportTitle{blockKey block}", text (l (blockKey block))
      "reportSummary",
      text (
          data
          |> Option.map (fun r -> $"""{r.Title}: {status}. {l "accepted"} {n r.Counts.Accepted} / {l "expected"} {n r.Counts.Expected}.""")
          |> Option.defaultValue ""
      )
      "reportCounts",
      items (
          data
          |> Option.map (fun r ->
              [ "expected", n r.Counts.Expected
                "accepted", n r.Counts.Accepted
                "missing", n r.Counts.Missing
                "completion", Locale.percent locale 0 (r.Counts.CompletionPercent / 100.0) ]
              |> List.map (fun (k, v) -> [ "key", Text k; "label", Text(l k); "value", Text v ]))
          |> Option.defaultValue []
      )
      "reportOverall", text (data |> Option.map (fun r -> valueText tag decimals r.Overall) |> Option.defaultValue "")
      "reportColumns",
      items ([ "section"; "mean"; "median"; "range"; "scored"; "unscored" ] |> List.map (fun k -> [ "key", Text k; "label", Text(l k) ]))
      "reportSections",
      items (
          data
          |> Option.map (fun r ->
              r.Sections
              |> List.map (fun row ->
                  [ "key", Text row.Section
                    "section", Text $"{row.Section} {row.Title}"
                    "mean", Text(valueText tag decimals row.Mean)
                    "median", Text(valueText tag decimals row.Median)
                    "range", Text(range tag decimals row)
                    "scored", Text(n row.Scored)
                    "unscored", Text(n row.Unscored) ]))
          |> Option.defaultValue []
      )
      "reportStrengths", items (data |> Option.map (fun r -> r.Strengths |> List.map (fun s -> [ "key", Text s; "text", Text s ])) |> Option.defaultValue [])
      "reportWeaknesses", items (data |> Option.map (fun r -> r.Weaknesses |> List.map (fun s -> [ "key", Text s; "text", Text s ])) |> Option.defaultValue [])
      "reportRecommendations",
      items (data |> Option.map (fun r -> r.Recommendations |> List.map (fun x -> [ "key", Text x.Id; "title", Text x.Title; "priority", Text x.Priority; "frequency", Text(n x.Frequency) ])) |> Option.defaultValue [])
      "reportCoverage",
      items (
          data
          |> Option.map (fun r -> [ "answered", r.Coverage.Answered; "special", r.Coverage.Special; "unanswered", r.Coverage.Unanswered ] |> List.map (fun (k, v) -> [ "key", Text k; "label", Text(l k); "value", Text(n v) ]))
          |> Option.defaultValue []
      )
      "reportWarnings", items (data |> Option.map (fun r -> r.Warnings |> List.map (fun w -> [ "code", Text(warningCode w); "text", Text(l (warningCode w)) ])) |> Option.defaultValue [])
      "reportComparisons",
      items (data |> Option.map (fun r -> r.Comparisons |> List.map (fun c -> [ "key", Text $"{c.Input.Label}/{c.Input.Metric}"; "label", Text c.Input.Label; "baseline", Text c.Input.Baseline; "delta", Text(valueText tag decimals c.Delta) ])) |> Option.defaultValue [])
      "reportRoles", items (data |> Option.map (fun r -> r.Roles |> List.map (fun (role, count) -> [ "key", Text(sprintf "%A" role); "role", Text(sprintf "%A" role); "count", Text(n count) ])) |> Option.defaultValue [])
      "reportMethodology", items (data |> Option.map (fun r -> r.Methodology |> List.mapi (fun i m -> [ "key", Text(string i); "text", Text m ])) |> Option.defaultValue [])
      "reportAudit",
      items (
          data
          |> Option.bind _.Audit
          |> Option.map (fun a ->
              [ "Survey", $"{a.SurveyId} {a.TemplateVersion}"; "Template", a.TemplateHash; "Group result", $"{a.GroupResultHash} (v{a.GroupResultVersion})"; "Definition", $"{a.DefinitionId} v{a.DefinitionVersion}" ]
              |> List.map (fun (k, v) -> [ "key", Text k; "label", Text k; "value", Text v ]))
          |> Option.defaultValue []
      ) ]
