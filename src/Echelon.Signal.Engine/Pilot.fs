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
