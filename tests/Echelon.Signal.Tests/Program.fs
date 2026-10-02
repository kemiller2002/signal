open Echelon.Signal.Semantic
open Echelon.Signal.Engine
open Echelon.Signal.Application

let fail message =
    eprintfn "FAIL: %s" message
    1

let run () =
    let surveyId = SurveyId "survey-1"
    let responseId = ResponseId "response-1"
    let q1 = { Id = QuestionId "q1"; Prompt = "The build is understandable." }
    let q2 = { Id = QuestionId "q2"; Prompt = "The workflow is predictable." }
    let draft = { Id = surveyId; Title = "Readiness"; Questions = [ q1; q2 ] }

    match Engine.transition Engine.initial Publish with
    | Ok _ -> fail "Publishing without a draft must be illegal."
    | Error NoDraft ->
        match Engine.transition Engine.initial (StartDraft draft) with
        | Error error -> fail $"Could not create draft: {error}"
        | Ok drafted ->
            match Engine.transition drafted Publish with
            | Error error -> fail $"Could not publish: {error}"
            | Ok published ->
                let submission =
                    { Id = responseId
                      SurveyId = surveyId
                      SurveyVersion = 1
                      Answers =
                        [ { QuestionId = q1.Id; Value = 4 }
                          { QuestionId = q2.Id; Value = 3 } ] }

                match Engine.transition published (Submit submission) with
                | Error error -> fail $"Could not accept response: {error}"
                | Ok accepted ->
                    match Engine.transition accepted (ScoreResponse responseId) with
                    | Error error -> fail $"Could not score response: {error}"
                    | Ok scored ->
                        let results = Application.projectAdminResults scored
                        match results with
                        | [ result ] when result.Total = 7 && result.Maximum = 8 ->
                            printfn "PASS: Signal first vertical slice"
                            0
                        | _ -> fail "Admin result projection did not preserve the deterministic score."
    | Error error -> fail $"Expected NoDraft, got {error}"

System.Environment.ExitCode <- run ()
