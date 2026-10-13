/// Pages on the survey page (VER-004, ARX-013, WI-0081): the respondent sees
/// one page of applicable questions at a time and moves with Back and Next.
/// Pages come from the template's pagination (`Layout.pages`) after flow
/// (`Navigation.pages`), so they change as conditional questions apply.
/// Moving obeys the template's navigation policy: Back is offered only when
/// the template allows going back, and never onto locked answers; Next past
/// a locking boundary needs the page's required questions answered first.
///
/// The page shown is in the link as `p` (SIG-LINK-014, DF-SIGNAL-2026-0007),
/// 1-based and left out on the first page. The link names a page; it never
/// grants one: the page is reached by moving there under the same rules, so
/// a link can't skip a required answer or open a locked page. A reopened link
/// first resumes on the last page holding an answer, counted as reached, so
/// reloading doesn't lift a lock that held before.
///
/// Pure.
module Echelon.Signal.Engine.GenericPaging

open System
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

/// The position after moving toward a page, one page at a time under the
/// rules, stopping where a move is refused.
let reach (content: Content) (answers: Answers) (from: Position) (target: int) : Position =
    let rec go (position: Position) =
        let direction =
            if target > position.Page then Some Next
            elif target < position.Page then Some Back
            else None

        match direction |> Option.map (move content answers position) with
        | Some(Ok next) when next.Page <> position.Page -> go next
        | _ -> position

    go { from with Page = clamp (pagesOf content answers) from.Page }

/// The link parameter that names the page.
[<Literal>]
let PageKey = "p"

/// The page a fragment names (0-based), if it names one well: `p` is a
/// whole number from 2, written once; anything else names no page.
let pageIn (hash: string) : int option =
    let body = if hash.StartsWith "#" then hash.Substring 1 else hash

    let values =
        body.Split('&')
        |> Array.choose (fun part ->
            match part.Split('=', 2) with
            | [| key; value |] when key = PageKey -> Some value
            | _ -> None)

    match values with
    | [| value |] when value.Length <= 4 && value |> Seq.forall Char.IsAsciiDigit && not (value.StartsWith "0") ->
        match int value with
        | n when n >= 2 -> Some(n - 1)
        | _ -> None
    | _ -> None

/// The page parameter for a position: empty on the first page.
let pageParameter (position: Position) =
    if position.Page > 0 then $"&{PageKey}={position.Page + 1}" else ""
