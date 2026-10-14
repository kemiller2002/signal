/// Scoring authoring in the console (ALG-004, AUT-002, SCS-017 "expose
/// ordinary built-in scorers before custom expression authoring"; WI-0081).
/// The source's primary choices, offered by name, each compiling to the
/// engine's ordinary scorer algebra (`Scoring`, `Registry.Presets`), never
/// to custom code:
///
/// - a section's model: sum, average, weighted sum, weighted average,
///   percentage of the maximum, normalized to 0-100, median, lowest, highest;
///   reverse scoring; mapped values for each answer position; how special
///   answers count and how many answers a score needs;
/// - interpretations: bands (threshold / banding) and pass / fail, on a
///   section or the overall score;
/// - the overall score: a balanced mean of sections, declared section
///   weights, weakest link, or a minimum-domain cap; or none, which leaves
///   sections as a profile.
///
/// Each edit is checked here for shape; the draft is then judged by
/// publication validation, as every other authoring area is.
///
/// Pure.
module Echelon.Signal.Admin.ScoringAuthoring

open System
open System.Globalization
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.ResultModel

type Edit =
    | SetSectionModel of sectionId: string * model: string * weights: string * values: string
    | SetSectionReverse of sectionId: string * on: bool
    | SetSectionSpecials of sectionId: string * substitute: string * minimum: string
    | AddBands of target: string * bands: string
    | AddPassFail of target: string * threshold: string * direction: string
    | RemoveInterpretation of id: string
    | SetOverall of method: string * weights: string * threshold: string * cap: string * direction: string

/// The section models an author picks from: page value, label.
let models =
    [ "sum", "Sum"
      "mean", "Average"
      "weighted-sum", "Weighted sum"
      "weighted-mean", "Weighted average"
      "percent-of-maximum", "Percentage of the maximum"
      "normalized", "Average on 0–100"
      "mapped", "Mapped values, averaged"
      "median", "Median"
      "minimum", "Lowest (weakest item)"
      "maximum", "Highest" ]

/// The overall methods: page value, label.
let overallMethods =
    [ "none", "No overall score (sections are a profile)"
      "balanced", "Average of sections"
      "weighted", "Weighted sections"
      "weakest-link", "Weakest section"
      "minimum-domain", "Average, capped when a section is below a threshold" ]

/// Both results, or the first error.
let private both (f: 'a -> 'b -> 'c) (a: Result<'a, string>) (b: Result<'b, string>) =
    a |> Result.bind (fun x -> b |> Result.map (f x))

let private number (what: string) (text: string) =
    match Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, v when not (Double.IsNaN v || Double.IsInfinity v) -> Ok v
    | _ -> Error $"The {what} must be a number."

let private numbers (what: string) (text: string) =
    text.Split([| ','; ';' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
    |> Array.toList
    |> List.map (number what)
    |> List.fold (fun acc r -> Result.bind (fun xs -> Result.map (fun x -> xs @ [ x ]) r) acc) (Ok [])

/// The section an edit names; none named is the first, as the page shows it selected.
let private section (content: Content) (id: string) =
    match id.Trim(), content.Sections with
    | "", first :: _ -> Ok first
    | "", [] -> Error "Add a section first."
    | id, sections -> sections |> List.tryFind (fun s -> s.Id = id) |> Option.map Ok |> Option.defaultValue (Error $"There is no section '{id}'.")

/// The highest stored value the section's scored questions can take.
let private maximumOf (s: Section) =
    scoredQuestions s
    |> List.choose (fun q ->
        match q.Answer with
        | Primitives.Ordinal points -> Some(float (points - 1))
        | Primitives.Boolean -> Some 1.0
        | _ -> None)
    |> function
        | [] -> 4.0
        | maxima -> List.max maxima

let private withSection (sectionId: string) (change: Section -> Section) (content: Content) =
    { content with Sections = content.Sections |> List.map (fun s -> if s.Id = sectionId then change s else s) }

let private withScorer (defaultScorer: Scoring.Scorer) (change: Scoring.Scorer -> Scoring.Scorer) (s: Section) =
    let scoring = s.Scoring |> Option.defaultValue { Scorer = defaultScorer; Questions = [] }
    { s with Scoring = Some { scoring with Scorer = change scoring.Scorer } }

/// A band list written "Low 0-40; Medium 40-70; High 70-100".
let parseBands (text: string) : Result<Scoring.Band list, string> =
    let pattern = Text.RegularExpressions.Regex(@"^(.+?)\s+(-?[0-9.]+)\s*[-–]\s*(-?[0-9.]+)$")

    text.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
    |> Array.toList
    |> List.map (fun part ->
        let m = pattern.Match part

        if not m.Success then
            Error $"'{part}' is not a band: write a label, then its range, as 'High 70-100'."
        else
            both
                (fun lo hi -> ({ Label = m.Groups[1].Value.Trim(); From = lo; To = hi }: Scoring.Band))
                (number "band start" m.Groups[2].Value)
                (number "band end" m.Groups[3].Value))
    |> List.fold (fun acc r -> Result.bind (fun xs -> Result.map (fun x -> xs @ [ x ]) r) acc) (Ok [])
    |> Result.bind (fun bands -> if bands.IsEmpty then Error "Give at least one band." else Ok bands)

let private targetOf (content: Content) (target: string) =
    match target.Trim() with
    | ""
    | "overall" -> Ok OverallResult
    | id when content.Sections |> List.exists (fun s -> s.Id = id) -> Ok(SectionResultOf id)
    | id -> Error $"There is no section '{id}'; name a section, or 'overall'."

let private directionOf =
    function
    | "lower" -> HigherIsWorse
    | _ -> HigherIsBetter

let private nextId (prefix: string) (taken: string list) =
    Seq.initInfinite (fun i -> $"{prefix}{i + 1}") |> Seq.find (fun id -> not (List.contains id taken))

let private withInterpretation (kind: InterpretationKind) (target: ResultTarget) (content: Content) =
    let prefix = match kind with Bands _ -> "bands-" | PassFail _ -> "pass-" | Stages _ -> "stages-"
    let id = nextId prefix (content.Results.Interpretations |> List.map _.Id)
    { content with Results = { content.Results with Interpretations = content.Results.Interpretations @ [ { Id = id; Target = target; Kind = kind } ] } }

let apply (defaultScorer: Scoring.Scorer) (edit: Edit) (content: Content) : Result<Content, string> =
    match edit with
    | SetSectionModel(sectionId, model, weightsText, valuesText) ->
        section content sectionId
        |> Result.bind (fun s ->
            let count = (scoredQuestions s).Length
            let maximum = maximumOf s

            let weights () =
                numbers "weight" weightsText
                |> Result.bind (fun ws ->
                    if ws.Length <> count then Error $"Give one weight for each of the section's {count} scored question(s), in order."
                    elif ws |> List.exists (fun w -> w < 0.0) then Error "Weights cannot be negative."
                    else Ok ws)

            let change: Result<Scoring.Scorer -> Scoring.Scorer, string> =
                match model with
                | "sum" -> Ok(fun sc -> { sc with Aggregate = Scoring.RawSum; Transforms = [] })
                | "mean" -> Ok(fun sc -> { sc with Aggregate = Scoring.Mean; Transforms = [] })
                | "median" -> Ok(fun sc -> { sc with Aggregate = Scoring.Median; Transforms = [] })
                | "minimum" -> Ok(fun sc -> { sc with Aggregate = Scoring.Minimum; Transforms = [] })
                | "maximum" -> Ok(fun sc -> { sc with Aggregate = Scoring.Maximum; Transforms = [] })
                | "weighted-sum" -> weights () |> Result.map (fun ws -> fun sc -> { sc with Aggregate = Scoring.WeightedSum ws; Transforms = [] })
                | "weighted-mean" -> weights () |> Result.map (fun ws -> fun sc -> { sc with Aggregate = Scoring.WeightedMean ws; Transforms = [] })
                | "percent-of-maximum" -> Ok(fun sc -> { sc with Aggregate = Scoring.PercentageOfMaximum maximum; Transforms = []; Decimals = 1 })
                | "normalized" ->
                    Ok(fun sc -> { sc with Aggregate = Scoring.Mean; Transforms = [ Scoring.LinearNormalize(0.0, maximum, 0.0, 100.0) ]; Decimals = 1 })
                | "mapped" ->
                    numbers "value" valuesText
                    |> Result.bind (fun vs ->
                        if vs.Length <> int maximum + 1 then
                            Error $"Give one value for each answer position, from the first ({int maximum + 1} in all)."
                        else
                            Ok(fun sc -> { sc with Scale = Scoring.Mapped(vs |> List.mapi (fun i v -> float i, v) |> Map.ofList); Aggregate = Scoring.Mean; Transforms = [] }))
                | other -> Error $"'{other}' is not a scoring model the console offers."

            change |> Result.map (fun f -> content |> withSection s.Id (withScorer defaultScorer f)))
    | SetSectionReverse(sectionId, on) ->
        section content sectionId
        |> Result.map (fun s ->
            let scale = if on then Scoring.Reverse(0.0, maximumOf s) else Scoring.Direct
            content |> withSection s.Id (withScorer defaultScorer (fun sc -> { sc with Scale = scale })))
    | SetSectionSpecials(sectionId, substitute, minimum) ->
        section content sectionId
        |> Result.bind (fun s ->
            let policy =
                if String.IsNullOrWhiteSpace substitute then Ok Scoring.Exclude
                else number "substitute" substitute |> Result.map Scoring.Substitute

            let least =
                match Int32.TryParse(minimum.Trim()) with
                | true, n when n >= 1 -> Ok n
                | _ when String.IsNullOrWhiteSpace minimum -> Ok 1
                | _ -> Error "The minimum number of answers must be a whole number of at least 1."

            both
                (fun p m -> content |> withSection s.Id (withScorer defaultScorer (fun sc -> { sc with Missing = { MinimumObservations = m; Special = p } })))
                policy
                least)
    | AddBands(target, text) ->
        both (fun t bands -> content |> withInterpretation (Bands bands) t) (targetOf content target) (parseBands text)
    | AddPassFail(target, threshold, direction) ->
        both (fun t v -> content |> withInterpretation (PassFail(v, directionOf direction)) t) (targetOf content target) (number "threshold" threshold)
    | RemoveInterpretation id when content.Results.Interpretations |> List.exists (fun i -> i.Id = id) ->
        Ok { content with Results = { content.Results with Interpretations = content.Results.Interpretations |> List.filter (fun i -> i.Id <> id) } }
    | RemoveInterpretation id -> Error $"There is no interpretation '{id}'."
    | SetOverall(method, weightsText, threshold, cap, direction) ->
        let spec m =
            Ok(Some(Composite { Method = m; Sections = []; MinimumScoredSections = 1; Direction = directionOf direction; Decimals = 1 }))

        let overall =
            match method with
            | "none" -> Ok None
            | "balanced" -> spec BalancedMean
            | "weakest-link" -> spec WeakestLink
            | "weighted" ->
                weightsText.Split([| ','; ';' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                |> Array.toList
                |> List.map (fun pair ->
                    match pair.Split('=', StringSplitOptions.TrimEntries) with
                    | [| id; w |] when content.Sections |> List.exists (fun s -> s.Id = id) -> number "section weight" w |> Result.map (fun v -> id, v)
                    | _ -> Error $"'{pair}' is not a section weight: write it as 'S1=2'.")
                |> List.fold (fun acc r -> Result.bind (fun xs -> Result.map (fun x -> xs @ [ x ]) r) acc) (Ok [])
                |> Result.bind (fun ws -> if ws.IsEmpty then Error "Give a weight for at least one section, as 'S1=2'." else spec (WeightedMean ws))
            | "minimum-domain" -> both (fun t c -> t, c) (number "threshold" threshold) (number "cap" cap) |> Result.bind (MinimumDomain >> spec)
            | other -> Error $"'{other}' is not an overall method the console offers."

        overall |> Result.map (fun o -> { content with Results = { content.Results with Overall = o } })

/// How an interpretation reads in the console.
let describeInterpretation (i: Interpretation) =
    let target = match i.Target with OverallResult -> "overall score" | SectionResultOf id -> $"section {id}"
    let n (v: float) = v.ToString("0.##", CultureInfo.InvariantCulture)

    match i.Kind with
    | Bands bands -> $"""{i.Id}: {target} in bands {bands |> List.map (fun b -> $"{b.Label} {n b.From}–{n b.To}") |> String.concat ", "}"""
    | PassFail(t, HigherIsWorse) -> $"{i.Id}: {target} passes at or below {n t}"
    | PassFail(t, _) -> $"{i.Id}: {target} passes at or above {n t}"
    | Stages(baseline, stages) -> $"""{i.Id}: {target} stages {baseline :: (stages |> List.map _.Label) |> String.concat " → "}"""

/// What the overall score is now, in words.
let describeOverall (content: Content) =
    match content.Results.Overall with
    | None -> "No overall score: sections are reported as a profile."
    | Some(Custom _) -> "A custom expression (imported)."
    | Some(Composite c) ->
        match c.Method with
        | BalancedMean -> "The average of the scored sections."
        | WeightedMean ws -> $"""Weighted sections: {ws |> List.map (fun (s, w) -> $"{s}={w}") |> String.concat ", "}."""
        | WeakestLink -> "The weakest scored section."
        | MinimumDomain(t, cap) -> $"The average of sections, capped at {cap} when any section is below {t}."
        | other -> $"%A{other}"

// ---- The form -------------------------------------------------------------------------------------

[<NoComparison>]
type Form =
    { /// The section the section edits apply to, and the model chosen for it.
      Section: string
      Model: string
      Reverse: bool
      Weights: string
      Values: string
      Substitute: string
      Minimum: string
      BandTarget: string
      Bands: string
      PassTarget: string
      PassThreshold: string
      PassDirection: string
      OverallMethod: string
      OverallWeights: string
      OverallThreshold: string
      OverallCap: string
      OverallDirection: string }

let emptyForm =
    { Section = ""
      Model = "mean"
      Reverse = false
      Weights = ""
      Values = ""
      Substitute = ""
      Minimum = ""
      BandTarget = "overall"
      Bands = ""
      PassTarget = "overall"
      PassThreshold = ""
      PassDirection = "higher"
      OverallMethod = "balanced"
      OverallWeights = ""
      OverallThreshold = ""
      OverallCap = ""
      OverallDirection = "higher" }

let events =
    set
        [ "scoringSection"; "scoringModel"; "scoringWeights"; "scoringValues"; "applySectionModel"; "scoringReverse"; "applySectionReverse"
          "scoringSubstitute"; "scoringMinimum"; "applySectionSpecials"
          "bandTarget"; "bandText"; "addBands"; "passTarget"; "passThreshold"; "passDirection"; "addPassFail"; "removeInterpretation"
          "overallMethod"; "overallWeights"; "overallThreshold"; "overallCap"; "overallDirection"; "setOverall" ]

let private checkedValue (value: string) = value <> "" && value <> "false"

/// How a section is scored now, in words.
let describeSection (s: Section) =
    match s.Scoring with
    | None -> $"{s.Title} ({s.Id}): not scored"
    | Some sc ->
        let sc' = sc.Scorer
        let n (v: float) = v.ToString("0.##", CultureInfo.InvariantCulture)

        let aggregate =
            match sc'.Aggregate, sc'.Transforms with
            | Scoring.Mean, [ Scoring.LinearNormalize(_, _, 0.0, 100.0) ] -> "average on 0–100"
            | Scoring.Mean, _ -> "average"
            | Scoring.RawSum, _ -> "sum"
            | Scoring.Median, _ -> "median"
            | Scoring.Minimum, _ -> "lowest"
            | Scoring.Maximum, _ -> "highest"
            | Scoring.WeightedSum ws, _ -> $"""weighted sum ({ws |> List.map n |> String.concat ", "})"""
            | Scoring.WeightedMean ws, _ -> $"""weighted average ({ws |> List.map n |> String.concat ", "})"""
            | Scoring.PercentageOfMaximum m, _ -> $"percentage of the maximum ({n m} each)"
            | other, _ -> $"%A{other}"

        let scale =
            match sc'.Scale with
            | Scoring.Direct -> ""
            | Scoring.Reverse _ -> ", reversed"
            | Scoring.Mapped m -> $""", mapped ({m |> Map.toList |> List.map (snd >> n) |> String.concat ", "})"""

        let specials =
            match sc'.Missing.Special with
            | Scoring.Exclude -> "special answers left out"
            | Scoring.Substitute v -> $"special answers count as {n v}"

        $"{s.Title} ({s.Id}): {aggregate}{scale}; {specials}; at least {sc'.Missing.MinimumObservations} answer(s); {(scoredQuestions s).Length} scored question(s)"

let update (name: string) (key: string option) (value: string) (form: Form) : Form * Edit option =
    match name, key with
    | "scoringSection", _ -> { form with Section = value }, None
    | "scoringModel", _ -> { form with Model = value }, None
    | "scoringWeights", _ -> { form with Weights = value }, None
    | "scoringValues", _ -> { form with Values = value }, None
    | "applySectionModel", _ -> form, Some(SetSectionModel(form.Section, form.Model, form.Weights, form.Values))
    | "scoringReverse", _ -> { form with Reverse = checkedValue value }, None
    | "applySectionReverse", _ -> form, Some(SetSectionReverse(form.Section, form.Reverse))
    | "scoringSubstitute", _ -> { form with Substitute = value }, None
    | "scoringMinimum", _ -> { form with Minimum = value }, None
    | "applySectionSpecials", _ -> form, Some(SetSectionSpecials(form.Section, form.Substitute, form.Minimum))
    | "bandTarget", _ -> { form with BandTarget = value }, None
    | "bandText", _ -> { form with Bands = value }, None
    | "addBands", _ -> form, Some(AddBands(form.BandTarget, form.Bands))
    | "passTarget", _ -> { form with PassTarget = value }, None
    | "passThreshold", _ -> { form with PassThreshold = value }, None
    | "passDirection", _ -> { form with PassDirection = value }, None
    | "addPassFail", _ -> form, Some(AddPassFail(form.PassTarget, form.PassThreshold, form.PassDirection))
    | "removeInterpretation", Some id -> form, Some(RemoveInterpretation id)
    | "overallMethod", _ -> { form with OverallMethod = value }, None
    | "overallWeights", _ -> { form with OverallWeights = value }, None
    | "overallThreshold", _ -> { form with OverallThreshold = value }, None
    | "overallCap", _ -> { form with OverallCap = value }, None
    | "overallDirection", _ -> { form with OverallDirection = value }, None
    | "setOverall", _ -> form, Some(SetOverall(form.OverallMethod, form.OverallWeights, form.OverallThreshold, form.OverallCap, form.OverallDirection))
    | _ -> form, None

/// The form after an edit was applied: what it used is cleared.
let cleared (edit: Edit) (form: Form) =
    match edit with
    | AddBands _ -> { form with Bands = "" }
    | AddPassFail _ -> { form with PassThreshold = "" }
    | _ -> form
