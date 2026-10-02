namespace Echelon.Signal.Semantic

[<Struct>]
type SurveyId = SurveyId of string

[<Struct>]
type QuestionId = QuestionId of string

[<Struct>]
type ResponseId = ResponseId of string

type Question =
    { Id: QuestionId
      Prompt: string }

type SurveyDraft =
    { Id: SurveyId
      Title: string
      Questions: Question list }

type PublishedSurvey =
    { Id: SurveyId
      Version: int
      Title: string
      Questions: Question list }

type Answer =
    { QuestionId: QuestionId
      Value: int }

type Submission =
    { Id: ResponseId
      SurveyId: SurveyId
      SurveyVersion: int
      Answers: Answer list }

type Score =
    { Total: int
      Maximum: int }
