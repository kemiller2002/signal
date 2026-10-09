/// One respondent session for a published generic template, on the survey
/// page (web/survey/, DF-SIGNAL-2026-0005, WI-0078).
///
/// The link carries only the answer envelope (`#r=`): its 8-byte template
/// reference names the published version, and the page reads that version
/// from the site's static catalog (`published-templates/<reference>.json`).
/// What arrives is trusted only after it is verified here: the canonical
/// form (`TemplateDecode.decode`), and a hash that begins with the reference
/// the link names. A missing file, a damaged or altered one, or a different
/// template is refused with the reason, and nothing else is tried in its
/// place.
///
/// Pure: the fetched bytes and the entropy arrive as values; `update` and
/// `view` are total functions of the state.
module Echelon.Signal.Engine.GenericSession

open System
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState

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

/// What fetching the catalog file produced.
type Fetched =
    | Found of bytes: byte[]
    | NotFound
    | Unreachable of reason: string

/// Why the page cannot show a survey. Each is an expected outcome, shown to
/// the respondent; none is guessed around.
type Refusal =
    | NoAnswerLink
    | AmbiguousLink
    | UnreadableLink of DecodeError
    | TemplateNotPublished
    | CatalogUnreachable of reason: string
    /// The file is not a canonical published template: damaged or altered.
    | NotATemplate of reason: string
    /// The file is a template, but not the version the link names.
    | WrongTemplate
    | UnsupportedAnswer of questionId: string
    /// The invitation's last day has passed (VER-003).
    | InvitationExpired of expiresOn: DateOnly

let describe =
    function
    | NoAnswerLink -> "This page needs a survey link. Open the link you were sent; it ends with #r= and a code."
    | AmbiguousLink -> "This link carries more than one answer code, so Signal cannot tell which one is yours. Open the link you were sent."
    | UnreadableLink error -> describe error
    | TemplateNotPublished -> "The survey this link belongs to is not published on this site. Ask the person who sent you the link."
    | CatalogUnreachable reason -> $"The survey could not be loaded ({reason}). Check your connection and reload the page."
    | NotATemplate _ ->
        "The survey file on this site is damaged or not in its published form, so it was not used. Ask the person who sent you the link."
    | WrongTemplate ->
        "The survey file on this site does not match this link (it was changed, or it is a different survey), so it was not used. Ask the person who sent you the link."
    | InvitationExpired day ->
        $"""This invitation has expired. It could be answered until {day.ToString("d MMMM yyyy", Globalization.CultureInfo.InvariantCulture)}. Ask the person who sent it for a new link."""
    | UnsupportedAnswer questionId ->
        $"This survey has a question ({questionId}) of a kind this page cannot show yet. Ask the person who sent you the link."

[<NoComparison>]
type Published =
    { SurveyId: string
      Version: string
      Form: Import.GenericForm }

type Phase =
    | Answering
    /// Finalized into a submission: sealed, read-only.
    | Submitted

[<NoComparison>]
type Response =
    { Published: Published
      Binding: Binding
      Answers: Answers
      /// The invitation's locale and expiry (link format 2), kept as they
      /// arrived and written back unchanged.
      Terms: GenericEnvelope.Terms
      Phase: Phase
      /// Why the last request to submit was refused, if it was.
      Refusal: string option }

[<NoComparison>]
type Session =
    | Fetching of reference: byte[] * payload: string
    | Refused of Refusal
    | Responding of Response

/// The answer code in a fragment (`location.hash`), parsed as `LiveUrl.read`
/// does: other parameters are ignored, and a repeated `r` is refused.
let payloadIn (hash: string) : Result<string, Refusal> =
    let body = if hash.StartsWith "#" then hash.Substring 1 else hash

    let values =
        body.Split('&')
        |> Array.choose (fun part ->
            match part.Split('=', 2) with
            | [| key; value |] when key = LiveUrl.FragmentKey -> Some value
            | _ -> None)

    match values with
    | [||] -> Error NoAnswerLink
    | [| value |] -> Ok value
    | _ -> Error AmbiguousLink

/// A session for the link in a fragment: waiting for its template, or refused.
let start (hash: string) : Session =
    match payloadIn hash |> Result.bind (GenericEnvelope.referenceIn >> Result.mapError UnreadableLink) with
    | Error refusal -> Refused refusal
    | Ok reference -> Fetching(reference, (payloadIn hash |> Result.defaultValue ""))

/// The catalog file a session is waiting for, if it is waiting.
let wanted =
    function
    | Fetching(reference, _) -> Some(catalogFile reference)
    | Refused _
    | Responding _ -> None

/// The most ticks a bounded number may have to be shown as choices.
[<Literal>]
let MaximumTicks = 21

/// The answer kinds this page renders: one choice per question, a
/// multi-choice as toggles, and a short bounded number as its ticks.
let supported (q: Question) =
    match q.Answer with
    | Boolean
    | Ordinal _
    | SingleChoice _
    | MultiChoice _ -> true
    | BoundedNumber b -> ticks b > 0 && ticks b <= MaximumTicks
    | _ -> false

/// Verifies fetched bytes against the reference the link names.
let verify (reference: byte[]) (bytes: byte[]) : Result<Published, Refusal> =
    match TemplateDecode.decode bytes with
    | Error reason -> Error(NotATemplate reason)
    | Ok decoded ->
        let form = GenericImport.formOf decoded.SurveyId decoded.Version decoded.Content

        if form.Reference <> reference then
            Error WrongTemplate
        else
            match questions decoded.Content |> List.map snd |> List.tryFind (supported >> not) with
            | Some q -> Error(UnsupportedAnswer q.Id)
            | None ->
                Ok
                    { SurveyId = decoded.SurveyId
                      Version = decoded.Version
                      Form = form }

/// The fetched catalog file arrives, on a day: an unsubmitted response to an
/// expired invitation is refused, so it cannot be filled in.
let received (today: DateOnly) (fetched: Fetched) (session: Session) : Session =
    match session with
    | Fetching(reference, payload) ->
        match fetched with
        | NotFound -> Refused TemplateNotPublished
        | Unreachable reason -> Refused(CatalogUnreachable reason)
        | Found bytes ->
            match verify reference bytes with
            | Error refusal -> Refused refusal
            | Ok published ->
                match GenericEnvelope.decodeWithTerms published.Form.Content published.Form.Reference payload with
                | Error error -> Refused(UnreadableLink error)
                | Ok(envelope, { ExpiresOn = Some day }) when today > day && not (Submission.isFinal { Binding = envelope.Binding; Answers = Map.empty }) ->
                    Refused(InvitationExpired day)
                | Ok(envelope, terms) ->
                    Responding
                        { Published = published
                          Binding = envelope.Binding
                          Answers = envelope.Answers
                          Terms = terms
                          Phase = if Submission.isFinal { Binding = envelope.Binding; Answers = Map.empty } then Submitted else Answering
                          Refusal = None }
    | Refused _
    | Responding _ -> session

// ---------------------------------------------------------------------------
// Answering and finalizing.
// ---------------------------------------------------------------------------

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
        | BoundedNumber b ->
            [ for t in 0 .. ticks b - 1 -> Value(Tick t), (tickValue b t).ToString($"F{b.Decimals}", Globalization.CultureInfo.InvariantCulture) ]
        // A multi-choice's options are toggles (`toggles`); only its special states are choices.
        | _ -> []

    values @ (q.SpecialStates |> List.map (fun s -> Special s, specialLabel s))

/// A multi-choice question's options, toggled one at a time (SCS-011).
let toggles (q: Question) : (string * string) list =
    match q.Answer with
    | MultiChoice m -> m.Options |> List.map (fun o -> o.Id, o.Label)
    | _ -> []

let indexedQuestions (r: Response) =
    questions r.Published.Form.Content |> List.map snd |> List.indexed

/// The choice a row key names (`<question>-<choice>`), if it names one.
let choiceFor (r: Response) (key: string) : (string * AnswerState) option =
    match key.Split '-' with
    | [| q; c |] ->
        match Int32.TryParse q, Int32.TryParse c with
        | (true, qi), (true, ci) ->
            indexedQuestions r
            |> List.tryItem qi
            |> Option.bind (fun (_, question) -> choices question |> List.tryItem ci |> Option.map (fun (state, _) -> question.Id, state))
        | _ -> None
    | _ -> None

/// The option a toggle row key (`<question>-t<option>`) names, if it names one.
let toggleFor (r: Response) (key: string) : (string * string) option =
    match key.Split '-' with
    | [| q; t |] when t.StartsWith "t" ->
        match Int32.TryParse q, Int32.TryParse(t.Substring 1) with
        | (true, qi), (true, ti) ->
            indexedQuestions r
            |> List.tryItem qi
            |> Option.bind (fun (_, question) -> toggles question |> List.tryItem ti |> Option.map (fun (optionId, _) -> question.Id, optionId))
        | _ -> None
    | _ -> None

[<NoComparison>]
type Msg =
    | Chose of questionId: string * state: AnswerState
    /// A multi-choice option was selected or cleared.
    | Toggled of questionId: string * optionId: string * selected: bool
    /// Finalize. The bytes are fresh secure entropy from the application
    /// edge; only an anonymous invitation uses them.
    | SubmitRequested of entropy: byte[]

let envelope (r: Response) : GenericEnvelope.Envelope = { Binding = r.Binding; Answers = r.Answers }

let private result (r: Response) =
    SurveyResult.compute r.Published.Form.Hash r.Published.Form.Content r.Answers true

let private contains (haystack: byte[]) (needle: byte[]) =
    needle.Length > 0 && Seq.windowed needle.Length haystack |> Seq.exists (fun window -> window = needle)

/// The submission binding (LURL-002): an identified invitation keeps its
/// ids; an anonymous one gets a fresh unlinkable id and loses its instance.
/// A test artifact finalizes as the binding it stands in for, and stays a
/// test artifact (AUT-006 §60).
let rec private finalOf (r: Response) (binding: Binding) (entropy: byte[]) : Result<Binding, string> =
    match binding with
    | Test inner -> finalOf r inner entropy |> Result.map asTest
    | IdentifiedInvitation(instance, group) -> Ok(Identified(instance, group))
    | AnonymousInvitation(instance, group) ->
        match OpaqueId.ofBytes entropy with
        | Some fresh when fresh <> instance && fresh <> group ->
            let encoded =
                Buffers.Text.Base64Url.DecodeFromChars(
                    (GenericEnvelope.encodeWith r.Published.Form.Content r.Published.Form.Reference r.Terms { envelope r with Binding = Anonymous(fresh, group) })
                        .AsSpan()
                )

            if contains encoded (OpaqueId.toBytes instance) then
                Error "The submission could not be sealed safely. Try again."
            else
                Ok(Anonymous(fresh, group))
        | _ -> Error "The submission could not be sealed safely. Try again."
    | Unbound -> Error "This link is not an invitation, so no group can receive a submission from it."
    | Identified _
    | Anonymous _ -> Error "This response is already submitted."

let private plural count singular pluralForm =
    if count = 1 then $"{count} {singular}" else $"{count} {pluralForm}"

let private submit (r: Response) (entropy: byte[]) =
    let refuse text = { r with Refusal = Some text }

    match (result r).Evaluation.Completion with
    | Rules.ReadyToSubmit
    | Rules.Terminated _ ->
        match finalOf r r.Binding entropy with
        | Ok binding ->
            { r with
                Binding = binding
                Phase = Submitted
                Refusal = None }
        | Error text -> refuse text
    | Rules.NotStarted -> refuse "Answer the questions before you submit."
    | Rules.InProgress missing -> refuse $"""{plural missing "required question still needs" "required questions still need"} an answer."""
    | Rules.Invalid problems -> refuse $"""{plural problems "answer breaks" "answers break"} this survey's rules. Change it before you submit."""

let update (msg: Msg) (session: Session) : Session =
    match session with
    | Responding r when r.Phase = Submitted -> session
    | Responding r ->
        match msg with
        | Chose(questionId, state) when (tryQuestion r.Published.Form.Content questionId |> Option.exists (fun q -> choices q |> List.exists (fst >> (=) state))) ->
            Responding
                { r with
                    Answers = r.Answers.Add(questionId, state)
                    Refusal = None }
        | Chose _ -> session
        | Toggled(questionId, optionId, selected) ->
            match tryQuestion r.Published.Form.Content questionId |> Option.map _.Answer with
            | Some(MultiChoice m) when m.Options |> List.exists (fun o -> o.Id = optionId) ->
                let current =
                    match r.Answers.TryFind questionId with
                    | Some(Value(Choices ids)) -> ids
                    | _ -> Set.empty

                if current.Contains optionId = selected then
                    session
                else
                    let next = select m current optionId

                    Responding
                        { r with
                            Answers = (if next.IsEmpty then r.Answers.Remove questionId else r.Answers.Add(questionId, Value(Choices next)))
                            Refusal = None }
            | _ -> session
        | SubmitRequested entropy -> Responding(submit r entropy)
    | Fetching _
    | Refused _ -> session

/// The fragment, including `#`, that carries a response.
let fragment (r: Response) =
    $"#{LiveUrl.FragmentKey}={GenericEnvelope.encodeWith r.Published.Form.Content r.Published.Form.Reference r.Terms (envelope r)}"
