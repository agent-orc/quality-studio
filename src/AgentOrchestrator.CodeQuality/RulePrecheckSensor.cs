using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed partial class RulePrecheckSensor : IDeterministicEvidenceSensor
{
    public const string SensorId = "quality-rules";
    public const string SensorVersion = "1.0.0";
    private readonly RuleLibrary library;

    public RulePrecheckSensor(RuleLibrary? library = null) => this.library = library ?? new RuleLibrary();

    public string Id => SensorId;
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository, SensorScope.Path];

    public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
        {
            [SensorId] = SensorVersion,
        }));

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        var paths = ResolvePaths(root, request).ToArray();
        var reviewKind = request.Configuration?.GetValueOrDefault("reviewKind") ?? "code";
        if (reviewKind is not ("code" or "security" or "performance"))
            throw new ArgumentException($"Unsupported rule pre-check review kind '{reviewKind}'.");
        var rules = library.Resolve(root, reviewKind, paths);
        var findings = new List<ReviewFinding>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var applicable = rules.Rules.Where(rule =>
                rule.Definition.AppliesTo.FileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) &&
                rule.Definition.DeterministicCheck is not null).ToArray();
            if (applicable.Length == 0) continue;
            var absolute = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            var lines = await File.ReadAllLinesAsync(absolute, cancellationToken).ConfigureAwait(false);
            foreach (var rule in applicable)
            {
                findings.AddRange(Check(rule, path, lines));
            }
        }
        return new SensorScanResult(
            true,
            null,
            findings.DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .OrderBy(finding => finding.Locations[0].Path, StringComparer.Ordinal)
                .ThenBy(finding => finding.Locations[0].Range?.Start.Line ?? 0)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToArray(),
            new SensorProvenance(
                SensorId,
                SensorVersion,
                request.Scope.ToString().ToLowerInvariant(),
                request.Scope == SensorScope.Repository ? "." : request.Path ?? ".",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                new Dictionary<string, string> { [SensorId] = SensorVersion }));
    }

    private static IEnumerable<string> ResolvePaths(string root, SensorScanRequest request)
    {
        if (request.Scope == SensorScope.Path && !string.IsNullOrWhiteSpace(request.Path))
        {
            var selected = Path.GetFullPath(Path.Combine(root, request.Path));
            if (!IsWithin(root, selected)) throw new ArgumentException("Rule pre-check path escapes the repository.");
            if (File.Exists(selected)) return [Normalize(root, selected)];
            if (!Directory.Exists(selected)) throw new FileNotFoundException("Rule pre-check path does not exist.", selected);
            return Enumerate(root, selected);
        }
        return Enumerate(root, root);
    }

    private static IEnumerable<string> Enumerate(string root, string directory)
    {
        var scope = RepositoryScope.Load(root);
        return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        })
            .Select(path => (Absolute: path, Relative: Normalize(root, path)))
            .Where(item => scope.Evaluate(item.Relative, item.Absolute).Included)
            .Where(item => Path.GetExtension(item.Relative) is ".cs" or ".css" or ".scss" or ".html")
            .Select(item => item.Relative)
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static IEnumerable<ReviewFinding> Check(ResolvedRule rule, string path, IReadOnlyList<string> lines)
    {
        var checkId = rule.Definition.DeterministicCheck!.Id;
        if (checkId == "angular-no-ad-hoc-style-values" && IsCentralStylesheet(path)) yield break;
        for (var index = 0; index < lines.Count; index++)
        {
            var matches = checkId switch
            {
                "angular-no-ad-hoc-style-values" => AdHocStyleValue().Matches(lines[index]).Cast<Match>(),
                "angular-no-inline-style-attributes" => InlineStyle().Matches(lines[index]).Cast<Match>(),
                "dotnet-no-sync-over-async" => SyncOverAsync().Matches(lines[index]).Cast<Match>(),
                _ => [],
            };
            foreach (var match in matches)
            {
                if (checkId == "angular-no-ad-hoc-style-values" && lines[index].TrimStart().StartsWith("--", StringComparison.Ordinal))
                    continue;
                if (checkId == "dotnet-no-sync-over-async" && IsCSharpCommentOrString(lines[index], match.Index))
                    continue;
                yield return Finding(rule, path, index + 1, match.Index + 1, match.Length);
                break;
            }
        }
    }

    private static bool IsCentralStylesheet(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.EndsWith("/src/styles.css", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/src/styles.scss", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "src/styles.css", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "src/styles.scss", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCSharpCommentOrString(string line, int target)
    {
        var inString = false;
        var inCharacter = false;
        var escaped = false;
        for (var index = 0; index < target; index++)
        {
            var character = line[index];
            if (!inString && !inCharacter && character == '/' && index + 1 < target)
            {
                if (line[index + 1] == '/') return true;
                if (line[index + 1] == '*')
                {
                    var end = line.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    if (end < 0 || end >= target) return true;
                }
            }
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if ((inString || inCharacter) && character == '\\')
            {
                escaped = true;
                continue;
            }
            if (!inCharacter && character == '"') inString = !inString;
            if (!inString && character == '\'') inCharacter = !inCharacter;
        }
        return inString || inCharacter;
    }

    private static ReviewFinding Finding(ResolvedRule rule, string path, int line, int column, int length)
    {
        var canonical = $"{SensorId}\0{rule.Definition.Id}\0{path}\0{line}\0{column}";
        var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new ReviewFinding(
            $"{rule.Definition.Id.ToLowerInvariant()}-{fingerprint[^12..]}",
            "maintainability",
            rule.Severity,
            rule.Definition.Name,
            rule.Definition.Statement,
            $"Apply {rule.Definition.Id}: use the documented good pattern from the Quality Studio rule library.",
            [new FindingLocation(path, new FindingRange(
                new FindingPosition(line, column),
                new FindingPosition(line, column + Math.Max(length, 1))))],
            fingerprint,
            rule.Definition.Id,
            $"Deterministic pre-check '{rule.Definition.DeterministicCheck!.Id}' matched source text.",
            new FindingSource(FindingSourceKind.Deterministic, SensorId, "Quality Studio rule pre-check", SensorVersion));
    }

    private static string Normalize(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(root, path, comparison) ||
               path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
    }

    [GeneratedRegex(@"(?<![-\w])(?:#[0-9a-fA-F]{3,8}\b|(?<![\w.-])(?:[2-9]|[1-9]\d+)px\b)", RegexOptions.CultureInvariant)]
    private static partial Regex AdHocStyleValue();

    [GeneratedRegex(@"\bstyle\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineStyle();

    [GeneratedRegex(@"\.(?:Result\b|Wait\s*\(|GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\()|\basync\s+void\b", RegexOptions.CultureInvariant)]
    private static partial Regex SyncOverAsync();
}
