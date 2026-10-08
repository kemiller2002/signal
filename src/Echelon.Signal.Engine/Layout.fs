/// Pagination, the encoding layout and URL capacity of a template
/// (VER-004, CAN-004 §26, AUT-003 §§27-28). Presentation and layout only:
/// pagination never changes the layout or scores.
module Echelon.Signal.Engine.Layout

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
      States: int
      Bits: int }

let private bitsFor states =
    let rec go n = if (1 <<< n) >= states then n else go (n + 1)
    go 1

let layout (content: Content) : Slot list =
    questions content
    |> List.map (fun (_, q) ->
        let states = 1 + cardinality q.Answer + q.SpecialStates.Length
        { QuestionId = q.Id; States = states; Bits = bitsFor states })

/// Bytes of a ResponseEncodingVersion 1 envelope around the answers: version
/// and binding kind, the 8-byte template reference, the largest binding (two
/// 16-byte ids), the 2-byte item count and the 4-byte integrity check.
[<Literal>]
let EnvelopeOverheadBytes = 2 + 8 + 32 + 2 + 4

type Capacity =
    { Questions: int
      AnswerBits: int
      AnswerBytes: int
      EnvelopeBytes: int
      /// Unpadded base64url characters of the largest envelope.
      EncodedCharacters: int }

let capacity (content: Content) : Capacity =
    let slots = layout content
    let bits = slots |> List.sumBy _.Bits
    let answerBytes = (bits + 7) / 8
    let envelope = answerBytes + EnvelopeOverheadBytes

    { Questions = slots.Length
      AnswerBits = bits
      AnswerBytes = answerBytes
      EnvelopeBytes = envelope
      EncodedCharacters = (envelope * 8 + 5) / 6 }
