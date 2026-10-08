/// Answer state (VER-007, CAN-001 §6): the minimal response is a map from
/// question id to a value or a special state; absence is "unanswered".
/// Special states stay distinct and never become numbers.
module Echelon.Signal.Engine.Responses

/// Answer states that are deliberately not values (CAN §6). Unanswered is
/// the absence of an answer and is never declared.
type SpecialState =
    | DontKnow
    | NotObserved
    | NotApplicable

/// Fixed order of special states: part of the canonical form and of the
/// encoding layout (answer states follow values in this order).
let specialStates = [ DontKnow; NotObserved; NotApplicable ]

type AnswerValue =
    | Flag of bool
    | Point of int
    | Choice of optionId: string

type AnswerState =
    | Value of AnswerValue
    | Special of SpecialState

type Answers = Map<string, AnswerState>
