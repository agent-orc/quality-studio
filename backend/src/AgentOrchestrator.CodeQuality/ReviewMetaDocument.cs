namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// A loaded review-meta document attached to a hierarchy unit. The complete document
/// remains available as <see cref="Payload"/> while aggregation only consumes its kind
/// and independently calculated staleness.
/// </summary>
public sealed record AttachedReviewMetaDocument
{
    public AttachedReviewMetaDocument(
        string unitId,
        ReviewKind kind,
        ReviewState state,
        string sourcePath,
        string? payload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (state is ReviewState.NotReviewed)
        {
            throw new ArgumentException("A metadata document must be current or stale.", nameof(state));
        }

        UnitId = unitId;
        Kind = kind;
        State = state;
        SourcePath = sourcePath;
        Payload = payload;
    }

    public string UnitId { get; }

    public ReviewKind Kind { get; }

    public ReviewState State { get; }

    public string SourcePath { get; }

    public string? Payload { get; }
}
