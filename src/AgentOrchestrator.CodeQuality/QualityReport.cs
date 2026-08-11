using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityReportSensor(
    string Id,
    string Version,
    bool Enabled,
    bool? Available = null,
    string? UnavailableReason = null,
    IReadOnlyDictionary<string, string>? ToolVersions = null);

public sealed record QualityReportRepository(
    string Id,
    string Name,
    string Root,
    IReadOnlyList<string>? EnabledKinds = null,
    IReadOnlyList<QualityReportSensor>? Sensors = null,
    string? GlobalInputsDirectory = null,
    int InputBudgetCharacters = InputResolver.DefaultBudgetCharacters,
    bool ObservationReadEnabled = false);

public sealed record QualityReportDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<RepositoryQualityReport> Repositories,
    QualityComparison Comparison);

public sealed record RepositoryQualityReport(
    string Id,
    string Name,
    QualityScorecard Scorecard,
    IReadOnlyList<QualityTrendSeries> Trend,
    IReadOnlyList<QualityFinding> Findings,
    IReadOnlyList<QualityModelRecord>? ModelRecords = null,
    IReadOnlyList<QualityUnknownAspect>? UnknownAspects = null);

public sealed record QualityScorecard(
    int Score,
    string Grade,
    IReadOnlyList<QualityKindScore> Kinds,
    FindingCounts Findings,
    StalenessCounts Staleness,
    CoverageSummary Coverage,
    IReadOnlyList<QualityReportSensor> Sensors);

public sealed record QualityKindScore(
    string Kind,
    int? Score,
    string Grade,
    IReadOnlyList<QualityLevelScore> Levels);

public sealed record QualityLevelScore(string Level, int Score, string Grade, int Reviews);

public sealed record FindingCounts(
    int Total,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByState);

public sealed record StalenessCounts(int Fresh, int Stale, int PolicyDrift, int Missing)
{
    public int Total => Fresh + Stale + PolicyDrift + Missing;
}

public sealed record CoverageSummary(int ReviewedFiles, int TotalFiles, double Percent);

public sealed record QualityTrendSeries(string Kind, IReadOnlyList<QualityTrendPoint> Points);

public sealed record QualityTrendPoint(string Commit, DateTimeOffset At, int Score, string Grade);

public sealed record QualityFinding(
    string RepositoryId,
    string Id,
    string RuleId,
    string Kind,
    string Severity,
    string State,
    string Title,
    string Description,
    string Recommendation,
    string Fingerprint,
    IReadOnlyList<QualityFindingLocation> Locations,
    string Source = "agent",
    string? SensorId = null,
    string? Producer = null);

public sealed record QualityFindingLocation(
    string Path,
    int? StartLine = null,
    int? StartColumn = null,
    int? EndLine = null,
    int? EndColumn = null);

public sealed record QualityComparison(IReadOnlyList<QualityComparisonEntry> Repositories);

public sealed record QualityComparisonEntry(
    int Rank,
    string RepositoryId,
    string Name,
    int Score,
    string Grade,
    double CoveragePercent,
    int OpenFindings);

public sealed class QualityReportException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class QualityReportBuilder
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/quality-report.v1.json";
    private static readonly string[] DefaultKinds = ["code", "security", "performance"];
    private static readonly string[] SeverityNames = ["critical", "high", "medium", "low", "info"];
    private static readonly string[] StateNames = ["open", "accepted", "waived", "false-positive", "resolved"];
    private readonly Func<DateTimeOffset> clock;

    public QualityReportBuilder(Func<DateTimeOffset>? clock = null) =>
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<QualityReportDocument> BuildAsync(
        IReadOnlyList<QualityReportRepository> repositories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        if (repositories.Count == 0) throw new ArgumentException("At least one repository is required.", nameof(repositories));

        var reports = new List<RepositoryQualityReport>(repositories.Count);
        foreach (var repository in repositories)
        {
            reports.Add(await BuildRepositoryAsync(repository, cancellationToken).ConfigureAwait(false));
        }

        var ranked = reports
            .OrderByDescending(report => report.Scorecard.Score)
            .ThenByDescending(report => report.Scorecard.Coverage.Percent)
            .ThenBy(report => report.Name, StringComparer.OrdinalIgnoreCase)
            .Select((report, index) => new QualityComparisonEntry(
                index + 1,
                report.Id,
                report.Name,
                report.Scorecard.Score,
                report.Scorecard.Grade,
                report.Scorecard.Coverage.Percent,
                report.Scorecard.Findings.ByState.GetValueOrDefault("open")))
            .ToArray();
        return new QualityReportDocument(SchemaId, 1, clock().ToUniversalTime(), reports, new QualityComparison(ranked));
    }

    private static async Task<RepositoryQualityReport> BuildRepositoryAsync(
        QualityReportRepository repository,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repository.Root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        var kinds = (repository.EnabledKinds is { Count: > 0 } ? repository.EnabledKinds : DefaultKinds)
            .Select(kind => kind.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (kinds.Any(kind => !DefaultKinds.Contains(kind, StringComparer.Ordinal)))
            throw new ArgumentException("Report kinds must be code, security, or performance.");

        try
        {
            var states = await new FindingStateStore(root).ReadAsync(cancellationToken).ConfigureAwait(false);
            QualityObservationReduction? reduction = null;
            IReadOnlyList<Observation> observations;
            if (repository.ObservationReadEnabled)
            {
                var immutable = await QualityObservationLedger.ReadAsync(root, cancellationToken).ConfigureAwait(false);
                reduction = QualityObservationReducer.Reduce(immutable);
                observations = LoadObservationBackedCurrent(root, immutable, repository.Id, states);
                if (observations.Count == 0)
                    observations = LoadCurrentObservations(root, repository.Id, states);
            }
            else
            {
                observations = LoadCurrentObservations(root, repository.Id, states);
            }
            var kindScores = BuildKindScores(kinds, observations);
            var scoredKinds = kindScores.Where(kind => kind.Score.HasValue).Select(kind => kind.Score!.Value).ToArray();
            var score = scoredKinds.Length == 0
                ? 0
                : (int)Math.Round(scoredKinds.Average(), MidpointRounding.AwayFromZero);
            var findingCounts = CountFindings(
                observations.SelectMany(observation => observation.Findings), states);

            var scans = new List<StalenessReport>(kinds.Length);
            foreach (var kind in kinds)
            {
                scans.Add(await new StalenessEvaluator().ScanAsync(root, new StalenessEvaluatorOptions
                {
                    ReviewKind = kind,
                    GlobalInputsDirectory = repository.GlobalInputsDirectory,
                    InputBudgetCharacters = repository.InputBudgetCharacters,
                }, cancellationToken).ConfigureAwait(false));
            }

            var files = scans.SelectMany(scan => scan.Files).ToArray();
            var staleness = new StalenessCounts(
                files.Count(file => file.State == StalenessState.Fresh),
                files.Count(file => file.State == StalenessState.Stale),
                files.Count(file => file.State == StalenessState.PolicyDrift),
                files.Count(file => file.State == StalenessState.Missing));
            var paths = files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).ToArray();
            var reviewedPaths = files.Where(file => file.State != StalenessState.Missing)
                .Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count();
            var coverage = new CoverageSummary(
                reviewedPaths,
                paths.Length,
                paths.Length == 0 ? 100 : Math.Round(reviewedPaths * 100d / paths.Length, 2));
            var trend = await LoadTrendAsync(root, kinds, cancellationToken).ConfigureAwait(false);
            var scorecard = new QualityScorecard(
                score,
                Grade(score),
                kindScores,
                findingCounts,
                staleness,
                coverage,
                repository.Sensors ?? []);
            return new RepositoryQualityReport(repository.Id, repository.Name, scorecard, trend,
                observations.SelectMany(observation => observation.Findings).ToArray(),
                reduction?.Models ?? [],
                reduction?.UnknownAspects ?? []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new QualityReportException($"Could not build quality report for repository '{repository.Id}'.", exception);
        }
    }

    private static IReadOnlyList<Observation> LoadObservationBackedCurrent(
        string root,
        IReadOnlyList<QualityObservationDocument> immutable,
        string repositoryId,
        IReadOnlyDictionary<string, FindingStateRecord> states)
    {
        var result = new List<Observation>();
        foreach (var sidecarPath in EnumerateSidecars(root))
        {
            var sidecar = JsonNode.Parse(File.ReadAllText(sidecarPath))?.AsObject()
                ?? throw new JsonException("Review metadata must be an object.");
            var unitId = sidecar["unit"]?["id"]?.GetValue<string>();
            var subjectHash = sidecar["reviewedHash"]?["value"]?.GetValue<string>();
            var promptId = sidecar["reviewInputs"]?["prompt"]?["id"]?.GetValue<string>();
            var promptVersion = sidecar["reviewInputs"]?["prompt"]?["version"]?.GetValue<string>();
            var promptHash = sidecar["reviewInputs"]?["prompt"]?["contentHash"]?.GetValue<string>();
            var inputHash = sidecar["reviewInputs"]?["effectiveHash"]?["value"]?.GetValue<string>();
            QualityObservationDocument? observation = null;
            if (unitId is not null && subjectHash is not null && promptId is not null &&
                promptVersion is not null && promptHash is not null && inputHash is not null)
            {
                observation = QualityObservationReducer.SelectCurrent(
                    immutable,
                    new QualityObservationSelectionTarget(
                        unitId,
                        subjectHash,
                        promptId,
                        promptVersion,
                        promptHash,
                        inputHash,
                        QualityTaxonomyCatalogue.CoreDocument.Id,
                        QualityTaxonomyCatalogue.CoreDocument.Version,
                        QualityTaxonomyCatalogue.CoreDigest));
            }
            observation ??= immutable.Where(item =>
                    string.Equals(item.Subject.UnitId, unitId, StringComparison.Ordinal) &&
                    QualityObservationReducer.ProjectCurrentSidecar(item) is not null)
                .OrderByDescending(item => item.ObservedAt)
                .ThenByDescending(item => item.ObservationId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (observation is null) continue;
            var metadata = QualityObservationReducer.ProjectCurrentSidecar(observation);
            if (metadata is null) continue;
            var projected = FindingStateProjection.Apply(metadata, states);
            if (ParseObservation(projected, repositoryId) is { } parsed) result.Add(parsed);
        }
        return result;
    }

    private static IReadOnlyList<Observation> LoadCurrentObservations(
        string root,
        string repositoryId,
        IReadOnlyDictionary<string, FindingStateRecord> states)
    {
        var result = new List<Observation>();
        foreach (var path in EnumerateSidecars(root))
        {
            JsonObject metadata;
            try
            {
                metadata = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                    ?? throw new JsonException("Review metadata must be an object.");
            }
            catch (JsonException exception)
            {
                throw new QualityReportException(
                    $"Cannot read review metadata '{Path.GetRelativePath(root, path)}'.", exception);
            }

            var projected = FindingStateProjection.Apply(metadata, states);
            if (ParseObservation(projected, repositoryId) is { } observation) result.Add(observation);
        }
        return result;
    }

    private static IEnumerable<string> EnumerateSidecars(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        return Directory.EnumerateFiles(root, "*.json", options)
            .Where(path => IsSidecar(Path.GetRelativePath(root, path).Replace('\\', '/')))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static bool IsSidecar(string path) =>
        (path.StartsWith(".quality/reviews/", StringComparison.Ordinal) ||
         path.Contains("/.quality/reviews/", StringComparison.Ordinal)) &&
        path.Contains(".review-meta.", StringComparison.Ordinal) &&
        path.EndsWith(".json", StringComparison.Ordinal);

    private static Observation? ParseObservation(JsonObject metadata, string repositoryId)
    {
        var kind = metadata["kind"]?.GetValue<string>()?.ToLowerInvariant();
        var level = metadata["unit"]?["level"]?.GetValue<string>()?.ToLowerInvariant();
        var score = metadata["grade"]?["score"]?.GetValue<int>();
        if (kind is null || level is null || score is null) return null;
        var findings = new List<QualityFinding>();
        foreach (var finding in metadata["findings"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var source = finding["source"]?["kind"]?.GetValue<string>() == "deterministic"
                ? "deterministic"
                : "agent";
            var sensorId = finding["source"]?["sensorId"]?.GetValue<string>();
            var producer = finding["source"]?["producer"]?.GetValue<string>();
            if (ParseFinding(finding, repositoryId, kind, source, sensorId, producer) is { } parsed)
                findings.Add(parsed);
        }
        foreach (var sensor in metadata["deterministicEvidence"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var sensorId = sensor["provenance"]?["sensorId"]?.GetValue<string>();
            foreach (var finding in sensor["findings"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var producer = finding["source"]?["producer"]?.GetValue<string>();
                if (ParseFinding(
                        finding, repositoryId, kind, "deterministic", sensorId, producer) is { } parsed)
                    findings.Add(parsed);
            }
        }
        return new Observation(kind, level, Math.Clamp(score.Value, 0, 100), findings);
    }

    private static QualityFinding? ParseFinding(
        JsonObject finding,
        string repositoryId,
        string kind,
        string source,
        string? sensorId,
        string? producer)
    {
        var id = finding["id"]?.GetValue<string>();
        if (id is null) return null;
        var ruleId = finding["ruleId"]?.GetValue<string>() ?? id;
        var locations = (finding["locations"]?.AsArray().OfType<JsonObject>() ?? [])
            .Select(location =>
            {
                var range = location["range"]?.AsObject();
                return new QualityFindingLocation(
                    location["path"]?.GetValue<string>() ?? ".",
                    IntAt(range, "start", "line"),
                    IntAt(range, "start", "column"),
                    IntAt(range, "end", "line"),
                    IntAt(range, "end", "column"));
            }).ToArray();
        var fingerprint = finding["fingerprint"]?.GetValue<string>() ??
                          LegacyFingerprint(kind, ruleId, finding, locations);
        return new QualityFinding(
            repositoryId,
            id,
            ruleId,
            kind,
            finding["severity"]?.GetValue<string>()?.ToLowerInvariant() ?? "info",
            finding["state"]?.GetValue<string>()?.ToLowerInvariant() ?? "open",
            finding["title"]?.GetValue<string>() ?? id,
            finding["description"]?.GetValue<string>() ?? string.Empty,
            finding["recommendation"]?.GetValue<string>() ?? string.Empty,
            fingerprint,
            locations,
            source,
            sensorId,
            producer);
    }

    private static string LegacyFingerprint(
        string kind,
        string ruleId,
        JsonObject finding,
        IReadOnlyList<QualityFindingLocation> locations)
    {
        var primary = locations.FirstOrDefault();
        var canonical = string.Join('\0',
            "quality-studio-report-legacy-finding-v1",
            kind,
            ruleId,
            primary?.Path ?? ".",
            primary?.StartLine?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            finding["title"]?.GetValue<string>() ?? string.Empty);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static int? IntAt(JsonObject? parent, string objectName, string propertyName) =>
        parent?[objectName]?[propertyName] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static IReadOnlyList<QualityKindScore> BuildKindScores(
        IReadOnlyList<string> kinds,
        IReadOnlyList<Observation> observations) =>
        kinds.Select(kind =>
        {
            var selected = observations.Where(observation => observation.Kind == kind).ToArray();
            var levels = selected.GroupBy(observation => observation.Level, StringComparer.Ordinal)
                .OrderBy(group => LevelOrder(group.Key))
                .Select(group =>
                {
                    var score = (int)Math.Round(group.Average(item => item.Score), MidpointRounding.AwayFromZero);
                    return new QualityLevelScore(group.Key, score, Grade(score), group.Count());
                }).ToArray();
            if (selected.Length == 0) return new QualityKindScore(kind, null, "not-reviewed", levels);
            var score = (int)Math.Round(selected.Average(item => item.Score), MidpointRounding.AwayFromZero);
            return new QualityKindScore(kind, score, Grade(score), levels);
        }).ToArray();

    private static int LevelOrder(string level) => level switch
    {
        "project" => 0,
        "module" => 1,
        "namespace" => 2,
        "file" => 3,
        "function" => 4,
        _ => 5,
    };

    private static FindingCounts CountFindings(
        IEnumerable<QualityFinding> findings,
        IReadOnlyDictionary<string, FindingStateRecord> states)
    {
        var all = findings.ToArray();
        var currentFingerprints = all.Select(finding => finding.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var historicalStates = states.Values.Where(state => !currentFingerprints.Contains(state.Fingerprint)).ToArray();
        var byState = StateNames.ToDictionary(name => name, name => all.Count(finding => finding.State == name),
            StringComparer.Ordinal);
        foreach (var state in historicalStates)
            byState[FindingStateStore.StateName(state.State)]++;
        var bySeverity = SeverityNames.ToDictionary(name => name,
            name => all.Count(finding => finding.Severity == name), StringComparer.Ordinal);
        if (historicalStates.Length > 0) bySeverity["unknown"] = historicalStates.Length;
        return new FindingCounts(
            all.Length + historicalStates.Length,
            bySeverity,
            byState);
    }

    private static async Task<IReadOnlyList<QualityTrendSeries>> LoadTrendAsync(
        string root,
        IReadOnlyList<string> enabledKinds,
        CancellationToken cancellationToken)
    {
        var log = await RunGitAsync(root,
            ["log", "--format=%x1e%H%x09%aI", "--date=iso-strict", "--name-only", "HEAD"],
            cancellationToken, allowEmpty: true)
            .ConfigureAwait(false);
        var commits = log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Where(lines => lines.Length > 1 && lines.Skip(1).Any(IsSidecar))
            .Select(lines => lines[0].Trim().Split('\t', 2))
            .Where(parts => parts.Length == 2 && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _))
            .Select(parts => new Commit(parts[0], DateTimeOffset.Parse(parts[1], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind)))
            .Reverse()
            .ToArray();
        var series = enabledKinds.ToDictionary(kind => kind, _ => new List<QualityTrendPoint>(), StringComparer.Ordinal);
        var previousScores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var commit in commits)
        {
            var tree = await RunGitAsync(root, ["ls-tree", "-r", "--name-only", commit.Hash],
                cancellationToken, allowEmpty: true).ConfigureAwait(false);
            var sidecars = tree.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(IsSidecar).ToArray();
            var scores = enabledKinds.ToDictionary(kind => kind, _ => new List<int>(), StringComparer.Ordinal);
            foreach (var path in sidecars)
            {
                var json = await RunGitAsync(root, ["show", $"{commit.Hash}:{path}"],
                    cancellationToken, allowEmpty: true).ConfigureAwait(false);
                try
                {
                    using var document = JsonDocument.Parse(json);
                    var kind = document.RootElement.GetProperty("kind").GetString();
                    if (kind is not null && scores.TryGetValue(kind, out var kindScores) &&
                        document.RootElement.TryGetProperty("grade", out var grade) &&
                        grade.TryGetProperty("score", out var score) && score.TryGetInt32(out var value))
                        kindScores.Add(Math.Clamp(value, 0, 100));
                }
                catch (JsonException)
                {
                    // A malformed historical sidecar does not make current quality truth unavailable.
                }
            }

            var currentScores = scores.Where(pair => pair.Value.Count > 0)
                .ToDictionary(pair => pair.Key,
                    pair => (int)Math.Round(pair.Value.Average(), MidpointRounding.AwayFromZero),
                    StringComparer.Ordinal);
            foreach (var pair in currentScores)
            {
                if (!previousScores.TryGetValue(pair.Key, out var previous) || previous != pair.Value)
                    series[pair.Key].Add(new QualityTrendPoint(commit.Hash[..Math.Min(12, commit.Hash.Length)],
                        commit.At, pair.Value, Grade(pair.Value)));
            }
            previousScores = currentScores;
        }
        return enabledKinds.Select(kind => new QualityTrendSeries(kind, series[kind])).ToArray();
    }

    private static async Task<string> RunGitAsync(
        string root,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowEmpty)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new QualityReportException("Git is required to reconstruct quality history.", exception);
        }
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var message = (await error.ConfigureAwait(false)).Trim();
            if (allowEmpty && (message.Contains("does not have any commits", StringComparison.OrdinalIgnoreCase) ||
                               message.Contains("unknown revision", StringComparison.OrdinalIgnoreCase) ||
                               message.Contains("bad revision", StringComparison.OrdinalIgnoreCase)))
                return string.Empty;
            throw new QualityReportException($"Git history query failed: {message}");
        }
        return await output.ConfigureAwait(false);
    }

    public static string Grade(int score) => score switch
    {
        >= 90 => "A",
        >= 80 => "B",
        >= 70 => "C",
        >= 60 => "D",
        _ => "F",
    };

    private sealed record Observation(
        string Kind,
        string Level,
        int Score,
        IReadOnlyList<QualityFinding> Findings);

    private sealed record Commit(string Hash, DateTimeOffset At);
}

public enum QualityReportFormat { Markdown, Html, Json, Sarif }

public static class QualityReportRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    public const string SarifSchema =
        "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/Schemata/sarif-schema-2.1.0.json";

    public static QualityReportFormat ParseFormat(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "markdown" or "md" => QualityReportFormat.Markdown,
        "html" => QualityReportFormat.Html,
        "json" => QualityReportFormat.Json,
        "sarif" => QualityReportFormat.Sarif,
        _ => throw new ArgumentException($"Unsupported report format '{value}'."),
    };

    public static string ContentType(QualityReportFormat format) => format switch
    {
        QualityReportFormat.Markdown => "text/markdown; charset=utf-8",
        QualityReportFormat.Html => "text/html; charset=utf-8",
        QualityReportFormat.Json => "application/json; charset=utf-8",
        QualityReportFormat.Sarif => "application/sarif+json; charset=utf-8",
        _ => "application/octet-stream",
    };

    public static string Render(QualityReportDocument report, QualityReportFormat format) => format switch
    {
        QualityReportFormat.Markdown => Markdown(report),
        QualityReportFormat.Html => Html(report),
        QualityReportFormat.Json => JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine,
        QualityReportFormat.Sarif => Sarif(report),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static string Markdown(QualityReportDocument report)
    {
        var text = new StringBuilder();
        text.AppendLine("# Quality report");
        text.AppendLine();
        text.AppendLine($"Generated {report.GeneratedAt:O}.");
        foreach (var repository in report.Repositories)
        {
            var scorecard = repository.Scorecard;
            text.AppendLine();
            text.AppendLine($"## {EscapeMarkdown(repository.Name)}");
            text.AppendLine();
            text.AppendLine($"**Score: {scorecard.Score}/100 ({scorecard.Grade}) · Coverage: {scorecard.Coverage.Percent:0.##}% ({scorecard.Coverage.ReviewedFiles}/{scorecard.Coverage.TotalFiles} files)**");
            text.AppendLine();
            text.AppendLine("| Kind / level | Score | Grade | Reviews |");
            text.AppendLine("| --- | ---: | :---: | ---: |");
            foreach (var kind in scorecard.Kinds)
            {
                text.AppendLine($"| {EscapeMarkdown(kind.Kind)} | {(kind.Score?.ToString(CultureInfo.InvariantCulture) ?? "—")} | {EscapeMarkdown(kind.Grade)} | {kind.Levels.Sum(level => level.Reviews)} |");
                foreach (var level in kind.Levels)
                    text.AppendLine($"| ↳ {EscapeMarkdown(level.Level)} | {level.Score} | {EscapeMarkdown(level.Grade)} | {level.Reviews} |");
            }
            text.AppendLine();
            text.AppendLine("### Findings");
            text.AppendLine();
            text.AppendLine($"Total: {scorecard.Findings.Total}. Severity: {JoinCounts(scorecard.Findings.BySeverity)}. State: {JoinCounts(scorecard.Findings.ByState)}.");
            var visibleFindings = repository.Findings.Where(finding => finding.State != "resolved").ToArray();
            if (visibleFindings.Length > 0) text.AppendLine();
            foreach (var finding in visibleFindings)
            {
                var location = finding.Locations.FirstOrDefault();
                var at = location is null ? string.Empty :
                    $" — {EscapeMarkdown(location.Path)}{(location.StartLine.HasValue ? $":{location.StartLine}" : string.Empty)}";
                var source = finding.Source == "deterministic"
                    ? $"deterministic:{finding.Producer ?? finding.SensorId ?? "analyzer"}"
                    : "agent";
                text.AppendLine($"- [{EscapeMarkdown(finding.Severity)}/{EscapeMarkdown(finding.State)}/{EscapeMarkdown(source)}] {EscapeMarkdown(finding.Title)}{at}");
            }
            text.AppendLine();
            text.AppendLine("### Staleness");
            text.AppendLine();
            text.AppendLine($"Fresh {scorecard.Staleness.Fresh}, stale {scorecard.Staleness.Stale}, policy drift {scorecard.Staleness.PolicyDrift}, missing {scorecard.Staleness.Missing}.");
            text.AppendLine();
            text.AppendLine("### Sensor posture");
            text.AppendLine();
            if (scorecard.Sensors.Count == 0) text.AppendLine("No sensors configured.");
            foreach (var sensor in scorecard.Sensors)
                text.AppendLine($"- {EscapeMarkdown(sensor.Id)} {EscapeMarkdown(sensor.Version)}: {(sensor.Enabled ? SensorStatus(sensor) : "disabled")}");
            text.AppendLine();
            text.AppendLine("### Trend");
            text.AppendLine();
            foreach (var series in repository.Trend)
            {
                var curve = series.Points.Count == 0
                    ? "no committed review history"
                    : string.Join(" → ", series.Points.Select(point =>
                        $"{point.Score} ({point.Commit}, {point.At:yyyy-MM-dd})"));
                text.AppendLine($"- {EscapeMarkdown(series.Kind)}: {curve}");
            }
        }
        if (report.Repositories.Count > 1)
        {
            text.AppendLine();
            text.AppendLine("## Repository comparison");
            text.AppendLine();
            text.AppendLine("| Rank | Repository | Score | Coverage | Open findings |");
            text.AppendLine("| ---: | --- | ---: | ---: | ---: |");
            foreach (var entry in report.Comparison.Repositories)
                text.AppendLine($"| {entry.Rank} | {EscapeMarkdown(entry.Name)} | {entry.Score} ({entry.Grade}) | {entry.CoveragePercent:0.##}% | {entry.OpenFindings} |");
        }
        return text.ToString();
    }

    private static string Html(QualityReportDocument report)
    {
        var markdown = Markdown(report);
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Quality report</title>" +
               "<style>body{font:16px system-ui;max-width:72rem;margin:2rem auto;padding:0 1rem;color:#17202a}" +
               "pre{white-space:pre-wrap;line-height:1.5;font:inherit}</style></head><body><pre>" +
               WebUtility.HtmlEncode(markdown) + "</pre></body></html>\n";
    }

    private static string Sarif(QualityReportDocument report)
    {
        var runs = report.Repositories.Select(repository =>
        {
            var findings = repository.Findings.Where(finding => finding.State != "resolved").ToArray();
            var rules = findings.GroupBy(finding => finding.RuleId, StringComparer.Ordinal)
                .Select(group =>
                {
                    var first = group.First();
                    return new JsonObject
                    {
                        ["id"] = first.RuleId,
                        ["name"] = SarifName(first.RuleId),
                        ["shortDescription"] = new JsonObject { ["text"] = first.Title },
                        ["help"] = new JsonObject
                        {
                            ["text"] = first.Recommendation,
                            ["markdown"] = first.Recommendation,
                        },
                    };
                }).ToArray();
            var results = findings.Select(finding =>
            {
                var result = new JsonObject
                {
                    ["ruleId"] = finding.RuleId,
                    ["level"] = SarifLevel(finding.Severity),
                    ["message"] = new JsonObject { ["text"] = $"{finding.Title}: {finding.Description}" },
                    ["partialFingerprints"] = new JsonObject { ["qualityStudioFingerprint/v1"] = finding.Fingerprint },
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = finding.Kind,
                        ["severity"] = finding.Severity,
                        ["state"] = finding.State,
                        ["recommendation"] = finding.Recommendation,
                        ["source"] = finding.Source,
                        ["sensorId"] = finding.SensorId,
                        ["producer"] = finding.Producer,
                    },
                    ["locations"] = new JsonArray(finding.Locations.Select(location =>
                    {
                        var physical = new JsonObject
                        {
                            ["artifactLocation"] = new JsonObject { ["uri"] = SarifUri(location.Path) },
                        };
                        if (location.StartLine.HasValue)
                        {
                            var region = new JsonObject { ["startLine"] = Math.Max(1, location.StartLine.Value) };
                            if (location.StartColumn.HasValue) region["startColumn"] = Math.Max(1, location.StartColumn.Value);
                            if (location.EndLine.HasValue) region["endLine"] = Math.Max(region["startLine"]!.GetValue<int>(), location.EndLine.Value);
                            if (location.EndColumn.HasValue) region["endColumn"] = Math.Max(1, location.EndColumn.Value);
                            physical["region"] = region;
                        }
                        return new JsonObject { ["physicalLocation"] = physical };
                    }).ToArray()),
                };
                if (finding.State is "waived" or "false-positive")
                {
                    result["suppressions"] = new JsonArray(new JsonObject
                    {
                        ["kind"] = "external",
                        ["status"] = "accepted",
                        ["justification"] = $"Quality Studio finding state: {finding.State}.",
                    });
                }
                return result;
            }).ToArray();
            return new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["driver"] = new JsonObject
                    {
                        ["name"] = "Quality Studio",
                        ["informationUri"] = "https://agent-orchestrator.dev/quality",
                        ["semanticVersion"] = "1.0.0",
                        ["rules"] = new JsonArray(rules),
                    },
                },
                ["automationDetails"] = new JsonObject { ["id"] = $"quality-studio/{repository.Id}/" },
                ["results"] = new JsonArray(results),
                ["properties"] = new JsonObject
                {
                    ["qualityReportSchema"] = report.Schema,
                    ["qualityReportSchemaVersion"] = report.SchemaVersion,
                    ["generatedAt"] = report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture),
                    ["repositoryId"] = repository.Id,
                    ["repositoryName"] = repository.Name,
                    ["scorecard"] = JsonSerializer.SerializeToNode(repository.Scorecard, JsonOptions),
                    ["trend"] = JsonSerializer.SerializeToNode(repository.Trend, JsonOptions),
                    ["comparison"] = JsonSerializer.SerializeToNode(report.Comparison, JsonOptions),
                },
            };
        }).ToArray();
        var sarif = new JsonObject
        {
            ["$schema"] = SarifSchema,
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(runs),
        };
        return sarif.ToJsonString(JsonOptions) + Environment.NewLine;
    }

    private static string JoinCounts(IReadOnlyDictionary<string, int> counts) =>
        string.Join(", ", counts.Select(pair => $"{pair.Key} {pair.Value}"));

    private static string SensorStatus(QualityReportSensor sensor) => sensor.Available switch
    {
        true => "available",
        false => $"unavailable ({sensor.UnavailableReason ?? "no reason reported"})",
        null => "availability not probed",
    };

    private static string EscapeMarkdown(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string SarifLevel(string severity) => severity switch
    {
        "critical" or "high" => "error",
        "medium" => "warning",
        _ => "note",
    };

    private static string SarifName(string ruleId)
    {
        var name = new string(ruleId.Select(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' ? character : '_').ToArray());
        if (name.Length == 0 || name[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'))
            name = "rule_" + name;
        return name;
    }

    private static string SarifUri(string path) => string.Join('/',
        path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Default,
        };
        return options;
    }
}

public static class QualityReportGate
{
    private static readonly IReadOnlyDictionary<string, int> SeverityRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = 0,
            ["high"] = 1,
            ["medium"] = 2,
            ["low"] = 3,
            ["info"] = 4,
        };

    public static IReadOnlyList<string> Evaluate(
        QualityReportDocument report,
        int? failUnder = null,
        string? failOnSeverity = null)
    {
        if (failUnder is < 0 or > 100) throw new ArgumentException("--fail-under must be between 0 and 100.");
        if (failOnSeverity is not null && !SeverityRank.ContainsKey(failOnSeverity))
            throw new ArgumentException("--fail-on must be critical, high, medium, low, or info.");
        var failures = new List<string>();
        foreach (var repository in report.Repositories)
        {
            if (failUnder.HasValue && repository.Scorecard.Score < failUnder.Value)
                failures.Add($"{repository.Id}: score {repository.Scorecard.Score} is below {failUnder.Value}");
            if (failOnSeverity is not null)
            {
                var threshold = SeverityRank[failOnSeverity];
                var blocking = repository.Findings.Where(finding =>
                        finding.State is "open" or "accepted" &&
                        SeverityRank.TryGetValue(finding.Severity, out var rank) && rank <= threshold)
                    .ToArray();
                if (blocking.Length > 0)
                    failures.Add($"{repository.Id}: {blocking.Length} active finding(s) at {failOnSeverity} or higher");
            }
        }
        return failures;
    }
}
