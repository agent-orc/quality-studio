namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Per-unit review state as defined by the staleness contract in docs/concept.md.
/// <see cref="Invalid"/> takes precedence over every content comparison: a sidecar whose JSON,
/// schema, or required fields cannot be validated is never fresh and never quietly stale.
/// </summary>
public enum StalenessState
{
    Fresh,
    Stale,
    PolicyDrift,
    Missing,
    Invalid,
}
