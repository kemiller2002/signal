/// The survey page in the link's locale (WI-0091 slice 2, CAN-001 §§2 and
/// 7, ACR-006 §§17-18, DF-SIGNAL-2026-0020): a translated template shows
/// its translation for the link's locale (or the same language), applies
/// its accessibility text, and answers, encodes and scores exactly as the
/// untranslated page. Writes the LOCALIZED survey file and
/// tests/browser/fixtures/localized-links.json for survey-localized.spec.js.
module Echelon.Signal.Tests.LocalizedPageTests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.LocalizationModel
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Engine.GenericSessionView
open Echelon.Signal.Tests.Support

let private frequency: Question =
    { Id = "often"
      Prompt = "How often does the team review its work?"
      HelpText = Some "Think of the last month."
      Answer = Ordinal 5
      Selector =
        { Preset = Frequency5
          Labels = [ "Never"; "Rarely"; "Sometimes"; "Often"; "Always" ]
          Accessibility = { Name = Some "Review frequency"; Description = Some "Five steps from never to always." } }
      SpecialStates = [ DontKnow ]
      Required = true
      Tags = [] }

let private tool: Question =
    { Id = "tool"
      Prompt = "Which tool does the team use most?"
      HelpText = None
      Answer = SingleChoice [ { Id = "board"; Label = "A board"; Score = None }; { Id = "list"; Label = "A list"; Score = None } ]
      Selector = { Preset = RadioList; Labels = []; Accessibility = noAccessibility }
      SpecialStates = []
      Required = true
      Tags = [] }

let private french =
    { Locale = "fr"
      Texts =
        Map.ofList
            [ SurveyTitle, "Enquête traduite"
              SectionTitle "work", "Le travail"
              QuestionPrompt "often", "À quelle fréquence l'équipe revoit-elle son travail ?"
              QuestionHelp "often", "Pensez au dernier mois."
              AnswerLabel("often", 0), "Jamais"
              AnswerLabel("often", 4), "Toujours"
              AccessibleName "often", "Fréquence des revues"
              AccessibleDescription "often", "Cinq niveaux, de jamais à toujours."
              QuestionPrompt "tool", "Quel outil l'équipe utilise-t-elle le plus ?"
              OptionLabel("tool", "board"), "Un tableau" ] }

/// The published LOCALIZED survey: English source text, a French
/// translation, and accessibility text on the frequency question.
let localized: Content =
    { (Drafts.newDraft "LOCALIZED" "Translated survey").Content with
        Sections =
            [ { Id = "work"
                Title = "The work"
                Description = None
                Required = true
                Questions = [ frequency; tool ]
                Presentation = defaultSectionPresentation
                Scoring = None } ]
        Localizations = [ french ] }

let private opaque (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value

let private link locale =
    "#r="
    + GenericEnvelope.invitationWith { GenericEnvelope.noTerms with Locale = locale } "LOCALIZED" "1" localized Import.IdentifiedGroup (opaque 1) (opaque 40)

let private file, bytes = publishedFile "LOCALIZED" "1" localized

let private links =
    [ "localizedFile", file
      // Canadian French reads the "fr" translation (same language).
      "french", "web/survey/" + link (Some "fr-CA")
      // German has no translation: the source text shows.
      "untranslated", "web/survey/" + link (Some "de-DE")
      "source", "web/survey/" + link None ]

let private linksJson () =
    let o = JsonObject()
    links |> List.iter (fun (name, value) -> o[name] <- JsonValue.Create value)
    o.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)) + "\n"

[<Fact>]
let ``the LOCALIZED survey file and its browser links are what the code produces`` () =
    Assert.Empty((Validation.validate Validation.defaultPolicy { (Drafts.newDraft "LOCALIZED" "x") with Content = localized }).Blockers |> List.filter (fun f -> f.Category = Findings.LocalizationCategory))

    if Environment.GetEnvironmentVariable "SIGNAL_WRITE_FIXTURES" = "1" then
        File.WriteAllBytes(repoFile file, bytes)
        File.WriteAllText(repoFile "tests/browser/fixtures/localized-links.json", linksJson ())

    Assert.Equal<byte[]>(bytes, File.ReadAllBytes(repoFile file))
    Assert.Equal(linksJson (), readRepoFile "tests/browser/fixtures/localized-links.json")

let private opened locale =
    match start (link locale) |> received (DateOnly(2026, 1, 1)) (Found bytes) with
    | Responding r -> Responding r
    | other -> failwith $"%A{other}"

let private value (v: View.View) name =
    match v |> List.find (fst >> (=) name) |> snd with
    | View.Value(View.Text t) -> t
    | other -> failwith $"{name}: %A{other}"

type private Row = Map<string, View.Scalar>

let private rows (v: View.View) : Row list =
    match v |> List.find (fst >> (=) "rows") |> snd with
    | View.Items items -> items |> List.map Map.ofList
    | other -> failwith $"%A{other}"

let private field (row: Row) name =
    match row[name] with
    | View.Text t -> t
    | View.Flag f -> string f
    | View.Number n -> string n

let private keyOf (row: Row) = field row "id"
let private rowWith (v: View.View) key = rows v |> List.find (fun r -> keyOf r = key)

[<Fact>]
let ``a link in a translated language shows the translation and names its language`` () =
    let v = view (opened (Some "fr-CA"))
    Assert.Equal("Enquête traduite", value v "surveyTitle")
    Assert.Equal("fr", value v "lang")
    Assert.Equal("Le travail", field (rowWith v "s0") "title")
    Assert.Equal("À quelle fréquence l'équipe revoit-elle son travail ?", field (rowWith v "0") "prompt")
    Assert.Contains("Pensez au dernier mois.", field (rowWith v "0") "help")
    // Answer labels: translated where given, the source where not.
    let labels = rows v |> List.filter (fun r -> (keyOf r).StartsWith "0-") |> List.map (fun r -> field r "label")
    Assert.Contains("Jamais", labels)
    Assert.Contains("Toujours", labels)
    Assert.Contains("Often", labels)
    Assert.Contains("Un tableau", rows v |> List.filter (fun r -> (keyOf r).StartsWith "1-") |> List.map (fun r -> field r "label"))

[<Fact>]
let ``accessibility text names and describes the question, in the shown language`` () =
    let v = view (opened (Some "fr-CA"))
    let q = rowWith v "0"
    Assert.Equal("Fréquence des revues", field q "accessibleName")
    Assert.Equal("true", field q "promptHidden")
    Assert.Equal("Cinq niveaux, de jamais à toujours.", field q "accessibleDescription")
    Assert.Equal("False", field q "hideAccessibleDescription")
    Assert.EndsWith("q0-description", field (rowWith v "0-0") "describedBy")

    // A question without accessibility text keeps its prompt and description as before.
    let plain = rowWith v "1"
    Assert.Equal("false", field plain "promptHidden")
    Assert.Equal("True", field plain "hideAccessibleName")
    Assert.Equal("q1-prompt", field (rowWith v "1-0") "describedBy")

[<Fact>]
let ``a language without a translation shows the source text`` () =
    let v = view (opened (Some "de-DE"))
    Assert.Equal("Translated survey", value v "surveyTitle")
    Assert.Equal("de-DE", value v "lang")
    Assert.Equal("How often does the team review its work?", field (rowWith v "0") "prompt")
    Assert.Equal("Review frequency", field (rowWith v "0") "accessibleName")

[<Fact>]
let ``the translated page answers and encodes exactly as the source page`` () =
    let answer locale =
        match opened locale |> update (Chose("often", Value(Point 3))) |> update (Chose("tool", Value(Choice "board"))) with
        | Responding r -> r.Answers
        | other -> failwith $"%A{other}"

    Assert.Equal<Answers>(answer None, answer (Some "fr-CA"))
    Assert.Equal<Answers>(answer None, answer (Some "de-DE"))
