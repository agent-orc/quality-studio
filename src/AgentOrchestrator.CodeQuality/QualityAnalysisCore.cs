using System.Security.Cryptography;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Stable names accepted by <see cref="IQualityAnalysisCore"/>.</summary>
public static class QualityAnalysisNames
{
    public const string Boundaries = "boundaries";
    public const string Dependencies = "dependencies";
    public const string Gitleaks = "gitleaks";

    public static IReadOnlyList<string> All { get; } = [Boundaries, Dependencies, Gitleaks];
}

/// <summary>Options for one named analysis.</summary>
public sealed record QualityNamedAnalysisConfiguration(
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>Configuration shared by all requested analyses.</summary>
public sealed record QualityAnalysisConfiguration(
    string? RepositoryId = null,
    bool PersistEvidence = false,
    string? RuleLibraryPath = null,
    IReadOnlyDictionary<string, QualityNamedAnalysisConfiguration>? Analyses = null);

/// <summary>Requests one or more named analyses over a local repository checkout.</summary>
public sealed record QualityAnalysisRequest(
    string RepositoryPath,
    IReadOnlyList<string> Analyses,
    QualityAnalysisConfiguration? Configuration = null);

/// <summary>Availability and provenance for one completed analysis.</summary>
public sealed record QualityAnalysisExecution(
    string Name,
    bool Available,
    string? UnavailableReason,
    int FindingCount,
    SensorProvenance Provenance);

/// <summary>In-process analysis output using the canonical Quality Studio finding model.</summary>
public sealed record QualityAnalysisResult(
    string RepositoryPath,
    string RepositoryId,
    IReadOnlyList<string> RequestedAnalyses,
    IReadOnlyList<QualityAnalysisExecution> Executions,
    IReadOnlyList<QualityFindingEnvelope> Findings);

public interface IQualityAnalysisCore
{
    Task<QualityAnalysisResult> RunAsync(
        QualityAnalysisRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs repository analyses without HTTP or UI hosting. Consumers may inject sensors for
/// composition and tests; the default set provides all names in <see cref="QualityAnalysisNames"/>.
/// </summary>
public sealed class QualityAnalysisCore : IQualityAnalysisCore
{
    private readonly SensorRegistry sensors;

    public QualityAnalysisCore(IEnumerable<IReviewSensor>? sensors = null)
    {
        this.sensors = new SensorRegistry(sensors ??
        [
            new BoundaryInventorySensor(),
            new DependencyVulnerabilitySensor(),
            new GitleaksSecurityScanner(),
        ]);
    }

    public async Task<QualityAnalysisResult> RunAsync(
        QualityAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentNullException.ThrowIfNull(request.Analyses);
        if (request.Analyses.Count == 0)
        {
            throw new ArgumentException("At least one analysis name is required.", nameof(request));
        }

        var root = Path.GetFullPath(request.RepositoryPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        }

        var configuration = request.Configuration ?? new QualityAnalysisConfiguration();
        var repositoryId = string.IsNullOrWhiteSpace(configuration.RepositoryId)
            ? new DirectoryInfo(root).Name
            : configuration.RepositoryId.Trim();
        var names = request.Analyses
            .Select(NormalizeName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var ruleLibraryPath = ResolveRuleLibraryPath(root, configuration.RuleLibraryPath);
        var subjectHash = await ComputeRepositoryManifestAsync(
            root, names, configuration.Analyses, ruleLibraryPath, cancellationToken).ConfigureAwait(false);
        var executions = new List<QualityAnalysisExecution>(names.Length);
        var findings = new List<QualityFindingEnvelope>();

        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sensor = sensors.Get(name);
            var settings = FindConfiguration(configuration.Analyses, name)?.Settings is { } configured
                ? new Dictionary<string, string>(configured, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            if (ruleLibraryPath is not null)
            {
                settings["ruleLibraryPath"] = ruleLibraryPath;
            }

            var scan = await sensor.RunAsync(new SensorScanRequest(
                root,
                Configuration: settings,
                PersistMetadata: configuration.PersistEvidence), cancellationToken).ConfigureAwait(false);
            var executionFindings = scan.Findings.Select(finding => QualityFindingEnvelope.FromReviewFinding(
                finding,
                CreateSubject(repositoryId, subjectHash),
                new QualityFindingProducer(
                    QualityFindingProducerKind.Deterministic,
                    scan.Provenance.SensorId,
                    scan.Provenance.SensorVersion))).ToArray();
            findings.AddRange(executionFindings);
            executions.Add(new QualityAnalysisExecution(
                name,
                scan.Available,
                scan.UnavailableReason,
                executionFindings.Length,
                scan.Provenance));
        }

        return new QualityAnalysisResult(
            root,
            repositoryId,
            names,
            executions,
            findings
                .OrderBy(finding => finding.Severity)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ThenBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .ToArray());
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim().ToLowerInvariant();
        if (!QualityAnalysisNames.All.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Unknown analysis '{name}'. Available analyses: {string.Join(", ", QualityAnalysisNames.All)}.");
        }

        return normalized;
    }

    private static QualityNamedAnalysisConfiguration? FindConfiguration(
        IReadOnlyDictionary<string, QualityNamedAnalysisConfiguration>? configuration,
        string name) =>
        configuration?.FirstOrDefault(pair =>
            string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static StandingUnitFindingSubject CreateSubject(string repositoryId, string subjectHash)
    {
        var unitHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("quality-studio/repository-unit/v1\0" + repositoryId)));
        return new StandingUnitFindingSubject(
            repositoryId,
            $"qs-v1/generic/repository/{unitHash}",
            ".",
            ReviewKind.Security,
            ManifestHash.Subject(subjectHash));
    }

    private static string? ResolveRuleLibraryPath(string root, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return null;
        var path = Path.GetFullPath(Path.Combine(root, configuredPath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, comparison) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            throw new ArgumentException("RuleLibraryPath must identify existing content inside the repository.");
        }

        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    private static async Task<string> ComputeRepositoryManifestAsync(
        string root,
        IReadOnlyList<string> analysisNames,
        IReadOnlyDictionary<string, QualityNamedAnalysisConfiguration>? analysisConfiguration,
        string? ruleLibraryPath,
        CancellationToken cancellationToken)
    {
        var scope = RepositoryScope.Load(root);
        var inputs = new List<SubjectInputHash>();
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                 }).OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!scope.Evaluate(relative, file).Included) continue;
            await using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            inputs.Add(new SubjectInputHash(relative, "file", "sha256:" + Convert.ToHexStringLower(hash)));
        }

        if (ruleLibraryPath is not null)
        {
            var absolute = Path.Combine(root, ruleLibraryPath.Replace('/', Path.DirectorySeparatorChar));
            var ruleFiles = File.Exists(absolute)
                ? [absolute]
                : Directory.EnumerateFiles(absolute, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                }).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            foreach (var file in ruleFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                inputs.Add(new SubjectInputHash(
                    Path.GetRelativePath(root, file).Replace('\\', '/'),
                    "rule-library",
                    "sha256:" + Convert.ToHexStringLower(hash)));
            }
        }

        var settings = new StringBuilder("quality-studio/analysis-configuration/v1")
            .Append('\0').Append("rule-library").Append('\0').Append(ruleLibraryPath ?? string.Empty);
        foreach (var name in analysisNames.OrderBy(value => value, StringComparer.Ordinal))
        {
            settings.Append('\0').Append(name);
            var configured = FindConfiguration(analysisConfiguration, name)?.Settings;
            foreach (var pair in (configured ?? new Dictionary<string, string>())
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Value, StringComparer.Ordinal))
            {
                settings.Append('\0').Append(pair.Key).Append('\0').Append(pair.Value);
            }
        }
        inputs.Add(new SubjectInputHash(
            ".quality/analysis-configuration",
            "configuration",
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ToString())))));

        return ReviewSubjectHasher.ComputeManifestHash("repository", inputs);
    }
}
