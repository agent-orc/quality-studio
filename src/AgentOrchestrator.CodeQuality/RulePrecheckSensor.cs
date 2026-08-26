using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Deterministic checks for the mechanically provable subset of named Quality Studio rules.</summary>
public sealed partial class RulePrecheckSensor(RuleLibrary? library = null) : IDeterministicEvidenceSensor
{
    public const string SensorVersion = "1.0.0";
    private readonly RuleLibrary ruleLibrary = library ?? new RuleLibrary();

    public string Id => "quality-rules";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository, SensorScope.Path];

    public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
        {
            [Id] = SensorVersion,
        }));

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        var files = EnumerateFiles(root, request).Order(StringComparer.Ordinal).ToArray();
        var relativePaths = files.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToArray();
        var rules = ruleLibrary.Resolve(root, "code", relativePaths).Rules
            .ToDictionary(rule => rule.Definition.Id, StringComparer.Ordinal);
        var findings = new List<ReviewFinding>();
        for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = files[fileIndex];
            var relativePath = relativePaths[fileIndex];
            var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            if (rules.TryGetValue("QS-NG-002", out var tokens) &&
                Path.GetExtension(path).ToLowerInvariant() is ".css" or ".scss" &&
                !string.Equals(Path.GetFileName(path), "styles.css", StringComparison.OrdinalIgnoreCase))
                AddMatches(findings, lines, relativePath, tokens, RawPixel(),
                    "Hard-coded pixel value bypasses the design-token scale",
                    "A component stylesheet contains a raw pixel value instead of a central design token.",
                    "Replace the value with the existing semantic or scale token; add a central token only when the design system lacks the concept.");
            if (rules.TryGetValue("QS-NG-004", out var templates) &&
                string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase))
                AddMatches(findings, lines, relativePath, templates, InlineStyle(),
                    "Inline template style bypasses shared styling",
                    "An Angular template contains an inline style attribute.",
                    "Move the presentation into the component stylesheet and express visual values with central tokens.");
            if (rules.TryGetValue("QS-NG-005", out var changeDetection) &&
                string.Equals(Path.GetExtension(path), ".ts", StringComparison.OrdinalIgnoreCase))
                AddMissingOnPush(findings, lines, relativePath, changeDetection);
            if (rules.TryGetValue("QS-CS-003", out var asyncRule) &&
                string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
                AddMatches(findings, lines, relativePath, asyncRule, SyncOverAsync(),
                    "Synchronous wait in an asynchronous flow",
                    "The code synchronously waits on Task completion, which can block workers or deadlock a captured context.",
                    "Await the operation and propagate CancellationToken through the asynchronous call chain.");
        }

        return new SensorScanResult(true, null,
            findings.OrderBy(finding => finding.Locations[0].Path, StringComparer.Ordinal)
                .ThenBy(finding => finding.Locations[0].Range!.Start.Line)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal).ToArray(),
            new SensorProvenance(Id, Version,
                request.Scope.ToString().ToLowerInvariant(), request.Scope == SensorScope.Repository ? "." : request.Path ?? ".",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                new Dictionary<string, string> { [Id] = Version }));
    }

    private static IEnumerable<string> EnumerateFiles(string root, SensorScanRequest request)
    {
        var target = request.Scope == SensorScope.Path && !string.IsNullOrWhiteSpace(request.Path)
            ? Path.GetFullPath(request.Path, root)
            : root;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(target, normalizedRoot, comparison) &&
            !target.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Rule pre-check path must remain inside the repository.");
        if (File.Exists(target)) return IsCandidate(target) ? [target] : [];
        if (!Directory.Exists(target)) throw new ArgumentException("Rule pre-check path must be an existing file or directory.");
        return Directory.EnumerateFiles(target, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Where(IsCandidate)
            .Where(path => !Path.GetRelativePath(root, path).Replace('\\', '/').Split('/').Any(segment =>
                segment is ".git" or ".quality" or "node_modules" or "bin" or "obj"));
    }

    private static bool IsCandidate(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".css" or ".scss" or ".html" or ".ts" or ".cs";

    private static void AddMatches(
        ICollection<ReviewFinding> findings,
        IReadOnlyList<string> lines,
        string path,
        EffectiveQualityRule rule,
        Regex pattern,
        string title,
        string description,
        string recommendation)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            foreach (Match match in pattern.Matches(lines[index]))
            {
                if (!match.Success || (rule.Definition.Id == "QS-NG-002" &&
                    lines[index].TrimStart().StartsWith("--", StringComparison.Ordinal))) continue;
                findings.Add(Finding(rule, path, index + 1, match.Index + 1, match.Length,
                    title, description, recommendation, match.Value));
            }
        }
    }

    private static void AddMissingOnPush(
        ICollection<ReviewFinding> findings,
        IReadOnlyList<string> lines,
        string path,
        EffectiveQualityRule rule)
    {
        var componentLine = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Contains("@Component", StringComparison.Ordinal))
            {
                componentLine = index;
                break;
            }
        }
        if (componentLine < 0 || lines.Any(line => line.Contains(
                "changeDetection: ChangeDetectionStrategy.OnPush", StringComparison.Ordinal))) return;
        var column = lines[componentLine].IndexOf("@Component", StringComparison.Ordinal) + 1;
        findings.Add(Finding(rule, path, componentLine + 1, column, "@Component".Length,
            "Angular component does not declare OnPush change detection",
            "The component metadata does not opt into ChangeDetectionStrategy.OnPush.",
            "Import ChangeDetectionStrategy and set changeDetection: ChangeDetectionStrategy.OnPush in the component metadata.",
            "@Component"));
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
        var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"quality-rules\0{rule.Definition.Id}\0{path}\0{line}\0{column}\0{evidence}")));
        return new ReviewFinding(
            $"{rule.Definition.Id.ToLowerInvariant()}-{fingerprint[^12..]}",
            rule.Definition.Category,
            Enum.Parse<FindingSeverity>(rule.Severity, true),
            title,
            description,
            recommendation,
            [new FindingLocation(path, new FindingRange(
                new FindingPosition(line, column),
                new FindingPosition(line, column + Math.Max(0, length - 1))))],
            fingerprint,
            rule.Definition.Id,
            evidence,
            new FindingSource(FindingSourceKind.Deterministic, "quality-rules", "Quality Studio rule pre-check", SensorVersion));
    }

    [GeneratedRegex(@"(?<![-\w])(?:[1-9]\d*|0?\.\d+)px\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RawPixel();

    [GeneratedRegex(@"\bstyle\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineStyle();

    [GeneratedRegex(@"\.(?:Result\b|Wait\s*\()", RegexOptions.CultureInvariant)]
    private static partial Regex SyncOverAsync();
}
