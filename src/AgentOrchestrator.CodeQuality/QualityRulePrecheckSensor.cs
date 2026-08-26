using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Enforces only named rules whose violations can be established without agent judgement.
/// The complete named rule set is still supplied to the review prompt.
/// </summary>
public sealed partial class QualityRulePrecheckSensor : IDeterministicEvidenceSensor
{
    public const string SensorId = "quality-rules";
    public const string SensorVersion = "1.0.0";
    private readonly QualityRuleResolver resolver;

    public QualityRulePrecheckSensor(QualityRuleResolver? resolver = null)
    {
        this.resolver = resolver ?? new QualityRuleResolver();
    }

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
        var files = ResolveFiles(root, request).ToArray();
        var findings = new List<ReviewFinding>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var rules = resolver.Resolve(root, [relativePath]).Rules
                .Where(rule => rule.Definition.Deterministic is not null)
                .ToArray();
            if (rules.Length == 0) continue;
            var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            foreach (var rule in rules)
                findings.AddRange(Check(rule, relativePath, content));
        }

        var provenance = new SensorProvenance(
            SensorId,
            SensorVersion,
            request.Scope.ToString().ToLowerInvariant(),
            request.Path ?? ".",
            DateTimeOffset.UtcNow.ToString("O"),
            new Dictionary<string, string> { [SensorId] = SensorVersion });
        return new SensorScanResult(true, null,
            findings.OrderBy(finding => finding.Locations[0].Path, StringComparer.Ordinal)
                .ThenBy(finding => finding.Locations[0].Range?.Start.Line)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToArray(),
            provenance);
    }

    private static IEnumerable<string> ResolveFiles(string root, SensorScanRequest request)
    {
        var target = request.Scope == SensorScope.Path && !string.IsNullOrWhiteSpace(request.Path)
            ? Path.GetFullPath(Path.Combine(root, request.Path))
            : root;
        EnsureContained(root, target);
        if (File.Exists(target)) return QualityRuleResolver.LanguageFor(target) is null ? [] : [target];
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"Rule pre-check target does not exist: {target}");
        return Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Where(path => QualityRuleResolver.LanguageFor(path) is not null)
            .Where(path => !Path.GetRelativePath(root, path).Replace('\\', '/').Split('/')
                .Any(segment => segment is ".git" or ".quality" or "node_modules" or "bin" or "obj"));
    }

    private static IEnumerable<ReviewFinding> Check(
        EffectiveQualityRule rule,
        string path,
        string content)
    {
        return rule.Definition.Deterministic!.Check switch
        {
            "angular-no-ad-hoc-style-values" => AdHocStyleFindings(rule, path, content),
            "angular-require-on-push" => OnPushFindings(rule, path, content),
            "csharp-no-sync-over-async" => AsyncFindings(rule, path, content),
            _ => [],
        };
    }

    private static IEnumerable<ReviewFinding> AdHocStyleFindings(
        EffectiveQualityRule rule,
        string path,
        string content)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".css" or ".scss" or ".html" or ".ts"))
            yield break;
        var lines = Lines(content);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (CustomPropertyDeclaration().IsMatch(line)) continue;
            var match = RawStyleValue().Match(line);
            if (!match.Success) continue;
            yield return Finding(rule, path, index + 1, match.Index + 1, match.Length,
                "Ad-hoc visual value bypasses design tokens",
                $"The value `{match.Value}` is local styling rather than a shared semantic design token.",
                "Replace the raw value with the existing design token for this role, or add a centrally named token when the role is genuinely new.",
                line.Trim());
        }
    }

    private static IEnumerable<ReviewFinding> OnPushFindings(
        EffectiveQualityRule rule,
        string path,
        string content)
    {
        if (!path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            !content.Contains("@Component", StringComparison.Ordinal) ||
            content.Contains("ChangeDetectionStrategy.OnPush", StringComparison.Ordinal)) yield break;
        var lines = Lines(content);
        var line = Array.FindIndex(lines, value => value.Contains("@Component", StringComparison.Ordinal));
        if (line < 0) yield break;
        yield return Finding(rule, path, line + 1, lines[line].IndexOf("@Component", StringComparison.Ordinal) + 1,
            "@Component".Length,
            "Component does not declare OnPush change detection",
            "The component uses Angular's default change-detection strategy.",
            "Declare `changeDetection: ChangeDetectionStrategy.OnPush` and use explicit reactive view state.",
            lines[line].Trim());
    }

    private static IEnumerable<ReviewFinding> AsyncFindings(
        EffectiveQualityRule rule,
        string path,
        string content)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) yield break;
        var lines = Lines(content);
        for (var index = 0; index < lines.Length; index++)
        {
            var code = lines[index].Split("//", 2, StringSplitOptions.None)[0];
            var match = SyncOverAsync().Match(code);
            if (!match.Success) continue;
            yield return Finding(rule, path, index + 1, match.Index + 1, match.Length,
                "Asynchronous flow blocks or cannot be awaited",
                $"`{match.Value}` violates the repository's async-hygiene rule.",
                "Keep the call chain asynchronous, return Task/ValueTask, await the operation, and propagate CancellationToken.",
                lines[index].Trim());
        }
    }

    private static ReviewFinding Finding(
        EffectiveQualityRule rule,
        string path,
        int line,
        int column,
        int length,
        string title,
        string description,
        string recommendation,
        string evidence)
    {
        var canonical = $"quality-rule-precheck-v1\0{rule.Definition.Id}\0{path}\0{line}\0{evidence}";
        var fingerprint = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new ReviewFinding(
            $"rule-{fingerprint[7..]}",
            rule.Definition.Category,
            rule.Severity,
            title,
            description,
            recommendation,
            [new FindingLocation(path, new FindingRange(
                new FindingPosition(line, Math.Max(1, column)),
                new FindingPosition(line, Math.Max(1, column + Math.Max(1, length)))))],
            fingerprint,
            rule.Definition.Id,
            evidence,
            new FindingSource(FindingSourceKind.Deterministic, SensorId, "quality-rule-precheck", SensorVersion));
    }

    private static string[] Lines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static void EnsureContained(string root, string target)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(normalizedRoot, normalizedTarget, comparison) &&
            !normalizedTarget.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Rule pre-check target must remain inside the repository root.");
    }

    [GeneratedRegex(@"^\s*--[a-zA-Z0-9-]+\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex CustomPropertyDeclaration();

    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\s*\(|(?<![\w.-])\d+(?:\.\d+)?px\b", RegexOptions.CultureInvariant)]
    private static partial Regex RawStyleValue();

    [GeneratedRegex(@"\basync\s+void\b|\.Result\b|\.Wait\s*\(|\.GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex SyncOverAsync();
}
