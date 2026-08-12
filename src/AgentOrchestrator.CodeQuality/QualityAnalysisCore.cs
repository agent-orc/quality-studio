namespace AgentOrchestrator.CodeQuality;

/// <summary>Stable names for the analyses supplied by the package.</summary>
public static class QualityAnalysisNames
{
    public const string Boundaries = "boundaries";
    public const string Coverage = "coverage";
    public const string Dependencies = "dependencies";
    public const string Eslint = "eslint";
    public const string Gitleaks = "gitleaks";
    public const string Roslyn = "roslyn";
    public const string Sarif = "sarif";
    public const string TypeScript = "tsc";
}

/// <summary>The repository scope at which a named analysis runs.</summary>
public enum QualityAnalysisScope
{
    Repository,
    Path,
}

/// <summary>A named analysis and the configuration passed only to that analysis.</summary>
public sealed record NamedQualityAnalysis(
    string Name,
    IReadOnlyDictionary<string, string>? Configuration = null,
    QualityAnalysisScope Scope = QualityAnalysisScope.Repository,
    string? Path = null);

/// <summary>
/// An in-process request. PersistArtifacts is opt-in so pipeline and CI consumers can
/// obtain findings without changing the repository working tree.
/// </summary>
public sealed record QualityAnalysisRequest(
    string RepositoryPath,
    IReadOnlyList<NamedQualityAnalysis> Analyses,
    bool PersistArtifacts = false);

/// <summary>Discoverable metadata for an analysis registered with the core.</summary>
public sealed record QualityAnalysisDescriptor(
    string Name,
    string Version,
    IReadOnlyList<QualityAnalysisScope> SupportedScopes);

/// <summary>The result of one named analysis.</summary>
public sealed record NamedQualityAnalysisResult(
    string Name,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<ReviewFinding> Findings,
    SensorProvenance Provenance);

/// <summary>The complete result returned to an in-process caller.</summary>
public sealed record QualityAnalysisResult(
    string RepositoryPath,
    IReadOnlyList<NamedQualityAnalysisResult> Analyses)
{
    public IReadOnlyList<ReviewFinding> Findings =>
        Analyses.SelectMany(analysis => analysis.Findings).ToArray();
}

/// <summary>
/// Runs Quality Studio analyses directly in the caller's process. It has no dependency
/// on the Quality Studio HTTP host or UI.
/// </summary>
public sealed class QualityAnalysisCore
{
    private readonly IReadOnlyDictionary<string, IReviewSensor> analyses;

    public QualityAnalysisCore(IEnumerable<IReviewSensor> analyses)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        this.analyses = analyses.ToDictionary(analysis => analysis.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Creates the package's standard deterministic analysis set.</summary>
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

    public IReadOnlyList<QualityAnalysisDescriptor> ListAnalyses() => analyses.Values
        .Select(analysis => new QualityAnalysisDescriptor(
            analysis.Id,
            analysis.Version,
            analysis.SupportedScopes.Select(ToPublicScope).ToArray()))
        .OrderBy(analysis => analysis.Name, StringComparer.Ordinal)
        .ToArray();

    public async Task<QualityAnalysisResult> RunAsync(
        QualityAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentNullException.ThrowIfNull(request.Analyses);
        if (request.Analyses.Count == 0)
        {
            throw new ArgumentException("At least one named analysis is required.", nameof(request));
        }

        var repositoryPath = Path.GetFullPath(request.RepositoryPath);
        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {repositoryPath}");
        }

        var results = new List<NamedQualityAnalysisResult>(request.Analyses.Count);
        foreach (var requested in request.Analyses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(requested.Name);
            if (!analyses.TryGetValue(requested.Name, out var analysis))
            {
                throw new QualityAnalysisNotFoundException(
                    $"Analysis '{requested.Name}' was not found. Available analyses: " +
                    string.Join(", ", analyses.Keys.Order(StringComparer.Ordinal)) + ".");
            }

            var scope = ToSensorScope(requested.Scope);
            if (!analysis.SupportedScopes.Contains(scope))
            {
                throw new ArgumentException(
                    $"Analysis '{analysis.Id}' does not support {requested.Scope.ToString().ToLowerInvariant()} scope.",
                    nameof(request));
            }

            var result = await analysis.RunAsync(new SensorScanRequest(
                repositoryPath,
                scope,
                requested.Path,
                requested.Configuration,
                request.PersistArtifacts), cancellationToken).ConfigureAwait(false);
            results.Add(new NamedQualityAnalysisResult(
                analysis.Id,
                result.Available,
                result.UnavailableReason,
                result.Findings,
                result.Provenance));
        }

        return new QualityAnalysisResult(repositoryPath, results);
    }

    private static QualityAnalysisScope ToPublicScope(SensorScope scope) => scope switch
    {
        SensorScope.Repository => QualityAnalysisScope.Repository,
        SensorScope.Path => QualityAnalysisScope.Path,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    private static SensorScope ToSensorScope(QualityAnalysisScope scope) => scope switch
    {
        QualityAnalysisScope.Repository => SensorScope.Repository,
        QualityAnalysisScope.Path => SensorScope.Path,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };
}

public sealed class QualityAnalysisNotFoundException(string message) : Exception(message);
