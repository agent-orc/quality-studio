namespace AgentOrchestrator.CodeQuality;

/// <summary>Names of analyses supplied by the default in-process runner.</summary>
public static class AnalysisNames
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

/// <summary>Configuration shared by every named analysis in one run.</summary>
public sealed record AnalysisConfiguration
{
    public SensorScope Scope { get; init; } = SensorScope.Repository;

    public string? Path { get; init; }

    /// <summary>
    /// Caller-owned overrides keyed first by analysis name and then by setting name.
    /// Values override settings supplied by <see cref="IAnalysisRuleProvider"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Settings { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Allows analyzers to write their documented repository artifacts. Defaults to false for library callers.</summary>
    public bool PersistArtifacts { get; init; }
}

/// <summary>Runs one or more named analyses against a local repository.</summary>
public sealed record AnalysisRequest(
    string RepositoryPath,
    IReadOnlyList<string> Analyses,
    AnalysisConfiguration? Configuration = null);

/// <summary>External rule content resolved for one named analysis.</summary>
public sealed record AnalysisRuleSet(
    string Id,
    string Version,
    IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// Supplies rule-library content without coupling the analysis package to a rule package,
/// registry, HTTP endpoint, or UI host.
/// </summary>
public interface IAnalysisRuleProvider
{
    ValueTask<AnalysisRuleSet?> GetRulesAsync(
        string analysisName,
        string repositoryPath,
        CancellationToken cancellationToken = default);
}

public sealed record AnalysisDescriptor(
    string Name,
    string Version,
    IReadOnlyList<SensorScope> SupportedScopes);

/// <summary>Result of one named analysis, including the canonical Quality Studio finding model.</summary>
public sealed record AnalysisExecution(
    string Name,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<ReviewFinding> Findings,
    SensorProvenance Provenance,
    string? RuleSetId = null,
    string? RuleSetVersion = null);

/// <summary>Complete result of an in-process analysis request.</summary>
public sealed record AnalysisResult(
    string RepositoryPath,
    IReadOnlyList<AnalysisExecution> Executions)
{
    public IReadOnlyList<ReviewFinding> Findings => Executions.SelectMany(execution => execution.Findings).ToArray();

    public bool Available => Executions.All(execution => execution.Available);
}

/// <summary>
/// Stable, host-neutral entry point for Quality Studio analysis. It owns orchestration only;
/// rule content arrives through <see cref="IAnalysisRuleProvider"/> or request settings.
/// </summary>
public sealed class AnalysisRunner
{
    private readonly SensorRegistry registry;
    private readonly IAnalysisRuleProvider? ruleProvider;

    public AnalysisRunner(
        IEnumerable<IReviewSensor>? analyses = null,
        IAnalysisRuleProvider? ruleProvider = null)
    {
        registry = new SensorRegistry(analyses ?? DefaultAnalyses());
        this.ruleProvider = ruleProvider;
    }

    public IReadOnlyList<AnalysisDescriptor> ListAnalyses() => registry.List()
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
        {
            throw new ArgumentException("At least one analysis name is required.", nameof(request));
        }

        var names = request.Analyses.Select(NormalizeName).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
        {
            throw new ArgumentException("Analysis names must be unique.", nameof(request));
        }

        var repositoryPath = Path.GetFullPath(request.RepositoryPath);
        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {repositoryPath}");
        }

        var configuration = request.Configuration ?? new AnalysisConfiguration();
        if (configuration.Scope == SensorScope.Path && string.IsNullOrWhiteSpace(configuration.Path))
        {
            throw new ArgumentException("Path-scoped analysis requires a relative path.", nameof(request));
        }

        var executions = new List<AnalysisExecution>(names.Length);
        foreach (var requestedName in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sensor = registry.Get(requestedName);
            var name = sensor.Id;
            if (!sensor.SupportedScopes.Contains(configuration.Scope))
            {
                throw new ArgumentException(
                    $"Analysis '{name}' does not support {configuration.Scope.ToString().ToLowerInvariant()} scope.",
                    nameof(request));
            }

            var ruleSet = ruleProvider is null
                ? null
                : await ruleProvider.GetRulesAsync(name, repositoryPath, cancellationToken).ConfigureAwait(false);
            var settings = MergeSettings(ruleSet?.Settings, GetOverrides(configuration.Settings, name));
            var result = await sensor.RunAsync(new SensorScanRequest(
                    repositoryPath,
                    configuration.Scope,
                    configuration.Path,
                    settings,
                    configuration.PersistArtifacts), cancellationToken)
                .ConfigureAwait(false);
            executions.Add(new AnalysisExecution(
                name,
                result.Available,
                result.UnavailableReason,
                result.Findings,
                result.Provenance,
                ruleSet?.Id,
                ruleSet?.Version));
        }

        return new AnalysisResult(repositoryPath, executions);
    }

    private static string NormalizeName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }

    private static IReadOnlyDictionary<string, string>? GetOverrides(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> settings,
        string name)
    {
        foreach (var pair in settings)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string>? MergeSettings(
        IReadOnlyDictionary<string, string>? rules,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (rules is null && overrides is null) return null;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rules is not null)
        {
            foreach (var pair in rules) merged[pair.Key] = pair.Value;
        }

        if (overrides is not null)
        {
            foreach (var pair in overrides) merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private static IEnumerable<IReviewSensor> DefaultAnalyses()
    {
        yield return new BoundaryInventorySensor();
        yield return new CoverageSensor();
        yield return new DependencyVulnerabilitySensor();
        yield return new EslintAnalyzerSensor();
        yield return new GitleaksSecurityScanner();
        yield return new RoslynAnalyzerSensor();
        yield return new SarifSensor();
        yield return new TypeScriptAnalyzerSensor();
    }
}
