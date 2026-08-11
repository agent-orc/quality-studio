using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record ChangeSetQuery(
    string RepositoryRoot,
    string? BaseRevision = null,
    string HeadRevision = "HEAD",
    string? IntegrationBranch = null,
    int Count = 1);

public interface IChangeSetProvider
{
    string Id { get; }

    Task<IReadOnlyList<ChangeSet>> GetAsync(
        ChangeSetQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record ChangeSet(
    string Provider,
    string BaseCommit,
    string HeadCommit,
    string? MergeCommit,
    string Title,
    DateTimeOffset CommittedAt,
    IReadOnlyList<ChangedPath> TouchedFiles)
{
    [JsonIgnore]
    public string ResultCommit => MergeCommit ?? HeadCommit;
}

[JsonConverter(typeof(JsonStringEnumConverter<ChangeKind>))]
public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
}

public sealed record ChangedPath(
    string Path,
    ChangeKind Kind,
    string? PreviousPath,
    int? Additions,
    int? Deletions,
    bool ContentChanged,
    bool Binary = false);

public sealed record ChangeReviewDocument(
    [property: JsonPropertyName("$schema")]
    string Schema,
    int SchemaVersion,
    ChangeSetSubject ChangeSet,
    DateTimeOffset ReviewedAt,
    IReadOnlyList<TouchedHierarchyUnit> TouchedUnits,
    DeterministicChangeDelta Delta,
    ChangeJudgement Judgement,
    ChangeReviewEconomy Economy,
    ChangeReviewVerdict Verdict,
    string Summary);

public sealed record ChangeSetSubject(
    string Provider,
    string BaseCommit,
    string HeadCommit,
    string? MergeCommit,
    string Title,
    DateTimeOffset CommittedAt,
    IReadOnlyList<ChangedPath> TouchedFiles);

public sealed record TouchedHierarchyUnit(
    string Id,
    string Adapter,
    string Level,
    string Path,
    string DisplayName);

public sealed record DeterministicChangeDelta(
    IReadOnlyList<UnitGradeDelta> Grades,
    FindingDelta Findings,
    IReadOnlyList<UnitStalenessDelta> NewlyStale,
    BoundaryDelta Boundaries,
    CoverageDelta Coverage,
    ChangeChurn Churn,
    bool OnlyMoves,
    bool HasQualityDelta);

public sealed record UnitGradeDelta(
    string UnitId,
    string UnitPath,
    string Kind,
    GradeSnapshot? Before,
    GradeSnapshot? After,
    int? ScoreChange,
    bool Regression);

public sealed record GradeSnapshot(int Score, string Band);

public sealed record FindingDelta(
    IReadOnlyList<FindingDeltaItem> New,
    IReadOnlyList<FindingDeltaItem> Resolved,
    IReadOnlyList<FindingDeltaItem> Persisting);

public sealed record FindingDeltaItem(
    string Identity,
    string UnitId,
    string UnitPath,
    string Kind,
    string RuleId,
    string Severity,
    string Title,
    string? Aspect = null,
    string? Description = null,
    string? Recommendation = null,
    IReadOnlyList<FindingLocation>? Locations = null,
    string? Evidence = null);

public sealed record UnitStalenessDelta(
    string UnitId,
    string UnitPath,
    string Kind,
    IReadOnlyList<string> Reasons);

public sealed record BoundaryDelta(
    IReadOnlyList<BoundaryChange> New,
    IReadOnlyList<BoundaryChange> Changed,
    IReadOnlyList<BoundaryChange> Removed);

public sealed record BoundaryChange(
    string Identity,
    string Kind,
    string Name,
    string Path,
    int Line,
    string UnitId);

public sealed record CoverageDelta(
    string Status,
    double? BeforePercent = null,
    double? AfterPercent = null,
    double? PercentagePointChange = null,
    string? Reason = null);

public sealed record ChangeChurn(
    int FilesTouched,
    int FilesAdded,
    int FilesModified,
    int FilesDeleted,
    int FilesMoved,
    int LinesAdded,
    int LinesDeleted,
    int RepositoryFiles,
    double BlastRadiusPercent);

public interface IChangeCoverageProvider
{
    Task<CoverageDelta> GetDeltaAsync(
        ChangeSet changeSet,
        CancellationToken cancellationToken = default);
}

public interface IChangeDeltaReviewer
{
    Task<ChangeJudgement> ReviewAsync(
        string repositoryRoot,
        ChangeSet changeSet,
        DeterministicChangeDelta delta,
        string diff,
        CancellationToken cancellationToken = default);
}

public sealed record ChangeJudgement(
    string Status,
    string Reviewer,
    IReadOnlyList<ChangeJudgementAspect> Aspects,
    string Summary,
    string? Provider = null,
    string? RunId = null,
    ChangeReviewPromptProvenance? Prompt = null,
    TokenUsage? Usage = null,
    string? UnavailableReason = null)
{
    public static ChangeJudgement NotRun { get; } = new(
        "not-run",
        "none",
        [
            new("risk", "Risk of the change", "not-reviewed", "Agent judgement was not requested."),
            new("test-evidence", "Test evidence", "not-reviewed", "Agent judgement was not requested."),
            new("scope-discipline", "Scope discipline", "not-reviewed", "Agent judgement was not requested."),
            new("architecture-drift", "Architecture drift", "not-reviewed", "Agent judgement was not requested."),
        ],
        "Deterministic evidence only.");
}

public sealed record ChangeReviewPromptProvenance(
    string Id,
    string Version,
    string TemplateHash,
    string? EffectivePromptHash = null);

public sealed record ChangeJudgementAspect(
    string Id,
    string Title,
    string Verdict,
    string Rationale);

[JsonConverter(typeof(JsonStringEnumConverter<ChangeReviewVerdict>))]
public enum ChangeReviewVerdict
{
    NoQualityDelta,
    Improved,
    Neutral,
    Regression,
}

public sealed record ChangeReviewEconomy(
    int DiffFiles,
    int FullSweepFiles,
    int DiffLines,
    int DiffCharacters,
    int FullSweepCharacters,
    double EvidenceRatioPercent,
    double SavedPercent);

public sealed record ChangeReviewResult(
    ChangeReviewDocument Document,
    string Path);

public sealed record ChangeReviewOptions(
    bool Persist = true,
    IChangeDeltaReviewer? Reviewer = null);

public sealed class ChangeReviewException(string message, Exception? innerException = null)
    : Exception(message, innerException);
