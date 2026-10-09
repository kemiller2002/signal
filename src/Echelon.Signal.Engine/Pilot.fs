/// The pilot assessment the first slice administers: the first three
/// dimensions of the Software Delivery Reality Assessment core battery
/// (D01-D03, items CORE-001 to CORE-015), copied verbatim from the draft item
/// bank `input-documents/software-delivery-reality-assessment-v1-item-bank.json`
/// (SDRA 0.1.0-draft). The item bank is an experimental authoring draft, not
/// a published SurveyTemplate; publication and canonicalization (WI-0003)
/// replace this literal when they exist.
module Echelon.Signal.Engine.Pilot

open Echelon.Signal.Engine.Assessment

let assessment: Assessment =
    { Id = "SDRA"
      Title = "Software Delivery Reality Assessment"
      Version = "0.1.0-draft"
      Dimensions =
        [ { Id = "D01"; Label = "Plan Commitment" }
          { Id = "D02"; Label = "Change Responsiveness" }
          { Id = "D03"; Label = "Timebox Discipline" } ]
      Items =
        [ { Id = "CORE-001"
            DimensionId = "D01"
            Prompt = "Major requirements are expected to be substantially defined before implementation begins." }
          { Id = "CORE-002"
            DimensionId = "D01"
            Prompt = "Release scope is committed well before the work needed to deliver it is complete." }
          { Id = "CORE-003"
            DimensionId = "D01"
            Prompt = "Changes discovered after implementation begins normally require formal change approval." }
          { Id = "CORE-004"
            DimensionId = "D01"
            Prompt = "Design, build, test, and release milestones are managed against an upfront baseline plan." }
          { Id = "CORE-005"
            DimensionId = "D01"
            Prompt = "Delivery success is judged primarily by conformance to the original scope/schedule plan." }
          { Id = "CORE-006"
            DimensionId = "D02"
            Prompt = "New customer or operational evidence can change upcoming priorities through the normal process." }
          { Id = "CORE-007"
            DimensionId = "D02"
            Prompt = "The team can clarify or renegotiate planned scope when it learns something important." }
          { Id = "CORE-008"
            DimensionId = "D02"
            Prompt = "Findings from implementation regularly change what the team plans to do next." }
          { Id = "CORE-009"
            DimensionId = "D02"
            Prompt = "Stakeholder feedback can alter priorities without waiting for a major project phase boundary." }
          { Id = "CORE-010"
            DimensionId = "D02"
            Prompt = "Planning is revisited often enough that newly learned information can materially affect near-term work." }
          { Id = "CORE-011"
            DimensionId = "D03"
            Prompt = "Work is organized in consistent short iterations or timeboxes." }
          { Id = "CORE-012"
            DimensionId = "D03"
            Prompt = "Each iteration has a shared outcome or goal rather than only a collection of assigned tasks." }
          { Id = "CORE-013"
            DimensionId = "D03"
            Prompt = "Planning, inspection/review, and improvement happen on a consistent iteration cadence." }
          { Id = "CORE-014"
            DimensionId = "D03"
            Prompt = "The team aims to produce usable, integrated work by the end of each iteration." }
          { Id = "CORE-015"
            DimensionId = "D03"
            Prompt = "Work that has not met the team's completion/quality criteria is not counted as finished merely because the iteration ended." } ]
      MinimumNumericAnswers = 3 }

/// The same pilot as a generic canonical template (WI-0042): one section per
/// dimension, five-point frequency questions offering the three special
/// states, each section scored by the SDRA catalog scorer. A differential
/// test holds its section scores equal to `Assessment.score`. Any assessment
/// converts the same way (`contentOf`).
let contentOf (assessment: Assessment) : Template.Content =
    let question (item: Item) : Template.Question =
        { Id = item.Id
          Prompt = item.Prompt
          HelpText = None
          Answer = Primitives.Ordinal 5
          Selector =
            { Preset = Selectors.Frequency5
              Labels = [ Never; Rarely; Sometimes; Often; AlmostAlways ] |> List.map frequencyLabel }
          SpecialStates = [ Responses.DontKnow; Responses.NotObserved; Responses.NotApplicable ]
          Required = true
          Tags = [] }

    { Metadata =
        { Title = assessment.Title
          ShortTitle = Some assessment.Id
          Description = None
          Instructions = None
          Tags = [] }
      Compatibility = Template.defaultCompatibility
      Presentation = Template.defaultPresentation
      Runtime = Template.defaultRuntime
      Sections =
        assessment.Dimensions
        |> List.map (fun d ->
            { Id = d.Id
              Title = d.Label
              Description = None
              Required = true
              Questions = itemsOf assessment d |> List.map question
              Presentation = Template.defaultSectionPresentation
              Scoring =
                Some
                    { Scorer = dimensionScorer assessment
                      Questions = [] } })
      Rules = RuleModel.noRules
      Results = ResultModel.noResults }

let content: Template.Content = contentOf assessment

/// Assessment answers as generic answer state.
let answerState (answer: Answer) : Responses.AnswerState =
    match answer with
    | Rated f -> Responses.Value(Responses.Point(frequencyValue f))
    | Withheld DontKnow -> Responses.Special Responses.DontKnow
    | Withheld NotObserved -> Responses.Special Responses.NotObserved
    | Withheld NotApplicable -> Responses.Special Responses.NotApplicable

let answers (answers: Answers) : Responses.Answers = answers |> Map.map (fun _ a -> answerState a)

/// The assessment a generic template is, when it has the shape the group
/// pipeline reads (`contentOf`'s image): one scored section per dimension of
/// five-point frequency questions with the three special states, each
/// section scored by the catalog scorer, no rules or results. Metadata
/// beyond the title is the template's own. Anything else is refused with the
/// reason, never approximated (WI-0073).
let assessmentOf (surveyId: string) (version: string) (content: Template.Content) : Result<Assessment, string> =
    let minimum =
        content.Sections
        |> List.tryPick (fun s -> s.Scoring |> Option.map _.Scorer.Missing.MinimumObservations)
        |> Option.defaultValue 1

    let candidate =
        { Id = surveyId
          Title = content.Metadata.Title
          Version = version
          Dimensions = content.Sections |> List.map (fun s -> { Id = s.Id; Label = s.Title })
          Items = content.Sections |> List.collect (fun s -> s.Questions |> List.map (fun q -> { Id = q.Id; DimensionId = s.Id; Prompt = q.Prompt }))
          MinimumNumericAnswers = minimum }

    if content.Sections.IsEmpty then Error "it has no sections"
    elif { contentOf candidate with Metadata = content.Metadata } = content then Ok candidate
    else Error "the group pipeline reads five-point frequency sections scored by the catalog scorer, without rules or results; other templates cannot start groups yet"
