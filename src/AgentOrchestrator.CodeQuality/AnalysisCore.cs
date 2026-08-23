namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Stable, in-process entry point for running Quality Studio's deterministic sensors over a repository
/// path. This is the package's public surface for callers that embed analysis (Agent Studio pipeline
/// steps, quality-cli, CI jobs) without hosting QualityStudio.Api or a dependency-injection container.
/// </summary>
public sealed class AnalysisCore
{
    private readonly SensorRegistry sensors;

    public AnalysisCore(IEnumerable<IReviewSensor> sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        this.sensors = new SensorRegistry(sensors);
    }

    /// <summary>The analysis ids this instance can run, e.g. "gitleaks", "dependency-vulnerability",
    /// "boundary-inventory", "coverage", "roslyn", "eslint", "typescript".</summary>
    public IReadOnlyList<string> AnalysisIds =>
        sensors.List().Select(sensor => sensor.Id).ToArray();

    /// <summary>
    /// Builds an <see cref="AnalysisCore"/> wired to Quality Studio's built-in deterministic sensors using
    /// their default, dependency-free constructors. Individual sensors probe their own tool availability
    /// (git, gitleaks, dotnet, npx/eslint, tsc) at run time and report themselves unavailable rather than
    /// throwing when a tool is missing.
    /// </summary>
    public static AnalysisCore CreateDefault() => new(
    [
        new GitleaksSecurityScanner(),
        new DependencyVulnerabilitySensor(),
        new BoundaryInventorySensor(),
        new CoverageSensor(),
        new RoslynAnalyzerSensor(),
        new EslintAnalyzerSensor(),
        new TypeScriptAnalyzerSensor(),
    ]);

    /// <summary>Runs one named analysis over a repository path and returns its findings.</summary>
    /// <exception cref="SensorNotFoundException">No analysis is registered under <paramref name="analysisId"/>.</exception>
    public Task<SensorScanResult> RunAsync(
        string analysisId,
        string repositoryRoot,
        SensorScope scope = SensorScope.Repository,
        string? path = null,
        IReadOnlyDictionary<string, string>? configuration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(analysisId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var sensor = sensors.Get(analysisId);
        var request = new SensorScanRequest(repositoryRoot, scope, path, configuration);
        return sensor.RunAsync(request, cancellationToken);
    }

    /// <summary>
    /// Runs every requested analysis (or all registered analyses when <paramref name="analysisIds"/> is
    /// null) over a repository path and returns each analysis's result keyed by its id. A per-analysis
    /// failure to run does not stop the others; each sensor reports its own availability and findings.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SensorScanResult>> RunAsync(
        string repositoryRoot,
        IReadOnlyList<string>? analysisIds = null,
        IReadOnlyDictionary<string, string>? configuration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var targets = analysisIds is null
            ? sensors.List()
            : analysisIds.Select(sensors.Get).ToArray();

        var results = new Dictionary<string, SensorScanResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var sensor in targets)
        {
            var request = new SensorScanRequest(repositoryRoot, Configuration: configuration);
            results[sensor.Id] = await sensor.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }
}
