/// Timers (ARX-013 "Timer Model") as pure data and a pure status function:
/// the clock is an argument, so a timer's state is reproducible and the page
/// only displays what the engine decides.
module Echelon.Signal.Engine.Timers

open System

type Scope =
    | SurveyScope
    | SectionScope of sectionId: string
    | PageScope of page: int
    | QuestionScope of questionId: string

type ExpiryAction =
    /// Lock the scope's answers and move on.
    | LockAndAdvance
    | LockOnly
    | WarnOnly
    | EndSurvey

type Enforcement =
    | Advisory
    | Enforced

type Timer =
    { Id: string
      Scope: Scope
      Duration: TimeSpan
      /// Remaining-time thresholds that warn, largest first.
      Warnings: TimeSpan list
      Expiry: ExpiryAction
      Enforcement: Enforcement }

type Status =
    | NotStarted
    | Running of remaining: TimeSpan * warningsReached: int
    | Expired of action: ExpiryAction

/// The timer's status `elapsed` after it started (paused time excluded by
/// the caller's resume policy); None for elapsed means not started.
let status (timer: Timer) (elapsed: TimeSpan option) : Status =
    match elapsed with
    | None -> NotStarted
    | Some e when e >= timer.Duration -> Expired timer.Expiry
    | Some e ->
        let remaining = timer.Duration - e
        Running(remaining, timer.Warnings |> List.filter (fun w -> remaining <= w) |> List.length)

/// Whether answers in the timer's scope may still change.
let allowsChanges (timer: Timer) (s: Status) =
    match s, timer.Enforcement with
    | Expired(LockAndAdvance | LockOnly | EndSurvey), Enforced -> false
    | _ -> true

let check (timer: Timer) : string list =
    [ if timer.Duration <= TimeSpan.Zero then $"Timer '{timer.Id}' needs a positive duration."
      if timer.Warnings |> List.exists (fun w -> w <= TimeSpan.Zero || w >= timer.Duration) then
          $"Timer '{timer.Id}' has a warning outside its duration." ]
