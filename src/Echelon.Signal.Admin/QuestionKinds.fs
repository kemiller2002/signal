/// The answer kinds the authoring screens offer (WI-0078, SCS-009..011,
/// AUT-003) and the conditional-question rule (ACR-001 flow): what each kind
/// stores, how it is labelled, and how a section's score keeps to the
/// questions the catalog scorer can read. Pure.
module Echelon.Signal.Admin.QuestionKinds

open System
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.RuleModel

type Kind =
    | Frequency5
    | Agreement5
    | YesNo
    | Choice
    | Multi
    | Number

/// Each kind's page value, its label, and what its details field holds.
let kinds =
    [ "frequency5", Frequency5, "Five-point frequency (scored)", ""
      "agreement5", Agreement5, "Five-point agreement (scored)", ""
      "yesno", YesNo, "Yes or no", ""
      "choice", Choice, "One choice from a list", "the options, separated by commas"
      "multi", Multi, "Any choices from a list", "the options, separated by commas"
      "number", Number, "A whole number on a short scale", "the lowest and highest numbers, e.g. 1-10" ]

let parse (value: string) =
    kinds |> List.tryFind (fun (key, _, _, _) -> key = value) |> Option.map (fun (_, kind, _, _) -> kind)

/// The kinds the catalog scorer reads (five ordered points).
let isScored (q: Question) =
    match q.Answer with
    | Ordinal 5 -> true
    | _ -> false

let private ordinal5 preset labels special : AnswerDefinition * Selectors.Selector * Responses.SpecialState list =
    Ordinal 5, { Preset = preset; Labels = labels }, special

let private optionsOf (details: string) =
    let labels = details.Split(',', StringSplitOptions.TrimEntries ||| StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

    if labels.Length < 2 || labels.Length > 20 then
        Error "List between 2 and 20 options, separated by commas."
    elif (labels |> List.distinctBy (fun l -> l.ToLowerInvariant())).Length <> labels.Length then
        Error "Each option must be different."
    else
        Ok(labels |> List.mapi (fun i label -> { Id = $"o{i + 1}"; Label = label; Score = None }))

let private rangeOf (details: string) =
    match details.Split([| '-'; ','; ' ' |], StringSplitOptions.RemoveEmptyEntries) with
    | [| low; high |] ->
        match Int32.TryParse low, Int32.TryParse high with
        | (true, low), (true, high) when high > low && high - low + 1 <= GenericSession.MaximumTicks ->
            Ok { Minimum = float low; Maximum = float high; Step = 1.0; Decimals = 0 }
        | (true, _), (true, _) -> Error $"Give a lowest number below the highest, at most {GenericSession.MaximumTicks} numbers apart."
        | _ -> Error "Give the lowest and highest whole numbers, e.g. 1-10."
    | _ -> Error "Give the lowest and highest whole numbers, e.g. 1-10."

/// A question of a kind, or why the details do not make one.
let build (id: string) (prompt: string) (kind: Kind) (details: string) : Result<Question, string> =
    let shaped =
        match kind with
        | Frequency5 ->
            Ok(
                ordinal5
                    Selectors.Frequency5
                    ([ Assessment.Never; Assessment.Rarely; Assessment.Sometimes; Assessment.Often; Assessment.AlmostAlways ] |> List.map Assessment.frequencyLabel)
                    [ Responses.DontKnow; Responses.NotObserved; Responses.NotApplicable ]
            )
        | Agreement5 ->
            Ok(
                ordinal5
                    Selectors.Agreement5
                    [ "Strongly disagree"; "Disagree"; "Neither agree nor disagree"; "Agree"; "Strongly agree" ]
                    [ Responses.DontKnow; Responses.NotApplicable ]
            )
        | YesNo -> Ok(Boolean, { Preset = Selectors.YesNo; Labels = [ "No"; "Yes" ] }, [ Responses.NotApplicable ])
        | Choice -> optionsOf details |> Result.map (fun os -> SingleChoice os, { Preset = Selectors.RadioList; Labels = [] }, [ Responses.Declined ])
        | Multi ->
            optionsOf details
            |> Result.map (fun os ->
                MultiChoice { Options = os; Selection = AnyCount; Exclusive = []; WhenExclusive = RejectCombination },
                { Preset = Selectors.CheckboxList; Labels = [] },
                [ Responses.Declined ])
        | Number -> rangeOf details |> Result.map (fun b -> BoundedNumber b, { Preset = Selectors.NumericStepper; Labels = [] }, [ Responses.Declined ])

    shaped
    |> Result.map (fun (answer, selector, special) ->
        { Id = id
          Prompt = prompt
          HelpText = None
          Answer = answer
          Selector = selector
          SpecialStates = special
          Required = true
          Tags = [] })

// ---- Section scoring -------------------------------------------------------------------------

/// A section after a question joins it: its score keeps to the scored kinds
/// (an empty list means "every question"), and a section that gains its
/// first scored question is scored with `scorer`.
let afterAdding (scorer: Scoring.Scorer) (question: Question) (before: Section) (after: Section) : Section =
    match before.Scoring with
    | Some sc when sc.Questions.IsEmpty && not (isScored question) ->
        { after with Scoring = (if before.Questions.IsEmpty then None else Some { sc with Questions = before.Questions |> List.map _.Id }) }
    | Some sc when not sc.Questions.IsEmpty && isScored question -> { after with Scoring = Some { sc with Questions = sc.Questions @ [ question.Id ] } }
    | None when isScored question ->
        { after with Scoring = Some { Scorer = scorer; Questions = (if before.Questions.IsEmpty then [] else [ question.Id ]) } }
    | _ -> after

/// A section after a question leaves it: the question leaves its score too.
let afterRemoving (questionId: string) (section: Section) : Section =
    match section.Scoring with
    | Some sc when List.contains questionId sc.Questions ->
        match sc.Questions |> List.filter ((<>) questionId) with
        | [] -> { section with Scoring = None }
        | rest -> { section with Scoring = Some { sc with Questions = rest } }
    | _ -> section

// ---- Conditional questions ------------------------------------------------------------------

/// "Show a question only when an earlier question has an answer" (ACR-001
/// flow): the answer is named by its label, as respondents see it.
let showRule (ruleId: string) (content: Content) (questionId: string) (whenQuestion: string) (answerLabel: string) : Result<FlowRule, string> =
    let ordered = questions content |> List.map snd
    let indexOf id = ordered |> List.tryFindIndex (fun q -> q.Id = id)

    match indexOf questionId, indexOf whenQuestion with
    | None, _ -> Error $"There is no question '{questionId}'."
    | _, None -> Error $"There is no question '{whenQuestion}'."
    | Some shown, Some controlling when controlling >= shown -> Error "The question it depends on must come before it."
    | Some _, Some controlling ->
        let q = ordered[controlling]

        match GenericSession.choices q |> List.tryFind (fun (_, label) -> String.Equals(label, answerLabel.Trim(), StringComparison.OrdinalIgnoreCase)) with
        | None when not (GenericSession.toggles q).IsEmpty -> Error "A condition needs a question with one answer; this one allows several."
        | None ->
            let offered = GenericSession.choices q |> List.map snd |> String.concat ", "
            Error $"'{answerLabel}' is not an answer to {q.Id}. Its answers are: {offered}."
        | Some(Responses.Value value, _) -> Ok { Id = ruleId; When = AnswerIs(q.Id, value); Then = ShowQuestion questionId }
        | Some(Responses.Special special, _) -> Ok { Id = ruleId; When = IsSpecial(q.Id, special); Then = ShowQuestion questionId }

/// How a flow rule reads in the console.
let describeRule (content: Content) (rule: FlowRule) =
    let label questionId state =
        tryQuestion content questionId
        |> Option.bind (fun q -> GenericSession.choices q |> List.tryFind (fst >> (=) state) |> Option.map snd)
        |> Option.defaultValue "?"

    match rule.When, rule.Then with
    | AnswerIs(q, value), ShowQuestion shown -> $"Show {shown} only when {q} is '{label q (Responses.Value value)}'"
    | IsSpecial(q, special), ShowQuestion shown -> $"Show {shown} only when {q} is '{label q (Responses.Special special)}'"
    | _ -> $"Rule {rule.Id}"
