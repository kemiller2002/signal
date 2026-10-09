/// Attempts to introduce PII through every surface ARX-009 names (WI-0076):
/// question definitions, metadata, localization, report labels, example
/// submissions, import metadata, stored DTOs and diagnostics. Direct schema
/// support for PII is refused; wording that asks for it is surfaced (or
/// blocks, by policy).
module Echelon.Signal.Tests.PiiInjectionTests

open System
open Xunit
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Drafts
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin
open Echelon.Signal.Admin.ReportLibrary

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private at = DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero)
let private email = "jane.doe@example.com"

// ---- Question definitions, metadata and labels --------------------------------------------------

let private base' = { SurveyId = "SDRA"; Parent = None; Content = Pilot.content; Fixtures = [] }

let private codes (policy: Validation.Policy) (draft: Draft) =
    (Validation.validate policy draft).Findings |> List.filter (fun f -> f.Category = Findings.PrivacyCategory) |> List.map _.Code |> Set.ofList

let private editFirstQuestion (f: Question -> Question) (draft: Draft) =
    let section = draft.Content.Sections.Head
    let edited = { section with Questions = f section.Questions.Head :: section.Questions.Tail }
    { draft with Content = { draft.Content with Sections = edited :: draft.Content.Sections.Tail } }

[<Fact>]
let ``wording that asks for identity is found in every author-written text, and blocks by policy`` () =
    Assert.Empty(codes Validation.defaultPolicy base')

    let attempts =
        [ "PRIVACY-PII-LIKE-METADATA", { base' with Content = { base'.Content with Metadata = { base'.Content.Metadata with Instructions = Some "Start by entering your full name." } } }
          "PRIVACY-PII-LIKE-SECTION",
          { base' with Content = { base'.Content with Sections = { base'.Content.Sections.Head with Description = Some "Tell us your e-mail first." } :: base'.Content.Sections.Tail } }
          "PRIVACY-PII-LIKE-PROMPT", editFirstQuestion (fun q -> { q with Prompt = "What is your phone number?" }) base'
          "PRIVACY-PII-LIKE-LABEL", editFirstQuestion (fun q -> { q with Selector = { q.Selector with Labels = q.Selector.Labels |> List.mapi (fun i l -> if i = 0 then "My employee ID is below" else l) } }) base' ]

    for code, draft in attempts do
        Assert.Contains(code, codes Validation.defaultPolicy draft)
        let blocking = Validation.validate { Validation.defaultPolicy with PiiPromptsBlock = true } draft
        Assert.Contains(code, blocking.Blockers |> List.map _.Code)
        Assert.False(blocking.Passes)

[<Fact>]
let ``a template carrying a field this version does not write is refused, not silently dropped`` () =
    let bytes = TemplateCanonical.bytes "SDRA" "1" Pilot.content
    Assert.True(TemplateDecode.decode bytes |> Result.isOk)
    let text = Text.Encoding.UTF8.GetString bytes
    let injected = text.Insert(text.IndexOf "\"form\"", $"\"email\":\"{email}\",")
    Assert.True(TemplateDecode.decode (Text.Encoding.UTF8.GetBytes injected) |> Result.isError)

// ---- Localization and report labels -------------------------------------------------------------

[<Fact>]
let ``no report label in any locale asks for identity`` () =
    for key in ReportLabels.keys do
        for locale in [ "en-US"; "de-DE"; "fr-FR"; "ar-EG" ] do
            Assert.False(Validation.isPiiLike (ReportLabels.label locale key), $"{locale} {key}")

// ---- Example submissions and import metadata ----------------------------------------------------

let private group = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (40 + i)))).Value
let private definition: GroupDefinition = { Group = group; Mode = AnonymousGroup; ExpectedCount = 5; Template = Pilot.assessment; Generic = None }

let private link =
    let answers = Pilot.assessment.Items |> List.map (fun item -> item.Id, Assessment.Rated Assessment.Often) |> Map.ofList
    "https://signal.example" + LiveUrl.urlFor Pilot.assessment "/web/" "" { Binding = Anonymous((OpaqueId.ofBytes (Array.init 16 byte)).Value, group); Answers = answers }

[<Fact>]
let ``a submission link carrying identity is never stored with it; only its hash and outcome are`` () =
    let injected = link.Replace("#", $"?email={Uri.EscapeDataString email}&name=Jane#")
    let quarantined = Intake.quarantine (System.DateOnly(2026, 6, 1)) definition (fun _ -> None) [ Intake.artifact injected ]

    for q in quarantined do
        let outcome = Intake.itemCode (Intake.outcomeOf q)
        Assert.Equal("accepted", outcome)
        Assert.DoesNotContain("jane", outcome.ToLowerInvariant())

        match Intake.promote group GroupRecord.NoneAfterImport ResultRecord.MultiPaste "batch-1" q with
        | Some contribution ->
            let stored = ResultRecord.body "ds_engagement" contribution |> Json.canonicalText
            Assert.DoesNotContain("jane", stored.ToLowerInvariant())
            Assert.DoesNotContain("example.com", stored)
            Assert.DoesNotContain("https://", stored)
        | None -> ()

// ---- Stored DTOs --------------------------------------------------------------------------------

let private withMember (value: Json) =
    match value with
    | Json.Object members -> Json.Object(members @ [ "email", Json.String email ])
    | other -> other

let private bodyOf (recordText: string) = (Record.decode Record.DefaultMaxBytes recordText |> ok).Body

[<Fact>]
let ``every stored record refuses a field for identity`` () =
    let config: GroupRecord.GroupConfig =
        { Group = group; Mode = AnonymousGroup; ExpectedCount = 5; SurveyIdentifier = "SDRA"; TemplateVersion = "1"; TemplateHash = "sha256:" + String('a', 64)
          MinimumReportableCount = 5; Retention = GroupRecord.NoneAfterImport; Revision = 1 }

    let contribution =
        Intake.quarantine (System.DateOnly(2026, 6, 1)) definition (fun _ -> None) [ Intake.artifact link ]
        |> List.pick (Intake.promote group GroupRecord.NoneAfterImport ResultRecord.MultiPaste "batch-1")

    let pins = currentPins (LatestTemplate "SDRA")
    let audit = Audit.create Audit.ExportCreated [ "snapshot", "snap-0123456789" ] [] 1 [] None None None |> ok

    let attempts: (string * Json * (Json -> bool)) list =
        [ "group", GroupRecord.body "ds_engagement" config, (GroupRecord.ofBody >> Result.isOk)
          "contribution", ResultRecord.body "ds_engagement" contribution, (ResultRecord.ofBody Pilot.assessment >> Result.isOk)
          "report definition", bodyOf (ReportRecord.encodeDefinitions "ds_engagement" "quarterly" [ { Definition = ReportExport.anonymousAggregate; Pins = pins; Used = false } ] |> ok), (ReportRecord.definitionsOfBody >> Result.isOk)
          "audit", bodyOf (GovernanceRecord.encodeAudit "ds_engagement" "op-1" audit |> ok), (GovernanceRecord.auditOfBody >> Result.isOk)
          "lifecycle", bodyOf (GovernanceRecord.encodeLifecycle "ds_engagement" Retention.Active |> ok), (GovernanceRecord.lifecycleOfBody >> Result.isOk)
          "release", bodyOf (GovernanceRecord.encodeRelease { DatasetId = "ds_engagement"; Group = string group; Keys = [] } |> ok), (GovernanceRecord.releaseOfBody >> Result.isOk)
          "draft", bodyOf (TemplateRecord.encodeDraft "ds_engagement" base' |> ok), (TemplateRecord.draftOfBody >> Result.isOk) ]

    for name, body, reads in attempts do
        Assert.True(reads body, $"{name} reads as written")
        Assert.False(reads (withMember body), $"{name} accepted an e-mail field")

// ---- Diagnostics --------------------------------------------------------------------------------

[<Fact>]
let ``a refused record's diagnostic names the field, never its value`` () =
    let body = GovernanceRecord.encodeLifecycle "ds_engagement" Retention.Active |> ok |> bodyOf |> withMember

    match GovernanceRecord.lifecycleOfBody body with
    | Error message ->
        Assert.Contains("'email'", message)
        Assert.DoesNotContain(email, message)
    | Ok _ -> failwith "accepted"
