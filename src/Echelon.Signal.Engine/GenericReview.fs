/// What the survey page shows a person before they submit (VER-004): each
/// question's number when the template numbers them, the progress the
/// template asks for, and, for a template that reviews before submitting,
/// every applicable question with its answer in words.
///
/// Pure.
module Echelon.Signal.Engine.GenericReview

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template

/// An answer as the person chose it, in the words the page offered.
let answerText (q: Question) (answer: AnswerState option) : string =
    match answer with
    | None -> "Not answered"
    | Some state ->
        match GenericChoices.choices q |> List.tryFind (fst >> (=) state) with
        | Some(_, label) -> label
        | None ->
            match state, GenericChoices.toggles q, GenericInputs.numberInputs q, GenericInputs.pairs q with
            | Value(Choices ids), toggles, _, _ when not toggles.IsEmpty ->
                toggles |> List.filter (fst >> ids.Contains) |> List.map snd |> String.concat ", "
            | _, _, inputs, _ when not inputs.IsEmpty ->
                inputs |> List.map (fun i -> $"{i.Label}: {GenericInputs.numberShown q answer i.Id}") |> String.concat "; "
            | _, _, _, pairs when not pairs.IsEmpty ->
                let labelOf side = pairs |> List.tryFind (fun (id, _) -> side id) |> Option.map snd |> Option.defaultValue ""
                let best = labelOf (fun id -> fst (GenericInputs.pairShown answer id))
                let worst = labelOf (fun id -> snd (GenericInputs.pairShown answer id))
                $"Fits best: {best}; fits least: {worst}"
            | _ -> "Answered"

/// Each applicable question's number, in template order, when the template
/// numbers its questions; numbers never skip a question the person sees.
let numbers (content: Content) (applicable: Set<string>) : Map<string, int> =
    if not content.Presentation.ShowQuestionNumbers then
        Map.empty
    else
        content.Sections
        |> List.collect _.Questions
        |> List.filter (fun q -> applicable.Contains q.Id)
        |> List.mapi (fun i q -> q.Id, i + 1)
        |> Map.ofList

/// The progress line the template asks for; "" for none (VER-004).
/// `page` and `pageCount` are the layout page shown and the number of pages.
let progress (content: Content) (applicable: Set<string>) (answered: int) (page: int) (pageCount: int) (onPage: string list) : string =
    match content.Presentation.Progress with
    | NoProgress -> ""
    | QuestionProgress -> $"{answered} of {applicable.Count} answered"
    | PageProgress -> $"Page {page + 1} of {pageCount}"
    | SectionProgress ->
        let sections = content.Sections |> List.filter (fun s -> s.Questions |> List.exists (fun q -> applicable.Contains q.Id))

        match sections |> List.tryFindIndex (fun s -> s.Questions |> List.exists (fun q -> List.contains q.Id onPage)) with
        | Some i -> $"Section {i + 1} of {sections.Length}"
        | None -> ""
