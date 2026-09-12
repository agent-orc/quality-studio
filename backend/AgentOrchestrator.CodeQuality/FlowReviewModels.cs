using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum FlowReviewVerdict
{
    Pass,
    Fail,
    Undetermined,
}

public enum BusinessLogicClass
{
    SessionLifecycle,
    HorizontalPrivilegeEscalation,
    VerticalPrivilegeEscalation,
    ObjectOwnership,
    FlowBypass,
    Replay,
    RaceCondition,
    QuotaAbuse,
    UnenforcedInvariant,
}

public enum FlowPathStage
{
    Entry,
    Authentication,
    Authorization,
    StateTransition,
    Persistence,
    Response,
    External,
}

public sealed record FlowDefinition(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> EntryBoundaryIds,
    IReadOnlyList<string>? ExternalDependencies = null);

public sealed record FlowReviewRequest(
    string RepositoryRoot,
    FlowDefinition Flow,
    BoundaryInventory BoundaryInventory,
    string DataModel,
    string CallGraph,
    IReadOnlyList<string> SubjectFiles,
    bool PersistMetadata = true);

public sealed record FlowPathStep(
    int Order,
    FlowPathStage Stage,
    string Path,
    int Line,
    string Symbol,
    string Action);

public sealed record FlowFinding(
    string Id,
    string Fingerprint,
    string RuleId,
    BusinessLogicClass Class,
    FindingSeverity Severity,
    string Title,
    string Description,
    string Recommendation,
    int WeakestPointIndex,
    IReadOnlyList<FlowPathStep> FlowPath,
    FindingState State = FindingState.Open);

public sealed record FlowReviewCost(
    string Status,
    decimal? Amount,
    string? Currency);

public sealed record FlowReviewProvenance(
    string Agent,
    string Model,
    string RunId,
    string PromptId,
    string PromptVersion,
    string PromptHash,
    string InputHash,
    string BoundaryCatalogueHash,
    DateTimeOffset ReviewedAt,
    TokenUsage Usage,
    FlowReviewCost Cost);

public sealed record FlowFindingCounts(
    int Total,
    int Open,
    int Accepted,
    int Waived,
    int FalsePositive,
    int Resolved);

public sealed record FlowReviewReport(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    FlowDefinition Flow,
    FlowReviewVerdict Verdict,
    string Summary,
    string? UndeterminedReason,
    IReadOnlyList<FlowFinding> Findings,
    FlowFindingCounts FindingCounts,
    FlowReviewProvenance Provenance);

public sealed record FlowReviewResult(string? ReportPath, FlowReviewReport Report);

public sealed record FlowReviewStaleness(
    bool Stale,
    IReadOnlyList<string> Reasons);
