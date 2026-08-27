namespace AgentOrchestrator.CodeQuality;

/// <summary>Selects one registered analysis and supplies analysis-specific configuration.</summary>
public sealed record QualityAnalysisSelection(
    string Name,
    IReadOnlyDictionary<string, string>? Configuration = null);

/// <summary>
/// Describes an in-process analysis run. RepositoryPath is always resolved by the runner;
/// Path, when present, remains repository-relative and is confined by the selected analysis.
/// </summary>
public sealed record QualityAnalysisRequest(
    string RepositoryPath,
    IReadOnlyList<QualityAnalysisSelection> Analyses,
    SensorScope Scope = SensorScope.Repository,
    string? Path = null,
    bool PersistMetadata = false);

/// <summary>The output of one named analysis.</summary>
public sealed record QualityAnalysisExecution(
    string Name,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<ReviewFinding> Findings,
    SensorProvenance Provenance);

/// <summary>A complete run result. Findings preserves the Quality Studio finding model.</summary>
public sealed record QualityAnalysisResult(
    string RepositoryPath,
    IReadOnlyList<QualityAnalysisExecution> Analyses)
{
    public IReadOnlyList<ReviewFinding> Findings { get; } = Analyses
        .SelectMany(analysis => analysis.Findings)
        .ToArray();
}

/// <summary>Stable host-facing contract for running Quality Studio analyses in process.</summary>
public interface IQualityAnalysisRunner
{
    IReadOnlyList<string> AvailableAnalyses { get; }

    Task<QualityAnalysisResult> RunAsync(
        QualityAnalysisRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs registered analyses without an HTTP host. Hosts can use <see cref="CreateDefault"/>
/// or inject their own sensors, including adapters backed by an external rule library.
/// </summary>
public sealed class QualityAnalysisRunner : IQualityAnalysisRunner
{
    private readonly SensorRegistry registry;

    public QualityAnalysisRunner(IEnumerable<IReviewSensor> analyses)
        : this(new SensorRegistry(analyses))
    {
    }

    public QualityAnalysisRunner(SensorRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        AvailableAnalyses = registry.List().Select(sensor => sensor.Id).ToArray();
    }

    public IReadOnlyList<string> AvailableAnalyses { get; }

    /// <summary>Creates the package's built-in deterministic analysis set.</summary>
    public static QualityAnalysisRunner CreateDefault() => new(
    [
        new BoundaryInventorySensor(),
        new CoverageSensor(),
        new DependencyVulnerabilitySensor(),
        new EslintAnalyzerSensor(),
        new GitleaksSecurityScanner(),
        new RoslynAnalyzerSensor(),
        new SarifSensor(),
        new TypeScriptAnalyzerSensor(),
    ]);

    public async Task<QualityAnalysisResult> RunAsync(
        QualityAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentNullException.ThrowIfNull(request.Analyses);
        if (request.Analyses.Count == 0)
            throw new ArgumentException("At least one named analysis is required.", nameof(request));

        var root = System.IO.Path.GetFullPath(request.RepositoryPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");

        var duplicate = request.Analyses
            .GroupBy(selection => selection.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Analysis '{duplicate.Key}' was selected more than once.", nameof(request));

        var executions = new List<QualityAnalysisExecution>(request.Analyses.Count);
        foreach (var selection in request.Analyses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(selection.Name);
            var analysis = registry.Get(selection.Name);
            if (!analysis.SupportedScopes.Contains(request.Scope))
                throw new ArgumentException(
                    $"Analysis '{analysis.Id}' does not support {request.Scope.ToString().ToLowerInvariant()} scope.",
                    nameof(request));

            var result = await analysis.RunAsync(new SensorScanRequest(
                root,
                request.Scope,
                request.Path,
                selection.Configuration,
                request.PersistMetadata), cancellationToken).ConfigureAwait(false);
            executions.Add(new QualityAnalysisExecution(
                analysis.Id,
                result.Available,
                result.UnavailableReason,
                result.Findings,
                result.Provenance));
        }

        return new QualityAnalysisResult(root, executions);
    }
}
