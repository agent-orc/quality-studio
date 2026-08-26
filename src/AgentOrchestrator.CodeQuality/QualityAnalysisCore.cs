namespace AgentOrchestrator.CodeQuality;

/// <summary>A named analysis and the configuration understood by that analysis.</summary>
public sealed record NamedAnalysis(
    string Name,
    IReadOnlyDictionary<string, string>? Configuration = null,
    SensorScope Scope = SensorScope.Repository,
    string? Path = null);

/// <summary>Runs one or more named analyses against an existing repository checkout.</summary>
public sealed record QualityAnalysisRequest(
    string RepositoryPath,
    IReadOnlyList<NamedAnalysis> Analyses,
    bool PersistMetadata = false);

/// <summary>Discovery metadata for an analysis available from this package instance.</summary>
public sealed record QualityAnalysisDescriptor(
    string Name,
    string Version,
    IReadOnlyList<SensorScope> SupportedScopes);

/// <summary>The outcome and Quality Studio findings produced by one named analysis.</summary>
public sealed record QualityAnalysisExecution(
    string Name,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<ReviewFinding> Findings,
    SensorProvenance Provenance);

/// <summary>The combined, in-process result returned to a pipeline, CLI, or CI host.</summary>
public sealed record QualityAnalysisResult(
    string RepositoryPath,
    IReadOnlyList<QualityAnalysisExecution> Analyses,
    IReadOnlyList<ReviewFinding> Findings);

public interface IQualityAnalysisCore
{
    IReadOnlyList<QualityAnalysisDescriptor> ListAnalyses();

    Task<QualityAnalysisResult> RunAsync(
        QualityAnalysisRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stable in-process entry point for Quality Studio analyses. Hosting, path authorization,
/// repository lifecycle, and publication of the returned findings remain caller concerns.
/// </summary>
public sealed class QualityAnalysisCore : IQualityAnalysisCore
{
    private readonly SensorRegistry registry;

    public QualityAnalysisCore(IEnumerable<IReviewSensor> analyses)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        registry = new SensorRegistry(analyses);
    }

    /// <summary>Creates the first-party analysis set shipped in this package.</summary>
    public static QualityAnalysisCore CreateDefault() => new(
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

    public IReadOnlyList<QualityAnalysisDescriptor> ListAnalyses() => registry.List()
        .Select(analysis => new QualityAnalysisDescriptor(
            analysis.Id,
            analysis.Version,
            analysis.SupportedScopes))
        .ToArray();

    public async Task<QualityAnalysisResult> RunAsync(
        QualityAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentNullException.ThrowIfNull(request.Analyses);
        if (request.Analyses.Count == 0)
            throw new ArgumentException("At least one named analysis is required.", nameof(request));

        var repositoryPath = Path.GetFullPath(request.RepositoryPath);
        if (!Directory.Exists(repositoryPath))
            throw new DirectoryNotFoundException($"Repository path does not exist: {repositoryPath}");

        var executions = new List<QualityAnalysisExecution>(request.Analyses.Count);
        foreach (var named in request.Analyses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(named.Name);
            var analysis = registry.Get(named.Name);
            if (!analysis.SupportedScopes.Contains(named.Scope))
                throw new ArgumentException(
                    $"Analysis '{analysis.Id}' does not support {named.Scope.ToString().ToLowerInvariant()} scope.",
                    nameof(request));
            if (named.Scope == SensorScope.Path && string.IsNullOrWhiteSpace(named.Path))
                throw new ArgumentException(
                    $"Analysis '{analysis.Id}' requires Path for path scope.", nameof(request));

            var result = await analysis.RunAsync(new SensorScanRequest(
                repositoryPath,
                named.Scope,
                named.Path,
                named.Configuration,
                request.PersistMetadata), cancellationToken).ConfigureAwait(false);
            executions.Add(new QualityAnalysisExecution(
                analysis.Id,
                result.Available,
                result.UnavailableReason,
                result.Findings,
                result.Provenance));
        }

        var findings = executions
            .SelectMany(execution => execution.Findings)
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .OrderBy(finding => finding.Locations.FirstOrDefault()?.Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ToArray();
        return new QualityAnalysisResult(repositoryPath, executions, findings);
    }
}
