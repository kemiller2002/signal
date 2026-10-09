/// The survey page's view of a respondent session (`GenericSession`): one
/// flat list of rows (section headings, questions and their choices), since
/// Limen views hold no nested lists, and the page's phase flags.
///
/// Pure.
module Echelon.Signal.Engine.GenericSessionView

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession

let private text = View.Text
let private flag = View.Flag

let private row (key: string) (kind: string) (fields: (string * View.Scalar) list) =
    [ "id", text key
      "hideSection", flag (kind <> "section")
      "hideQuestion", flag (kind <> "question")
      "hideOption", flag (kind <> "option")
      "hideToggle", flag (kind <> "toggle") ]
    @ fields

/// A question's toggles (multi-choice options) and choices, as rows.
let private choiceRows (qi: int) (q: Question) (answer: AnswerState option) (locked: bool) =
    let selected =
        match answer with
        | Some(Value(Choices ids)) -> ids
        | _ -> Set.empty

    let choice key kind label isChecked =
        row
            key
            kind
            [ "title", text ""
              "prompt", text ""
              "label", text label
              "help", text ""
              "group", text $"q{qi}"
              "checked", flag isChecked
              "locked", flag locked ]

    (toggles q |> List.mapi (fun ti (optionId, label) -> choice $"{qi}-t{ti}" "toggle" label (selected.Contains optionId)))
    @ (choices q |> List.mapi (fun ci (state, label) -> choice $"{qi}-{ci}" "option" label (answer = Some state)))

let private rows (r: Response) =
    let applicable = (Rules.applicability r.Published.Form.Content r.Answers).Questions
    let sealedResponse = r.Phase = Submitted
    let indexed = indexedQuestions r

    r.Published.Form.Content.Sections
    |> List.indexed
    |> List.collect (fun (si, section) ->
        let shown =
            indexed |> List.filter (fun (_, q) -> applicable.Contains q.Id && section.Questions |> List.exists (fun s -> s.Id = q.Id))

        if shown.IsEmpty then
            []
        else
            row $"s{si}" "section" [ "title", text section.Title; "prompt", text ""; "label", text ""; "help", text ""; "group", text ""; "checked", flag false; "locked", flag true ]
            :: (shown
                |> List.collect (fun (qi, q) ->
                    let answer = r.Answers.TryFind q.Id

                    row
                        $"{qi}"
                        "question"
                        [ "title", text ""
                          "prompt", text q.Prompt
                          "label", text ""
                          "help", text (defaultArg q.HelpText "")
                          "group", text $"q{qi}"
                          "checked", flag false
                          "locked", flag true ]
                    :: choiceRows qi q answer sealedResponse)))

/// Which part of the page shows: loading, refused, answering or submitted.
let phaseName =
    function
    | Fetching _ -> "loading"
    | Refused _ -> "refused"
    | Responding r when r.Phase = Submitted -> "submitted"
    | Responding _ -> "answering"

let view (session: Session) : View.View =
    let value = View.Value
    let current = phaseName session

    let title, version, refusal, items, progress, submitRefusal =
        match session with
        | Fetching _ -> "Loading the survey…", "", "", [], "", None
        | Refused reason -> "Survey unavailable", "", describe reason, [], "", None
        | Responding r ->
            let applicable = (Rules.applicability r.Published.Form.Content r.Answers).Questions
            let answered = r.Answers |> Map.filter (fun id _ -> applicable.Contains id) |> Map.count

            r.Published.Form.Content.Metadata.Title,
            $"Version {r.Published.Version}",
            "",
            rows r,
            $"{answered} of {applicable.Count} answered",
            r.Refusal

    [ for name in [ "loading"; "refused"; "answering"; "submitted" ] -> name, value (flag (name = current)) ]
    @ [ "surveyTitle", value (text title)
        "surveyVersion", value (text version)
        "refusal", value (text refusal)
        "rows", View.Items items
        "progress", value (text progress)
        "hasRefusal", value (flag submitRefusal.IsSome)
        "submitRefusal", value (text (defaultArg submitRefusal ""))
        // A test link (AUT-006 §§21, 60) says so on every screen.
        "isTest",
        value (
            flag (
                match session with
                | Responding r -> isTest r.Binding
                | Fetching _
                | Refused _ -> false
            )
        ) ]
