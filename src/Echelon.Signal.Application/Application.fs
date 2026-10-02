namespace Echelon.Signal.Application

open Echelon.Signal.Semantic
open Echelon.Signal.Engine

type AdminResult =
    { ResponseId: ResponseId
      Total: int
      Maximum: int }

module Application =
    let execute state command = Engine.transition state command

    let projectAdminResults state =
        state.Scores
        |> Map.toList
        |> List.map (fun (responseId, score) ->
            { ResponseId = responseId
              Total = score.Total
              Maximum = score.Maximum })
