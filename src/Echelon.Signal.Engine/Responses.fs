/// Answer state (VER-007, CAN-001 §6, SCS-009): the minimal response is a map
/// from question id to a value or a special state; absence is "unanswered".
/// Special states stay distinct and never become numbers.
module Echelon.Signal.Engine.Responses

/// Answer states that are deliberately not values (CAN §6, SCS-009).
/// Unanswered is the absence of an answer and is never declared.
type SpecialState =
    | DontKnow
    | NotObserved
    | NotApplicable
    /// Prefer not to answer: a semantic response, not an alias for
    /// unanswered (SCS-009).
    | Declined

/// Fixed order of special states: part of the canonical form and of the
/// encoding layout (answer states follow values in this order).
let specialStates = [ DontKnow; NotObserved; NotApplicable; Declined ]

type AnswerValue =
    | Flag of bool
    | Point of int
    | Choice of optionId: string
    /// A multi-choice selection: the resulting semantic set, never a
    /// transient UI state (SCS-011).
    | Choices of optionIds: Set<string>
    /// A bounded number as its tick: minimum + tick * step.
    | Tick of int
    /// A bounded range as two ticks, low <= high.
    | TickRange of low: int * high: int
    /// A ranking, best first.
    | Order of optionIds: string list
    /// An allocation in steps per option.
    | Allocated of steps: Map<string, int>
    /// One best and one different worst option.
    | BestWorstPick of best: string * worst: string

type AnswerState =
    | Value of AnswerValue
    | Special of SpecialState

type Answers = Map<string, AnswerState>
