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

/// The catalog and its files (`GenericChoices`).
[<Literal>]
let CatalogFolder = GenericChoices.CatalogFolder

let referenceHex = GenericChoices.referenceHex
let catalogFile = GenericChoices.catalogFile
let publishedFile = GenericChoices.publishedFile

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
    /// The catalog file the link names was looked for on this site and is
    /// not there: searched, not found (ARX-002), never "does not exist".
    | TemplateNotPublished of file: string
    | CatalogUnreachable of reason: string
    /// The file is not a canonical published template: damaged or altered.
    | NotATemplate of reason: string
    /// The file is a template, but not the version the link names.
    | WrongTemplate
    | UnsupportedAnswer of questionId: string
    /// Timers this page does not run yet (section, page or question timers;
    /// DF-SIGNAL-2026-0010): shown without them, the survey would not be the
    /// one published.
    | UnsupportedPresentation of what: string
    /// The invitation's last day has passed (VER-003).
    | InvitationExpired of expiresOn: DateOnly

let describe =
    function
    | NoAnswerLink -> "This page needs a survey link. Open the link you were sent; it ends with #r= and a code."
    | AmbiguousLink -> "This link carries more than one answer code, so Signal cannot tell which one is yours. Open the link you were sent."
    | UnreadableLink error -> describe error
    | TemplateNotPublished _ -> "The survey this link belongs to is not published on this site. Ask the person who sent you the link."
    | CatalogUnreachable reason -> $"The survey could not be loaded ({reason}). Check your connection and reload the page."
    | NotATemplate _ ->
        "The survey file on this site is damaged or not in its published form, so it was not used. Ask the person who sent you the link."
    | WrongTemplate ->
        "The survey file on this site does not match this link (it was changed, or it is a different survey), so it was not used. Ask the person who sent you the link."
    | InvitationExpired day ->
        $"""This invitation has expired. It could be answered until {day.ToString("d MMMM yyyy", Globalization.CultureInfo.InvariantCulture)}. Ask the person who sent it for a new link."""
    | UnsupportedPresentation what ->
        $"This survey uses {what}, which this page cannot run yet. Ask the person who sent you the link."
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
      /// The time when the page last heard from the person (`Ticked`), for
      /// the survey timer (`GenericTimer`).
      Now: DateTimeOffset
      /// The page shown and the furthest reached (`GenericPaging`).
      Position: GenericPaging.Position
      /// Why the last request to submit or move was refused, if it was.
      Refusal: string option
      /// Once a submit or move was refused for missing answers, unanswered
      /// required questions say so until answered (SCS-014).
      ShowMissing: bool
      /// Compound input the link cannot carry yet (WI-0086): not an answer,
      /// not counted, not in the link; the page says so until it is finished.
      Pending: Map<string, GenericInputs.Held> }

[<NoComparison>]
type Session =
    /// Waiting for the template; the page the link names arrives with it.
    | Fetching of reference: byte[] * payload: string * page: int
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
    | Ok reference -> Fetching(reference, (payloadIn hash |> Result.defaultValue ""), (GenericPaging.pageIn hash |> Option.defaultValue 0))

/// The catalog file a session is waiting for, if it is waiting.
let wanted =
    function
    | Fetching(reference, _, _) -> Some(catalogFile reference)
    | Refused _
    | Responding _ -> None

/// The most ticks a bounded number may have to be shown as choices.
[<Literal>]
let MaximumTicks = GenericChoices.MaximumTicks

let supported = GenericChoices.supported

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
            | None when not (GenericTimer.supported decoded.Content) -> Error(UnsupportedPresentation "question timers, or two timers on the same part")
            | None ->
                Ok
                    { SurveyId = decoded.SurveyId
                      Version = decoded.Version
                      Form = form }

/// A response with a start stamped for every timer the page shown opens
/// (DF-SIGNAL-2026-0010, DF-SIGNAL-2026-0015); a submitted one never changes.
let private stamped (r: Response) =
    if r.Phase = Submitted then
        Responding r
    else
        let content = r.Published.Form.Content
        let _, _, shown = GenericPaging.current content r.Answers r.Position
        Responding { r with Terms = GenericTimer.stamp content shown r.Now r.Terms }

/// The fetched catalog file arrives, at an instant: an unsubmitted response
/// to an expired invitation is refused, so it cannot be filled in. A timed
/// survey opened for the first time gets its start minute (DF-SIGNAL-2026-0010).
let receivedAt (now: DateTimeOffset) (fetched: Fetched) (session: Session) : Session =
    let today = DateOnly.FromDateTime now.UtcDateTime

    match session with
    | Fetching(reference, payload, page) ->
        match fetched with
        | NotFound -> Refused(TemplateNotPublished(catalogFile reference))
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
                    // The instance's bank selection (DF-SIGNAL-2026-0009): the
                    // questions its banks leave out don't apply. Its seed comes
                    // from ids that survive submission, so it holds throughout.
                    let published =
                        { published with Form = { published.Form with Content = BankSelection.forBinding envelope.Binding published.Form.Content } }

                    { Published = published
                      Binding = envelope.Binding
                      Answers = envelope.Answers
                      Terms = terms
                      Phase = if Submission.isFinal { Binding = envelope.Binding; Answers = Map.empty } then Submitted else Answering
                      Now = now
                      ShowMissing = false
                      Pending = Map.empty
                      Position = GenericPaging.reach published.Form.Content envelope.Answers (GenericPaging.resume published.Form.Content envelope.Answers) page
                      Refusal = None }
                    |> stamped
    | Refused _
    | Responding _ -> session

/// As `receivedAt`, at the start of a UTC day.
let received (today: DateOnly) (fetched: Fetched) (session: Session) : Session =
    receivedAt (DateTimeOffset(today.ToDateTime TimeOnly.MinValue, TimeSpan.Zero)) fetched session

// ---------------------------------------------------------------------------
// Answering and finalizing.
// ---------------------------------------------------------------------------

let choices = GenericChoices.choices
let toggles = GenericChoices.toggles

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

/// The question and item a compound row key (`<question>-<prefix><item>`)
/// names: `n` a number input, `p` a best-worst option.
let private compoundFor (prefix: string) (items: Question -> string list) (r: Response) (key: string) : (string * string) option =
    match key.Split '-' with
    | [| q; t |] when t.StartsWith prefix ->
        match Int32.TryParse q, Int32.TryParse(t.Substring prefix.Length) with
        | (true, qi), (true, ii) ->
            indexedQuestions r |> List.tryItem qi |> Option.bind (fun (_, question) -> items question |> List.tryItem ii |> Option.map (fun item -> question.Id, item))
        | _ -> None
    | _ -> None

let numberFor = compoundFor "n" (GenericInputs.numberInputs >> List.map _.Id)
let pairFor = compoundFor "p" (GenericInputs.pairs >> List.map fst)

[<NoComparison>]
type Msg =
    | Chose of questionId: string * state: AnswerState
    /// A multi-choice option was selected or cleared.
    | Toggled of questionId: string * optionId: string * selected: bool
    /// A number typed into a compound input (a ranking position, an allocation, a range end).
    | Numbered of questionId: string * inputId: string * text: string
    | Picked of questionId: string * side: GenericInputs.Side * optionId: string
    /// The time now, sent by the application edge before each event.
    | Ticked of now: DateTimeOffset
    /// Back or Next a page.
    | Turned of GenericPaging.Move
    /// The link now names a page (browser Back or Forward, or an edited link).
    | Reached of page: int
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
    | _ when not r.Pending.IsEmpty ->
        refuse $"""{plural r.Pending.Count "answer is" "answers are"} not finished. Finish or clear each answer marked "not in the link yet" before you submit."""
    | Rules.ReadyToSubmit
    | Rules.Terminated _ ->
        match finalOf r r.Binding entropy with
        | Ok binding ->
            { r with
                Binding = binding
                Phase = Submitted
                // A timed response records the minute it was submitted, and
                // nothing else, for intake's timer flags (DF-SIGNAL-2026-0015).
                Terms =
                    if r.Published.Form.Content.Presentation.Timers.IsEmpty then r.Terms
                    else { r.Terms with SubmittedAt = Some(GenericEnvelope.startMinute r.Now) }
                Refusal = None }
        | Error text -> refuse text
    | Rules.NotStarted -> refuse "Answer the questions before you submit."
    | Rules.InProgress missing -> refuse $"""{plural missing "required question still needs" "required questions still need"} an answer."""
    | Rules.Invalid problems -> refuse $"""{plural problems "answer breaks" "answers break"} this survey's rules. Change it before you submit."""

/// Applies a compound input's change: what the link can carry becomes the
/// answer, the rest is held (WI-0086).
let private compound
    (r: Response)
    (questionId: string)
    (change: Question -> AnswerState option -> GenericInputs.Held option -> (AnswerState option * GenericInputs.Held option) option)
    (session: Session)
    =
    match tryQuestion r.Published.Form.Content questionId with
    | Some q ->
        match change q (r.Answers.TryFind questionId) (r.Pending.TryFind questionId) with
        | Some(answer, held) ->
            Responding
                { r with
                    Answers = (match answer with Some a -> r.Answers.Add(questionId, a) | None -> r.Answers.Remove questionId)
                    Pending = (match held with Some h -> r.Pending.Add(questionId, h) | None -> r.Pending.Remove questionId)
                    Refusal = None }
        | None -> session
    | None -> session

/// The question an answer message changes, if it changes one.
let private target =
    function
    | Chose(questionId, _)
    | Toggled(questionId, _, _)
    | Numbered(questionId, _, _)
    | Picked(questionId, _, _) -> Some questionId
    | Turned _
    | Reached _
    | Ticked _
    | SubmitRequested _ -> None

let rec update (msg: Msg) (session: Session) : Session =
    match step msg session with
    | Responding r -> stamped r
    | other -> other

and private step (msg: Msg) (session: Session) : Session =
    match session with
    | Responding r when r.Phase = Submitted -> session
    // A locked answer (ARX-013) cannot change, whatever the page sends.
    | Responding r when target msg |> Option.exists (GenericPaging.locked r.Published.Form.Content r.Answers r.Position).Contains -> session
    // An enforced timer that ran out holds the answers it covers (ARX-013).
    | Responding r when target msg |> Option.exists (GenericTimer.locked r.Published.Form.Content r.Terms r.Now).Contains -> session
    | Responding r ->
        match msg with
        | Chose(questionId, state) when (tryQuestion r.Published.Form.Content questionId |> Option.exists (fun q -> choices q |> List.exists (fst >> (=) state))) ->
            Responding
                { r with
                    Answers = r.Answers.Add(questionId, state)
                    // A chosen answer (such as "Prefer not to answer") replaces what was held.
                    Pending = r.Pending.Remove questionId
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
            | Some(HierarchicalMulti(nodes, _, _)) when nodes |> List.exists (fun n -> n.Option.Id = optionId) ->
                let current =
                    match r.Answers.TryFind questionId with
                    | Some(Value(Choices ids)) -> ids
                    | _ -> Set.empty

                let next = if selected then current.Add optionId else current.Remove optionId
                Responding { r with Answers = (if next.IsEmpty then r.Answers.Remove questionId else r.Answers.Add(questionId, Value(Choices next))); Refusal = None }
            | _ -> session
        | Numbered(questionId, inputId, text) -> compound r questionId (fun q answer held -> GenericInputs.typed q answer held inputId text) session
        | Picked(questionId, side, optionId) -> compound r questionId (fun q answer held -> GenericInputs.pickedHeld q answer held side optionId) session
        | Turned direction ->
            match GenericPaging.move r.Published.Form.Content r.Answers r.Position direction with
            | Ok position -> Responding { r with Position = position; Refusal = None }
            | Error text -> Responding { r with Refusal = Some text; ShowMissing = true }
        | Ticked now -> Responding { r with Now = now }
        | Reached page -> Responding { r with Position = GenericPaging.reach r.Published.Form.Content r.Answers r.Position page; Refusal = None }
        | SubmitRequested entropy ->
            let next = submit r entropy
            Responding { next with ShowMissing = r.ShowMissing || next.Refusal.IsSome }
    | Fetching _
    | Refused _ -> session

/// The fragment, including `#`, that carries a response, and the page shown
/// while it is answered. A submission carries no page: it is the envelope
/// alone, exactly as before.
let fragment (r: Response) =
    let page = if r.Phase = Submitted then "" else GenericPaging.pageParameter r.Position
    $"#{LiveUrl.FragmentKey}={GenericEnvelope.encodeWith r.Published.Form.Content r.Published.Form.Reference r.Terms (envelope r)}{page}"

/// Whether a link is this response's own (the same template and binding),
/// so a change to it is a move between pages, not a different response. An
/// earlier history entry holds earlier answers; the answers held now stand.
let isOwnLink (r: Response) (hash: string) =
    match payloadIn hash with
    | Ok payload ->
        match GenericEnvelope.decodeWithTerms r.Published.Form.Content r.Published.Form.Reference payload with
        | Ok(envelope, _) -> envelope.Binding = r.Binding
        | Error _ -> false
    | Error _ -> false
