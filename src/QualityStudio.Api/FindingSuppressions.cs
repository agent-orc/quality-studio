using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record FindingSuppressionMutationRequest(
    string Path,
    string Kind,
    string Fingerprint,
    string Author,
    string Reason,
    DateTimeOffset? ExpiresAt);

public sealed record FindingSuppressionRemovalRequest(string Fingerprint, string Author);

public sealed record FindingSuppressionsResponse(IReadOnlyList<FindingSuppression> Ignored);
