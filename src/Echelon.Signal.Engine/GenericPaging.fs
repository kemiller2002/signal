/// Pages on the survey page (VER-004, ARX-013, WI-0081): the respondent sees
/// one page of applicable questions at a time and moves with Back and Next.
/// Pages come from the template's pagination (`Layout.pages`) after flow
/// (`Navigation.pages`), so they change as conditional questions apply.
/// Moving obeys the template's navigation policy: Back is offered only when
/// the template allows going back, and never onto locked answers; Next past
/// a locking boundary needs the page's required questions answered first.
///
/// The position is not in the link. A reopened link resumes on the last page
/// holding an answer, and counts as having reached it, so a lock that held
/// before is not lifted by reloading.
///
/// Pure.
module Echelon.Signal.Engine.GenericPaging

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template

/// Where the respondent is: the page shown, and the furthest page reached.
type Position = { Page: int; Furthest: int }

type Move =
    | Next
    | Back

/// The pages of applicable question ids, for the answers held now.
let pagesOf (content: Content) (answers: Answers) : string list list =
    Navigation.pages content (Rules.applicability content answers).Questions

/// A page index kept within the pages there are now.
let clamp (pages: string list list) (page: int) = max 0 (min page (pages.Length - 1))

/// Where a reopened link resumes: the last page holding an answer.
let resume (content: Content) (answers: Answers) : Position =
    let page = pagesOf content answers |> List.tryFindIndexBack (List.exists answers.ContainsKey) |> Option.defaultValue 0
    { Page = page; Furthest = page }

/// The page shown and the question ids on it.
let current (content: Content) (answers: Answers) (position: Position) =
    let pages = pagesOf content answers
    let page = clamp pages position.Page
    page, pages.Length, (pages |> List.tryItem page |> Option.defaultValue [])

/// Questions whose answers can no longer change.
let locked (content: Content) (answers: Answers) (position: Position) : Set<string> =
    Navigation.locked content (pagesOf content answers) position.Furthest

/// Whether Back is offered at all.
let canGoBack (content: Content) (answers: Answers) (position: Position) =
    let pages = pagesOf content answers
    let page = clamp pages position.Page
    content.Presentation.AllowBackNavigation && Navigation.canNavigateTo content pages position.Furthest (page - 1)

let private plural count singular pluralForm =
    if count = 1 then $"{count} {singular}" else $"{count} {pluralForm}"

/// The position after a move, or why the move is refused.
let move (content: Content) (answers: Answers) (position: Position) (direction: Move) : Result<Position, string> =
    let pages = pagesOf content answers
    let page = clamp pages position.Page

    match direction with
    | Next when page + 1 >= pages.Length -> Error "This is the last page. Submit your answers when you are ready."
    | Next ->
        match Navigation.canAdvance content pages answers page with
        | Ok() ->
            Ok
                { Page = page + 1
                  Furthest = max position.Furthest (page + 1) }
        | Error missing ->
            Error $"""{plural missing.Length "required question on this page needs" "required questions on this page need"} an answer before you go on, because earlier answers cannot be changed after you leave."""
    | Back when canGoBack content answers position -> Ok { position with Page = page - 1 }
    | Back -> Error "This survey does not let you go back to earlier answers."
