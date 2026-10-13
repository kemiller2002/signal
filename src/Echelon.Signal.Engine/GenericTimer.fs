/// Timers on the survey page (ARX-013; DF-SIGNAL-2026-0010, DF-SIGNAL-2026-0015).
///
/// - A survey timer runs from the start minute the link carries.
/// - A section or page timer runs from the minute the respondent first
///   reached a page holding its section or layout page. That minute is
///   stamped into the link's terms then.
/// - When an enforced timer runs out, the answers it covers can no longer
///   change.
///
/// The page shows the nearest deadline among the timers covering the page
/// shown, as a UTC time, so it needs no ticking clock: the time is read
/// again on every event and when the page opens.
///
/// Question timers would need a start per question and stay refused, as
/// does more than one timer for the same scope.
///
/// Pure.
module Echelon.Signal.Engine.GenericTimer

open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.GenericEnvelope

type TimerModel = Echelon.Signal.Engine.Timers.Timer

/// Whether the page can run every timer the template has.
let supported (content: Content) =
    let scopes = content.Presentation.Timers |> List.map _.Scope

    scopes |> List.forall (function Timers.QuestionScope _ -> false | _ -> true)
    && (List.distinct scopes).Length = scopes.Length

let private sectionIndex (content: Content) (id: string) = content.Sections |> List.tryFindIndex (fun s -> s.Id = id)

let private keyOf =
    function
    | SectionStart i -> 1, i
    | PageStart n -> 2, n

/// The scope start a scoped timer records, if it is scoped.
let private scopeOf (content: Content) (timer: TimerModel) =
    match timer.Scope with
    | Timers.SectionScope id -> sectionIndex content id |> Option.map SectionStart
    | Timers.PageScope n -> Some(PageStart n)
    | Timers.SurveyScope
    | Timers.QuestionScope _ -> None

/// When a timer started, as the terms record it.
let startOf (content: Content) (terms: Terms) (timer: TimerModel) =
    match timer.Scope with
    | Timers.SurveyScope -> terms.StartedAt
    | _ -> scopeOf content timer |> Option.bind (fun scope -> terms.ScopeStarts |> List.tryFind (fst >> (=) scope) |> Option.map snd)

/// The questions a timer covers.
let covers (content: Content) (timer: TimerModel) : Set<string> =
    match timer.Scope with
    | Timers.SurveyScope -> questions content |> List.map (fun (_, q) -> q.Id) |> Set.ofList
    | Timers.SectionScope id ->
        content.Sections |> List.filter (fun s -> s.Id = id) |> List.collect (fun s -> s.Questions |> List.map _.Id) |> Set.ofList
    | Timers.PageScope n -> Layout.pages content |> List.filter (fun p -> p.Number = n) |> List.collect _.Questions |> Set.ofList
    | Timers.QuestionScope id -> Set.singleton id

/// The terms with a start stamped for every timer the page shown opens:
/// the survey timer at first open, a section or page timer when one of its
/// questions is first shown. A start already recorded never moves.
let stamp (content: Content) (shown: string list) (now: System.DateTimeOffset) (terms: Terms) : Terms =
    let minute = startMinute now

    let opens (timer: TimerModel) =
        timer.Scope = Timers.SurveyScope || shown |> List.exists (covers content timer).Contains

    let timers = content.Presentation.Timers |> List.filter opens

    let survey =
        match terms.StartedAt with
        | None when timers |> List.exists (fun t -> t.Scope = Timers.SurveyScope) -> Some minute
        | started -> started

    let added =
        timers
        |> List.choose (scopeOf content)
        |> List.filter (fun scope -> terms.ScopeStarts |> List.forall (fst >> (<>) scope))
        |> List.map (fun scope -> scope, minute)

    { terms with
        StartedAt = survey
        ScopeStarts = (terms.ScopeStarts @ added) |> List.sortBy (fst >> keyOf) }

/// A timer's deadline and status at `now`.
let private statusAt (content: Content) (terms: Terms) (now: System.DateTimeOffset) (timer: TimerModel) =
    let started = startOf content terms timer
    let elapsed = started |> Option.map (fun start -> max System.TimeSpan.Zero (now - start))
    started |> Option.map (fun start -> start + timer.Duration), Timers.status timer elapsed

/// The questions whose answers an enforced, expired timer holds.
let locked (content: Content) (terms: Terms) (now: System.DateTimeOffset) : Set<string> =
    content.Presentation.Timers
    |> List.filter (fun timer -> not (Timers.allowsChanges timer (snd (statusAt content terms now timer))))
    |> List.map (covers content)
    |> Set.unionMany

/// The enforced timers whose deadline had passed by a submission minute
/// (intake's flags). Both are whole minutes, rounded down, so a submission
/// in the deadline's own minute may have been in time and is not flagged:
/// only a minute strictly after the deadline is.
let expiredBy (content: Content) (terms: Terms) (submitted: System.DateTimeOffset) : string list =
    content.Presentation.Timers
    |> List.filter (fun timer -> timer.Enforcement = Timers.Enforced)
    |> List.filter (fun timer -> startOf content terms timer |> Option.exists (fun start -> submitted > start + timer.Duration))
    |> List.map _.Id

let private clock (instant: System.DateTimeOffset) =
    instant.ToUniversalTime().ToString("d MMMM yyyy, HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture)

let private scopeWord (timer: TimerModel) =
    match timer.Scope with
    | Timers.SurveyScope -> "this survey"
    | Timers.SectionScope _ -> "this section"
    | Timers.PageScope _ -> "this page"
    | Timers.QuestionScope _ -> "this question"

/// What the page says about the nearest deadline among the started timers
/// covering the page shown, or "" when there is none.
let notice (content: Content) (terms: Terms) (now: System.DateTimeOffset) (shown: string list) =
    let candidates =
        content.Presentation.Timers
        |> List.filter (fun timer -> shown |> List.exists (covers content timer).Contains)
        |> List.choose (fun timer ->
            match statusAt content terms now timer with
            | Some deadline, state -> Some(timer, deadline, state)
            | None, _ -> None)
        |> List.sortBy (fun (_, deadline, _) -> deadline)

    match candidates with
    | [] -> ""
    | (timer, deadline, state) :: _ ->
        // The survey timer keeps its wording (DF-SIGNAL-2026-0010); a section
        // or page timer names its scope.
        let survey = timer.Scope = Timers.SurveyScope
        let held = if survey then "Your answers" else "Its answers"

        match state with
        | Timers.Expired _ when not (Timers.allowsChanges timer state) ->
            if survey then $"Time is up ({clock deadline}). {held} can no longer be changed."
            else $"Time is up for {scopeWord timer} ({clock deadline}). {held} can no longer be changed."
        | Timers.Expired _ ->
            if survey then $"The suggested time ran out at {clock deadline}. You can still finish."
            else $"The suggested time for {scopeWord timer} ran out at {clock deadline}. You can still finish."
        | Timers.Running(_, warnings) when warnings > 0 ->
            if survey then $"Time is nearly up: answer by {clock deadline}."
            else $"Time is nearly up for {scopeWord timer}: answer by {clock deadline}."
        | _ ->
            let minutes = int timer.Duration.TotalMinutes
            let rule = if timer.Enforcement = Timers.Enforced then "after that, answers can't be changed" else "this is a suggestion"

            if survey then $"Answer by {clock deadline} ({minutes} minutes from when you first opened this survey; {rule})."
            else $"Answer {scopeWord timer} by {clock deadline} ({minutes} minutes from when you first reached it; {rule})."
