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

/// The most points Forma's ordinal scale lays out (`.forma-ordinal-scale--11`).
[<Literal>]
let ScaleSlots = 11

/// The special answers a question may offer (Don't know, Not observed, Not
/// applicable, Prefer not to answer).
[<Literal>]
let SpecialSlots = 4

/// The point counts Forma's ordinal scale has a layout for; a scale of any
/// other size keeps the list of choices.
let private scaleSizes = set [ 3; 4; 5; 6; 7; 10; 11 ]

/// A question's points, when the page shows it as Forma's ordinal scale.
let scalePoints (q: Question) =
    match q.Answer with
    | Primitives.Ordinal points when scaleSizes.Contains points && q.SpecialStates.Length <= SpecialSlots -> Some points
    | _ -> None

/// Every row carries every field the page's row template binds; a row's
/// kind decides which of its parts show.
let private kinds = [ "section"; "question"; "option"; "toggle"; "number"; "pair"; "review" ]

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
      // The question's number, and its answer in words on the review (VER-004).
      "questionNumber", text ""
      "hideQuestionNumber", flag true
      "answerText", text ""
      // Where a multi-choice selection stands (SCS-011).
      "selectionStatus", text ""
      "hideSelectionStatus", flag true
      "component", text ""
      "variant", text ""
      // Template accessibility metadata (CAN-001 §7, WI-0091).
      "promptHidden", text "false"
      "accessibleName", text ""
      "hideAccessibleName", flag true
      "accessibleDescription", text ""
      "descriptionId", text ""
      "hideAccessibleDescription", flag true
      // A row the page does not show: a scale's choice, shown on its
      // question's row instead.
      "hideRow", flag false
      // The first heading of the page's card: where focus moves on a new page.
      "focusTarget", text ""
      // A scale question as Forma's ordinal scale (SIG-ECHELON-APP-002):
      // its points, and its special answers after them.
      "hideScale", flag true
      "scaleClass", text ""
      "hideSpecials", flag true
      yield!
          [ for n in 0 .. ScaleSlots - 1 do
                yield $"s{n}Label", text ""
                yield $"s{n}Marker", text ""
                yield $"s{n}Checked", flag false
                yield $"hideS{n}", flag true ]
      yield!
          [ for j in 0 .. SpecialSlots - 1 do
                yield $"x{j}Label", text ""
                yield $"x{j}Value", text ""
                yield $"x{j}Checked", flag false
                yield $"hideX{j}", flag true ] ]

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
    // A selection the count refuses is held, and still shown as chosen.
    let selected = GenericInputs.selectedWith answer held

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
    @ (choices q
       |> List.mapi (fun ci (state, label) ->
           // A scale's choices are on its question's row (`scaleFields`).
           row $"{qi}-{ci}" "option" (common label @ [ "checked", flag (answer = Some state); "hideRow", flag (scalePoints q).IsSome ])))

/// A point's marker: its position on the scale, or its label when the label
/// is itself a number (a 0 to 10 rating), so the two never disagree.
let private marker (n: int) (label: string) =
    match Int32.TryParse label with
    | true, _ -> label
    | _ -> string (n + 1)

/// A scale question's row as Forma's ordinal scale: one slot per point,
/// numbered, then its special answers, all one radio group. Each input's
/// value is its choice's index, so the page sends the question and the index.
let private scaleFields (q: Question) (answer: AnswerState option) =
    match scalePoints q with
    | None -> []
    | Some points ->
        let options = choices q
        let specials = options |> List.skip points

        [ "hideScale", flag false
          "scaleClass", text $"forma-ordinal-scale forma-ordinal-scale--{points}"
          "hideSpecials", flag specials.IsEmpty ]
        @ (options
           |> List.take points
           |> List.mapi (fun n (state, label) ->
               [ $"s{n}Label", text label
                 $"s{n}Marker", text (marker n label)
                 $"s{n}Checked", flag (answer = Some state)
                 $"hideS{n}", flag false ])
           |> List.concat)
        @ (specials
           |> List.mapi (fun j (state, label) ->
               [ $"x{j}Label", text label
                 $"x{j}Value", text (string (points + j))
                 $"x{j}Checked", flag (answer = Some state)
                 $"hideX{j}", flag false ])
           |> List.concat)

/// A scale's inputs are described as every other choice is: by the
/// question's prompt (also the group's name), its error when it shows one,
/// and the template's description (SCS-014).
let private scaleDescription (qi: int) (hasDescription: bool) (showsError: bool) =
    (if showsError then $"q{qi}-prompt q{qi}-error" else $"q{qi}-prompt")
    + (if hasDescription then $" q{qi}-description" else "")

/// The page's first row (its first section heading) is where focus moves on
/// a new page (SIG-ECHELON-APP-002 (d)).
let private startingAt (rows: (string * View.Scalar) list list) =
    match rows with
    | first :: rest -> (first |> List.map (fun (name, value) -> if name = "focusTarget" then name, text "page-start" else name, value)) :: rest
    | [] -> []

/// The rows of the page shown: its section headings and questions.
let private pageRows (r: Response) =
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
    let allApplicable = (Rules.applicability content r.Answers).Questions
    let numbers = GenericReview.numbers content allApplicable
    let numbered (q: Question) =
        match numbers.TryFind q.Id with
        | Some n -> [ "questionNumber", text $"{n}."; "hideQuestionNumber", flag false ]
        | None -> []

    // The review (VER-004): every applicable question with its answer, read only.
    let reviewRows () =
        content.Sections
        |> List.indexed
        |> List.collect (fun (si, section) ->
            let shown = indexed |> List.filter (fun (_, q) -> allApplicable.Contains q.Id && section.Questions |> List.exists (fun s -> s.Id = q.Id))

            if shown.IsEmpty then
                []
            else
                row $"s{si}" "section" [ "title", text (sectionTitle si) ]
                :: (shown
                    |> List.map (fun (qi, q) ->
                        let shownQuestion = displayed[q.Id]
                        row $"{qi}-review" "review" ([ "prompt", text shownQuestion.Prompt; "answerText", text (GenericReview.answerText shownQuestion (r.Answers.TryFind q.Id)) ] @ numbered q))))

    if r.Reviewing then reviewRows () else

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

                    let selectionStatus = SelectionGuidance.status q.Answer (GenericInputs.selectedWith (r.Answers.TryFind q.Id) held)

                    let error =
                        if invalid then "This question needs an answer."
                        elif held.IsSome && selectionStatus <> "" then "Not in the link yet: change the selection to fit, or clear it, to keep it."
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
                          "selectionStatus", text selectionStatus
                          "hideSelectionStatus", flag (selectionStatus = "")
                          "component", text family
                          "variant", text variant
                          // The scale's inputs, on this row (`scaleFields`).
                          "locked", flag (sealedResponse || locked.Contains q.Id)
                          "required", text (if required then "true" else "false")
                          "invalid", text (if invalid then "true" else "false")
                          "describedBy", text (scaleDescription qi a11y.Description.IsSome (invalid || held.IsSome))
                          yield! scaleFields shownQuestion (r.Answers.TryFind q.Id)
                          yield! numbered q ]
                    :: choiceRows qi shownQuestion (r.Answers.TryFind q.Id) held (sealedResponse || locked.Contains q.Id) required invalid)))

/// The page's rows, its first heading marked as where focus moves.
let private rows (r: Response) = startingAt (pageRows r)

/// Where a question's multi-choice selection stands (SCS-011), by its index
/// in the survey: what #activity announces after the respondent changes it.
let selectionStatus (session: Session) (qi: int) =
    match session with
    | Responding r when r.Phase = Answering ->
        indexedQuestions r
        |> List.tryItem qi
        |> Option.map (fun (_, q) -> SelectionGuidance.status q.Answer (GenericInputs.selectedWith (r.Answers.TryFind q.Id) (r.Pending.TryFind q.Id)))
        |> Option.defaultValue ""
    | _ -> ""

/// The section of the page shown, when all its questions are in one (in the
/// link's locale): the progress line names it beside the page.
let private pageSection (r: Response) =
    let content = r.Published.Form.Content
    let _, _, onPage = GenericPaging.current content r.Answers r.Position
    let shown = (Localization.display r.Terms.Locale content).Sections

    match
        content.Sections
        |> List.indexed
        |> List.filter (fun (_, section) -> section.Questions |> List.exists (fun q -> List.contains q.Id onPage))
    with
    | [ si, _ ] -> shown[si].Title
    | _ -> ""

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

    let reviewing, reviewsFirst =
        match session with
        | Responding r -> r.Reviewing, GenericSession.reviewsFirst r
        | Fetching _
        | Refused _ -> false, false

    // Page n of total, and whether Back is offered.
    let page, pageCount, canBack =
        match session with
        | Responding r ->
            let page, count, _ = GenericPaging.current r.Published.Form.Content r.Answers r.Position
            page, count, GenericPaging.canGoBack r.Published.Form.Content r.Answers r.Position
        | Fetching _
        | Refused _ -> 0, 1, false

    let section =
        match session with
        | Responding r when not r.Reviewing -> pageSection r
        | _ -> ""

    let title, version, refusal, items, progress, submitRefusal =
        match session with
        | Fetching _ -> "Loading the survey…", "", "", [], "", None
        | Refused reason -> "Survey unavailable", "", describe reason, [], "", None
        | Responding r ->
            let applicable = (Rules.applicability r.Published.Form.Content r.Answers).Questions
            let answered = r.Answers |> Map.filter (fun id _ -> applicable.Contains id) |> Map.count
            let page, count, onPage = GenericPaging.current r.Published.Form.Content r.Answers r.Position

            (Localization.display r.Terms.Locale r.Published.Form.Content).Metadata.Title,
            $"Version {r.Published.Version}",
            "",
            rows r,
            GenericReview.progress r.Published.Form.Content applicable answered page count onPage,
            r.Refusal

    [ for name in [ "loading"; "refused"; "answering"; "submitted" ] -> name, value (flag (name = current)) ]
    @ [ "surveyTitle", value (text title)
        "surveyVersion", value (text version)
        "refusal", value (text refusal)
        "rows", View.Items items
        "progress", value (text progress)
        "hasProgress", value (flag (progress <> ""))
        // The review before submitting (VER-004).
        "reviewing", value (flag reviewing)
        "submitLabel", value (text (if reviewsFirst && not reviewing then "Review answers" else "Submit answers"))
        "hasTimer", value (flag (timerNotice <> ""))
        "timerNotice", value (text timerNotice)
        "hasPages", value (flag (pageCount > 1))
        "pageLabel", value (text $"Page {page + 1} of {pageCount}")
        "pageSection", value (text section)
        "hasPageSection", value (flag (pageCount > 1 && section <> ""))
        "canBack", value (flag (canBack && not reviewing))
        "hasNext", value (flag (page + 1 < pageCount && not reviewing))
        "isLastPage", value (flag (page + 1 >= pageCount || reviewing))
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
