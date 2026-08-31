using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Runs the repository-local Angular AOT compiler and normalizes template and TypeScript diagnostics.
/// </summary>
public sealed partial class AngularCompilerSensor :
    IDeterministicEvidenceSensor,
    IRepositoryScopedAvailabilitySensor
{
    public const string SensorVersion = "1.0.0";
    private const int MaximumOutputCharacters = 262_144;
    private const int MaximumReasonCharacters = 1_000;
    private const int MaximumDiagnosticCharacters = 4_000;
    private const string ProjectRelativePath = "frontend/tsconfig.app.json";
    private const string CompilerRelativePath =
        "frontend/node_modules/@angular/compiler-cli/bundles/src/bin/ngc.js";
    private const string CompilerManifestRelativePath =
        "frontend/node_modules/@angular/compiler-cli/package.json";
    private const string TypeScriptManifestRelativePath =
        "frontend/node_modules/typescript/package.json";

    private readonly ISensorCommandRunner runner;
    private readonly string? availabilityRoot;

    public AngularCompilerSensor() : this(
        new ProcessSensorCommandRunner(MaximumOutputCharacters), null)
    {
    }

    public AngularCompilerSensor(
        ISensorCommandRunner commandRunner,
        string? availabilityRoot = null)
    {
        runner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        this.availabilityRoot = availabilityRoot;
    }

    public string Id => "angular-compiler";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public Task<SensorAvailability> ProbeAvailabilityAsync(
        CancellationToken cancellationToken = default) =>
        ProbeAvailabilityAsync(
            availabilityRoot ?? Directory.GetCurrentDirectory(), cancellationToken);

    public async Task<SensorAvailability> ProbeAvailabilityAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ResolvedCompiler resolved;
        try
        {
            resolved = Resolve(repositoryRoot);
        }
        catch (Exception exception) when (IsResolutionException(exception))
        {
            return new SensorAvailability(false, BoundedReason(exception.Message));
        }

        try
        {
            var probe = await runner.RunAsync(
                "node", [resolved.CompilerPath, "--version"], resolved.Root, cancellationToken)
                .ConfigureAwait(false);
            if (probe.OutputTruncated)
                return new SensorAvailability(false,
                    "Angular compiler is unavailable: version output exceeded the bounded limit.");
            return probe.ExitCode == 0
                ? new SensorAvailability(true, ToolVersions: ToolVersions(resolved))
                : new SensorAvailability(false, BoundedReason(
                    $"Angular compiler is unavailable: version probe exited with code {probe.ExitCode}."));
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return new SensorAvailability(false,
                BoundedReason($"Angular compiler is unavailable: {exception.Message}"));
        }
    }

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scope != SensorScope.Repository)
            return Unavailable(request, EmptyVersions(),
                "Angular compiler supports repository scans only.");

        ResolvedCompiler resolved;
        try
        {
            resolved = Resolve(request.RepositoryRoot);
        }
        catch (Exception exception) when (IsResolutionException(exception))
        {
            return Unavailable(request, EmptyVersions(), exception.Message);
        }

        var versions = ToolVersions(resolved);
        try
        {
            var probe = await runner.RunAsync(
                "node", [resolved.CompilerPath, "--version"], resolved.Root, cancellationToken)
                .ConfigureAwait(false);
            if (probe.OutputTruncated || probe.ExitCode != 0)
                return Unavailable(request, versions,
                    probe.OutputTruncated
                        ? "Angular compiler version output exceeded the bounded limit."
                        : $"Angular compiler version probe exited with code {probe.ExitCode}.");

            var compilation = await runner.RunAsync(
                "node",
                [resolved.CompilerPath, "-p", ProjectRelativePath],
                resolved.Root,
                cancellationToken).ConfigureAwait(false);
            if (compilation.OutputTruncated)
                return Unavailable(request, versions,
                    $"Angular compiler output exceeded the {MaximumOutputCharacters}-character limit.");

            var output = CombinedOutput(compilation);
            if (output.Length > MaximumOutputCharacters)
                return Unavailable(request, versions,
                    $"Angular compiler output exceeded the {MaximumOutputCharacters}-character limit.");

            var findings = Parse(
                output, resolved.Root, resolved.ProjectDirectory, resolved.AngularCompilerVersion);
            if ((compilation.ExitCode != 0 || !string.IsNullOrWhiteSpace(output)) &&
                findings.Count == 0)
            {
                return Unavailable(request, versions,
                    $"Angular compiler exited with code {compilation.ExitCode} without parseable diagnostics: " +
                    OutputDetail(compilation));
            }

            return Available(request, findings, versions);
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return Unavailable(request, versions,
                $"Angular compiler is unavailable: {exception.Message}");
        }
    }

    public static IReadOnlyList<ReviewFinding> Parse(
        string output,
        string repositoryRoot,
        string? projectDirectory = null,
        string? producerVersion = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var project = Path.GetFullPath(projectDirectory ?? Path.Combine(root, "frontend"));
        var stripped = StripAnsi(output).Replace("\r\n", "\n", StringComparison.Ordinal);
        var matches = ColonDiagnostic().Matches(stripped)
            .Concat(ParenthesizedDiagnostic().Matches(stripped))
            .OrderBy(match => match.Index)
            .ToArray();
        var findings = new List<ReviewFinding>(matches.Length);

        for (var index = 0; index < matches.Length; index++)
        {
            var match = matches[index];
            var path = NormalizeReportedPath(root, project, match.Groups["path"].Value);
            if (path is null) continue;

            var line = Math.Max(1,
                int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture));
            var column = Math.Max(1,
                int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture));
            var ruleId = match.Groups["rule"].Value.ToUpperInvariant();
            var message = Trim(match.Groups["message"].Value.Trim(), MaximumDiagnosticCharacters);
            var severity = string.Equals(
                match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase)
                ? FindingSeverity.High
                : FindingSeverity.Medium;
            var blockEnd = index + 1 < matches.Length ? matches[index + 1].Index : stripped.Length;
            var underline = Underline().Match(stripped[match.Index..blockEnd]);
            var endColumn = underline.Success
                ? column + underline.Groups["marks"].Length - 1
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
                        new FindingPosition(line, column),
                        new FindingPosition(line, Math.Max(column, endColumn))))],
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
            .ThenBy(finding => finding.Locations[0].Range!.Start.Line)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.Locations[0].Range!.Start.Column)
            .ToArray();
    }

    public static bool HasTarget(string repositoryRoot)
    {
        try
        {
            var root = Path.GetFullPath(repositoryRoot);
            return Directory.Exists(root) && File.Exists(Path.Combine(
                root, ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ResolvedCompiler Resolve(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
            throw new ArgumentException("Angular repository root does not exist.");

        var project = AnalyzerCommand.ContainedPath(root, ProjectRelativePath);
        if (!File.Exists(project))
            throw new ArgumentException(
                $"Angular compiler is unavailable: {ProjectRelativePath} was not found.");
        var compiler = AnalyzerCommand.ContainedPath(root, CompilerRelativePath);
        if (!File.Exists(compiler))
            throw new ArgumentException(
                "Angular compiler is unavailable: the repository-local ngc binary was not found.");
        var compilerManifest = AnalyzerCommand.ContainedPath(root, CompilerManifestRelativePath);
        var typeScriptManifest = AnalyzerCommand.ContainedPath(root, TypeScriptManifestRelativePath);

        return new ResolvedCompiler(
            root,
            Path.GetDirectoryName(project)!,
            compiler,
            ReadPackageVersion(compilerManifest, "Angular compiler"),
            ReadPackageVersion(typeScriptManifest, "TypeScript"));
    }

    private static string ReadPackageVersion(string path, string tool)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"{tool} is unavailable: package metadata was not found.");
        if (new FileInfo(path).Length > 64_000)
            throw new ArgumentException($"{tool} package metadata exceeds the supported size.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("version", out var version) ||
            string.IsNullOrWhiteSpace(version.GetString()))
            throw new ArgumentException($"{tool} package metadata does not declare a version.");
        return version.GetString()!;
    }

    private static string? NormalizeReportedPath(string root, string project, string value)
    {
        var normalized = value.Trim().Replace('\\', '/');
        var projectRelative = Path.GetRelativePath(root, project).Replace('\\', '/').TrimEnd('/');
        var absolute = Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : normalized.StartsWith(projectRelative + "/", StringComparison.Ordinal)
                ? Path.GetFullPath(Path.Combine(root, value))
                : Path.GetFullPath(Path.Combine(project, value));
        return AnalyzerCommand.IsWithin(root, absolute)
            ? Path.GetRelativePath(root, absolute).Replace('\\', '/')
            : null;
    }

    private SensorScanResult Available(
        SensorScanRequest request,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyDictionary<string, string> versions) =>
        new(true, null, findings, Provenance(request, versions));

    private SensorScanResult Unavailable(
        SensorScanRequest request,
        IReadOnlyDictionary<string, string> versions,
        string reason) =>
        new(false, BoundedReason(reason), [], Provenance(request, versions));

    private SensorProvenance Provenance(
        SensorScanRequest request,
        IReadOnlyDictionary<string, string> versions) =>
        new(Id, Version, "repository", ".",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), versions);

    private static IReadOnlyDictionary<string, string> ToolVersions(ResolvedCompiler resolved) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["angularCompiler"] = resolved.AngularCompilerVersion,
            ["typescript"] = resolved.TypeScriptVersion,
        };

    private static IReadOnlyDictionary<string, string> EmptyVersions() =>
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static bool IsResolutionException(Exception exception) =>
        exception is ArgumentException or IOException or UnauthorizedAccessException or JsonException;

    private static string CombinedOutput(SensorCommandResult result) =>
        string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string OutputDetail(SensorCommandResult result)
    {
        var detail = CombinedOutput(result).Trim();
        return detail.Length == 0
            ? "The command returned no diagnostic output."
            : Trim(detail, MaximumReasonCharacters / 2);
    }

    private static string BoundedReason(string reason) =>
        Trim(string.IsNullOrWhiteSpace(reason) ? "Angular compiler is unavailable." : reason.Trim(),
            MaximumReasonCharacters);

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static string StripAnsi(string value) => Ansi().Replace(value, string.Empty);

    private sealed record ResolvedCompiler(
        string Root,
        string ProjectDirectory,
        string CompilerPath,
        string AngularCompilerVersion,
        string TypeScriptVersion);

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex Ansi();

    [GeneratedRegex(
        @"^(?<path>.+?):(?<line>[1-9]\d*):(?<column>[1-9]\d*)\s+-\s+(?<severity>error|warning)\s+(?<rule>(?:NG|TS)\d+):\s*(?<message>[^\r\n]+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ColonDiagnostic();

    [GeneratedRegex(
        @"^(?<path>.+?)\((?<line>[1-9]\d*),(?<column>[1-9]\d*)\):\s*(?<severity>error|warning)\s+(?<rule>(?:NG|TS)\d+):\s*(?<message>[^\r\n]+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedDiagnostic();

    [GeneratedRegex(@"^[^\r\n~]*(?<marks>~+)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Underline();
}
