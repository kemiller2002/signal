/// Sample respondent links for a published version (AUT-006 §§21, 60-61):
/// test artifacts, marked in the envelope, that open the survey page on
/// the respondent site so an author can try a version on any device. The
/// survey page says each one is a test, and production import refuses what
/// it submits (`Import.TestSubmission`).
///
/// Pure and deterministic: the test ids derive from the version's hash, so
/// they never coincide with a real group's random ids and need no entropy.
module Echelon.Signal.Admin.TestLinks

open System.Security.Cryptography
open System.Text
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState

/// The survey page, relative to the console (web/admin/).
[<Literal>]
let SurveyPage = "../survey/"

type Shown =
    { Title: string
      /// Label and link, in order.
      Links: (string * string) list }

let private idFor (purpose: string) (hash: string) =
    (OpaqueId.ofBytes (SHA256.HashData(Encoding.UTF8.GetBytes $"signal-test-link/1:{purpose}:{hash}")[..IdLength - 1])).Value

/// The test links for one published version: a blank identified and a
/// blank anonymous invitation, then one identified link per test fixture,
/// carrying that fixture's answers.
let forVersion (published: Publication.Published) : Shown =
    let group = idFor "group" published.Hash

    let link label (mode: Import.IdentityMode) (instanceOf: string) answers =
        let payload =
            GenericEnvelope.testLink published.SurveyId published.Version published.Content mode (idFor $"instance:{instanceOf}" published.Hash) group answers

        label, $"{SurveyPage}#{LiveUrl.FragmentKey}={payload}"

    { Title = $"Test links for {published.Content.Metadata.Title} ({published.SurveyId} {published.Version})"
      Links =
        [ link "Blank, identified" Import.IdentifiedGroup "blank-identified" Map.empty
          link "Blank, anonymous" Import.AnonymousGroup "blank-anonymous" Map.empty ]
        @ (published.Fixtures |> List.map (fun f -> link $"Fixture: {f.Name}" Import.IdentifiedGroup $"fixture:{f.Id}" f.Answers)) }
