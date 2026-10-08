/// The reporting contract (RPT-001..RPT-006, SRPP-001..SRPP-010): report
/// definitions, blocks with privacy classes, explicit value states, warnings,
/// status, comparisons and the format-neutral ReportData every renderer
/// (screen, print HTML, Folio PDF, JSON, CSV) consumes.
///
/// The engine decides what results mean; reporting decides how they are
/// communicated. Nothing here scores, decides applicability, validates
/// submissions or reconstructs identity (RPT-006 §70).
module Echelon.Signal.Engine.ReportModel

open System

type Audience =
    | AdministratorAudience
    | ExecutiveAudience
    | RespondentAudience
    | FacilitatorAudience
    | GroupReviewerAudience
    | TechnicalAudience

type Block =
    | Header
    | Summary
    | ResponseCounts
    | OverallResult
    | SectionResults
    | GroupDistributions
    | Strengths
    | Weaknesses
    | Recommendations
    | CoverageAndConfidence
    | Comparisons
    | RespondentDetail
    | RoleBreakdown
    | Methodology
    | AuditMetadata

/// Privacy classes (RPT-005 §57) and the block capability matrix (§58).
type PrivacyClass =
    | AnonymousSafe
    /// Safe in anonymous groups only at or above the minimum count.
    | AnonymousSafeAboveMinimum
    | IdentifiedOnly
    | AdministratorOnly
    | AuditOnly

let privacyOf =
    function
    | Header
    | Summary
    | ResponseCounts
    | OverallResult
    | SectionResults
    | Strengths
    | Weaknesses
    | Recommendations
    | CoverageAndConfidence
    | Comparisons
    | Methodology -> AnonymousSafe
    | GroupDistributions
    | RoleBreakdown -> AnonymousSafeAboveMinimum
    | RespondentDetail -> IdentifiedOnly
    | AuditMetadata -> AuditOnly

type DetailLevel =
    | SummaryDetail
    | StandardDetail
    | DetailedDetail
    | AuditDetail

/// A declarative, versioned report definition (RPT-004 §36). A version used
/// for a formal report is never edited; a change is a new version.
type Definition =
    { Id: string
      Version: int
      Audience: Audience
      /// Included blocks in reading order.
      Blocks: Block list
      Detail: DetailLevel
      /// Anonymous subgroup threshold for this report (at least the group's).
      MinimumGroupSize: int
      /// Presentation rounding (RPT-005 §48); results keep full precision.
      Decimals: int
      /// How many strengths/weaknesses/recommendations to show.
      HighlightCount: int }

/// A reported value, or explicitly why there is none (RPT-005 §49,
/// SRPP-046). Never zero by implication.
type Value =
    | Shown of float
    | NotAvailable
    | NotApplicableValue
    | InsufficientResponses of have: int * need: int
    | NotComparable of reason: string
    | SuppressedValue of have: int * need: int

type Status =
    | Complete
    /// Some accepted responses, expected count not yet reached.
    | Partial of accepted: int * expected: int
    | InsufficientData

/// Warnings come from result, comparison and privacy evaluation, not UI
/// logic (RPT-005 §51); each has a stable code.
type Warning =
    | LowCoverage
    | LowResponseCount
    | HighDontKnowRate
    | IncompleteGroup
    | NotComparableToPriorVersion
    | AnonymousSmallGroupSuppressed

let warningCode =
    function
    | LowCoverage -> "low-coverage"
    | LowResponseCount -> "low-response-count"
    | HighDontKnowRate -> "high-dont-know-rate"
    | IncompleteGroup -> "incomplete-group"
    | NotComparableToPriorVersion -> "not-comparable-to-prior-version"
    | AnonymousSmallGroupSuppressed -> "anonymous-small-group-suppressed"

type ComparisonKind =
    | PriorGroup
    | Target
    | BenchmarkComparison
    | Cohort
    | RoleComparison
    | SelfVsOthers

/// A prepared comparison (RPT-003 §22) from a comparison provider. The
/// report shows a delta only when it is comparable (SRPP-081).
type ComparisonInput =
    { Kind: ComparisonKind
      Label: string
      /// Baseline identity and version (SRPP-082).
      Baseline: string
      Metric: string
      Current: float option
      BaselineValue: float option
      Comparable: bool
      Reason: string option }

type ComparisonRow =
    { Input: ComparisonInput
      Delta: Value }

type SectionRow =
    { Section: string
      Title: string
      Mean: Value
      Median: Value
      Minimum: Value
      Maximum: Value
      Scored: int
      Unscored: int }

type RecommendationRow =
    { Id: string
      Title: string
      Priority: string
      /// Contributions that triggered it.
      Frequency: int }

type Counts =
    { Expected: int
      Accepted: int
      /// Never who is missing (anonymous groups, RPT-001 §13).
      Missing: int
      CompletionPercent: float }

type Audit =
    { SurveyId: string
      TemplateVersion: string
      TemplateHash: string
      GroupResultHash: string
      GroupResultVersion: int
      DefinitionId: string
      DefinitionVersion: int }

/// The minimum report data contract (RPT-006 §69): format-neutral and
/// independent of PDF (SRPP-003).
type ReportData =
    { Definition: string * int
      Audience: Audience
      Status: Status
      Title: string
      GeneratedAt: DateTimeOffset
      Blocks: Block list
      Counts: Counts
      Overall: Value
      Sections: SectionRow list
      Strengths: string list
      Weaknesses: string list
      Recommendations: RecommendationRow list
      Coverage: GroupResult.Coverage
      Warnings: Warning list
      Comparisons: ComparisonRow list
      Roles: (Groups.Role * int) list
      Methodology: string list
      Audit: Audit option }
