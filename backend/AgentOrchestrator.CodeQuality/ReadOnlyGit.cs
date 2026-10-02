namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Options for Git queries that must not run a repository-configured filesystem monitor
/// or refresh the index as an optional side effect. These do not sandbox Git or bound its
/// process lifetime; callers retain their existing cancellation and error contracts.
/// </summary>
public static class ReadOnlyGit
{
    public static IReadOnlyList<string> WithSafetyOptions(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return ["--no-optional-locks", "-c", "core.fsmonitor=false", .. arguments];
    }
}
