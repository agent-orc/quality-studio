namespace AgentOrchestrator.CodeQuality;

/// <summary>Configuration for one named in-process analysis.</summary>
public sealed record AnalysisConfiguration(
    string Name,
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>A repository analysis request. Rule and tool settings remain caller-owned content.</summary>
public sealed record AnalysisRequest(
    string RepositoryPath,
    IReadOnlyList<AnalysisConfiguration> Analyses,
    SensorScope Scope = SensorScope.Repository,
    string? Path = null);

/// <summary>The result of one named analysis, including an explicit unavailable state.</summary>
public sealed record NamedAnalysisResult(
    string Name,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<ReviewFinding> Findings,
    SensorProvenance Provenance);

/// <summary>A complete in-process analysis result using Quality Studio's finding model.</summary>
public sealed record AnalysisResult(
    string RepositoryPath,
    IReadOnlyList<NamedAnalysisResult> Analyses,
    IReadOnlyList<ReviewFinding> Findings);

/// <summary>
/// Stable host-independent entry point for running named Quality Studio analyses.
/// Callers can use the built-in analyses or inject additional <see cref="IReviewSensor"/>
/// implementations, including analyses backed by a separately versioned rule library.
/// </summary>
public sealed class AnalysisRunner
{
    private readonly SensorRegistry sensors;

    public AnalysisRunner(IEnumerable<IReviewSensor>? analyses = null) =>
        sensors = new SensorRegistry(analyses ?? BuiltInAnalyses());

    /// <summary>Describes the analyses available to this runner without probing external tools.</summary>
    public IReadOnlyList<AnalysisDescriptor> ListAnalyses() => sensors.List()
        .Select(sensor => new AnalysisDescriptor(sensor.Id, sensor.Version, sensor.SupportedScopes))
        .ToArray();

    public async Task<AnalysisResult> RunAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentNullException.ThrowIfNull(request.Analyses);
        if (request.Analyses.Count == 0)
            throw new ArgumentException("At least one named analysis is required.", nameof(request));

        var root = Path.GetFullPath(request.RepositoryPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        if (request.Scope == SensorScope.Path && string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException("A relative path is required for path-scoped analysis.", nameof(request));

        var duplicate = request.Analyses
            .GroupBy(analysis => analysis.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Analysis '{duplicate.Key}' is configured more than once.", nameof(request));

        var results = new List<NamedAnalysisResult>(request.Analyses.Count);
        foreach (var configured in request.Analyses)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configured.Name);
            var sensor = sensors.Get(configured.Name);
            if (!sensor.SupportedScopes.Contains(request.Scope))
                throw new ArgumentException(
                    $"Analysis '{configured.Name}' does not support scope '{request.Scope}'.",
                    nameof(request));

            var result = await sensor.RunAsync(
                new SensorScanRequest(
                    root,
                    request.Scope,
                    request.Path,
                    configured.Settings,
                    PersistMetadata: false),
                cancellationToken).ConfigureAwait(false);
            results.Add(new NamedAnalysisResult(
                sensor.Id,
                result.Available,
                result.UnavailableReason,
                result.Findings,
                result.Provenance));
        }

        return new AnalysisResult(
            root,
            results,
            results.SelectMany(result => result.Findings).ToArray());
    }

    private static IReadOnlyList<IReviewSensor> BuiltInAnalyses() =>
    [
        new BoundaryInventorySensor(),
        new CoverageSensor(),
        new DependencyVulnerabilitySensor(),
        new EslintAnalyzerSensor(),
        new GitleaksSecurityScanner(),
        new RoslynAnalyzerSensor(),
        new SarifSensor(),
        new TypeScriptAnalyzerSensor(),
    ];
}

public sealed record AnalysisDescriptor(
    string Name,
    string Version,
    IReadOnlyList<SensorScope> SupportedScopes);
