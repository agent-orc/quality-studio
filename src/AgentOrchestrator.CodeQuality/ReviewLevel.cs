namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Identifies the hierarchy level addressed by a review.
/// </summary>
public enum ReviewLevel
{
    /// <summary>Reviews an entire project.</summary>
    Project,

    /// <summary>Reviews a module within a project.</summary>
    Module,

    /// <summary>Reviews a namespace within a module.</summary>
    Namespace,

    /// <summary>Reviews a source file.</summary>
    File,

    /// <summary>Reviews a function or method.</summary>
    Function,
}
