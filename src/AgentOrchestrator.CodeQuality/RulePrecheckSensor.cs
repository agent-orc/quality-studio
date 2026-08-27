using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Fast, deterministic enforcement for the mechanically decidable subset of named rules.</summary>
public sealed partial class RulePrecheckSensor : IDeterministicEvidenceSensor
{
    public const string SensorVersion = "1.0.0";
    private readonly RuleLibrary library;

    public RulePrecheckSensor() : this(RuleLibrary.BuiltIn) { }

    public RulePrecheckSensor(RuleLibrary library) => this.library = library;

    public string Id => "qs-rules";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository, SensorScope.Path];

    public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
        {
            [Id] = SensorVersion,
            ["rule-library"] = string.Join(",", library.List().Select(rule => $"{rule.Id}@{rule.Version}")),
        }));

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        RuleConfiguration configuration;
        try
        {
            configuration = RuleConfiguration.Load(root, library.List());
        }
        catch (RuleConfigurationException exception)
        {
            return Unavailable(request, exception.Message);
        }

        var files = EnumerateFiles(root, request).ToArray();
        var scope = RepositoryScope.Load(root);
        var findings = new List<ReviewFinding>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!scope.Evaluate(relative, file).Included) continue;
            var active = library.Resolve(configuration, [relative], "code")
                .Where(rule => rule.Rule.Deterministic)
                .ToDictionary(rule => rule.Rule.Id, StringComparer.Ordinal);
            if (active.Count == 0) continue;
            var lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            if (active.TryGetValue("QS-NG-002", out var tokens) &&
                Path.GetExtension(file) is ".css" or ".scss")
                FindVisualLiterals(relative, lines, tokens, findings);
            if (active.TryGetValue("QS-NG-004", out var template) &&
                Path.GetExtension(file).Equals(".html", StringComparison.OrdinalIgnoreCase))
                FindInlineTemplateStyles(relative, lines, template, findings);
            if (active.TryGetValue("QS-NG-004", out template) &&
                Path.GetExtension(file).Equals(".ts", StringComparison.OrdinalIgnoreCase))
                FindInlineComponentMetadata(relative, lines, template, findings);
            if (active.TryGetValue("QS-DN-003", out var asyncRule) &&
                Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                FindBlockingAsync(relative, lines, asyncRule, findings);
        }
        return new SensorScanResult(
            true,
            null,
            findings.DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .OrderBy(finding => finding.Locations[0].Path, StringComparer.Ordinal)
                .ThenBy(finding => finding.Locations[0].Range!.Start.Line)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToArray(),
            Provenance(request));
    }

    private static IEnumerable<string> EnumerateFiles(string root, SensorScanRequest request)
    {
        if (request.Scope == SensorScope.Path && !string.IsNullOrWhiteSpace(request.Path))
        {
            var target = AnalyzerCommand.ContainedPath(root, request.Path);
            if (File.Exists(target)) return Supported(target) ? [target] : [];
            if (!Directory.Exists(target)) return [];
            return Directory.EnumerateFiles(target, "*", Options()).Where(Supported);
        }
        return Directory.EnumerateFiles(root, "*", Options()).Where(Supported);
    }

    private static EnumerationOptions Options() => new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private static bool Supported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".css" or ".scss" or ".html" or ".ts" or ".cs";

    private static void FindVisualLiterals(
        string path,
        IReadOnlyList<string> lines,
        EffectiveQualityRule rule,
        ICollection<ReviewFinding> findings)
    {
        var allowedPixels = StringOptions(rule.Options, "allowedPixelValues", ["0", "1"]);
        var ignoredPaths = StringOptions(rule.Options, "ignorePaths", []);
        if (ignoredPaths.Any(pattern => RepositoryScope.PatternMatches(pattern, path))) return;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            foreach (Match declaration in CssDeclaration().Matches(line))
            {
                if (declaration.Groups["property"].Value.StartsWith("--", StringComparison.Ordinal)) continue;
                var property = declaration.Groups["property"].Value;
                var value = declaration.Groups["value"].Value;
                Match? literal = null;
                if (VisualColorProperty().IsMatch(property)) literal = ColorLiteral().Match(value);
                if ((literal is null || !literal.Success) && VisualGeometryProperty().IsMatch(property))
                {
                    literal = PixelLiteral().Matches(value).Cast<Match>()
                        .FirstOrDefault(match => !allowedPixels.Contains(match.Groups["number"].Value, StringComparer.Ordinal));
                }
                if (literal is null || !literal.Success) continue;
                var column = declaration.Groups["value"].Index + literal.Index + 1;
                findings.Add(Finding(rule, path, index + 1, column, literal.Length,
                    $"Ad-hoc visual literal '{literal.Value}' bypasses the shared design-token scale.", line));
            }
        }
    }

    private static void FindInlineTemplateStyles(
        string path,
        IReadOnlyList<string> lines,
        EffectiveQualityRule rule,
        ICollection<ReviewFinding> findings)
    {
        var allowed = StringOptions(rule.Options, "allowedBindings", []);
        for (var index = 0; index < lines.Count; index++)
        {
            foreach (Match match in InlineTemplateStyle().Matches(lines[index]))
            {
                if (allowed.Contains(match.Value, StringComparer.Ordinal)) continue;
                findings.Add(Finding(rule, path, index + 1, match.Index + 1, match.Length,
                    $"Inline presentation binding '{match.Value}' belongs in a class and component stylesheet.", lines[index]));
            }
        }
    }

    private static void FindBlockingAsync(
        string path,
        IReadOnlyList<string> lines,
        EffectiveQualityRule rule,
        ICollection<ReviewFinding> findings)
    {
        if (BooleanOption(rule.Options, "allowBlockingCalls")) return;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            foreach (Match match in BlockingAsyncCall().Matches(line))
            {
                findings.Add(Finding(rule, path, index + 1, match.Index + 1, match.Length,
                    $"Blocking async call '{match.Value}' can deadlock or starve request threads.", line));
            }
        }
    }

    private static void FindInlineComponentMetadata(
        string path,
        IReadOnlyList<string> lines,
        EffectiveQualityRule rule,
        ICollection<ReviewFinding> findings)
    {
        var componentStart = -1;
        var braceDepth = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (componentStart < 0 && line.Contains("@Component", StringComparison.Ordinal))
            {
                componentStart = index;
                braceDepth = 0;
            }
            if (componentStart < 0) continue;
            braceDepth += line.Count(character => character == '{');
            braceDepth -= line.Count(character => character == '}');
            foreach (Match match in InlineComponentMetadata().Matches(line))
            {
                findings.Add(Finding(rule, path, index + 1, match.Index + 1, match.Length,
                    $"Inline Angular metadata '{match.Value}' bypasses the colocated external template/style contract.", line));
            }
            if (index > componentStart && braceDepth <= 0) componentStart = -1;
        }
    }

    private static ReviewFinding Finding(
        EffectiveQualityRule rule,
        string path,
        int line,
        int column,
        int length,
        string observation,
        string evidence)
    {
        var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{SensorVersion}\0{rule.Rule.Id}\0{path}\0{line}\0{column}\0{observation}")));
        return new ReviewFinding(
            $"qs-rule-{fingerprint[^16..]}",
            "named-rules",
            rule.Severity,
            rule.Rule.Title,
            observation,
            rule.Rule.Statement,
            [new FindingLocation(path, new FindingRange(
                new FindingPosition(line, column),
                new FindingPosition(line, column + Math.Max(0, length - 1))))],
            fingerprint,
            rule.Rule.Id,
            evidence.Trim(),
            new FindingSource(FindingSourceKind.Deterministic, "qs-rules", "Quality Studio rule pre-check", SensorVersion));
    }

    private static IReadOnlyList<string> StringOptions(
        IReadOnlyDictionary<string, JsonElement> options,
        string name,
        IReadOnlyList<string> fallback)
    {
        if (!options.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Array) return fallback;
        return value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).ToArray();
    }

    private static bool BooleanOption(IReadOnlyDictionary<string, JsonElement> options, string name) =>
        options.TryGetValue(name, out var value) && value.ValueKind is JsonValueKind.True;

    private static SensorScanResult Unavailable(SensorScanRequest request, string reason) =>
        new(false, reason, [], Provenance(request));

    private static SensorProvenance Provenance(SensorScanRequest request) =>
        new("qs-rules", SensorVersion, request.Scope.ToString().ToLowerInvariant(),
            request.Scope == SensorScope.Repository ? "." : request.Path ?? "(missing)",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            new Dictionary<string, string> { ["qs-rules"] = SensorVersion });

    [GeneratedRegex(@"(?<property>(?:--)?[a-zA-Z][a-zA-Z-]*)\s*:\s*(?<value>[^;{}]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CssDeclaration();

    [GeneratedRegex(@"^(color|background(-color)?|border(-top|-right|-bottom|-left)?-color|box-shadow|text-shadow)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VisualColorProperty();

    [GeneratedRegex(@"^(margin(-top|-right|-bottom|-left)?|padding(-top|-right|-bottom|-left)?|gap|row-gap|column-gap|border-radius|width|height|min-width|max-width|min-height|max-height)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VisualGeometryProperty();

    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ColorLiteral();

    [GeneratedRegex(@"(?<number>\d+(?:\.\d+)?)px\b", RegexOptions.CultureInvariant)]
    private static partial Regex PixelLiteral();

    [GeneratedRegex(@"\bstyle\s*=|\[style(?:\.[^\]]+)?\]|\[ngStyle\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineTemplateStyle();

    [GeneratedRegex(@"\b(?:template|styles?)\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex InlineComponentMetadata();

    [GeneratedRegex(@"(?:\.Result\b|\.Wait\s*\(\s*\)|\.GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(\s*\))", RegexOptions.CultureInvariant)]
    private static partial Regex BlockingAsyncCall();
}
