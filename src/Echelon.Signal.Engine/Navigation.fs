/// Forward-only progression (ARX-013): legal navigation is derived from the
/// template's revisit policy and how far the respondent has advanced, and
/// enforced here rather than by presentation. Browser back/forward asks the
/// same question (`canNavigateTo`) and is refused the same way.
///
/// Pages hold only applicable questions (pagination happens after flow).
/// Locked answers stay in the response but cannot change; a locking
/// boundary can only be crossed when the page's required applicable
/// questions are answered, so nothing required is silently skipped.
module Echelon.Signal.Engine.Navigation

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template

/// Pages of applicable questions, in order (ARX-013 "a page MUST contain
/// only questions that are selected and applicable").
let pages (content: Content) (applicable: Set<string>) : string list list =
    Layout.pages content
    |> List.map (fun p -> p.Questions |> List.filter applicable.Contains)
    |> List.filter (List.isEmpty >> not)

let private sectionOf (content: Content) (questionId: string) =
    questions content |> List.find (fun (_, q) -> q.Id = questionId) |> fst

/// Questions whose answers can no longer change, given the furthest page
/// index the respondent has advanced to.
let locked (content: Content) (pages: string list list) (furthest: int) : Set<string> =
    let revisit = content.Presentation.Revisit
    let before = pages |> List.indexed |> List.filter (fun (i, _) -> i < furthest) |> List.collect snd

    let byQuestion =
        match revisit.Questions with
        | LockPreviousQuestionsAfterAdvance -> Set.ofList before
        | AllowPreviousQuestions -> Set.empty

    let bySection =
        match revisit.Sections with
        | AllowPreviousSections -> Set.empty
        | LockPreviousSectionsAfterExit ->
            // A section is left once every page holding its questions is behind.
            let lastPage (sectionId: string) =
                pages |> List.indexed |> List.filter (fun (_, qs) -> qs |> List.exists (fun q -> (sectionOf content q).Id = sectionId)) |> List.map fst |> List.max

            before |> List.filter (fun q -> lastPage (sectionOf content q).Id < furthest) |> Set.ofList

    Set.union byQuestion bySection

/// Whether the respondent may move to a page (CanNavigateToSection/Question
/// derived from policy, ARX-013).
let canNavigateTo (content: Content) (pages: string list list) (furthest: int) (target: int) =
    target >= 0
    && target < pages.Length
    && (target >= furthest || pages[target] |> List.forall (fun q -> not ((locked content pages furthest).Contains q)))

/// Whether the respondent may advance past a page: under a locking policy
/// the page's required applicable questions must be answered first.
let canAdvance (content: Content) (pages: string list list) (answers: Answers) (page: int) : Result<unit, string list> =
    let locking = content.Presentation.Revisit <> fullRevisit

    let missing =
        if not locking || page < 0 || page >= pages.Length then
            []
        else
            pages[page]
            |> List.filter (fun id ->
                let section, question = questions content |> List.find (fun (_, q) -> q.Id = id)
                section.Required && question.Required && not (answers.ContainsKey id))

    if missing.IsEmpty then Ok() else Error missing

/// Applies an answer change unless the question is locked.
let change (content: Content) (pages: string list list) (furthest: int) (questionId: string) (state: AnswerState option) (answers: Answers) =
    if (locked content pages furthest).Contains questionId then
        Error $"'{questionId}' is locked"
    else
        match state with
        | Some s -> Ok(answers.Add(questionId, s))
        | None -> Ok(answers.Remove questionId)
