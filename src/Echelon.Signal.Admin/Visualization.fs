/// Signal's visualization grammar (ADM-015, ADM-016, ADM-017): a typed,
/// declarative specification that F# validates and compiles into what the
/// page draws. The browser only draws (HTML meters and tables); it never
/// computes a value or decides a chart's meaning (ADM-035).
///
/// - Every chart type declares its legal shape (the kind of dimension and
///   how many measures); an invalid combination fails validation rather than
///   rendering something misleading.
/// - Suitability rules: a line needs an ordered dimension, a histogram a
///   numeric distribution, a 100% stack a declared denominator and missing
///   policy; too many categories suggest a table; a truncated axis on a bar
///   chart is explicit and warned; percentages say 0-1 or 0-100; sorting is
///   deterministic; a missing value is never drawn as zero; totals are not
///   shown beside suppressed cells (so they cannot be inferred); pies only
///   for a few categories; no 3D; a dual axis needs a stated justification.
/// - Accessibility: a title and a generated description, a table with the
///   same data, a pattern and symbol beside every colour (colour is never the
///   only carrier), a palette safe for common colour-vision deficiencies with
///   its contrast checked, a minimum text size, no hover-only interaction,
///   no animation.
///
/// Pure.
module Echelon.Signal.Admin.Visualization

open System
open Echelon.Signal.Admin.Analysis

[<Literal>]
let SpecVersion = 1

type ChartType =
    | Bar
    | GroupedBar
    | StackedBar
    | Stacked100
    | Line
    | Area
    | DotPlot
    | Histogram
    | BoxPlot
    | Heatmap
    | Radar
    | Bullet
    | SmallMultiples
    | RankedTable
    | MetricCards
    | Matrix
    | Sparkline
    | Pie
    | DualAxis

/// What a dimension is.
type DimensionKind =
    | Nominal
    | Ordinal
    | Temporal
    /// Numeric bins of a distribution.
    | Binned

/// The unit of the measure axis.
type Unit =
    | Score0To100
    | Percent0To1
    | Percent0To100
    | Count

/// One row of data: a category, its value (or why it has none), its order.
type Datum =
    { Category: string
      Value: Metric
      /// The category's position in an ordered dimension.
      Order: int }

type Sort =
    | ByCategory
    | ByValueDescending
    /// The dimension's own order (ordinal or temporal).
    | ByOrder

/// The axis scale. A truncated axis does not start at the unit's zero.
type Scale = { Minimum: float; Maximum: float }

type Interaction =
    | NoInteraction
    /// Marks can be selected by keyboard and pointer, never hover-only.
    | SelectMark

/// A visualization specification (ADM-015).
type Spec =
    { Version: int
      Chart: ChartType
      Title: string
      Dimension: DimensionKind
      Unit: Unit
      Scale: Scale
      Sort: Sort
      Interaction: Interaction
      /// Required for a 100% stack: the denominator and the missing policy.
      Denominator: string option
      /// Required for a dual axis.
      Justification: string option
      /// Show a total beside the bars.
      ShowTotal: bool
      /// Text size in CSS pixels.
      TextSize: int }

let defaultSpec chart title =
    { Version = SpecVersion
      Chart = chart
      Title = title
      Dimension = Nominal
      Unit = Score0To100
      Scale = { Minimum = 0.0; Maximum = 100.0 }
      Sort = ByCategory
      Interaction = SelectMark
      Denominator = None
      Justification = None
      ShowTotal = false
      TextSize = 14 }

/// A problem that stops a specification from being drawn.
type Refusal =
    | IllegalShape of chart: string * reason: string
    | MissingTitle
    | TextTooSmall of size: int
    | MissingDenominator
    | MissingJustification
    | InvalidScale

/// Something worth saying that does not stop drawing.
type Warning =
    | TruncatedAxis
    | TooManyCategories of count: int
    | MissingValues of count: int
    | TotalWithheldForSuppression

/// A palette after Okabe-Ito, safe for common colour-vision deficiencies and
/// darkened where needed so every colour has at least 3:1 contrast on white
/// (WCAG non-text contrast), each with a pattern and a symbol so colour is
/// never the only carrier.
let palette =
    [ "#0072B2", "solid", "circle"
      "#B34700", "diagonal", "square"
      "#00795A", "dots", "triangle"
      "#A3487F", "cross", "diamond"
      "#8A6100", "horizontal", "star"
      "#7B5EA7", "vertical", "plus"
      "#3F3F3F", "grid", "ring" ]

/// WCAG relative luminance contrast between two `#rrggbb` colours.
let contrast (a: string) (b: string) =
    let luminance (hex: string) =
        let channel (i: int) =
            let c = float (Convert.ToInt32(hex.Substring(1 + 2 * i, 2), 16)) / 255.0
            if c <= 0.03928 then c / 12.92 else Math.Pow((c + 0.055) / 1.055, 2.4)

        0.2126 * channel 0 + 0.7152 * channel 1 + 0.0722 * channel 2

    let l1, l2 = luminance a, luminance b
    (max l1 l2 + 0.05) / (min l1 l2 + 0.05)

/// One drawn mark.
type Mark =
    { Category: string
      /// The value as text, or the reason there is none ("unavailable: …").
      Text: string
      /// Position along the axis, 0-1; None when there is no value (never zero).
      Position: float option
      Colour: string
      Pattern: string
      Symbol: string
      Available: bool }

/// What the page draws, and its accessible equivalents.
type Compiled =
    { Spec: Spec
      Marks: Mark list
      /// The same data as a table: category, value text.
      Table: (string * string) list
      Description: string
      Total: string option
      Warnings: Warning list }

let private legal (spec: Spec) (data: Datum list) =
    let name = $"%A{spec.Chart}"

    [ match spec.Chart, spec.Dimension with
      | (Line | Area | Sparkline), (Nominal | Binned) -> IllegalShape(name, "a line needs an ordered or temporal dimension")
      | Histogram, d when d <> Binned -> IllegalShape(name, "a histogram needs a numeric distribution")
      | (Bar | DotPlot | RankedTable | MetricCards | Bullet | Radar), Binned -> IllegalShape(name, "use a histogram for a distribution")
      | Pie, _ when data.Length > 5 -> IllegalShape(name, "a pie is only for five categories or fewer")
      | (GroupedBar | StackedBar | Heatmap | Matrix | SmallMultiples | BoxPlot), _ -> IllegalShape(name, "needs a second dimension, which version 1 data does not carry")
      | _ -> ()
      if spec.Chart = Stacked100 && spec.Denominator.IsNone then MissingDenominator
      if spec.Chart = DualAxis && spec.Justification.IsNone then MissingJustification
      if String.IsNullOrWhiteSpace spec.Title then MissingTitle
      if spec.TextSize < 12 then TextTooSmall spec.TextSize
      if spec.Scale.Maximum <= spec.Scale.Minimum then InvalidScale ]

let private zero (unit: Unit) =
    match unit with
    | _ -> 0.0

let private text (unit: Unit) (value: float) =
    match unit with
    | Percent0To1 -> (value * 100.0).ToString("0.#", Globalization.CultureInfo.InvariantCulture) + "%"
    | Percent0To100 -> value.ToString("0.#", Globalization.CultureInfo.InvariantCulture) + "%"
    | Score0To100 -> value.ToString("0.0", Globalization.CultureInfo.InvariantCulture)
    | Count -> value.ToString("0", Globalization.CultureInfo.InvariantCulture)

let private reason =
    function
    | Suppressed(minimum, _) -> $"suppressed below {minimum}"
    | InsufficientSample(required, found) -> $"needs {required}, has {found}"
    | NotRetained _ -> "not retained"
    | other -> $"unavailable ({other})"

/// Validates and compiles a specification over data (ADM-015..017).
let compile (spec: Spec) (data: Datum list) : Result<Compiled, Refusal list> =
    match legal spec data with
    | problems when not problems.IsEmpty -> Error problems
    | _ ->
        let sorted =
            match spec.Sort with
            | ByCategory -> data |> List.sortBy (fun d -> d.Category, d.Order)
            | ByOrder -> data |> List.sortBy (fun d -> d.Order, d.Category)
            | ByValueDescending ->
                data |> List.sortBy (fun d -> (match d.Value with Value v -> -v | Unavailable _ -> Double.MaxValue), d.Category)

        let span = spec.Scale.Maximum - spec.Scale.Minimum

        let marks =
            sorted
            |> List.map (fun d ->
                // Keyed on the category's own order, not its sorted position, so a
                // category keeps its colour, pattern and symbol however it is sorted.
                let colour, pattern, symbol = palette[((d.Order % palette.Length) + palette.Length) % palette.Length]

                match d.Value with
                | Value v ->
                    { Category = d.Category
                      Text = text spec.Unit v
                      Position = Some(Math.Clamp((v - spec.Scale.Minimum) / span, 0.0, 1.0))
                      Colour = colour
                      Pattern = pattern
                      Symbol = symbol
                      Available = true }
                | Unavailable why ->
                    { Category = d.Category
                      Text = reason why
                      Position = None
                      Colour = colour
                      Pattern = pattern
                      Symbol = symbol
                      Available = false })

        let missing = marks |> List.filter (fun m -> not m.Available) |> List.length
        let suppressed = data |> List.exists (fun d -> match d.Value with Unavailable(Suppressed _) -> true | _ -> false)

        let total =
            if not spec.ShowTotal || suppressed then None
            else Some(text spec.Unit (data |> List.sumBy (fun d -> match d.Value with Value v -> v | Unavailable _ -> 0.0)))

        let available = marks |> List.filter _.Available

        Ok
            { Spec = spec
              Marks = marks
              Table = marks |> List.map (fun m -> m.Category, m.Text)
              Description =
                match available with
                | [] -> $"{spec.Title}: no values can be shown."
                | _ ->
                    let highest = available |> List.maxBy (fun m -> m.Position.Value)
                    let lowest = available |> List.minBy (fun m -> m.Position.Value)
                    $"{spec.Title}: {available.Length} of {marks.Length} values shown; highest {highest.Category} ({highest.Text}), lowest {lowest.Category} ({lowest.Text})."
                    + (if missing > 0 then $" {missing} without a value." else "")
              Total = total
              Warnings =
                [ if (spec.Chart = Bar || spec.Chart = Bullet) && spec.Scale.Minimum <> zero spec.Unit then TruncatedAxis
                  if data.Length > 12 then TooManyCategories data.Length
                  if missing > 0 then MissingValues missing
                  if spec.ShowTotal && suppressed then TotalWithheldForSuppression ] }

/// Accessibility problems (ADM-017), for a compiled chart and a background.
let accessibility (compiled: Compiled) (background: string) =
    [ if compiled.Spec.Interaction = NoInteraction && compiled.Marks.IsEmpty then "NO_CONTENT"
      if compiled.Table.Length <> compiled.Marks.Length then "TABLE_DIFFERS_FROM_CHART"
      if String.IsNullOrWhiteSpace compiled.Description then "NO_DESCRIPTION"
      for mark in compiled.Marks do
          if contrast mark.Colour background < 3.0 then $"LOW_CONTRAST:{mark.Category}"
      if compiled.Marks |> List.map (fun m -> m.Pattern, m.Symbol) |> List.distinct |> List.length < min compiled.Marks.Length palette.Length then
          "COLOUR_ONLY_DISTINCTION" ]
