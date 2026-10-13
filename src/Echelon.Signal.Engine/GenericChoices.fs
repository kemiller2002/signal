/// What the survey page shows for a published template's questions, and
/// where the respondent site keeps published templates (split out of
/// `GenericSession`, which re-exports each name): the catalog's files, the
/// answer kinds the page renders, and each question's choices and toggles.
///
/// Pure.
module Echelon.Signal.Engine.GenericChoices

open System
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Template

/// The site folder the published templates are served from.
[<Literal>]
let CatalogFolder = "published-templates"

let referenceHex (reference: byte[]) = Convert.ToHexString(reference).ToLowerInvariant()

/// A published version's file in the catalog, relative to the site root.
let catalogFile (reference: byte[]) = $"{CatalogFolder}/{referenceHex reference}.json"

/// The file the respondent site serves for a published version: its catalog
/// path and its canonical bytes, exactly (the operator commits it unchanged).
let publishedFile (surveyId: string) (version: string) (content: Content) =
    catalogFile (TemplateCanonical.reference surveyId version content), TemplateCanonical.bytes surveyId version content

/// The most ticks a bounded number may have to be shown as choices.
[<Literal>]
let MaximumTicks = 21

/// The answer kinds this page renders: one choice per question (an option
/// tree indented), multi-choices as toggles, a short bounded number as its
/// ticks, and the compound kinds through `GenericInputs`.
let supported (q: Question) =
    match q.Answer with
    | BoundedNumber b -> ticks b > 0 && ticks b <= MaximumTicks
    | _ -> true

let private specialLabel =
    function
    | DontKnow -> "Don't know"
    | NotObserved -> "Not observed"
    | NotApplicable -> "Not applicable"
    | Declined -> "Prefer not to answer"

/// A question's choices, in encoding order: its values, then its special states.
let choices (q: Question) : (AnswerState * string) list =
    let labelAt index fallback =
        q.Selector.Labels |> List.tryItem index |> Option.defaultValue fallback

    let values =
        match q.Answer with
        | Boolean -> [ Value(Flag false), labelAt 0 "No"; Value(Flag true), labelAt 1 "Yes" ]
        | Ordinal points -> [ for p in 0 .. points - 1 -> Value(Point p), labelAt p (string p) ]
        | SingleChoice options -> options |> List.map (fun o -> Value(Choice o.Id), o.Label)
        | Hierarchical(nodes, terminalOnly) ->
            GenericInputs.treeLabels nodes |> List.filter (fun (_, _, leaf) -> leaf || not terminalOnly) |> List.map (fun (id, label, _) -> Value(Choice id), label)
        | BoundedNumber b ->
            [ for t in 0 .. ticks b - 1 -> Value(Tick t), (tickValue b t).ToString($"F{b.Decimals}", Globalization.CultureInfo.InvariantCulture) ]
        // A multi-choice's options are toggles (`toggles`); only its special states are choices.
        | _ -> []

    values @ (q.SpecialStates |> List.map (fun s -> Special s, specialLabel s))

/// A multi-choice question's options, toggled one at a time (SCS-011).
let toggles (q: Question) : (string * string) list =
    match q.Answer with
    | MultiChoice m -> m.Options |> List.map (fun o -> o.Id, o.Label)
    | HierarchicalMulti(nodes, _, _) -> GenericInputs.treeLabels nodes |> List.map (fun (id, label, _) -> id, label)
    | _ -> []
