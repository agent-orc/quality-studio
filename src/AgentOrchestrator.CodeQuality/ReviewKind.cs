namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Identifies the quality characteristic assessed by a review.
/// </summary>
public enum ReviewKind
{
    /// <summary>Reviews code quality and maintainability.</summary>
    Code,

    /// <summary>Reviews security risks and safeguards.</summary>
    Security,

    /// <summary>Reviews runtime performance and resource usage.</summary>
    Performance,
}
