/// Matrix, grid and side-by-side questions (SCS-012) as an authoring
/// construct: a matrix expands into ordinary questions, one per row and
/// scale, plus the validation rules its constraints declare. Each row keeps
/// its own primitive answer, encoding slot and accessible prompt, so scoring
/// a matrix is exactly scoring its rows (SCS-012 "matrix scoring MUST be
/// equivalent").
module Echelon.Signal.Engine.Matrix

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Template

type Row =
    { Id: string
      Prompt: string
      /// Row-specific "not applicable" (SCS-012).
      AllowNotApplicable: bool }

/// One scale of the matrix. A side-by-side matrix has two or more, each
/// named, each its own answer.
type Scale =
    { Id: string
      Label: string
      Answer: AnswerDefinition
      Selector: Selector }

type Spec =
    { Id: string
      Prompt: string
      Rows: Row list
      Scales: Scale list
      SpecialStates: SpecialState list
      RequireAllRows: bool
      /// At least / at most this many rows answered, per scale.
      AnsweredRows: (int * int) option
      /// No two rows may share a value on a scale (forced ranking).
      OneUsePerColumn: bool }

/// The question id of a row on a scale: `matrix.row`, or
/// `matrix.row.scale` for side-by-side matrices.
let questionId (m: Spec) (row: Row) (scale: Scale) =
    if m.Scales.Length = 1 then $"{m.Id}.{row.Id}" else $"{m.Id}.{row.Id}.{scale.Id}"

let expand (m: Spec) : Question list * ValidationRule list =
    let rowQuestion (row: Row) (scale: Scale) : Question =
        let specials =
            if row.AllowNotApplicable then
                specialStates |> List.filter (fun s -> s = NotApplicable || List.contains s m.SpecialStates)
            else
                m.SpecialStates

        { Id = questionId m row scale
          // The full accessible name: matrix prompt, row and, side by side, scale.
          Prompt = if m.Scales.Length = 1 then $"{m.Prompt}: {row.Prompt}" else $"{m.Prompt}: {row.Prompt} ({scale.Label})"
          HelpText = None
          Answer = scale.Answer
          Selector = scale.Selector
          SpecialStates = specials
          Required = m.RequireAllRows
          Tags = [ $"matrix:{m.Id}" ] }

    let questions = [ for row in m.Rows do for scale in m.Scales -> rowQuestion row scale ]

    let rules =
        [ for scale in m.Scales do
              let ids = m.Rows |> List.map (fun r -> questionId m r scale)
              let suffix = if m.Scales.Length = 1 then "" else $".{scale.Id}"

              match m.AnsweredRows with
              | Some(lo, hi) ->
                  { Id = $"{m.Id}{suffix}.answered-rows"
                    Check = AnsweredBetween(ids, lo, hi)
                    Message = $"Answer between {lo} and {hi} rows." }
              | None -> ()

              if m.OneUsePerColumn then
                  { Id = $"{m.Id}{suffix}.one-per-column"
                    Check = DistinctAnswers ids
                    Message = "Use each column at most once." } ]

    questions, rules
