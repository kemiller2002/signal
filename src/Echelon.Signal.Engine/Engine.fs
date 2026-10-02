namespace Echelon.Signal.Engine

open Echelon.Signal.Semantic

type State =
    { Draft: SurveyDraft option
      Published: PublishedSurvey option
      Responses: Map<ResponseId, Submission>
      Scores: Map<ResponseId, Score> }

type Command =
    | StartDraft of SurveyDraft
    | Publish
    | Submit of Submission
    | ScoreResponse of ResponseId

type Error =
    | DraftAlreadyExists
    | NoDraft
    | SurveyAlreadyPublished
    | SurveyNotPublished
    | SurveyIdentityMismatch
    | VersionMismatch
    | DuplicateResponse
    | ResponseNotFound
    | AlreadyScored
    | AnswerSetMismatch
    | AnswerOutOfRange of QuestionId

module Engine =
    let initial =
        { Draft = None
          Published = None
          Responses = Map.empty
          Scores = Map.empty }

    let private validateAnswers (survey: PublishedSurvey) (submission: Submission) =
        let expected = survey.Questions |> List.map _.Id |> Set.ofList
        let actual = submission.Answers |> List.map _.QuestionId |> Set.ofList

        if expected <> actual then
            Error AnswerSetMismatch
        else
            submission.Answers
            |> List.tryPick (fun answer ->
                if answer.Value < 0 || answer.Value > 4 then Some (AnswerOutOfRange answer.QuestionId)
                else None)
            |> function
                | Some error -> Error error
                | None -> Ok ()

    let transition state command =
        match command with
        | StartDraft _ when state.Draft.IsSome -> Error DraftAlreadyExists
        | StartDraft _ when state.Published.IsSome -> Error SurveyAlreadyPublished
        | StartDraft draft -> Ok { state with Draft = Some draft }

        | Publish when state.Published.IsSome -> Error SurveyAlreadyPublished
        | Publish ->
            match state.Draft with
            | None -> Error NoDraft
            | Some draft when List.isEmpty draft.Questions -> Error NoDraft
            | Some draft ->
                let published =
                    { Id = draft.Id
                      Version = 1
                      Title = draft.Title
                      Questions = draft.Questions }

                Ok
                    { state with
                        Draft = None
                        Published = Some published }

        | Submit submission ->
            match state.Published with
            | None -> Error SurveyNotPublished
            | Some survey when submission.SurveyId <> survey.Id -> Error SurveyIdentityMismatch
            | Some survey when submission.SurveyVersion <> survey.Version -> Error VersionMismatch
            | Some _ when state.Responses.ContainsKey submission.Id -> Error DuplicateResponse
            | Some survey ->
                match validateAnswers survey submission with
                | Error error -> Error error
                | Ok () ->
                    Ok { state with Responses = state.Responses.Add(submission.Id, submission) }

        | ScoreResponse responseId when state.Scores.ContainsKey responseId -> Error AlreadyScored
        | ScoreResponse responseId ->
            match state.Responses.TryFind responseId with
            | None -> Error ResponseNotFound
            | Some submission ->
                let score =
                    { Total = submission.Answers |> List.sumBy _.Value
                      Maximum = submission.Answers.Length * 4 }

                Ok { state with Scores = state.Scores.Add(responseId, score) }
