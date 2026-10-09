/// The administrator page's summaries: what the engine knows about the
/// catalog, the open dataset and its groups, and the notices it shows.
/// (Kept apart from `AdminApp` so its update stays readable.)
module Echelon.Signal.Admin.AdminTypes

open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin.Access

/// A template the catalog offers for new groups.
type CatalogEntry =
    { Hash: string
      SurveyIdentifier: string
      Version: string
      Title: string
      /// Its sections and questions, for the assessment views.
      Content: Echelon.Signal.Engine.Assessment.Assessment }

/// What the page shows about one group.
[<NoComparison>]
type GroupSummary =
    { Key: string
      SurveyIdentifier: string
      TemplateVersion: string
      Mode: IdentityMode
      Expected: int
      Accepted: int
      /// Accepted responses not yet in what the page shows (ARX-009: released
      /// only when they cannot be singled out by difference).
      Withheld: int
      /// The privacy minimum below which scores are suppressed.
      MinimumReportable: int
      Status: GroupLifecycle.Status
      Phase: AdminState.GroupPhase
      /// Section scores as the report state shows them (suppressed are None).
      Sections: (string * float option) list
      /// Where the report state lives: an embedded fragment or a reference.
      ReportFragment: string
      LastBatch: Intake.BatchSummary option
      /// Each item of the last batch: artifact hash and outcome code.
      Items: (string * string) list
      UnreconciledBatches: int
      /// Calculated measures per section (ADM-012): section, measure, value or reason.
      Analysis: (string * string * string) list
      /// Score distribution bins per section, or why there are none.
      Distributions: Map<string, Result<(float * float * int) list, Analysis.Unavailable>>
      /// The group result's derivation hash and contribution count (ADM-020).
      Lineage: string
      /// The group result reports render from, and the survey it reports on (WI-0062).
      Report: Echelon.Signal.Engine.GroupResult.Result
      Template: Echelon.Signal.Engine.Assessment.Assessment
      Problems: string list }

/// What the page shows about the open dataset.
[<NoComparison>]
type DatasetSummary =
    { DatasetId: string
      Label: string
      Roster: Roster
      Credential: Credential.CredentialState
      ReadOnlyReasons: string list
      Verification: string
      Groups: GroupSummary list }

/// A message for the person: a stable code and a sentence (ADM-031).
type Notice =
    { Code: string
      Message: string
      /// Domain refusal (the person can act) or infrastructure (the system).
      Infrastructure: bool }

