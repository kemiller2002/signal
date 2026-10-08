/// The canonical survey result (CAN-006, VER-005, ARX-014; DF-SIGNAL-2026-0002
/// Q4, Q8, Q9, Q12): the rule evaluation, every section's outcome with its
/// explanation, the optional overall score with its explanation,
/// interpretations, coverage and the lineage that makes the result exactly
/// reproducible. One pure function computes it from the template and the
/// answers, so a live (incremental) display and a final result can never
/// disagree: both are this function.
///
/// What a respondent sees is a separate projection under the template's
/// display policy; evaluation never depends on it.
module Echelon.Signal.Engine.SurveyResult

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Template

/// Version of this result shape.
[<Literal>]
let ResultShapeVersion = 1

/// Provisional until the response is finalized and submittable (Q4).
type Status =
    | Provisional
    | Final

type Coverage =
    { Applicable: int
      Answered: int
      /// Don't know, not observed, not applicable: coverage, not performance.
      Special: int
      Unanswered: int }

/// Which scorer and semantics version produced a section (Q9).
type ScorerUse = { Section: string; Scorer: string }

type Lineage =
    { TemplateHash: string
      ResultShapeVersion: int
      Scorers: ScorerUse list
      /// Set when a custom expression produced the overall score.
      ExpressionLanguageVersion: int option }

type Overall =
    { Outcome: Scoring.Outcome
      Composite: Composite.Trace option
      Expression: Expression.Node option }

type Result =
    { Status: Status
      Evaluation: Rules.Evaluation
      /// The explanation of each applicable scored section, from the same
      /// evaluation that produced its outcome.
      SectionTraces: (string * Scoring.Trace) list
      Overall: Overall option
      Interpretations: Interpretation.Outcome list
      Coverage: Coverage
      Lineage: Lineage }

let private coverage (content: Content) (e: Rules.Evaluation) (answers: Answers) =
    let applicable = questions content |> List.filter (fun (_, q) -> e.Applicability.Questions.Contains q.Id)
    let state (q: Question) = answers.TryFind q.Id

    { Applicable = applicable.Length
      Answered = applicable |> List.filter (fun (_, q) -> match state q with Some(Value _) -> true | _ -> false) |> List.length
      Special = applicable |> List.filter (fun (_, q) -> match state q with Some(Special _) -> true | _ -> false) |> List.length
      Unanswered = applicable |> List.filter (fun (_, q) -> (state q).IsNone) |> List.length }

/// The result of a template and answers. `templateHash` names the exact
/// published template; `finalized` is whether the response was finalized.
let compute (templateHash: string) (content: Content) (answers: Answers) (finalized: bool) : Result =
    let e = Rules.evaluate content answers
    let valid = answers |> Map.filter (fun id state -> (checkAnswer content id state).IsNone)

    let env: Rules.Env =
        { Content = content
          Applicable = e.Applicability.Questions
          Answers = valid |> Map.filter (fun id _ -> e.Applicability.Questions.Contains id) }

    let traces =
        content.Sections
        |> List.filter (fun s -> e.Applicability.Sections.Contains s.Id)
        |> List.choose (fun s -> Rules.sectionExplain env s |> Option.map (fun (_, trace) -> s.Id, trace))

    let overall =
        content.Results.Overall
        |> Option.map (function
            | Composite spec ->
                let outcome, trace = Composite.compose spec e.Sections
                { Outcome = outcome; Composite = Some trace; Expression = None }
            | Custom custom ->
                let outcome, node = Expression.score env custom
                { Outcome = outcome; Composite = None; Expression = Some node })

    { Status = if finalized && Rules.isSubmittable e.Completion then Final else Provisional
      Evaluation = e
      SectionTraces = traces
      Overall = overall
      Interpretations =
        content.Results.Interpretations
        |> List.map (Interpretation.interpret env (overall |> Option.map _.Outcome) e.Sections)
      Coverage = coverage content e valid
      Lineage =
        { TemplateHash = templateHash
          ResultShapeVersion = ResultShapeVersion
          Scorers =
            content.Sections
            |> List.choose (fun s -> s.Scoring |> Option.map (fun sc -> { Section = s.Id; Scorer = Registry.text (Registry.identify sc.Scorer) }))
          ExpressionLanguageVersion =
            match content.Results.Overall with
            | Some(Custom c) -> Some c.LanguageVersion
            | _ -> None } }

// ---------------------------------------------------------------------------
// The respondent's view under the display policy (ARX-014).
// ---------------------------------------------------------------------------

/// What the respondent has done, for display decisions only.
type ViewContext =
    { /// The respondent has moved past at least one page.
      PageAdvanced: bool
      Finalized: bool }

type RespondentView =
    { Overall: Scoring.Outcome option
      Sections: (string * Rules.SectionResult) list
      Interpretations: Interpretation.Outcome list
      /// Explanation traces are shown only when the template opts in (Q12).
      ShowExplanation: bool
      Status: Status }

let private sectionComplete (content: Content) (result: Result) (answers: Answers) (sectionId: string) =
    content.Sections
    |> List.filter (fun s -> s.Id = sectionId)
    |> List.collect _.Questions
    |> List.filter (fun q -> result.Evaluation.Applicability.Questions.Contains q.Id)
    |> List.forall (fun q -> answers.ContainsKey q.Id)

let private shows (policy: DisplayPolicy) (context: ViewContext) (complete: bool) =
    match policy with
    | DisplayPolicy.Hidden -> false
    | AfterEachResponse -> true
    | AfterPageAdvance -> context.PageAdvanced || context.Finalized
    | AfterSectionComplete -> complete || context.Finalized
    | FinalOnly -> context.Finalized

let respondentView (content: Content) (answers: Answers) (context: ViewContext) (result: Result) : RespondentView =
    let display = content.Results.Display
    let everything = Rules.isSubmittable result.Evaluation.Completion

    { Overall =
        if shows display.Overall context everything then result.Overall |> Option.map _.Outcome else None
      Sections =
        result.Evaluation.Sections
        |> List.filter (fun (id, _) -> shows display.Sections context (sectionComplete content result answers id))
      Interpretations =
        if shows display.Interpretations context everything then result.Interpretations else []
      ShowExplanation = display.Explanation = RespondentsToo
      Status = result.Status }
