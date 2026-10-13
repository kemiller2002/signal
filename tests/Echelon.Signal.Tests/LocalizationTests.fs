/// Template localization and selector accessibility metadata (WI-0091,
/// CAN-001 §§2 and 7, ACR-006 §§17-18, DF-SIGNAL-2026-0020): canonical,
/// hash-neutral when absent, checked against what the template has, and
/// never changing scoring or the encoding.
module Echelon.Signal.Tests.LocalizationTests

open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.LocalizationModel
open Echelon.Signal.Engine.Drafts
open Echelon.Signal.Engine.TemplateDiff

let private sdra = Pilot.content
let private h c = TemplateCanonical.templateHash "SDRA" "1" c

let private firstQuestion = (questions sdra).Head |> snd
let private q = firstQuestion.Id
let private section = sdra.Sections.Head.Id

let private french texts = { Locale = "fr-FR"; Texts = Map.ofList texts }

let private localized (ls: Localization list) = { sdra with Localizations = ls }

let private codes (c: Content) = Localization.check c |> List.map _.Code

let private valid =
    french
        [ SurveyTitle, "Évaluation"
          SectionTitle section, "Section traduite"
          QuestionPrompt q, "À quelle fréquence ?"
          AnswerLabel(q, 0), "Jamais" ]

[<Fact>]
let ``a template without translations or accessibility text keeps its hash`` () =
    Assert.Equal("sha256:d9649e8a9b3d9616157d4d7a5ef1f6482bd4eedb5742b385243a46ea9aeebccd", h sdra)
    let text = System.Text.Encoding.UTF8.GetString(TemplateCanonical.bytes "SDRA" "1" sdra)
    Assert.DoesNotContain("\"localizations\"", text)
    Assert.DoesNotContain("\"accessibility\"", text)

[<Fact>]
let ``translations enter the hash, in canonical order, without touching the encoding`` () =
    let german = { Locale = "de"; Texts = Map.ofList [ SurveyTitle, "Bewertung" ] }
    Assert.NotEqual<string>(h sdra, h (localized [ valid ]))
    Assert.Equal(h (localized [ valid; german ]), h (localized [ german; valid ]))
    Assert.Equal(TemplateCanonical.layoutFingerprint sdra, TemplateCanonical.layoutFingerprint (localized [ valid; german ]))

    match TemplateDecode.decode (TemplateCanonical.bytes "SDRA" "1" (localized [ valid; german ])) with
    | Ok decoded -> Assert.Equal<Localization list>([ german; valid ], decoded.Content.Localizations)
    | Error e -> failwith e

[<Fact>]
let ``every text key has one spelling that reads back as itself`` () =
    let keys =
        [ SurveyTitle; SurveyShortTitle; SurveyDescription; SurveyInstructions
          SectionTitle "s1"; SectionDescription "s1"; QuestionPrompt "q1"; QuestionHelp "q1"
          AnswerLabel("q1", 0); AnswerLabel("q1", 12); OptionLabel("q1", "o-2"); AccessibleName "q1"; AccessibleDescription "q1"
          RecommendationTitle "r1"; RecommendationDescription "r1"; ResultLabel("band", "a/b c") ]

    for key in keys do
        Assert.Equal(Some key, keyOf (keyName key))

    for spelling in [ ""; "title/x"; "question/q1"; "question/q1/label/01"; "question/q1/label/-1"; "question/q1/option/a/b"; "result/band/"; "section/s1/prompt" ] do
        Assert.Equal(None, keyOf spelling)

[<Fact>]
let ``a translation of something the template has passes; anything else is a blocker`` () =
    Assert.Empty(Localization.check (localized [ valid ]))

    let bad texts = codes (localized [ french texts ])
    Assert.Contains("LOCALE-REFERENCE", bad [ QuestionPrompt "nowhere", "x" ])
    Assert.Contains("LOCALE-REFERENCE", bad [ AnswerLabel(q, firstQuestion.Selector.Labels.Length), "x" ])
    Assert.Contains("LOCALE-REFERENCE", bad [ OptionLabel(q, "none"), "x" ])
    Assert.Contains("LOCALE-REFERENCE", bad [ QuestionHelp q, "x" ])
    Assert.Contains("LOCALE-REFERENCE", bad [ AccessibleName q, "x" ])
    Assert.Contains("LOCALE-REFERENCE", bad [ ResultLabel("nowhere", "high"), "x" ])
    Assert.Contains("LOCALE-REFERENCE", bad [ RecommendationTitle "nowhere", "x" ])
    Assert.Contains("LOCALE-BLANK", bad [ SurveyTitle, "  " ])
    Assert.Contains("LOCALE-EMPTY", bad [])
    Assert.Contains("LOCALE-TAG", codes (localized [ { valid with Locale = "French" } ]))
    Assert.Contains("LOCALE-DUPLICATE", codes (localized [ valid; valid ]))

[<Fact>]
let ``result labels and accessibility text are translatable once the template has them`` () =
    let bands: Scoring.Band list = [ { Label = "low"; From = 0.0; To = 2.0 }; { Label = "high"; From = 2.0; To = 4.0 } ]
    let interpretation = { Id = "band"; Target = SectionResultOf section; Kind = Bands bands }

    let withMeta =
        { sdra with
            Results = { sdra.Results with Interpretations = [ interpretation ] }
            Sections =
                sdra.Sections
                |> List.map (fun s ->
                    { s with
                        Questions =
                            s.Questions
                            |> List.map (fun x ->
                                if x.Id = q then { x with Selector = { x.Selector with Accessibility = { Name = Some "Frequency"; Description = None } } } else x) }) }

    let texts = french [ ResultLabel("band", "high"), "élevé"; AccessibleName q, "Fréquence" ]
    Assert.Empty(Localization.check { withMeta with Localizations = [ texts ] })
    Assert.Contains("LOCALE-REFERENCE", codes { withMeta with Localizations = [ french [ ResultLabel("band", "medium"), "x" ] ] })

    let blankName =
        { withMeta with
            Sections =
                withMeta.Sections
                |> List.map (fun s -> { s with Questions = s.Questions |> List.map (fun x -> { x with Selector = { x.Selector with Accessibility = { Name = Some " "; Description = None } } }) }) }

    Assert.Contains("ACCESSIBILITY-BLANK", codes blankName)

[<Fact>]
let ``validation reports translation problems in their own category and holds translations to the PII rule`` () =
    let draft content = { (newDraft "SDRA" "x") with Content = content }
    let report content = Validation.validate Validation.defaultPolicy (draft content)

    let broken = report (localized [ french [ QuestionPrompt "nowhere", "x" ] ])
    let failing = Validation.summary broken |> List.filter (snd >> not) |> List.map fst
    Assert.Contains(Findings.LocalizationCategory, failing)

    let asking = report (localized [ french [ QuestionPrompt q, "What is your email address?" ] ])
    Assert.Contains("PRIVACY-PII-LIKE-TRANSLATION", asking.Findings |> List.map _.Code)

[<Fact>]
let ``translations are a comparable presentation change; accessibility text is a selector change`` () =
    let changes = diff sdra (localized [ valid ])
    Assert.Equal<Change list>([ LocalizationChanged ], changes)
    Assert.Equal({ Scoring = false; Encoding = false }, impact LocalizationChanged)
    Assert.Equal(Comparable, comparability changes)

    let accessible =
        { sdra with
            Sections =
                sdra.Sections
                |> List.map (fun s ->
                    { s with Questions = s.Questions |> List.map (fun x -> if x.Id = q then { x with Selector = { x.Selector with Accessibility = { Name = Some "n"; Description = None } } } else x) }) }

    Assert.Equal<Change list>([ SelectorChanged q ], diff sdra accessible)

[<Fact>]
let ``a translated template scores and encodes exactly as the untranslated one`` () =
    let answers = Map.ofList [ q, Value(Point 3) ]
    let plain = SurveyResult.compute "" sdra answers false
    let translated = SurveyResult.compute "" (localized [ valid ]) answers false
    Assert.Equal(sprintf "%A" plain.Evaluation.Sections, sprintf "%A" translated.Evaluation.Sections)
    Assert.Equal<Layout.Slot list>(Layout.layout sdra, Layout.layout (localized [ valid ]))
