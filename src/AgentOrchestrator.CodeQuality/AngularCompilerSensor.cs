using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Runs the repository-local Angular AOT compiler and normalizes template and TypeScript diagnostics.
/// </summary>
public sealed partial class AngularCompilerSensor(
    ISensorCommandRunner? commandRunner = null,
    string? availabilityRoot = null) : IDeterministicEvidenceSensor, IRepositoryScopedAvailabilitySensor
{
    public const string SensorVersion = "1.0.0";
    private const int MaximumOutputCharacters = 262_144;
    private const int MaximumReasonCharacters = 500;
    private const string ProjectPath = "frontend/tsconfig.app.json";
    private const string CompilerPath =
        "frontend/node_modules/@angular/compiler-cli/bundles/src/bin/ngc.js";
    private const string CompilerManifestPath =
        "frontend/node_modules/@angular/compiler-cli/package.json";

    private readonly ISensorCommandRunner runner = commandRunner ?? new ProcessSensorCommandRunner();
    private readonly string probeRoot = Path.GetFullPath(availabilityRoot ?? Directory.GetCurrentDirectory());

    public string Id => "angular-compiler";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public async Task<SensorAvailability> ProbeAvailabilityAsync(
        CancellationToken cancellationToken = default) =>
        await ProbeAvailabilityAsync(probeRoot, cancellationToken).ConfigureAwait(false);

    public async Task<SensorAvailability> ProbeAvailabilityAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var resolution = Resolve(root);
        if (!resolution.Available)
            return new SensorAvailability(false, resolution.Reason);

        try
        {
            var probe = await runner.RunAsync(
                "node", [resolution.Compiler!, "--version"], root, cancellationToken)
                .ConfigureAwait(false);
            if (probe.ExitCode != 0)
                return new SensorAvailability(false, Bounded(
                    $"Angular compiler is unavailable: version probe exited with code {probe.ExitCode}."));
            return new SensorAvailability(true, ToolVersions: ToolVersions(resolution.Version));
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return new SensorAvailability(false,
                Bounded($"Angular compiler is unavailable: {exception.Message}"));
        }
    }

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");

        var resolution = Resolve(root);
        if (!resolution.Available)
            return Unavailable(request, resolution.Reason!,
                new Dictionary<string, string>(StringComparer.Ordinal));

        var versions = ToolVersions(resolution.Version);
        try
        {
            var probe = await runner.RunAsync(
                "node", [resolution.Compiler!, "--version"], root, cancellationToken)
                .ConfigureAwait(false);
            if (probe.ExitCode != 0)
                return Unavailable(request,
                    $"Angular compiler version probe exited with code {probe.ExitCode}.", versions);

            var result = await runner.RunAsync(
                "node", [resolution.Compiler!, "-p", ProjectPath], root, cancellationToken)
                .ConfigureAwait(false);
            var output = CombinedOutput(result);
            if (output.Length > MaximumOutputCharacters)
                return Unavailable(request,
                    $"Angular compiler output exceeded the {MaximumOutputCharacters}-character limit.",
                    versions);

            var findings = Parse(output, root, Path.Combine(root, "frontend"), resolution.Version);
            if ((result.ExitCode != 0 || !string.IsNullOrWhiteSpace(output)) && findings.Count == 0)
            {
                return Unavailable(request,
                    $"Angular compiler exited with code {result.ExitCode} without parseable diagnostics. " +
                    OutputDetail(result), versions);
            }

            return Available(request, findings, versions);
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return Unavailable(request,
                $"Angular compiler is unavailable: {exception.Message}", versions);
        }
    }

    public static IReadOnlyList<ReviewFinding> Parse(
        string output,
        string repositoryRoot,
        string? reportedPathRoot = null,
        string? producerVersion = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var pathRoot = Path.GetFullPath(reportedPathRoot ?? Path.Combine(root, "frontend"));
        var normalizedOutput = StripAnsi(output).Replace("\r\n", "\n", StringComparison.Ordinal);
        var matches = Diagnostic().Matches(normalizedOutput);
        var findings = new List<ReviewFinding>(matches.Count);

        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var path = NormalizeReportedPath(root, pathRoot, match.Groups["path"].Value);
            var line = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
            var column = int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture);
            var ruleId = match.Groups["rule"].Value.ToUpperInvariant();
            var message = match.Groups["message"].Value.Trim();
            var severity = string.Equals(
                match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase)
                ? FindingSeverity.High
                : FindingSeverity.Medium;
            var blockEnd = index + 1 < matches.Count ? matches[index + 1].Index : normalizedOutput.Length;
            var block = normalizedOutput[match.Index..blockEnd];
            var squiggle = Squiggle().Match(block);
            var endColumn = squiggle.Success
                ? column + squiggle.Groups["marks"].Length - 1
                : column;
            var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"angular-compiler\0{path}\0{line}\0{column}\0{endColumn}\0{ruleId}\0{message}")));

            findings.Add(new ReviewFinding(
                $"angular-{ruleId.ToLowerInvariant()}-{fingerprint[^12..]}",
                "compiler",
                severity,
                $"{ruleId}: {Trim(message, 260)}",
                message,
                $"Correct the Angular compiler diagnostic reported by {ruleId}.",
                [new FindingLocation(
                    path,
                    new FindingRange(
                        new FindingPosition(Math.Max(1, line), Math.Max(1, column)),
                        new FindingPosition(Math.Max(1, line), Math.Max(1, endColumn))))],
                fingerprint,
                ruleId,
                Source: new FindingSource(
                    FindingSourceKind.Deterministic,
                    "angular-compiler",
                    "AngularCompiler",
                    producerVersion)));
        }

        return findings
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .OrderBy(finding => finding.Locations[0].Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.Locations[0].Range?.Start.Line ?? 0)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool HasTarget(string root)
    {
        try
        {
            return File.Exists(Path.Combine(Path.GetFullPath(root),
                ProjectPath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Resolution Resolve(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var project = Path.Combine(fullRoot, ProjectPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(project))
            return new Resolution(false,
                "Angular compiler is unavailable: frontend/tsconfig.app.json was not found.");

        var compiler = Path.Combine(fullRoot, CompilerPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(compiler) || !AnalyzerCommand.IsWithin(fullRoot, compiler))
            return new Resolution(false,
                "Angular compiler is unavailable: the repository-local ngc binary was not found.");

        var manifest = Path.Combine(fullRoot,
            CompilerManifestPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(manifest) || !AnalyzerCommand.IsWithin(fullRoot, manifest))
            return new Resolution(false,
                "Angular compiler is unavailable: its repository-local package manifest was not found.");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var version = document.RootElement.TryGetProperty("version", out var value)
                ? value.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(version))
                return new Resolution(false,
                    "Angular compiler is unavailable: its package version was not reported.");
            return new Resolution(true, null, compiler, version);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new Resolution(false,
                Bounded($"Angular compiler is unavailable: its package manifest could not be read: {exception.Message}"));
        }
    }

    private SensorScanResult Available(
        SensorScanRequest request,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyDictionary<string, string> versions) =>
        new(true, null, findings, Provenance(request, versions));

    private SensorScanResult Unavailable(
        SensorScanRequest request,
        string reason,
        IReadOnlyDictionary<string, string> versions) =>
        new(false, Bounded(reason), [], Provenance(request, versions));

    private SensorProvenance Provenance(
        SensorScanRequest request,
        IReadOnlyDictionary<string, string> versions) =>
        new(Id, Version, "repository", ".",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), versions);

    private static IReadOnlyDictionary<string, string> ToolVersions(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["angularCompiler"] = version,
            };

    private static string NormalizeReportedPath(string root, string pathRoot, string value)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(pathRoot, value));
        return AnalyzerCommand.IsWithin(root, path)
            ? Normalize(Path.GetRelativePath(root, path))
            : "external/" + Path.GetFileName(path);
    }

    private static string CombinedOutput(SensorCommandResult result) =>
        string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string OutputDetail(SensorCommandResult result)
    {
        var detail = CombinedOutput(result).Trim();
        if (detail.Length == 0) return "The command returned no diagnostic output.";
        return Trim(detail, 1_000);
    }

    private static string Bounded(string value) => Trim(value, MaximumReasonCharacters);
    private static string Normalize(string path) => path.Replace('\\', '/');
    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
    private static string StripAnsi(string value) => Ansi().Replace(value, string.Empty);

    private sealed record Resolution(
        bool Available,
        string? Reason,
        string? Compiler = null,
        string? Version = null);

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex Ansi();

    [GeneratedRegex(
        @"^(?<path>.+?):(?<line>[1-9]\d*):(?<column>[1-9]\d*)\s+-\s+(?<severity>error|warning)\s+(?<rule>(?:NG|TS)\d+):\s*(?<message>[^\r\n]+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Diagnostic();

    [GeneratedRegex(@"^[^\r\n~]*(?<marks>~+)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Squiggle();
}
