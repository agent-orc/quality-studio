namespace AgentOrchestrator.CodeQuality;

/// <summary>The direct and rolled-up state for one kind at one hierarchy node.</summary>
public sealed record KindAggregation(
    ReviewKind Kind,
    ReviewState Direct,
    ReviewState Descendants,
    ReviewState Overall);

/// <summary>Applies the Quality Studio hierarchy aggregation rules.</summary>
public static class HierarchyAggregation
{
    /// <summary>
    /// Aggregates one kind. Missing direct metadata is always reported as
    /// <see cref="ReviewState.NotReviewed"/>; it is not converted into a failure and does
    /// not mask descendant evidence. Among documents that do exist, stale is worst.
    /// </summary>
    public static KindAggregation For(HierarchyNode node, ReviewKind kind)
    {
        ArgumentNullException.ThrowIfNull(node);

        var direct = node.Documents.TryGetValue(kind, out var document)
            ? document.State
            : ReviewState.NotReviewed;
        var descendantStates = node.Children.Select(child => For(child, kind).Overall).ToArray();
        var descendants = WorstOf(descendantStates);
        return new(kind, direct, descendants, WorstOf([direct, descendants]));
    }

    public static IReadOnlyDictionary<ReviewKind, KindAggregation> ForAllKinds(HierarchyNode node) =>
        Enum.GetValues<ReviewKind>().ToDictionary(kind => kind, kind => For(node, kind));

    /// <summary>
    /// Returns stale when code changed, policy drift when only guidelines changed, current
    /// when reviewed evidence remains applicable, and not-reviewed only when none exists.
    /// </summary>
    public static ReviewState WorstOf(IEnumerable<ReviewState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var result = ReviewState.NotReviewed;
        foreach (var state in states)
        {
            if (state is ReviewState.Stale)
            {
                return ReviewState.Stale;
            }

            if (state is ReviewState.PolicyDrift)
            {
                result = ReviewState.PolicyDrift;
                continue;
            }

            if (state is ReviewState.Current && result is ReviewState.NotReviewed)
            {
                result = ReviewState.Current;
            }
        }

        return result;
    }
}
