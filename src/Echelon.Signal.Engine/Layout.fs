/// Pagination, the encoding layout and URL capacity of a template
/// (VER-004, CAN-004 §26, AUT-003 §§27-28). Presentation and layout only:
/// pagination never changes the layout or scores.
module Echelon.Signal.Engine.Layout

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template

// ---------------------------------------------------------------------------
// Pagination (VER-004): presentation only; it never affects scoring or the
// encoding layout, which follow template order.
// ---------------------------------------------------------------------------

type Page =
    { Number: int
      /// Question ids on the page, in template order.
      Questions: string list }

/// Pages, in order. A section starts a new page when the survey or the
/// section says so; a page break starts one before its question; otherwise a
/// page fills to the items-per-page of the section that opened it (the
/// section's override, else the survey default, else unlimited).
let pages (content: Content) : Page list =
    let limitOf (section: Section) =
        section.Presentation.ItemsPerPage |> Option.orElse content.Presentation.ItemsPerPage

    // A page under construction: its limit and its question ids, newest first.
    let step (closed: string list list, current: (int option * string list) option) (section: Section, index: int, question: Question) =
        let startsSection = index = 0

        let forcedBreak =
            (startsSection && (content.Presentation.SectionStartsOnNewPage || section.Presentation.StartOnNewPage))
            || List.contains question.Id section.Presentation.PageBreaksBefore

        match current with
        | None -> closed, Some(limitOf section, [ question.Id ])
        | Some(limit, ids) ->
            let full =
                match limit with
                | Some l -> ids.Length >= l
                | None -> false

            if forcedBreak || full then
                List.rev ids :: closed, Some(limitOf section, [ question.Id ])
            else
                closed, Some(limit, question.Id :: ids)

    let items = content.Sections |> List.collect (fun s -> s.Questions |> List.mapi (fun i q -> s, i, q))
    let closed, last = items |> List.fold step ([], None)

    let all =
        match last with
        | Some(_, ids) -> List.rev ids :: closed
        | None -> closed

    all |> List.rev |> List.mapi (fun i ids -> { Number = i + 1; Questions = ids })

// ---------------------------------------------------------------------------
// Encoding layout and URL capacity (CAN-004 §26, AUT-003 §27-28).
// ---------------------------------------------------------------------------

/// One question's fixed-width slot: state 0 is unanswered, then each value,
/// then each offered special state, in `specialStates` order.
type Slot =
    { QuestionId: string
      States: uint64
      Bits: int }

/// Bits for a slot holding `states` states (at least 1).
let bitsFor (states: uint64) =
    let rec go n = if n >= 63 || (1UL <<< n) >= states then n else go (n + 1)
    go 1

let layout (content: Content) : Slot list =
    questions content
    |> List.map (fun (_, q) ->
        let states = 1UL + valueCount q.Answer + uint64 q.SpecialStates.Length
        { QuestionId = q.Id; States = states; Bits = bitsFor states })

/// A question's slot state: 0 for unanswered and for a value or special state
/// the question cannot hold, then 1 + the value's index, then each offered
/// special state. Shared by the envelope and fixture validation.
let slotState (q: Question) (state: AnswerState option) : uint64 =
    match state with
    | None -> 0UL
    | Some(Value v) ->
        match toIndex q.Answer v with
        | Some i -> 1UL + i
        | None -> 0UL
    | Some(Special s) ->
        match q.SpecialStates |> List.tryFindIndex ((=) s) with
        | Some i -> 1UL + valueCount q.Answer + uint64 i
        | None -> 0UL

/// The answer a slot state stands for; an error for a state the question
/// does not offer or a value that breaks the primitive's rules.
let slotAnswer (q: Question) (state: uint64) : Result<AnswerState option, unit> =
    let values = valueCount q.Answer

    if state = 0UL then
        Ok None
    elif state <= values then
        match ofIndex q.Answer (state - 1UL) with
        | Some v when (Primitives.check q.Answer v).IsNone -> Ok(Some(Value v))
        | _ -> Error()
    elif state <= values + uint64 q.SpecialStates.Length then
        Ok(Some(Special q.SpecialStates[int (state - values - 1UL)]))
    else
        Error()

/// The most bytes invitation terms add to a generic link (format 2): flags,
/// a locale of up to 24 characters with its length, a 2-byte expiry day and
/// a 4-byte start minute and a 4-byte submission minute
/// (`GenericEnvelope.TermsMaximumBytes`, which a test holds equal).
[<Literal>]
let TermsOverheadBytes = 1 + 1 + 24 + 2 + 4 + 4

/// Bytes of the largest envelope around the answers: version and binding
/// kind, the 8-byte template reference, the largest binding (two 16-byte
/// ids), the largest invitation terms, the 2-byte item count and the 4-byte
/// integrity check.
[<Literal>]
let EnvelopeOverheadBytes = 2 + 8 + 32 + TermsOverheadBytes + 2 + 4

type Capacity =
    { Questions: int
      AnswerBits: int
      AnswerBytes: int
      EnvelopeBytes: int
      /// Unpadded base64url characters of the largest envelope.
      EncodedCharacters: int }

/// The bytes a template's section, page and question timer starts can add
/// to a link: a count, then 7 bytes per timer (DF-SIGNAL-2026-0015).
let scopedTimerBytes (content: Content) =
    match content.Presentation.Timers |> List.filter (fun t -> t.Scope <> Timers.SurveyScope) with
    | [] -> 0
    | scoped -> 1 + 7 * scoped.Length

let capacity (content: Content) : Capacity =
    let slots = layout content
    let bits = slots |> List.sumBy _.Bits
    let answerBytes = (bits + 7) / 8
    let envelope = answerBytes + EnvelopeOverheadBytes + scopedTimerBytes content

    { Questions = slots.Length
      AnswerBits = bits
      AnswerBytes = answerBytes
      EnvelopeBytes = envelope
      EncodedCharacters = (envelope * 8 + 5) / 6 }
