/// What the draft preview shows (WI-0081, AUT-003 §§20-22): the survey
/// page's own projection of the running preview, under names prefixed
/// "preview" so they never collide with the console's, and the live
/// scoring explanation of its answers (`ScoringPreview`).
///
/// Pure.
module Echelon.Signal.Admin.DraftPreviewView

open Echelon.Signal.Engine.View

/// "rows" -> "previewRows": the survey page's names, kept apart.
let private prefixed (name: string) = "preview" + string (System.Char.ToUpperInvariant name[0]) + name.Substring 1

let project (editor: Authoring.Editor option) (preview: Echelon.Signal.Engine.GenericSession.Session option) : (string * ViewValue) list =
    let running = preview |> Option.filter (fun _ -> editor.IsSome)

    let explanation =
        match editor, running |> Option.bind DraftPreview.answers with
        | Some e, Some answers -> ScoringPreview.explain e.Draft.Content answers
        | _ -> []

    // The survey page's view of a session that is not running: so every
    // name the preview binds is always projected.
    let shown =
        running
        |> Option.defaultValue (Echelon.Signal.Engine.GenericSession.Refused Echelon.Signal.Engine.GenericSession.NoAnswerLink)
        |> Echelon.Signal.Engine.GenericSessionView.view
        |> List.map (fun (name, value) -> prefixed name, value)

    [ "previewing", Value(Flag running.IsSome)
      "canPreview", Value(Flag(editor.IsSome && running.IsNone))
      yield! shown
      "previewExplanation",
      Items(
          explanation
          |> List.map (fun l ->
              [ "key", Text l.Key
                "kind", Text(ScoringPreview.kindName l.Kind)
                "text", Text l.Text
                "indented", Flag(l.Kind = ScoringPreview.Contribution || l.Kind = ScoringPreview.Calculation) ])
      ) ]
