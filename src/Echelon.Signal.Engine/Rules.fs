/// Rule evaluation: applicability (flow), derived facts, validation,
/// completion, applicability-aware section scores and recommendations, in
/// the explicit order of ACR-007 §20 / CAN-002 §14:
///
///   1. validate primitive answer values
///   2. determine applicability, question by question in template order
///   3. calculate derived facts
///   4. run validation rules
///   5. determine completion state
///   6. calculate section scores over applicable questions
///   7. trigger recommendations and actions
///
/// Every step is a pure function of the template and the answers. Logic is
/// three-valued: a condition over an unanswered (or not applicable) question
/// is unknown, and each consumer states what unknown means. `RuleChecks`
/// guarantees, before publication, that facts are acyclic and that flow
/// depends only on earlier content, so one pass in template order decides
/// applicability.
module Echelon.Signal.Engine.Rules

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Template

/// What conditions read: the applicable questions decided so far and their
/// answers. Answers to questions that are not applicable are not visible.
type Env =
    { Content: Content
      Applicable: Set<string>
      Answers: Answers }

let private envOf (content: Content) (applicable: Set<string>) (answers: Answers) =
    { Content = content
      Applicable = applicable
      Answers = answers |> Map.filter (fun id _ -> applicable.Contains id) }

let private all3 (values: bool option list) =
    if List.contains (Some false) values then Some false
    elif List.forall ((=) (Some true)) values then Some true
    else None

let private any3 (values: bool option list) =
    if List.contains (Some true) values then Some true
    elif List.forall ((=) (Some false)) values then Some false
    else None

let private compare (op: Comparison) (a: float) (b: float) =
    match op with
    | Equal -> a = b
    | NotEqual -> a <> b
    | Greater -> a > b
    | GreaterOrEqual -> a >= b
    | Less -> a < b
    | LessOrEqual -> a <= b

/// A section's outcome over its applicable scored questions; None when it is
/// not scored, not applicable or has no applicable scored question.
/// The section's outcome and its explanation, from one evaluation.
let sectionExplain (env: Env) (section: Section) : (Scoring.Outcome * Scoring.Trace) option =
    section.Scoring
    |> Option.bind (fun scoring ->
        match scoredQuestions section |> List.filter (fun q -> env.Applicable.Contains q.Id) with
        | [] -> None
        | scored ->
            let observations = scored |> List.map (fun q -> observation q (env.Answers.TryFind q.Id))
            Some(Scoring.explain scoring.Scorer observations))

let sectionOutcome (env: Env) (section: Section) : Scoring.Outcome option =
    sectionExplain env section |> Option.map fst

let rec number (env: Env) (visiting: Set<string>) (expr: NumberExpr) : float option =
    match expr with
    | Constant v -> Some v
    | AnswerNumber id ->
        match env.Answers.TryFind id, tryQuestion env.Content id with
        | Some(Value v), Some q -> numeric q v
        | _ -> None
    | SectionScore id ->
        env.Content.Sections
        |> List.tryFind (fun s -> s.Id = id)
        |> Option.bind (sectionOutcome env)
        |> Option.bind (function
            | Scoring.Score(v, _, _) -> Some v
            | Scoring.NotScored _ -> None)
    | NumberFact id ->
        match fact env visiting id with
        | Some(NumberValue v) -> Some v
        | _ -> None

and condition (env: Env) (visiting: Set<string>) (c: Condition) : bool option =
    let answerOf id = env.Answers.TryFind id

    match c with
    | Always -> Some true
    | Answered id -> Some(env.Answers.ContainsKey id)
    | AnswerIs(id, v) ->
        match answerOf id with
        | None -> None
        | Some(Value a) -> Some(a = v)
        | Some(Special _) -> Some false
    | AnswerIn(id, vs) ->
        match answerOf id with
        | None -> None
        | Some(Value a) -> Some(List.contains a vs)
        | Some(Special _) -> Some false
    | IsSpecial(id, s) ->
        match answerOf id with
        | None -> None
        | Some state -> Some(state = Special s)
    | Compare(a, op, b) ->
        match number env visiting a, number env visiting b with
        | Some x, Some y -> Some(compare op x y)
        | _ -> None
    | FactTrue id ->
        match fact env visiting id with
        | Some(BoolValue b) -> Some b
        | _ -> None
    | CategoryIs(id, category) ->
        match fact env visiting id with
        | Some(CategoryValue v) -> Some(v = category)
        | _ -> None
    | All cs -> cs |> List.map (condition env visiting) |> all3
    | Any cs -> cs |> List.map (condition env visiting) |> any3
    | Not inner -> condition env visiting inner |> Option.map not

/// A fact's value; None when unknown. A cycle (rejected before publication)
/// evaluates to unknown rather than looping.
and fact (env: Env) (visiting: Set<string>) (id: string) : FactValue option =
    if visiting.Contains id then
        None
    else
        let visiting = visiting.Add id

        env.Content.Rules.Facts
        |> List.tryFind (fun f -> f.Id = id)
        |> Option.bind (fun f ->
            match f.Expr with
            | BooleanFact c -> condition env visiting c |> Option.map BoolValue
            | NumberFactOf n -> number env visiting n |> Option.map NumberValue
            | CategoryFact(cases, otherwise) ->
                let rec pick =
                    function
                    | [] -> otherwise |> Option.map CategoryValue
                    | (c, label) :: rest ->
                        match condition env visiting c with
                        | Some true -> Some(CategoryValue label)
                        | Some false -> pick rest
                        | None -> None

                pick cases)

let truth (env: Env) (c: Condition) = condition env Set.empty c

// ---------------------------------------------------------------------------
// Applicability (flow).
// ---------------------------------------------------------------------------

/// Show rules make their target conditional: shown only while one is true.
/// A true Hide rule hides, and wins over Show. Unknown shows nothing and
/// hides nothing.
let private visible (env: Env) (shows: Condition list) (hides: Condition list) =
    let shown = shows.IsEmpty || shows |> List.exists (fun c -> truth env c = Some true)
    let hidden = hides |> List.exists (fun c -> truth env c = Some true)
    shown && not hidden

type Applicability =
    { Questions: Set<string>
      Sections: Set<string>
      /// The first Terminate rule that fired: its id and reason.
      Terminated: (string * string) option }

let applicability (content: Content) (answers: Answers) : Applicability =
    let flow = content.Rules.Flow
    let order = questions content |> List.mapi (fun i (_, q) -> q.Id, i) |> Map.ofList
    let position id = order.TryFind id |> Option.defaultValue -1

    let sectionStart =
        content.Sections
        |> List.choose (fun s -> s.Questions |> List.tryHead |> Option.map (fun q -> s.Id, position q.Id))
        |> Map.ofList

    let conditionsFor (pick: FlowAction -> bool) =
        flow |> List.filter (fun r -> pick r.Then) |> List.map _.When

    let step (applicable: Set<string>, sections: Set<string>) (section: Section, question: Question) =
        let env = envOf content applicable answers
        let index = position question.Id

        let sectionVisible =
            if question.Id = (List.head section.Questions).Id then
                visible env (conditionsFor ((=) (ShowSection section.Id))) (conditionsFor ((=) (HideSection section.Id)))
            else
                sections.Contains section.Id

        let skipped =
            flow
            |> List.exists (fun r ->
                let between =
                    match r.Then with
                    | SkipToQuestion(from, target) -> position from < index && index < position target
                    | SkipToSection(from, target) ->
                        position from < index && index < (sectionStart.TryFind target |> Option.defaultValue -1)
                    | Terminate(after, _) -> position after < index
                    | _ -> false

                between && truth env r.When = Some true)

        let questionVisible =
            visible env (conditionsFor ((=) (ShowQuestion question.Id))) (conditionsFor ((=) (HideQuestion question.Id)))

        let sections = if sectionVisible then sections.Add section.Id else sections

        if sectionVisible && questionVisible && not skipped then
            applicable.Add question.Id, sections
        else
            applicable, sections

    let applicable, sections = questions content |> List.fold step (Set.empty, Set.empty)
    let env = envOf content applicable answers

    { Questions = applicable
      Sections = sections
      Terminated =
        flow
        |> List.tryPick (fun r ->
            match r.Then with
            | Terminate(_, reason) when truth env r.When = Some true -> Some(r.Id, reason)
            | _ -> None) }

// ---------------------------------------------------------------------------
// Validation, completion, recommendations and the whole evaluation.
// ---------------------------------------------------------------------------

type RuleViolation =
    { RuleId: string
      Subject: string
      Message: string }

let violations (env: Env) : RuleViolation list =
    env.Content.Rules.Validation
    |> List.choose (fun rule ->
        let violation subject = Some { RuleId = rule.Id; Subject = subject; Message = rule.Message }

        match rule.Check with
        | RequiredWhen(id, c) when env.Applicable.Contains id && not (env.Answers.ContainsKey id) && truth env c = Some true ->
            violation id
        | Prohibited c when truth env c = Some true -> violation ""
        | AllowedRange(id, lo, hi) ->
            match number env Set.empty (AnswerNumber id) with
            | Some v when v < lo || v > hi -> violation id
            | _ -> None
        | _ -> None)

/// Explicit completion state (ACR-001 §4, CAN-002 §11). Finalization, not
/// this state, makes an instance Completed (`Instance.instanceStatus`).
type CompletionState =
    | NotStarted
    | InProgress of missingRequired: int
    | Invalid of problems: int
    | ReadyToSubmit
    | Terminated of reason: string

let isSubmittable =
    function
    | ReadyToSubmit
    | Terminated _ -> true
    | _ -> false

type SectionResult =
    | SectionScored of Scoring.Outcome
    /// Not applicable, or no applicable scored question: never zero.
    | SectionNotApplicable

type Evaluation =
    { AnswerProblems: AnswerProblem list
      Applicability: Applicability
      /// Answers given to questions that are not applicable: kept out of
      /// every calculation.
      IgnoredAnswers: string list
      Facts: (string * FactValue option) list
      Violations: RuleViolation list
      Completion: CompletionState
      Sections: (string * SectionResult) list
      /// Triggered rules, most urgent first, then in declaration order.
      Recommendations: RecommendationRule list }

let private priorityRank =
    function
    | Critical -> 0
    | Important -> 1
    | Suggested -> 2
    | Informational -> 3

let private completion (env: Env) (applicability: Applicability) (invalid: int) =
    let content = env.Content
    let policy = content.Rules.Completion
    let applicableQuestions = questions content |> List.filter (fun (_, q) -> env.Applicable.Contains q.Id)

    let missingRequired =
        applicableQuestions
        |> List.filter (fun (s, q) -> (s.Required && q.Required) || List.contains s.Id policy.RequiredSections)
        |> List.filter (fun (_, q) -> not (env.Answers.ContainsKey q.Id))
        |> List.length

    let requiredSectionsApplicable = policy.RequiredSections |> List.forall applicability.Sections.Contains

    let counted =
        applicableQuestions
        |> List.filter (fun (_, q) ->
            match env.Answers.TryFind q.Id with
            | Some(Value _) -> true
            | Some(Special _) -> policy.SpecialCountsAsAnswered
            | None -> false)
        |> List.length

    let percentMet =
        match policy.MinimumAnsweredPercent with
        | None -> true
        | Some _ when applicableQuestions.IsEmpty -> true
        | Some minimum -> 100.0 * float counted / float applicableQuestions.Length >= minimum

    let conditionMet = policy.Condition |> Option.forall (fun c -> truth env c = Some true)

    if invalid > 0 then Invalid invalid
    elif env.Answers.IsEmpty && missingRequired > 0 then NotStarted
    elif missingRequired > 0 || not requiredSectionsApplicable || not percentMet || not conditionMet then InProgress missingRequired
    else
        match applicability.Terminated with
        | Some(_, reason) -> Terminated reason
        | None -> ReadyToSubmit

/// The whole deterministic evaluation of a template and answers.
let evaluate (content: Content) (answers: Answers) : Evaluation =
    let problems = checkAnswers content answers
    let valid = answers |> Map.filter (fun id state -> (checkAnswer content id state).IsNone)
    let applicability = applicability content valid
    let env = envOf content applicability.Questions valid
    let violations = violations env

    { AnswerProblems = problems
      Applicability = applicability
      IgnoredAnswers = valid |> Map.toList |> List.map fst |> List.filter (applicability.Questions.Contains >> not)
      Facts = content.Rules.Facts |> List.map (fun f -> f.Id, fact env Set.empty f.Id)
      Violations = violations
      Completion = completion env applicability (problems.Length + violations.Length)
      Sections =
        content.Sections
        |> List.filter (fun s -> s.Scoring.IsSome)
        |> List.map (fun s ->
            s.Id,
            match sectionOutcome env s with
            | Some outcome when applicability.Sections.Contains s.Id -> SectionScored outcome
            | _ -> SectionNotApplicable)
      Recommendations =
        content.Rules.Recommendations
        |> List.indexed
        |> List.filter (fun (_, r) -> truth env r.When = Some true)
        |> List.sortBy (fun (i, r) -> priorityRank r.Priority, i)
        |> List.map snd }
