using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Enforces the mechanically decidable subset of the named rule catalogue. The
/// sensor deliberately leaves semantic rules, such as component reuse, to review.
/// </summary>
public sealed class RulePrecheckSensor : IDeterministicEvidenceSensor
{
    public const string SensorId = "quality-rules";
    public const string SensorVersion = "1.0.0";
    private const int MaximumSourceBytes = 2 * 1024 * 1024;
    private static readonly Regex CssDeclaration = new(
        @"(?<![-\w])(?<property>(?:padding|margin|gap|row-gap|column-gap|color|background(?:-color)?|border-radius)(?:-(?:top|right|bottom|left|inline|block)(?:-start|-end)?)?)\s*:\s*(?<value>[^;{}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RawStyleLiteral = new(
        @"(?<![-\w.])(?:\d+(?:\.\d+)?px)\b|#[0-9a-f]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ComponentMetadata = new(
        @"@Component\s*\(\s*\{(?<metadata>[\s\S]*?)\}\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InlinePresentation = new(
        @"(?<![\w])(?<name>template|styles)\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Id => SensorId;
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository, SensorScope.Path];

    public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
        {
            [SensorId] = SensorVersion,
            ["rule-catalogue"] = "v1",
        }));

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        }

        var target = ResolveTarget(root, request);
        var findings = new List<ReviewFinding>();
        foreach (var file in EnumerateSources(target))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.Length > MaximumSourceBytes) continue;
            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                continue;
            }

            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is ".css" or ".scss")
            {
                AnalyzeStyle(path, normalized, findings);
            }
            else if (file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
                     !IsTestSource(file))
            {
                AnalyzeComponentMetadata(path, normalized, findings);
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
                Id,
                Version,
                request.Scope.ToString().ToLowerInvariant(),
                request.Scope == SensorScope.Repository ? "." : request.Path ?? ".",
                DateTimeOffset.UtcNow.ToString("O"),
                new Dictionary<string, string> { [SensorId] = Version }));
    }

    private static void AnalyzeStyle(
        string path,
        string content,
        ICollection<ReviewFinding> findings)
    {
        var lines = content.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            foreach (Match declaration in CssDeclaration.Matches(line))
            {
                var value = declaration.Groups["value"].Value;
                var literal = RawStyleLiteral.Match(value);
                if (!literal.Success) continue;
                var property = declaration.Groups["property"].Value;
                findings.Add(Finding(
                    "QS-NG-002",
                    FindingSeverity.Medium,
                    "Ad-hoc style value bypasses the central token vocabulary",
                    $"The `{property}` declaration contains raw value `{literal.Value}` outside a token definition.",
                    "Replace the literal with the existing central semantic or scale token. Add a central token only when the design system has no matching value.",
                    path,
                    index + 1,
                    declaration.Index + 1,
                    declaration.Index + declaration.Length,
                    new { property, literal = literal.Value, check = "design-token-literals" }));
            }
        }
    }

    private static void AnalyzeComponentMetadata(
        string path,
        string content,
        ICollection<ReviewFinding> findings)
    {
        foreach (Match component in ComponentMetadata.Matches(content))
        {
            var metadata = component.Groups["metadata"];
            foreach (Match presentation in InlinePresentation.Matches(metadata.Value))
            {
                var absoluteIndex = metadata.Index + presentation.Index;
                var (line, column) = Position(content, absoluteIndex);
                var name = presentation.Groups["name"].Value;
                findings.Add(Finding(
                    "QS-NG-004",
                    FindingSeverity.Medium,
                    "Angular component uses inline presentation metadata",
                    $"The component declares inline `{name}` instead of an external, independently testable file.",
                    name == "template"
                        ? "Move the markup to a colocated template file and reference it with templateUrl."
                        : "Move the styles to a colocated stylesheet and reference it with styleUrl.",
                    path,
                    line,
                    column,
                    column + presentation.Length - 1,
                    new { metadata = name, check = "external-templates" }));
            }
        }
    }

    private static ReviewFinding Finding(
        string ruleId,
        FindingSeverity severity,
        string title,
        string description,
        string recommendation,
        string path,
        int line,
        int startColumn,
        int endColumn,
        object evidence)
    {
        var canonical = $"{SensorId}\0{ruleId}\0{path}\0{line}\0{startColumn}\0{description}";
        var fingerprint = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new ReviewFinding(
            $"quality-rule-{fingerprint[7..19]}",
            "maintainability",
            severity,
            title,
            description,
            recommendation,
            [new FindingLocation(path, new FindingRange(
                new FindingPosition(line, startColumn),
                new FindingPosition(line, Math.Max(startColumn, endColumn))))],
            fingerprint,
            ruleId,
            JsonSerializer.Serialize(evidence),
            new FindingSource(FindingSourceKind.Deterministic, SensorId, "Quality Studio named-rule precheck", SensorVersion));
    }

    private static (int Line, int Column) Position(string content, int index)
    {
        var line = 1;
        var lastLineStart = 0;
        for (var cursor = 0; cursor < index; cursor++)
        {
            if (content[cursor] != '\n') continue;
            line++;
            lastLineStart = cursor + 1;
        }

        return (line, index - lastLineStart + 1);
    }

    private static string ResolveTarget(string root, SensorScanRequest request)
    {
        if (request.Scope == SensorScope.Repository || string.IsNullOrWhiteSpace(request.Path)) return root;
        var target = Path.GetFullPath(Path.Combine(root, request.Path.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if ((!string.Equals(target, root, PathComparison) && !target.StartsWith(prefix, PathComparison)) ||
            !File.Exists(target) && !Directory.Exists(target))
        {
            throw new ArgumentException(
                "Rule precheck path must be an existing path inside the repository.", nameof(request));
        }

        return target;
    }

    private static IEnumerable<string> EnumerateSources(string target)
    {
        if (File.Exists(target))
        {
            if (IsSupported(target) && !File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
                yield return target;
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (IsSupported(file) && !File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                    yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(path => path, StringComparer.Ordinal))
            {
                if (!IsIgnoredDirectory(child) &&
                    !File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    pending.Push(child);
            }
        }
    }

    private static bool IsSupported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".css" or ".scss" or ".ts";

    private static bool IsTestSource(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains(".spec.", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".fixture.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredDirectory(string path) =>
        Path.GetFileName(path) is ".git" or ".quality" or ".quality-studio" or "bin" or "obj" or
            "node_modules" or "dist" or "coverage" or ".angular" or ".next" or "TestResults";

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
