/// ARX-002 slice 2 (WI-0092): anonymous finalization, administrator import
/// and template authoring held to the shared state-system properties
/// (`StateModelTests.holds`), with the specifics of each.
module Echelon.Signal.Tests.StateSystemsTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.StateModel
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Admin
open Echelon.Signal.Tests.StateModelTests

let private opaque (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value

// ---- Finalization ------------------------------------------------------------------------------

let private assessment = Pilot.assessment
let private all = assessment.Items |> List.map (fun i -> i.Id, Assessment.answerDomain.Head) |> Map.ofList
let private live binding answers : Envelope = { Binding = binding; Answers = answers }
let private entropy = Array.init 16 (fun i -> byte (200 + i))

let private envelopes =
    [ live Unbound all
      live (IdentifiedInvitation(opaque 1, opaque 40)) all
      live (IdentifiedInvitation(opaque 1, opaque 40)) Map.empty
      live (AnonymousInvitation(opaque 1, opaque 40)) all
      live (AnonymousInvitation(opaque 1, opaque 40)) (all |> Map.remove (Map.keys all |> Seq.head))
      live (Identified(opaque 1, opaque 40)) all
      live (Anonymous(opaque 5, opaque 40)) all
      live (Test(IdentifiedInvitation(opaque 1, opaque 40))) all ]

[<Fact>]
let ``finalization is a state system with explicit results`` () =
    holds (FinalizationStates.system assessment) envelopes [ FinalizationStates.Finalize entropy; FinalizationStates.Finalize [||]; FinalizationStates.Finalize(OpaqueId.toBytes (opaque 1)) ]

[<Fact>]
let ``an anonymous response owes an unlinkable identifier, and finalizing discharges it`` () =
    let anonymous = live (AnonymousInvitation(opaque 1, opaque 40)) all
    let system = FinalizationStates.system assessment
    Assert.Contains("DrawUnlinkableIdentifier", system.Obligations anonymous |> List.map _.Code)
    Assert.Contains(Capability.CanFinalize, system.Capabilities anonymous)

    match system.Attempt (FinalizationStates.Finalize entropy) anonymous with
    | Ok submission ->
        Assert.Empty(system.Obligations submission)
        Assert.Equal<Set<string>>(set [ Capability.CanCopySubmission ], system.Capabilities submission)
        Assert.True(system.Terminal submission)
    | Error e -> failwith e.Reason

    // Entropy that reproduces the invitation is refused, with the reason.
    match system.Attempt (FinalizationStates.Finalize(OpaqueId.toBytes (opaque 1))) anonymous with
    | Error e -> Assert.Contains("private submission identifier", e.Reason)
    | Ok _ -> failwith "a linkable id was accepted"

// ---- Import ------------------------------------------------------------------------------------

let private artifacts = [ "a"; "b"; "c" ] |> List.map Intake.artifact
let private fresh = Intake.start (opaque 40) ResultRecord.MultiPaste artifacts
let private hashes = fresh.Items |> List.map fst

let private batches =
    [ fresh
      Intake.record [ hashes[0], Intake.AcceptedItem("k1", "s1") ] fresh
      Intake.record [ hashes[0], Intake.ReconciliationRequired; hashes[1], Intake.BlockedMissingTemplate "absent" ] fresh
      Intake.record [ for h in hashes -> h, Intake.RejectedItem "bad" ] fresh
      Intake.cancel (Intake.record [ hashes[0], Intake.AcceptedItem("k1", "s1") ] fresh) ]

let private batchCommands =
    [ BatchStates.RecordOutcome(hashes[0], Intake.AcceptedItem("k1", "s1"))
      BatchStates.RecordOutcome(hashes[2], Intake.DuplicateItem "k1")
      BatchStates.RecordOutcome("not-in-batch", Intake.RejectedItem "x")
      BatchStates.CancelRemainder ]

[<Fact>]
let ``import is a state system with explicit results`` () =
    holds BatchStates.system batches batchCommands

[<Fact>]
let ``an unknown write outcome is an obligation to reconcile, and a decided item never changes`` () =
    let unsure = batches[2]
    let open' = BatchStates.obligations unsure |> List.map _.Code
    Assert.Contains("ReconcileUnknownPersistenceOutcome", open')
    Assert.Contains("ResolveTemplate", open')
    Assert.Contains(Capability.CanReconcileEffect, BatchStates.capabilities unsure)
    Assert.Contains(Capability.CanRetryEffect, BatchStates.capabilities unsure)

    match BatchStates.item unsure hashes[0] with
    | Unknown reason -> Assert.Contains("not failed", reason)
    | other -> failwith $"%A{other}"

    match BatchStates.item unsure "never-seen" with
    | SearchedNotFound(what, scope) ->
        Assert.Equal("never-seen", what)
        Assert.Contains(unsure.BatchId, scope)
    | other -> failwith $"%A{other}"

    // Reconciling records the real outcome and discharges the obligation.
    match BatchStates.attempt (BatchStates.RecordOutcome(hashes[0], Intake.AcceptedItem("k1", "s1"))) unsure with
    | Ok reconciled -> Assert.DoesNotContain("ReconcileUnknownPersistenceOutcome", BatchStates.obligations reconciled |> List.map _.Code)
    | Error e -> failwith e.Reason

    match BatchStates.attempt (BatchStates.RecordOutcome(hashes[0], Intake.RejectedItem "x")) batches[1] with
    | Error e -> Assert.Contains("never changes", e.Reason)
    | Ok _ -> failwith "a decided item changed"

// ---- Template authoring and publication -------------------------------------------------------

let private q id : Question =
    { Id = id
      Prompt = $"How often does {id} happen?"
      HelpText = None
      Answer = Ordinal 5
      Selector = { Preset = Frequency5; Labels = [ "0"; "1"; "2"; "3"; "4" ]; Accessibility = noAccessibility }
      SpecialStates = [ DontKnow ]
      Required = true
      Tags = [] }

let private section: Section =
    { Id = "s"
      Title = "S"
      Description = None
      Required = true
      Questions = [ q "q1"; q "q2" ]
      Presentation = defaultSectionPresentation
      Scoring = None }

let private fixture: Drafts.Fixture = { Id = "f"; Name = "f"; Answers = Map.ofList [ "q1", Value(Point 1); "q2", Value(Point 2) ]; Expect = [ Drafts.ExpectComplete true ] }
let private valid = { (Drafts.newDraft "T" "Survey") with Content = { (Drafts.newDraft "T" "Survey").Content with Sections = [ section ] }; Fixtures = [ fixture ] }
let private invalid = { valid with Fixtures = [] }
let private policy = Validation.defaultPolicy

let private published =
    match Publication.publish policy Publication.emptyCatalog Set.empty DateTimeOffset.UnixEpoch "author" valid with
    | Ok(_, catalog) -> catalog
    | Error e -> failwith $"%A{e}"

let private authoring: TemplateStates.AuthoringState list =
    [ { Policy = policy; Catalog = Publication.emptyCatalog; Draft = None }
      { Policy = policy; Catalog = Publication.emptyCatalog; Draft = Some invalid }
      { Policy = policy; Catalog = Publication.emptyCatalog; Draft = Some valid }
      { Policy = policy; Catalog = published; Draft = None }
      { Policy = policy; Catalog = published; Draft = Some valid } ]

let private authoringCommands =
    [ TemplateStates.PublishDraft Set.empty; TemplateStates.DeriveFrom "1"; TemplateStates.DeriveFrom "9"; TemplateStates.DiscardDraft
      TemplateStates.HideVersion "1"; TemplateStates.HideVersion "9" ]

[<Fact>]
let ``template authoring is a state system with explicit results`` () =
    holds TemplateStates.system authoring authoringCommands

[<Fact>]
let ``a draft's blockers are obligations naming their discharge, and publishing needs them gone`` () =
    let blocked = authoring[1]
    Assert.Contains("SupplyPublicationEvidence", TemplateStates.obligations blocked |> List.map _.Code)
    Assert.DoesNotContain(Capability.CanPublishTemplate, TemplateStates.capabilities blocked)

    match TemplateStates.attempt (TemplateStates.PublishDraft Set.empty) blocked with
    | Error e -> Assert.Contains("SupplyPublicationEvidence", e.Reason)
    | Ok _ -> failwith "published with blockers"

    match TemplateStates.attempt (TemplateStates.PublishDraft Set.empty) authoring[2] with
    | Ok after ->
        Assert.Equal(None, after.Draft)
        Assert.Equal(1, after.Catalog.Templates.Length)
    | Error e -> failwith e.Reason

    // The same content again is refused, explicitly.
    match TemplateStates.attempt (TemplateStates.PublishDraft Set.empty) authoring[4] with
    | Error e -> Assert.Contains("same as published version 1", e.Reason)
    | Ok _ -> failwith "republished identical content"

[<Fact>]
let ``a version looked up and not found is searched-not-found, never absent`` () =
    match TemplateStates.lookup published "T" "9" with
    | SearchedNotFound(what, scope) ->
        Assert.Equal("T version 9", what)
        Assert.Contains("1 published version", scope)
    | other -> failwith $"%A{other}"

    Assert.True(match TemplateStates.lookup published "T" "1" with Known _ -> true | _ -> false)
