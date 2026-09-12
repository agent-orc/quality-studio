using System.Security.Cryptography;
using System.Text;
using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Stable names for analyses shipped with the core package.</summary>
public static class QualityAnalysisNames
{
    public const string Architecture = "architecture";
    public const string Boundaries = "boundaries";
    public const string Coverage = "coverage";
    public const string Dependencies = "dependencies";
    public const string Eslint = "eslint";
    public const string Gitleaks = "gitleaks";
    public const string Roslyn = "roslyn";
    public const string Sarif = "sarif";
    public const string TypeScript = "tsc";
}

/// <summary>A named analysis and its repository-local configuration.</summary>
public sealed record QualityAnalysisDefinition(
    string Name,
    IReadOnlyDictionary<string, string>? Configuration = null,
    SensorScope Scope = SensorScope.Repository,
    string? Path = null);

/// <summary>Input contract for an in-process Quality Studio analysis run.</summary>
public sealed record QualityAnalysisRequest(
    string RepositoryPath,
    IReadOnlyList<QualityAnalysisDefinition> Analyses,
    string? RepositoryId = null,
    bool PersistMetadata = false);

/// <summary>Execution status and provenance for one named analysis.</summary>
public sealed record QualityAnalysisExecution(
    string Name,
    string Version,
    bool Available,
    string? UnavailableReason,
    int FindingCount,
    SensorProvenance Provenance);

/// <summary>Findings and provenance returned by an in-process analysis run.</summary>
public sealed record QualityAnalysisResult(
    string RepositoryPath,
    string RepositoryId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<QualityAnalysisExecution> Analyses,
    IReadOnlyList<QualityFindingEnvelope> Findings);

/// <summary>
/// Runs named Quality Studio analyses without an HTTP or UI dependency.
/// </summary>
public sealed class QualityAnalysisRunner
{
    private readonly SensorRegistry sensors;

    public QualityAnalysisRunner(IEnumerable<IReviewSensor>? analyses = null)
    {
        sensors = new SensorRegistry(analyses ?? BuiltInAnalyses());
    }

    public IReadOnlyList<string> AvailableAnalyses => sensors.List()
        .Select(sensor => sensor.Id)
        .Order(StringComparer.Ordinal)
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

        var root = Path.GetFullPath(request.RepositoryPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");

        var definitions = request.Analyses.Select(ValidateDefinition).ToArray();
        var resolved = definitions.Select(definition => (Definition: definition, Sensor: sensors.Get(definition.Name)))
            .ToArray();
        var duplicate = resolved.GroupBy(item => item.Sensor.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Analysis '{duplicate.Key}' was requested more than once.", nameof(request));

        var startedAt = DateTimeOffset.UtcNow;
        var executions = new List<QualityAnalysisExecution>(resolved.Length);
        var findings = new List<QualityFindingEnvelope>();
        var repositoryId = string.IsNullOrWhiteSpace(request.RepositoryId)
            ? new DirectoryInfo(root).Name
            : request.RepositoryId.Trim();

        foreach (var item in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Sensor.SupportedScopes.Contains(item.Definition.Scope))
                throw new ArgumentException(
                    $"Analysis '{item.Sensor.Id}' does not support {item.Definition.Scope.ToString().ToLowerInvariant()} scope.",
                    nameof(request));

            var sensorResult = await item.Sensor.RunAsync(new SensorScanRequest(
                root,
                item.Definition.Scope,
                item.Definition.Path,
                item.Definition.Configuration,
                request.PersistMetadata), cancellationToken).ConfigureAwait(false);

            foreach (var finding in sensorResult.Findings)
            {
                findings.Add(await ToEnvelopeAsync(
                    root, repositoryId, item.Sensor, finding, cancellationToken).ConfigureAwait(false));
            }

            executions.Add(new QualityAnalysisExecution(
                item.Sensor.Id,
                item.Sensor.Version,
                sensorResult.Available,
                sensorResult.UnavailableReason,
                sensorResult.Findings.Count,
                sensorResult.Provenance));
        }

        return new QualityAnalysisResult(
            root,
            repositoryId,
            startedAt,
            DateTimeOffset.UtcNow,
            executions,
            findings.OrderBy(finding => finding.Locations.FirstOrDefault()?.Path, StringComparer.Ordinal)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ThenBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .ToArray());
    }

    private static QualityAnalysisDefinition ValidateDefinition(QualityAnalysisDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        if (definition.Scope == SensorScope.Path && string.IsNullOrWhiteSpace(definition.Path))
            throw new ArgumentException($"Analysis '{definition.Name}' uses path scope but has no path.");
        return definition with { Name = definition.Name.Trim() };
    }

    private static async Task<QualityFindingEnvelope> ToEnvelopeAsync(
        string root,
        string repositoryId,
        IReviewSensor sensor,
        ReviewFinding finding,
        CancellationToken cancellationToken)
    {
        var path = finding.Locations.FirstOrDefault()?.Path ?? ".";
        var subjectHash = await ComputeSubjectHashAsync(root, path, finding.Fingerprint, cancellationToken)
            .ConfigureAwait(false);
        var unitHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        var subject = new StandingUnitFindingSubject(
            repositoryId,
            $"quality-analysis/{sensor.Id}/{unitHash}",
            path,
            sensor.Id is QualityAnalysisNames.Gitleaks or QualityAnalysisNames.Dependencies
                ? ReviewKind.Security
                : ReviewKind.Code,
            ManifestHash.Subject(subjectHash));
        var producer = new QualityFindingProducer(
            QualityFindingProducerKind.Deterministic,
            finding.Source?.Producer ?? sensor.Id,
            finding.Source?.ProducerVersion ?? sensor.Version);
        return QualityFindingEnvelope.FromReviewFinding(finding, subject, producer);
    }

    private static async Task<string> ComputeSubjectHashAsync(
        string root,
        string relativePath,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (candidate.StartsWith(rootPrefix, pathComparison) && File.Exists(candidate))
        {
            var contentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            return contentHash["sha256:".Length..];
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"quality-analysis-subject-v1\0{relativePath}\0{fingerprint}")));
    }

    private static IReadOnlyList<IReviewSensor> BuiltInAnalyses() =>
    [
        new ArchitectureSensor(),
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
