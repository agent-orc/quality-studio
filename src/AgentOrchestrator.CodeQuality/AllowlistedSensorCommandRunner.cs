namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Wraps an <see cref="ISensorCommandRunner"/> and refuses to launch any executable that is not a bare,
/// host-configured name. Repository-owned sensor configuration (the "command" value on a SARIF/tsc analyzer)
/// can otherwise select an arbitrary executable path, including a shell; this closes that authorization-to-command
/// chain by resolving only host-approved tool names, never a repository-supplied path.
/// </summary>
public sealed class AllowlistedSensorCommandRunner(
    ISensorCommandRunner inner,
    IReadOnlyCollection<string> allowedExecutables) : ISensorCommandRunner
{
    private readonly IReadOnlySet<string> allowedExecutables = allowedExecutables
        .Select(executable => executable.Trim())
        .Where(executable => executable.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public Task<SensorCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (executable.IndexOfAny(['/', '\\']) >= 0 || !allowedExecutables.Contains(executable))
        {
            throw new SecurityScannerUnavailableException(
                $"Analyzer executable '{executable}' is not on the host-approved allowlist.");
        }
        return inner.RunAsync(executable, arguments, workingDirectory, cancellationToken);
    }
}
