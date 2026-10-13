/// Timers and banks as optional template fields (DF-SIGNAL-2026-0008,
/// ARX-013, WI-0081): a template without them is byte for byte what it was,
/// one with them round-trips canonically and is checked at publication, and
/// the survey page refuses what it can't run yet rather than ignoring it.
module Echelon.Signal.Tests.TimerBankTests

open System
open System.IO
open System.Security.Cryptography
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Tests.Support

/// The published templates committed before timers and banks existed, with
/// the full SHA-256 of each file then. Their bytes and hashes must not move.
let private published =
    [ "54fda02bb11a6113", "54fda02bb11a6113d694409d8380c9a606d64c8597ebb587ef02668a4627fdd0"
      "6beb132f5815c173", "6beb132f5815c173a122793be70339fefb2e78672a0616b9e1f694fe6057109a"
      "c4cbb73b2cfc96fc", "c4cbb73b2cfc96fc13abca11e661d53857ab7e9f20d07310ae1a4b21a9dd34e9"
      "d74de1122258c585", "d74de1122258c5853632948105322839ef1284bb9c3c9ba59f7a371b607e2200" ]

let private hex (bytes: byte[]) = Convert.ToHexString(bytes).ToLowerInvariant()

[<Fact>]
let ``every published template keeps its exact bytes and hash`` () =
    for reference, hash in published do
        let bytes = File.ReadAllBytes(repoFile $"published-templates/{reference}.json")
        Assert.Equal(hash, hex (SHA256.HashData bytes))

        match TemplateDecode.decode bytes with
        | Ok decoded ->
            Assert.Empty(decoded.Content.Presentation.Timers)
            Assert.Empty(decoded.Content.Presentation.Banks)
            // Re-encoded by today's code: the same bytes, the same hash.
            Assert.Equal<byte[]>(bytes, TemplateCanonical.bytes decoded.SurveyId decoded.Version decoded.Content)
            Assert.Equal($"sha256:{hash}", TemplateCanonical.templateHash decoded.SurveyId decoded.Version decoded.Content)
        | Error error -> failwith $"%A{error}"

let private demo = SurveyPageTests.demo
let private ids = questions demo |> List.map (fun (_, q) -> q.Id)

let private timer id scope : Timers.Timer =
    { Id = id
      Scope = scope
      Duration = TimeSpan.FromMinutes 10.0
      Warnings = [ TimeSpan.FromMinutes 2.0 ]
      Expiry = Timers.LockAndAdvance
      Enforcement = Timers.Enforced }

let private withTimersAndBanks timers banks =
    { demo with Presentation = { demo.Presentation with Timers = timers; Banks = banks } }

let private timed =
    withTimersAndBanks [ timer "whole" Timers.SurveyScope; timer "first" (Timers.SectionScope demo.Sections.Head.Id) ] [ { Id = "core"; Pool = ids[0..2]; Select = 2 } ]

[<Fact>]
let ``timers and banks round-trip canonically, and change the hash only when present`` () =
    let bytes = TemplateCanonical.bytes "DEMO" "1" timed
    let text = Text.Encoding.UTF8.GetString bytes
    Assert.Contains("\"timers\"", text)
    Assert.Contains("\"banks\"", text)

    match TemplateDecode.decode bytes with
    | Ok decoded -> Assert.Equal(timed, decoded.Content)
    | Error error -> failwith $"%A{error}"

    let plain = Text.Encoding.UTF8.GetString(TemplateCanonical.bytes "DEMO" "1" demo)
    Assert.DoesNotContain("\"timers\"", plain)
    Assert.DoesNotContain("\"banks\"", plain)
    Assert.NotEqual<string>(TemplateCanonical.templateHash "DEMO" "1" demo, TemplateCanonical.templateHash "DEMO" "1" timed)

let private codes (content: Content) =
    (Validation.validate Validation.defaultPolicy { SurveyId = "DEMO"; Parent = None; Content = content; Fixtures = [] }).Blockers
    |> List.map _.Code
    |> Set.ofList

[<Fact>]
let ``publication checks every timer and bank`` () =
    Assert.True(Set.intersect (codes timed) (set [ "STRUCT-TIMER"; "STRUCT-TIMER-REFERENCE"; "STRUCT-BANK"; "STRUCT-BANK-REFERENCE"; "STRUCT-BANK-OVERLAP" ]) |> Set.isEmpty)

    let broken =
        withTimersAndBanks
            [ timer "a" (Timers.SectionScope "NOPE")
              timer "a" (Timers.PageScope 9)
              { timer "b" (Timers.QuestionScope ids[0]) with Duration = TimeSpan.FromSeconds 90.5 } ]
            [ { Id = "x"; Pool = [ ids[0]; "NOPE" ]; Select = 3 }; { Id = "y"; Pool = [ ids[0] ]; Select = 1 } ]

    let found = codes broken

    for code in [ "STRUCT-DUPLICATE-TIMER"; "STRUCT-TIMER-REFERENCE"; "STRUCT-TIMER"; "STRUCT-BANK"; "STRUCT-BANK-REFERENCE"; "STRUCT-BANK-OVERLAP" ] do
        Assert.Contains(code, found)

[<Fact>]
// WI-0081 (DF-SIGNAL-2026-0015 §9): question timers now run; the page still
// refuses two timers on one part. Before, this test also asserted question
// timers were refused, the behaviour the coordinator chose to change.
let ``the survey page runs a question timer, and refuses two timers on one part`` () =
    let opened (content: Content) =
        let file = publishedFile "DEMO" "1" content |> snd
        let instance = (UrlState.OpaqueId.ofBytes (Array.init 16 byte)).Value
        let group = (UrlState.OpaqueId.ofBytes (Array.init 16 (fun i -> byte (40 + i)))).Value
        start ("#r=" + GenericEnvelope.invitation "DEMO" "1" content Import.IdentifiedGroup instance group) |> received (DateOnly(2026, 1, 1)) (Found file)

    let what = "two timers on the same part"
    Assert.True(match opened (withTimersAndBanks [ timer "first" (Timers.QuestionScope ids[0]) ] []) with Responding _ -> true | _ -> false)
    Assert.Equal(Refused(UnsupportedPresentation what), opened (withTimersAndBanks [ timer "a" (Timers.QuestionScope ids[0]); timer "b" (Timers.QuestionScope ids[0]) ] []))
    Assert.Equal(Refused(UnsupportedPresentation what), opened (withTimersAndBanks [ timer "a" Timers.SurveyScope; timer "b" Timers.SurveyScope ] []))
    Assert.Contains("cannot run yet", describe (UnsupportedPresentation what))

[<Fact>]
let ``a version diff names timer and bank changes, and a bank change can move scores`` () =
    let changes = TemplateDiff.diff demo timed
    Assert.Contains(TemplateDiff.TimersChanged, changes)
    Assert.Contains(TemplateDiff.BanksChanged, changes)
    Assert.DoesNotContain(TemplateDiff.PresentationChanged, changes)
    Assert.True((TemplateDiff.impact TemplateDiff.BanksChanged).Scoring)
    Assert.False((TemplateDiff.impact TemplateDiff.TimersChanged).Scoring)

// ---------------------------------------------------------------------------
// Bank selection on the page and at intake (DF-SIGNAL-2026-0009).
// ---------------------------------------------------------------------------

let private oid (seed: int) = (UrlState.OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private bank: Banking.Bank = { Id = "core"; Pool = ids[0..2]; Select = 2 }
let private banked = withTimersAndBanks [] [ bank ]

let private openAs (mode: Import.IdentityMode) (instance: int) =
    let file = publishedFile "DEMO" "1" banked |> snd
    start ("#r=" + GenericEnvelope.invitation "DEMO" "1" banked mode (oid instance) (oid 200)) |> received (DateOnly(2026, 1, 1)) (Found file)

let private responding =
    function
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private applicable (r: Response) = (Rules.applicability r.Published.Form.Content r.Answers).Questions

[<Fact>]
let ``an identified respondent answers their own reproducible selection`` () =
    let r = responding (openAs Import.IdentifiedGroup 1)
    let shown = ids[0..2] |> List.filter (applicable r).Contains
    Assert.Equal(2, shown.Length)
    Assert.Equal<string list>(Banking.select (BankSelection.seedFor r.Binding) bank, shown)
    // The same link opens the same selection; the rest of the survey is untouched.
    Assert.Equal<Set<string>>(applicable r, applicable (responding (openAs Import.IdentifiedGroup 1)))
    Assert.True(ids[3..] |> List.forall (applicable r).Contains)

[<Fact>]
let ``everyone in an anonymous group answers the group's selection, so a submission links to no invitation`` () =
    let selections = [ for i in 1..12 -> applicable (responding (openAs Import.AnonymousGroup i)) ] |> List.distinct
    Assert.Equal(1, selections.Length)
    // Identified respondents differ across instances.
    let identified = [ for i in 1..12 -> applicable (responding (openAs Import.IdentifiedGroup i)) ] |> List.distinct
    Assert.True(identified.Length > 1)

[<Fact>]
let ``intake reads a submission with the selection it was answered under`` () =
    for mode in [ Import.IdentifiedGroup; Import.AnonymousGroup ] do
        let r = responding (openAs mode 7)
        let answer (s: Session) (id: string) = update (Chose(id, choices (tryQuestion banked id).Value |> List.head |> fst)) s
        let shown = ids |> List.filter (applicable r).Contains
        let filled = shown |> List.fold answer (Responding r)

        match update (SubmitRequested(Array.init 16 (fun i -> byte (90 + i)))) filled with
        | Responding submitted when submitted.Phase = Submitted ->
            let definition: Import.GroupDefinition =
                { Group = oid 200
                  Mode = mode
                  ExpectedCount = 1
                  Template = GenericImport.shapeOf "DEMO" "1" banked
                  Generic = Some(GenericImport.formOf "DEMO" "1" banked) }

            match GenericImport.evaluate definition (fun _ -> None) ("https://signal.example/web/survey/" + fragment submitted) with
            | Import.Accepted accepted -> Assert.Equal(shown.Length, accepted.AnsweredCount)
            | other -> failwith $"{mode}: %A{other}"
        | other -> failwith $"{mode}: %A{other}"

[<Fact>]
let ``an authored flow rule may not use the bank-selection prefix`` () =
    let reserved: RuleModel.FlowRule = { Id = BankSelection.RulePrefix + "x"; When = RuleModel.Always; Then = RuleModel.HideQuestion ids[0] }
    Assert.Contains("STRUCT-RESERVED-RULE-ID", codes { banked with Rules = { banked.Rules with Flow = [ reserved ] } })
