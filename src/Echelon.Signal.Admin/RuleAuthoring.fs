/// Authoring a draft's facts, recommendations and completion policy
/// (WI-0081; AST-005, ALG-004, SCS-017, AUT-002; DF-SIGNAL-2026-0011).
///
/// Each is built from the same plain condition the console already uses
/// for conditional questions: an earlier question has a named answer, or a
/// fact is true.
/// - A fact is a yes/no fact, true when its condition holds.
/// - A recommendation is shown when its condition holds, with a priority, a
///   title and a description.
/// - Completion can require a minimum share of applicable questions answered
///   and name sections that must be fully answered.
/// Custom expressions are not authored here; they stay in imported
/// templates.
///
/// Pure.
module Echelon.Signal.Admin.RuleAuthoring

open System
open Echelon.Signal.Engine
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Template

type Edit =
    /// A yes/no fact, true when `whenName` (a question, or another fact) has the answer.
    | AddFact of whenName: string * answerLabel: string
    | RemoveFact of factId: string
    | AddRecommendation of whenName: string * answerLabel: string * priority: string * title: string * description: string
    | RemoveRecommendation of recommendationId: string
    /// A percentage from 1 to 100, or blank for none.
    | SetMinimumAnswered of percent: string
    | SetSectionRequiredForCompletion of sectionId: string * required: bool

/// The priorities an author picks from, by their page value.
let priorities =
    [ "informational", Informational, "For information"
      "suggested", Suggested, "Suggested"
      "important", Important, "Important"
      "critical", Critical, "Critical" ]

let private priorityName p =
    priorities |> List.tryFind (fun (_, q, _) -> q = p) |> Option.map (fun (_, _, label) -> label) |> Option.defaultValue "?"

let private nextId (prefix: string) (taken: string list) =
    Seq.initInfinite (fun i -> $"{prefix}{i + 1}") |> Seq.find (fun id -> not (List.contains id taken))

/// The condition a name and an answer make: a fact named alone is true
/// when it holds ("yes" or blank); a question needs one of its answers.
let condition (content: Content) (whenName: string) (answerLabel: string) : Result<Condition, string> =
    let name = whenName.Trim().ToUpperInvariant()

    match content.Rules.Facts |> List.tryFind (fun f -> f.Id = name), tryQuestion content name with
    | Some fact, _ when String.IsNullOrWhiteSpace answerLabel || answerLabel.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase) -> Ok(FactTrue fact.Id)
    | Some fact, _ -> Error $"{fact.Id} is a yes/no fact: leave the answer blank or write 'yes'."
    | None, Some q -> QuestionKinds.answerCondition q answerLabel
    | None, None -> Error $"There is no question or fact '{name}'."

let private nonEmpty (what: string) (text: string) =
    if String.IsNullOrWhiteSpace text then Error $"The {what} cannot be empty." else Ok(text.Trim())

let private withRules (change: RuleSet -> RuleSet) (content: Content) = { content with Rules = change content.Rules }

/// Whether a condition mentions an id (a question or a fact).
let rec mentions (id: string) (condition: Condition) =
    match condition with
    | AnswerIs(q, _)
    | AnswerIn(q, _)
    | IsSpecial(q, _)
    | Answered q -> q = id
    | FactTrue f
    | CategoryIs(f, _) -> f = id
    | All cs
    | Any cs -> cs |> List.exists (mentions id)
    | Not c -> mentions id c
    | Always
    | Compare _ -> false

/// The rules without anything that mentions a removed question or fact.
let rec withoutMentions (id: string) (rules: RuleSet) : RuleSet =
    let factMentions (f: Fact) =
        match f.Expr with
        | BooleanFact c -> mentions id c
        | _ -> false

    let gone = rules.Facts |> List.filter factMentions |> List.map _.Id

    let pruned =
        { rules with
            Facts = rules.Facts |> List.filter (fun f -> f.Id <> id && not (factMentions f))
            Recommendations = rules.Recommendations |> List.filter (fun r -> not (mentions id r.When))
            Completion =
                { rules.Completion with
                    Condition = rules.Completion.Condition |> Option.filter (mentions id >> not) } }

    gone |> List.fold (fun r factId -> withoutMentions factId r) pruned

/// Applies one rule edit to the content; it is unchanged when refused.
let apply (edit: Edit) (content: Content) : Result<Content, string> =
    let rules = content.Rules

    match edit with
    | AddFact(whenName, answerLabel) ->
        condition content whenName answerLabel
        |> Result.map (fun c -> content |> withRules (fun r -> { r with Facts = r.Facts @ [ { Id = nextId "F" (r.Facts |> List.map _.Id); Expr = BooleanFact c } ] }))
    | RemoveFact id when rules.Facts |> List.exists (fun f -> f.Id = id) -> Ok(content |> withRules (withoutMentions id))
    | RemoveFact id -> Error $"There is no fact '{id}'."
    | AddRecommendation(whenName, answerLabel, priority, title, description) ->
        match priorities |> List.tryFind (fun (key, _, _) -> key = priority) with
        | None -> Error "Choose a priority."
        | Some(_, p, _) ->
            condition content whenName answerLabel
            |> Result.bind (fun c -> nonEmpty "recommendation title" title |> Result.map (fun t -> c, t))
            |> Result.bind (fun (c, t) -> nonEmpty "recommendation text" description |> Result.map (fun d -> c, t, d))
            |> Result.map (fun (c, t, d) ->
                let recommendation =
                    { Id = nextId "R" (rules.Recommendations |> List.map _.Id)
                      When = c
                      Kind = Recommendation
                      Priority = p
                      Category = "authored"
                      Title = t
                      Description = d
                      RelatedSection = None
                      RelatedQuestion =
                        match c with
                        | AnswerIs(q, _)
                        | IsSpecial(q, _) -> Some q
                        | _ -> None }

                content |> withRules (fun r -> { r with Recommendations = r.Recommendations @ [ recommendation ] }))
    | RemoveRecommendation id when rules.Recommendations |> List.exists (fun r -> r.Id = id) ->
        Ok(content |> withRules (fun r -> { r with Recommendations = r.Recommendations |> List.filter (fun x -> x.Id <> id) }))
    | RemoveRecommendation id -> Error $"There is no recommendation '{id}'."
    | SetMinimumAnswered percent when String.IsNullOrWhiteSpace percent ->
        Ok(content |> withRules (fun r -> { r with Completion = { r.Completion with MinimumAnsweredPercent = None } }))
    | SetMinimumAnswered percent ->
        match Int32.TryParse(percent.Trim().TrimEnd('%')) with
        | true, n when n >= 1 && n <= 100 -> Ok(content |> withRules (fun r -> { r with Completion = { r.Completion with MinimumAnsweredPercent = Some(float n) } }))
        | _ -> Error "The share of questions answered is a whole percentage from 1 to 100, or blank for none."
    | SetSectionRequiredForCompletion(sectionId, required) when (content.Sections |> List.map _.Id) |> List.contains sectionId ->
        let others = rules.Completion.RequiredSections |> List.filter ((<>) sectionId)
        let next = if required then (content.Sections |> List.map _.Id) |> List.filter (fun s -> s = sectionId || List.contains s others) else others
        Ok(content |> withRules (fun r -> { r with Completion = { r.Completion with RequiredSections = next } }))
    | SetSectionRequiredForCompletion(sectionId, _) -> Error $"There is no section '{sectionId}'."

/// How a fact reads in the console.
let describeFact (content: Content) (fact: Fact) =
    match fact.Expr with
    | BooleanFact c -> $"{fact.Id}: true when {QuestionKinds.describeCondition content c}"
    | _ -> $"{fact.Id}: an imported fact"

/// How a recommendation reads in the console.
let describeRecommendation (content: Content) (r: RecommendationRule) =
    $"{r.Id} ({priorityName r.Priority}): \"{r.Title}\" when {QuestionKinds.describeCondition content r.When}"

/// How the completion policy reads in the console.
let describeCompletion (content: Content) =
    let c = content.Rules.Completion

    [ "Every applicable required question must be answered."
      match c.MinimumAnsweredPercent with
      | Some p -> $"At least {p}%% of applicable questions must be answered."
      | None -> ()
      if not c.RequiredSections.IsEmpty then
          $"""These sections must be fully answered: {String.Join(", ", c.RequiredSections)}.""" ]
    |> String.concat " "

// ---- The form the screens hold ------------------------------------------------------------------

type Form =
    { FactWhen: string
      FactAnswer: string
      RecommendationWhen: string
      RecommendationAnswer: string
      RecommendationPriority: string
      RecommendationTitle: string
      RecommendationText: string }

let emptyForm =
    { FactWhen = ""
      FactAnswer = ""
      RecommendationWhen = ""
      RecommendationAnswer = ""
      RecommendationPriority = "suggested"
      RecommendationTitle = ""
      RecommendationText = "" }

/// The page events this form owns.
let events =
    set
        [ "factWhen"; "factAnswer"; "addFact"; "removeFact"
          "recommendationWhen"; "recommendationAnswer"; "recommendationPriority"; "recommendationTitle"; "recommendationText"
          "addRecommendation"; "removeRecommendation"; "minimumAnswered"; "sectionRequired" ]

/// One form event: the form after it, and the edit it asks for, if any.
/// A successful add clears its fields (`cleared`).
let update (name: string) (key: string option) (value: string) (form: Form) : Form * Edit option =
    match name, key with
    | "factWhen", _ -> { form with FactWhen = value }, None
    | "factAnswer", _ -> { form with FactAnswer = value }, None
    | "addFact", _ -> form, Some(AddFact(form.FactWhen, form.FactAnswer))
    | "removeFact", Some id -> form, Some(RemoveFact id)
    | "recommendationWhen", _ -> { form with RecommendationWhen = value }, None
    | "recommendationAnswer", _ -> { form with RecommendationAnswer = value }, None
    | "recommendationPriority", _ -> { form with RecommendationPriority = value }, None
    | "recommendationTitle", _ -> { form with RecommendationTitle = value }, None
    | "recommendationText", _ -> { form with RecommendationText = value }, None
    | "addRecommendation", _ ->
        form,
        Some(AddRecommendation(form.RecommendationWhen, form.RecommendationAnswer, form.RecommendationPriority, form.RecommendationTitle, form.RecommendationText))
    | "removeRecommendation", Some id -> form, Some(RemoveRecommendation id)
    | "minimumAnswered", _ -> form, Some(SetMinimumAnswered value)
    // A checkbox per section: checked reports a value, unchecked reports "".
    | "sectionRequired", Some id -> form, Some(SetSectionRequiredForCompletion(id, value <> ""))
    | _ -> form, None

/// The form after its edit was applied: an add clears what it used.
let cleared (edit: Edit) (form: Form) =
    match edit with
    | AddFact _ -> { form with FactWhen = ""; FactAnswer = "" }
    | AddRecommendation _ ->
        { form with
            RecommendationWhen = ""
            RecommendationAnswer = ""
            RecommendationTitle = ""
            RecommendationText = "" }
    | _ -> form
