/// The survey page's view of a respondent session (`GenericSession`): one
/// flat list of rows (section headings, questions and their choices), since
/// Limen views hold no nested lists, and the page's phase flags.
///
/// Pure.
module Echelon.Signal.Engine.GenericSessionView

open System
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession

let private text = View.Text
let private flag = View.Flag

/// Every row carries every field the page's row template binds; a row's
/// kind decides which of its parts show.
let private kinds = [ "section"; "question"; "option"; "toggle"; "number"; "pair" ]

let private defaults =
    [ "title", text ""
      "prompt", text ""
      "label", text ""
      "help", text ""
      "group", text ""
      "checked", flag false
      "locked", flag true
      "numberValue", text ""
      "numberMin", text ""
      "numberMax", text ""
      "numberStep", text ""
      "groupBest", text ""
      "groupWorst", text ""
      "bestChecked", flag false
      "worstChecked", flag false
      // Accessibility and handoff (SCS-014, SCS-018, DF-SIGNAL-2026-0013).
      "promptId", text ""
      "errorId", text ""
      "describedBy", text ""
      "required", text "false"
      "invalid", text "false"
      "hideError", flag true
      "error", text ""
      "optionalMark", text ""
      "component", text ""
      "variant", text ""
      // Template accessibility metadata (CAN-001 §7, WI-0091).
      "promptHidden", text "false"
      "accessibleName", text ""
      "hideAccessibleName", flag true
      "accessibleDescription", text ""
      "descriptionId", text ""
      "hideAccessibleDescription", flag true ]

let private row (key: string) (kind: string) (fields: (string * View.Scalar) list) =
    let set = Map.ofList fields

    [ "id", text key ]
    @ (kinds |> List.map (fun k -> "hide" + string (Char.ToUpperInvariant k[0]) + k.Substring 1, flag (k <> kind)))
    @ (defaults |> List.map (fun (name, value) -> name, set |> Map.tryFind name |> Option.defaultValue value))

/// A question's answer rows: toggles (multi-choice and tree options),
/// choices, number inputs and best-worst pairs.
/// Whether a question must be answered: it and its section are required.
let private isRequired (content: Content) (q: Question) =
    q.Required && (content.Sections |> List.exists (fun s -> s.Required && s.Questions |> List.exists (fun x -> x.Id = q.Id)))

/// Every input names its question (and its error, when it has one) as its
/// description, and states whether it is required and invalid (SCS-014).
let private choiceRows (qi: int) (q: Question) (answer: AnswerState option) (held: GenericInputs.Held option) (locked: bool) (required: bool) (invalid: bool) =
    let selected =
        match answer with
        | Some(Value(Choices ids)) -> ids
        | _ -> Set.empty

    // The template's accessible description (CAN-001 §7) is read after the prompt.
    let description = if q.Selector.Accessibility.Description.IsSome then $" q{qi}-description" else ""
    let describedBy = (if invalid || held.IsSome then $"q{qi}-prompt q{qi}-error" else $"q{qi}-prompt") + description

    let common label =
        [ "label", text label
          "group", text $"q{qi}"
          "locked", flag locked
          "describedBy", text describedBy
          "required", text (if required then "true" else "false")
          "invalid", text (if invalid then "true" else "false") ]

    (toggles q |> List.mapi (fun ti (optionId, label) -> row $"{qi}-t{ti}" "toggle" (common label @ [ "checked", flag (selected.Contains optionId) ])))
    @ (GenericInputs.numberInputs q
       |> List.mapi (fun ni input ->
           row
               $"{qi}-n{ni}"
               "number"
               (common input.Label
                @ [ "numberValue", text (GenericInputs.shownWith q answer held input.Id)
                    "numberMin", text input.Minimum
                    "numberMax", text input.Maximum
                    "numberStep", text input.Step ])))
    @ (GenericInputs.pairs q
       |> List.mapi (fun pi (optionId, label) ->
           let isBest, isWorst = GenericInputs.pairShown (GenericInputs.working answer held) optionId
           row $"{qi}-p{pi}" "pair" (common label @ [ "groupBest", text $"q{qi}-best"; "groupWorst", text $"q{qi}-worst"; "bestChecked", flag isBest; "worstChecked", flag isWorst ])))
    @ (choices q |> List.mapi (fun ci (state, label) -> row $"{qi}-{ci}" "option" (common label @ [ "checked", flag (answer = Some state) ])))

/// The rows of the page shown: its section headings and questions.
let private rows (r: Response) =
    let content = r.Published.Form.Content
    let _, _, onPage = GenericPaging.current content r.Answers r.Position
    let applicable = Set.ofList onPage
    let locked = GenericPaging.locked content r.Answers r.Position
    let sealedResponse = r.Phase = Submitted
    let locked = Set.union locked (GenericTimer.locked content r.Terms r.Now)
    let indexed = indexedQuestions r
    // Text in the link's locale when the template translates it (WI-0091):
    // display only; every rule above reads the published content.
    let shownContent = Localization.display r.Terms.Locale content
    let displayed = questions shownContent |> List.map (fun (_, q) -> q.Id, q) |> Map.ofList
    let sectionTitle (si: int) = shownContent.Sections[si].Title

    r.Published.Form.Content.Sections
    |> List.indexed
    |> List.collect (fun (si, section) ->
        let shown =
            indexed |> List.filter (fun (_, q) -> applicable.Contains q.Id && section.Questions |> List.exists (fun s -> s.Id = q.Id))

        if shown.IsEmpty then
            []
        else
            row $"s{si}" "section" [ "title", text (sectionTitle si) ]
            :: (shown
                |> List.collect (fun (qi, q) ->
                    let shownQuestion = displayed[q.Id]
                    let a11y = shownQuestion.Selector.Accessibility
                    let help = [ defaultArg shownQuestion.HelpText ""; GenericInputs.hint q ] |> List.filter ((<>) "") |> String.concat " "
                    let required = isRequired content q
                    // After a refused submit or move, an unanswered required question says so.
                    let invalid = r.ShowMissing && required && r.Phase = Answering && not (r.Answers.ContainsKey q.Id)
                    let family, variant = Selectors.componentOf q.Selector.Preset
                    // Input the link cannot carry yet says so (WI-0086).
                    let held = if r.Phase = Answering then r.Pending.TryFind q.Id else None

                    let error =
                        if invalid then "This question needs an answer."
                        elif held.IsSome then "Not in the link yet: finish this answer, or clear it, to keep it."
                        else ""

                    row
                        $"{qi}"
                        "question"
                        [ "prompt", text shownQuestion.Prompt
                          // An accessible name replaces the prompt for
                          // assistive technology; the prompt stays visible.
                          "promptHidden", text (if a11y.Name.IsSome then "true" else "false")
                          "accessibleName", text (defaultArg a11y.Name "")
                          "hideAccessibleName", flag a11y.Name.IsNone
                          "accessibleDescription", text (defaultArg a11y.Description "")
                          "descriptionId", text $"q{qi}-description"
                          "hideAccessibleDescription", flag a11y.Description.IsNone
                          "help", text help
                          "group", text $"q{qi}"
                          "promptId", text $"q{qi}-prompt"
                          "errorId", text $"q{qi}-error"
                          "hideError", flag (error = "")
                          "error", text error
                          "optionalMark", text (if required then "" else "(optional)")
                          "component", text family
                          "variant", text variant ]
                    :: choiceRows qi shownQuestion (r.Answers.TryFind q.Id) held (sealedResponse || locked.Contains q.Id) required invalid)))

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

    let timerNotice =
        match session with
        | Responding r when r.Phase = Answering ->
            let _, _, shown = GenericPaging.current r.Published.Form.Content r.Answers r.Position
            GenericTimer.notice r.Published.Form.Content r.Terms r.Now shown
        | _ -> ""

    // Page n of total, and whether Back is offered.
    let page, pageCount, canBack =
        match session with
        | Responding r ->
            let page, count, _ = GenericPaging.current r.Published.Form.Content r.Answers r.Position
            page, count, GenericPaging.canGoBack r.Published.Form.Content r.Answers r.Position
        | Fetching _
        | Refused _ -> 0, 1, false

    let title, version, refusal, items, progress, submitRefusal =
        match session with
        | Fetching _ -> "Loading the survey…", "", "", [], "", None
        | Refused reason -> "Survey unavailable", "", describe reason, [], "", None
        | Responding r ->
            let applicable = (Rules.applicability r.Published.Form.Content r.Answers).Questions
            let answered = r.Answers |> Map.filter (fun id _ -> applicable.Contains id) |> Map.count

            (Localization.display r.Terms.Locale r.Published.Form.Content).Metadata.Title,
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
        "hasTimer", value (flag (timerNotice <> ""))
        "timerNotice", value (text timerNotice)
        "hasPages", value (flag (pageCount > 1))
        "pageLabel", value (text $"Page {page + 1} of {pageCount}")
        "canBack", value (flag canBack)
        "hasNext", value (flag (page + 1 < pageCount))
        "isLastPage", value (flag (page + 1 >= pageCount))
        "hasRefusal", value (flag submitRefusal.IsSome)
        "submitRefusal", value (text (defaultArg submitRefusal ""))
        // The invitation's locale is presentation only (VER-003): the page's language.
        "lang",
        value (
            text (
                match session with
                // The translation shown names the page's language; without
                // one, the link's locale as before (VER-003).
                | Responding r ->
                    match Localization.pick r.Terms.Locale r.Published.Form.Content with
                    | Some l -> l.Locale
                    | None -> r.Terms.Locale |> Option.defaultValue "en"
                | Fetching _
                | Refused _ -> "en"
            )
        )
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
