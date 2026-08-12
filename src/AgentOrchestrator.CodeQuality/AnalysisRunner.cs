using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Analysis;

/// <summary>A named analysis and the configuration passed only to that analysis.</summary>
public sealed record NamedAnalysis(
    string Name,
    IReadOnlyDictionary<string, string>? Configuration = null);

/// <summary>The stable in-process request for running Quality Studio analyses.</summary>
public sealed record AnalysisRunRequest(
    string RepositoryPath,
    IReadOnlyList<NamedAnalysis> Analyses,
    string? RelativePath = null,
    bool PersistMetadata = false);

/// <summary>The result of one named analysis.</summary>
public sealed record NamedAnalysisResult(
    string Name,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<ReviewFinding> Findings,
    SensorProvenance Provenance);

/// <summary>The aggregate result returned to an in-process caller.</summary>
public sealed record AnalysisRunResult(
    string RepositoryPath,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<NamedAnalysisResult> Analyses,
    IReadOnlyList<ReviewFinding> Findings);

public interface IAnalysisRunner
{
    IReadOnlyList<string> AvailableAnalyses { get; }

    Task<AnalysisRunResult> RunAsync(
        AnalysisRunRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs named Quality Studio analyses directly in the caller process. The constructor is
/// the extension seam for rule-library supplied sensors; HTTP is not the invocation transport.
/// </summary>
public sealed class AnalysisRunner : IAnalysisRunner
{
    private readonly SensorRegistry registry;

    public AnalysisRunner()
        : this(BuiltInAnalyses())
    {
    }

    public AnalysisRunner(IEnumerable<IReviewSensor> analyses)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        registry = new SensorRegistry(analyses);
        AvailableAnalyses = registry.List().Select(analysis => analysis.Id).ToArray();
    }

    public IReadOnlyList<string> AvailableAnalyses { get; }

    public async Task<AnalysisRunResult> RunAsync(
        AnalysisRunRequest request,
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
        var relativePath = NormalizeRelativePath(root, request.RelativePath);

        var requested = request.Analyses.Select(Validate).ToArray();
        var duplicate = requested.GroupBy(analysis => analysis.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Analysis '{duplicate.Key}' was requested more than once.", nameof(request));

        // Resolve all names before starting work so an invalid request never produces a partial run.
        var resolved = requested.Select(analysis => (Request: analysis, Sensor: registry.Get(analysis.Name))).ToArray();
        var scope = relativePath is null ? SensorScope.Repository : SensorScope.Path;
        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<NamedAnalysisResult>(resolved.Length);
        foreach (var (analysis, sensor) in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sensor.SupportedScopes.Contains(scope))
                throw new ArgumentException(
                    $"Analysis '{sensor.Id}' does not support {scope.ToString().ToLowerInvariant()} scope.",
                    nameof(request));

            var result = await sensor.RunAsync(new SensorScanRequest(
                root,
                scope,
                relativePath,
                analysis.Configuration,
                request.PersistMetadata), cancellationToken).ConfigureAwait(false);
            results.Add(new NamedAnalysisResult(
                sensor.Id,
                result.Available,
                result.UnavailableReason,
                result.Findings,
                result.Provenance));
        }

        var findings = results.SelectMany(result => result.Findings)
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .OrderBy(finding => finding.Severity)
            .ThenBy(finding => finding.Locations.FirstOrDefault()?.Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ToArray();
        return new AnalysisRunResult(root, startedAt, DateTimeOffset.UtcNow, results, findings);
    }

    private static NamedAnalysis Validate(NamedAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrWhiteSpace(analysis.Name);
        return analysis with { Name = analysis.Name.Trim() };
    }

    private static string? NormalizeRelativePath(string root, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Path.IsPathRooted(value))
            throw new ArgumentException("Analysis paths must be repository-relative.", nameof(value));
        var target = Path.GetFullPath(Path.Combine(root, value));
        var relative = Path.GetRelativePath(root, target);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new ArgumentException("Analysis paths cannot escape the repository.", nameof(value));
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException($"Analysis path does not exist: {target}");
        return relative.Replace('\\', '/');
    }

    private static IEnumerable<IReviewSensor> BuiltInAnalyses() =>
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
