/// The survey timer on the survey page (ARX-013, DF-SIGNAL-2026-0010): a
/// survey-wide timer runs from the start minute the link carries. When an
/// enforced timer runs out, the answers can no longer change. The page shows
/// the deadline as a UTC time, so it needs no ticking clock: the time is
/// read again on every event and when the page opens.
///
/// Only survey-wide timers run here. Section, page and question timers
/// would each need their own start, so a template with them is refused.
///
/// Pure.
module Echelon.Signal.Engine.GenericTimer

open Echelon.Signal.Engine.Template

type TimerModel = Echelon.Signal.Engine.Timers.Timer

/// The template's survey-wide timer, if it has one.
let surveyTimer (content: Content) : TimerModel option =
    content.Presentation.Timers |> List.tryFind (fun t -> t.Scope = Timers.SurveyScope)

/// Whether the page can run every timer the template has: at most one, survey-wide.
let supported (content: Content) =
    match content.Presentation.Timers with
    | [] -> true
    | [ t ] -> t.Scope = Timers.SurveyScope
    | _ -> false

/// The survey timer's deadline and status at `now`, given the start.
let status (content: Content) (startedAt: System.DateTimeOffset option) (now: System.DateTimeOffset) =
    surveyTimer content
    |> Option.map (fun timer ->
        let elapsed = startedAt |> Option.map (fun start -> max System.TimeSpan.Zero (now - start))
        timer, startedAt |> Option.map (fun start -> start + timer.Duration), Timers.status timer elapsed)

/// Whether the survey timer has run out and holds the answers where they are.
let locksAnswers (content: Content) (startedAt: System.DateTimeOffset option) (now: System.DateTimeOffset) =
    match status content startedAt now with
    | Some(timer, _, state) -> not (Timers.allowsChanges timer state)
    | None -> false

let private clock (instant: System.DateTimeOffset) =
    instant.ToUniversalTime().ToString("d MMMM yyyy, HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture)

/// What the page says about the timer, or "" when there is none.
let notice (content: Content) (startedAt: System.DateTimeOffset option) (now: System.DateTimeOffset) =
    match status content startedAt now with
    | None
    | Some(_, None, _) -> ""
    | Some(timer, Some deadline, Timers.Expired _) when not (Timers.allowsChanges timer (Timers.Expired timer.Expiry)) ->
        $"Time is up ({clock deadline}). Your answers can no longer be changed."
    | Some(_, Some deadline, Timers.Expired _) -> $"The suggested time ran out at {clock deadline}. You can still finish."
    | Some(timer, Some deadline, Timers.Running(_, warnings)) when warnings > 0 ->
        $"Time is nearly up: answer by {clock deadline}."
    | Some(timer, Some deadline, _) ->
        let minutes = int timer.Duration.TotalMinutes
        let rule = if timer.Enforcement = Timers.Enforced then "after that, answers can't be changed" else "this is a suggestion"
        $"Answer by {clock deadline} ({minutes} minutes from when you first opened this survey; {rule})."
