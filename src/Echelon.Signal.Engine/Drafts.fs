/// Template drafts and their pure edits (AUT-001 §§5-9, AUT-002 §§10-11).
///
/// A draft is a mutable value only in the sense that edits return a new one;
/// every edit is a total function that keeps section and question ids unique
/// and reports an unknown target instead of guessing. Validation, diff and
/// publication of drafts live in `Validation`, `TemplateDiff` and
/// `Publication`.
module Echelon.Signal.Engine.Drafts

open Echelon.Signal.Engine.Template

/// The published version a draft was derived from (AUT-001 §9).
type ParentReference = { Version: string; Hash: string }

/// An authored expectation of a fixture (AUT-003 §26).
type Assertion =
    /// The section's score, or None for "not scored".
    | SectionScore of sectionId: string * expected: float option
    | Completion of complete: bool

/// A response simulated directly from answer state (AUT-003 §24).
type Fixture =
    { Id: string
      Name: string
      Answers: Answers
      Expect: Assertion list }

type Draft =
    { SurveyId: string
      Parent: ParentReference option
      Content: Content
      Fixtures: Fixture list }

type EditError =
    | UnknownSection of sectionId: string
    | UnknownQuestionId of questionId: string
    | DuplicateId of id: string
    | PositionOutOfRange of position: int

let newDraft (surveyId: string) (title: string) : Draft =
    { SurveyId = surveyId
      Parent = None
      Content =
        { Metadata =
            { Title = title
              ShortTitle = None
              Description = None
              Instructions = None
              Tags = [] }
          Compatibility = defaultCompatibility
          Presentation = defaultPresentation
          Runtime = defaultRuntime
          Sections = [] }
      Fixtures = [] }

let sectionIds (content: Content) = content.Sections |> List.map _.Id
let questionIds (content: Content) = questions content |> List.map (fun (_, q) -> q.Id)

let private withContent (draft: Draft) (content: Content) = { draft with Content = content }

let private mapContent (f: Content -> Result<Content, EditError>) (draft: Draft) =
    f draft.Content |> Result.map (withContent draft)

/// Moves the element at `from` to `position` in a list.
let private move (from: int) (position: int) (items: 'a list) =
    if position < 0 || position >= items.Length then
        Error(PositionOutOfRange position)
    else
        let item = items[from]
        let rest = items |> List.removeAt from
        Ok(rest |> List.insertAt position item)

let addSection (section: Section) =
    mapContent (fun c ->
        if List.contains section.Id (sectionIds c) then
            Error(DuplicateId section.Id)
        else
            match section.Questions |> List.tryFind (fun q -> List.contains q.Id (questionIds c)) with
            | Some q -> Error(DuplicateId q.Id)
            | None -> Ok { c with Sections = c.Sections @ [ section ] })

let private updateSection (sectionId: string) (f: Section -> Result<Section, EditError>) (c: Content) =
    match c.Sections |> List.tryFindIndex (fun s -> s.Id = sectionId) with
    | None -> Error(UnknownSection sectionId)
    | Some i -> f c.Sections[i] |> Result.map (fun s -> { c with Sections = c.Sections |> List.updateAt i s })

let removeSection (sectionId: string) =
    mapContent (fun c ->
        if List.contains sectionId (sectionIds c) then
            Ok { c with Sections = c.Sections |> List.filter (fun s -> s.Id <> sectionId) }
        else
            Error(UnknownSection sectionId))

let moveSection (sectionId: string) (position: int) =
    mapContent (fun c ->
        match c.Sections |> List.tryFindIndex (fun s -> s.Id = sectionId) with
        | None -> Error(UnknownSection sectionId)
        | Some i -> move i position c.Sections |> Result.map (fun sections -> { c with Sections = sections }))

let editSection (sectionId: string) (f: Section -> Section) =
    mapContent (updateSection sectionId (f >> Ok))

let addQuestion (sectionId: string) (question: Question) =
    mapContent (fun c ->
        if List.contains question.Id (questionIds c) then
            Error(DuplicateId question.Id)
        else
            c |> updateSection sectionId (fun s -> Ok { s with Questions = s.Questions @ [ question ] }))

let private sectionOf (c: Content) (questionId: string) =
    c.Sections |> List.tryFind (fun s -> s.Questions |> List.exists (fun q -> q.Id = questionId))

let removeQuestion (questionId: string) =
    mapContent (fun c ->
        match sectionOf c questionId with
        | None -> Error(UnknownQuestionId questionId)
        | Some s -> c |> updateSection s.Id (fun s -> Ok { s with Questions = s.Questions |> List.filter (fun q -> q.Id <> questionId) }))

let editQuestion (questionId: string) (f: Question -> Question) =
    mapContent (fun c ->
        match sectionOf c questionId with
        | None -> Error(UnknownQuestionId questionId)
        | Some s ->
            let edited = s.Questions |> List.find (fun q -> q.Id = questionId) |> f

            // An edit may not give a question another question's id.
            if edited.Id <> questionId && List.contains edited.Id (questionIds c) then
                Error(DuplicateId edited.Id)
            else
                c |> updateSection s.Id (fun s -> Ok { s with Questions = s.Questions |> List.map (fun q -> if q.Id = questionId then edited else q) }))

/// Moves a question to `position` within a section (possibly another one).
let moveQuestion (questionId: string) (toSection: string) (position: int) =
    mapContent (fun c ->
        match sectionOf c questionId, c.Sections |> List.tryFind (fun s -> s.Id = toSection) with
        | None, _ -> Error(UnknownQuestionId questionId)
        | _, None -> Error(UnknownSection toSection)
        | Some source, Some target ->
            let question = source.Questions |> List.find (fun q -> q.Id = questionId)
            let remaining = if source.Id = target.Id then target.Questions |> List.filter (fun q -> q.Id <> questionId) else target.Questions

            if position < 0 || position > remaining.Length then
                Error(PositionOutOfRange position)
            else
                c
                |> updateSection source.Id (fun s -> Ok { s with Questions = s.Questions |> List.filter (fun q -> q.Id <> questionId) })
                |> Result.bind (updateSection target.Id (fun s -> Ok { s with Questions = s.Questions |> List.filter (fun q -> q.Id <> questionId) |> List.insertAt position question })))

let editContent (f: Content -> Content) (draft: Draft) = { draft with Content = f draft.Content }

let addFixture (fixture: Fixture) (draft: Draft) =
    if draft.Fixtures |> List.exists (fun f -> f.Id = fixture.Id) then
        Error(DuplicateId fixture.Id)
    else
        Ok { draft with Fixtures = draft.Fixtures @ [ fixture ] }
