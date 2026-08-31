using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Runs the repository-local Angular AOT compiler and normalizes template diagnostics.
/// </summary>
public sealed partial class AngularCompilerSensor : IDeterministicEvidenceSensor, IRepositoryAwareReviewSensor
{
    public const string SensorVersion = "1.0.0";
    private const string TargetRelativePath = "frontend/tsconfig.app.json";
    private const string CompilerRelativePath = "frontend/node_modules/.bin/ngc";
    private const int MaximumReasonCharacters = 1_000;
    private readonly ISensorCommandRunner runner;
    private readonly string? probeRoot;

    public AngularCompilerSensor() : this(new ProcessSensorCommandRunner(), null)
    {
    }

    public AngularCompilerSensor(ISensorCommandRunner runner, string? probeRoot = null)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.probeRoot = probeRoot;
    }

    public string Id => "angular-compiler";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public Task<SensorAvailability> ProbeAvailabilityAsync(
        CancellationToken cancellationToken = default) =>
        ProbeAvailabilityAsync(probeRoot ?? Directory.GetCurrentDirectory(), cancellationToken);

    public async Task<SensorAvailability> ProbeAvailabilityAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ResolvedAngularCompiler resolved;
        try
        {
            resolved = Resolve(repositoryRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            return new SensorAvailability(false, BoundedReason(exception.Message));
        }

        try
        {
            var probe = await runner.RunAsync(
                resolved.Executable, ["--version"], resolved.Root, cancellationToken).ConfigureAwait(false);
            if (probe.ExitCode != 0)
            {
                return new SensorAvailability(false, BoundedReason(
                    $"Angular compiler is unavailable: version probe exited with code {probe.ExitCode}."));
            }

            return new SensorAvailability(true, ToolVersions: ToolVersions(resolved));
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return new SensorAvailability(false, BoundedReason(
                $"Angular compiler is unavailable: {exception.Message}"));
        }
    }

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scope != SensorScope.Repository)
            return Unavailable(request, EmptyVersions(), "Angular compiler supports repository scans only.");

        ResolvedAngularCompiler resolved;
        try
        {
            resolved = Resolve(request.RepositoryRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            return Unavailable(request, EmptyVersions(), exception.Message);
        }

        var versions = ToolVersions(resolved);
        try
        {
            var probe = await runner.RunAsync(
                resolved.Executable, ["--version"], resolved.Root, cancellationToken).ConfigureAwait(false);
            if (probe.ExitCode != 0)
            {
                return Unavailable(request, versions,
                    $"Angular compiler version probe exited with code {probe.ExitCode}.");
            }

            var compilation = await runner.RunAsync(
                resolved.Executable,
                ["-p", TargetRelativePath],
                resolved.Root,
                cancellationToken).ConfigureAwait(false);
            var findings = Parse(
                CombinedOutput(compilation),
                resolved.Root,
                resolved.ProjectDirectory,
                resolved.AngularCompilerVersion);
            if (compilation.ExitCode != 0 && findings.Count == 0)
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
        return ColonDiagnostic().Matches(stripped)
            .Concat(ParenthesizedDiagnostic().Matches(stripped))
            .Select(match => ParseDiagnostic(match, root, project, producerVersion))
            .Where(finding => finding is not null)
            .Select(finding => finding!)
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
                root, TargetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ReviewFinding? ParseDiagnostic(
        Match match,
        string root,
        string project,
        string? producerVersion)
    {
        var path = NormalizeReportedPath(root, project, match.Groups["path"].Value);
        if (path is null) return null;
        var line = Math.Max(1, int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture));
        var column = Math.Max(1, int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture));
        var ruleId = match.Groups["rule"].Value.ToUpperInvariant();
        var message = match.Groups["message"].Value.Trim();
        if (message.Length > 4_000) message = message[..4_000];
        var severity = string.Equals(
            match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase)
            ? FindingSeverity.High
            : FindingSeverity.Medium;
        var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"angular-compiler\0{path}\0{line}\0{column}\0{ruleId}\0{message}")));

        return new ReviewFinding(
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
                    new FindingPosition(line, column)))],
            fingerprint,
            ruleId,
            Source: new FindingSource(
                FindingSourceKind.Deterministic,
                "angular-compiler",
                "AngularCompiler",
                producerVersion));
    }

    private static ResolvedAngularCompiler Resolve(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
            throw new ArgumentException("Angular repository root does not exist.");
        var target = Path.Combine(root, TargetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(target))
            throw new ArgumentException($"Angular project is unavailable: {TargetRelativePath} was not found.");
        var executable = Path.Combine(root, CompilerRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(executable))
            throw new ArgumentException("Angular compiler is unavailable: repository-local ngc was not found.");
        EnsureRepositoryConfined(root, executable);

        var packagePath = Path.Combine(
            root, "frontend", "node_modules", "@angular", "compiler-cli", "package.json");
        var angularVersion = ReadPackageVersion(packagePath, "Angular compiler");
        var typescriptVersion = ReadPackageVersion(
            Path.Combine(root, "frontend", "node_modules", "typescript", "package.json"), "TypeScript");
        return new ResolvedAngularCompiler(
            root,
            Path.GetDirectoryName(target)!,
            executable,
            angularVersion,
            typescriptVersion);
    }

    private static void EnsureRepositoryConfined(string root, string executable)
    {
        if (!AnalyzerCommand.IsWithin(root, executable))
            throw new ArgumentException("Angular compiler path is outside the repository.");
        var target = new FileInfo(executable).ResolveLinkTarget(returnFinalTarget: true);
        if (target is not null && !AnalyzerCommand.IsWithin(root, target.FullName))
            throw new ArgumentException("Angular compiler link resolves outside the repository.");
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
        {
            throw new ArgumentException($"{tool} package metadata does not declare a version.");
        }
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

    private static IReadOnlyDictionary<string, string> ToolVersions(ResolvedAngularCompiler resolved) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["angularCompiler"] = resolved.AngularCompilerVersion,
            ["typescript"] = resolved.TypeScriptVersion,
        };

    private static IReadOnlyDictionary<string, string> EmptyVersions() =>
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static string CombinedOutput(SensorCommandResult result) =>
        string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string OutputDetail(SensorCommandResult result)
    {
        var detail = CombinedOutput(result).Trim();
        if (detail.Length == 0) return "The command returned no diagnostic output.";
        return Trim(detail, MaximumReasonCharacters / 2);
    }

    private static string BoundedReason(string reason) =>
        Trim(string.IsNullOrWhiteSpace(reason) ? "Angular compiler is unavailable." : reason.Trim(),
            MaximumReasonCharacters);

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static string StripAnsi(string value) => Ansi().Replace(value, string.Empty);

    private sealed record ResolvedAngularCompiler(
        string Root,
        string ProjectDirectory,
        string Executable,
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
}
