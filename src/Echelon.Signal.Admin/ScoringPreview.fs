/// How a draft scores a set of answers, explained to its author (AUT-006 §63,
/// AST-005 §22, SCS-017, AUT-003 §§22-23; WI-0081). Everything shown comes
/// from the one evaluation that produces the result (`SurveyResult.compute`
/// and its traces, ARX-012): never a second scoring implementation. The
/// arithmetic written out for a section (numerator over denominator) is the
/// trace's own included values, and is checked against the trace's
/// aggregate by a test.
///
/// Pure.
module Echelon.Signal.Admin.ScoringPreview

open System
open System.Globalization
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.ResultModel

/// What a line explains, so the page can group and indent it.
type Kind =
    | RuleTrace
    | SectionHeading
    | Contribution
    | Calculation
    | Overall
    | Band
    | Fact
    | Recommendation
    | Coverage

let kindName =
    function
    | RuleTrace -> "rule"
    | SectionHeading -> "section"
    | Contribution -> "contribution"
    | Calculation -> "calculation"
    | Overall -> "overall"
    | Band -> "band"
    | Fact -> "fact"
    | Recommendation -> "recommendation"
    | Coverage -> "coverage"

type Line = { Key: string; Kind: Kind; Text: string }

let private n (v: float) = v.ToString("0.####", CultureInfo.InvariantCulture)

let private specialName =
    function
    | Scoring.DontKnow -> "Don't know"
    | Scoring.NotObserved -> "Not observed"
    | Scoring.NotApplicable -> "Not applicable"
    | Scoring.Declined -> "Prefer not to answer"
    | Scoring.Unanswered -> "unanswered"

let private aggregateName (a: Scoring.Aggregate) =
    match a with
    | Scoring.RawSum -> "sum"
    | Scoring.Mean -> "mean"
    | Scoring.Median -> "median"
    | Scoring.Minimum -> "lowest"
    | Scoring.Maximum -> "highest"
    | Scoring.CountAnswered -> "count of answers"
    | Scoring.CountAtLeast t -> $"count at or above {n t}"
    | Scoring.WeightedSum _ -> "weighted sum"
    | Scoring.WeightedMean _ -> "weighted mean"
    | Scoring.PercentageOfMaximum m -> $"percentage of the maximum ({n m} per item)"
    | Scoring.PercentageOfRange(lo, hi) -> $"percentage of the range {n lo} to {n hi}"
    | other -> $"%A{other}"

let private scaleText (scale: Scoring.ItemScale) =
    match scale with
    | Scoring.Direct -> "each answer's stored value counts directly"
    | Scoring.Reverse(lo, hi) -> $"each answer is reversed within {n lo} to {n hi}"
    | Scoring.Mapped _ -> "each answer is mapped to its declared value"

let private specialText (policy: Scoring.SpecialPolicy) =
    match policy with
    | Scoring.Exclude -> "special answers are left out of the numerator and the denominator"
    | Scoring.Substitute v -> $"special answers count as {n v}, as declared"

let private transformText (t: Scoring.Transform) =
    match t with
    | Scoring.LinearTransform(m, o) -> $"× {n m} + {n o}"
    | Scoring.LinearNormalize(a, b, c, d) -> $"rescaled from {n a}–{n b} to {n c}–{n d}"
    | Scoring.Clamp(lo, hi) -> $"bounded to {n lo}–{n hi}"
    | Scoring.Floor -> "rounded down"
    | Scoring.Ceiling -> "rounded up"

let outcomeText (outcome: Scoring.Outcome) =
    match outcome with
    | Scoring.Score(v, included, excluded) -> $"{n v} ({included} included, {excluded} excluded)"
    | Scoring.NotScored(Scoring.InsufficientObservations(usable, minimum)) -> $"not scored: {usable} usable answer(s), at least {minimum} needed; never counted as zero"
    | Scoring.NotScored(Scoring.Undefined why) -> $"not scored: undefined ({why})"
    | Scoring.NotScored(Scoring.InvalidConfiguration why) -> $"not scored: the scorer is not usable ({why})"

/// The included values with their weights, in item order.
let private included (scorer: Scoring.Scorer) (trace: Scoring.Trace) =
    let weights =
        match scorer.Aggregate with
        | Scoring.WeightedSum ws
        | Scoring.WeightedMean ws -> ws
        | _ -> []

    trace.Items |> List.choose (fun i -> i.Scaled |> Option.map (fun v -> v, weights |> List.tryItem i.Index |> Option.defaultValue 1.0))

/// The aggregate written out as numerator over denominator, where it is one.
let arithmetic (scorer: Scoring.Scorer) (trace: Scoring.Trace) : (float * float) option =
    let values = included scorer trace

    if values.IsEmpty then
        None
    else
        let sum = values |> List.sumBy fst
        let count = float values.Length

        match scorer.Aggregate with
        | Scoring.Mean -> Some(sum, count)
        | Scoring.RawSum -> Some(sum, 1.0)
        | Scoring.WeightedSum _ -> Some(values |> List.sumBy (fun (v, w) -> v * w), 1.0)
        | Scoring.WeightedMean _ -> Some(values |> List.sumBy (fun (v, w) -> v * w), values |> List.sumBy snd)
        | Scoring.PercentageOfMaximum m -> Some(sum * 100.0, count * m)
        | _ -> None

let private sectionLines (content: Content) (answers: Answers) (applicable: Set<string>) (section: Section) (trace: Scoring.Trace) (outcome: Rules.SectionResult) =
    match section.Scoring with
    | None -> []
    | Some scoring ->
        let scorer = scoring.Scorer
        let scored = scoredQuestions section |> List.filter (fun q -> applicable.Contains q.Id)
        let key suffix = $"{section.Id}/{suffix}"

        [ { Key = key "heading"
            Kind = SectionHeading
            Text =
              $"{section.Title} ({section.Id}): {aggregateName scorer.Aggregate}; {scaleText scorer.Scale}; {specialText scorer.Missing.Special}; at least {scorer.Missing.MinimumObservations} usable answer(s)" }
          for item in trace.Items do
              let q = scored |> List.tryItem item.Index
              let id = q |> Option.map _.Id |> Option.defaultValue $"#{item.Index}"
              let raw = q |> Option.map (fun q -> GenericReview.answerText q (answers.TryFind q.Id)) |> Option.defaultValue "?"

              let stored =
                  match item.Observation with
                  | Scoring.Numeric v -> $"stored {n v}"
                  | Scoring.Special s -> specialName s

              let weight =
                  match scorer.Aggregate with
                  | Scoring.WeightedSum ws
                  | Scoring.WeightedMean ws -> ws |> List.tryItem item.Index |> Option.map (fun w -> $", weight {n w}") |> Option.defaultValue ""
                  | _ -> ""

              let contribution =
                  match item.Observation, item.Scaled, scorer.Scale with
                  | _, None, _ -> "excluded: special answers are left out of the numerator and the denominator"
                  | Scoring.Special s, Some v, _ -> $"counted as {n v}, the declared substitute for {specialName s}"
                  | Scoring.Numeric raw, Some v, Scoring.Reverse _ -> $"reversed {n raw} → contribution {n v}{weight}"
                  | Scoring.Numeric raw, Some v, Scoring.Mapped _ -> $"mapped {n raw} → contribution {n v}{weight}"
                  | _, Some v, _ -> $"contribution {n v}{weight}"

              { Key = key $"item-{item.Index}"; Kind = Contribution; Text = $"{id}: '{raw}', {stored} → {contribution}" }

              // An answer key's reasoning: what was credited and penalized.
              match q, q |> Option.bind (fun q -> answers.TryFind q.Id) with
              | Some q, Some(Value v) ->
                  match keyedExplanation content q v with
                  | Some k ->
                      let listed (ids: string list) = if ids.IsEmpty then "none" else String.Join(", ", ids)
                      let limits = [ if k.Capped then "capped"; if k.Floored then "floored" ] |> String.concat ", "

                      { Key = key $"item-{item.Index}-key"
                        Kind = Contribution
                        Text =
                          $"""{q.Id} answer key: credited {listed k.Credited}, penalized {listed k.Penalized}; {n k.Credit} − {n k.Penalty} = {n k.Final}{(if limits = "" then "" else $" ({limits})")}""" }
                  | None -> ()
              | _ -> ()
          match arithmetic scorer trace, trace.Aggregated with
          | Some(num, den), Some aggregate when den <> 1.0 ->
              { Key = key "aggregate"; Kind = Calculation; Text = $"{aggregateName scorer.Aggregate}: {n num} / {n den} = {n aggregate}" }
          | _, Some aggregate -> { Key = key "aggregate"; Kind = Calculation; Text = $"{aggregateName scorer.Aggregate}: {n aggregate}" }
          | _ -> ()
          for i, (t, input, output) in List.indexed trace.Transformed do
              { Key = key $"transform-{i}"; Kind = Calculation; Text = $"{transformText t}: {n input} → {n output}" }
          match trace.Unrounded with
          | Some v -> { Key = key "rounding"; Kind = Calculation; Text = $"rounded to {trace.Decimals} decimal place(s): {n v}" }
          | None -> ()
          { Key = key "result"
            Kind = Calculation
            Text =
              match outcome with
              | Rules.SectionScored o -> $"section score: {outcomeText o}"
              | Rules.SectionNotApplicable -> "not applicable: no applicable scored question; never counted as zero" } ]

let private bandsText (kind: InterpretationKind) =
    match kind with
    | Bands bands -> bands |> List.map (fun b -> $"{b.Label} {n b.From}–{n b.To}") |> String.concat ", "
    | PassFail(threshold, direction) -> $"pass at {n threshold} ({direction})"
    | Stages(baseline, stages) -> baseline :: (stages |> List.map _.Label) |> String.concat " → "

let private targetText =
    function
    | OverallResult -> "the overall score"
    | SectionResultOf id -> $"section {id}"

/// Which questions and sections the answers hide, and why (the rule trace).
let private ruleLines (content: Content) (e: Rules.Evaluation) =
    [ for rule in content.Rules.Flow do
          let hidden =
              match rule.Then with
              | ShowQuestion q
              | HideQuestion q -> if e.Applicability.Questions.Contains q then None else Some q
              | ShowSection s
              | HideSection s -> if e.Applicability.Sections.Contains s then None else Some s
              | _ -> None

          match hidden with
          | Some target -> { Key = $"rule/{rule.Id}"; Kind = RuleTrace; Text = $"{target} is not shown: {QuestionKinds.describeRule content rule}" }
          | None -> ()
      match e.Applicability.Terminated with
      | Some(id, reason) -> { Key = "rule/terminated"; Kind = RuleTrace; Text = $"The survey ends early ({id}): {reason}" }
      | None -> () ]

/// The explanation of these answers, from one evaluation.
let explain (content: Content) (answers: Answers) : Line list =
    let result = SurveyResult.compute "preview" content answers false
    let e = result.Evaluation
    let applicable = e.Applicability.Questions

    let sections =
        content.Sections
        |> List.collect (fun section ->
            match result.SectionTraces |> List.tryFind (fst >> (=) section.Id), e.Sections |> List.tryFind (fst >> (=) section.Id) with
            | Some(_, trace), Some(_, outcome) -> sectionLines content answers applicable section trace outcome
            | _, Some(_, Rules.SectionNotApplicable) when section.Scoring.IsSome ->
                [ { Key = $"{section.Id}/result"; Kind = SectionHeading; Text = $"{section.Title} ({section.Id}): not applicable; never counted as zero" } ]
            | _ -> [])

    let overall =
        match result.Overall with
        | None -> []
        | Some o ->
            [ match o.Composite with
              | Some c ->
                  for section, value, weight in c.Included do
                      { Key = $"overall/{section}"; Kind = Overall; Text = $"{section}: {n value}, weight {n weight}" }
                  for section, why in c.Excluded do
                      { Key = $"overall/{section}"; Kind = Overall; Text = $"{section}: left out ({why})" }
                  if c.Capped then
                      { Key = "overall/capped"; Kind = Overall; Text = "capped by a minimum-domain rule" }
              | None -> ()
              match o.Expression with
              | Some node ->
                  let rec walk depth (node: Expression.Node) =
                      [ yield $"""{String(' ', depth * 2)}{node.Label} = {node.Value |> Option.map n |> Option.defaultValue "missing"}"""
                        for child in node.Children do
                            yield! walk (depth + 1) child ]

                  for i, text in List.indexed (walk 0 node) do
                      { Key = $"overall/expression-{i}"; Kind = Overall; Text = text }
              | None -> ()
              { Key = "overall/result"; Kind = Overall; Text = $"overall score: {outcomeText o.Outcome}" } ]

    let bands =
        List.zip content.Results.Interpretations result.Interpretations
        |> List.map (fun (spec, outcome) ->
            let label = outcome.Label |> Option.defaultValue "no label: the target is not scored, or a stage is still unknown"
            { Key = $"band/{spec.Id}"; Kind = Band; Text = $"{spec.Id} on {targetText spec.Target}: {label} ({bandsText spec.Kind})" })

    let facts =
        e.Facts
        |> List.map (fun (id, value) ->
            let shown =
                match value with
                | Some(BoolValue b) -> (if b then "true" else "false")
                | Some(NumberValue v) -> n v
                | Some(CategoryValue c) -> c
                | None -> "unknown"

            { Key = $"fact/{id}"; Kind = Fact; Text = $"{id} = {shown}" })

    let recommendations =
        e.Recommendations |> List.map (fun r -> { Key = $"recommendation/{r.Id}"; Kind = Recommendation; Text = $"triggered: {RuleAuthoring.describeRecommendation content r}" })

    let coverage =
        let c = result.Coverage

        [ { Key = "coverage"
            Kind = Coverage
            Text = $"{c.Answered} answered, {c.Special} with a special answer, {c.Unanswered} unanswered, of {c.Applicable} applicable" }
          { Key = "completion"; Kind = Coverage; Text = $"completion: %A{e.Completion}" } ]

    ruleLines content e @ sections @ overall @ bands @ facts @ recommendations @ coverage
