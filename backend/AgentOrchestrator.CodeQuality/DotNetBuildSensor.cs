using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Runs a repository's Release build and normalizes compiler and Roslyn analyzer diagnostics.
/// </summary>
public sealed partial class DotNetBuildSensor(ISensorCommandRunner? commandRunner = null)
    : IDeterministicEvidenceSensor
{
    public const string SensorVersion = "1.0.0";
    private readonly ISensorCommandRunner runner = commandRunner ?? new ProcessSensorCommandRunner();

    public string Id => "dotnet-build";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public async Task<SensorAvailability> ProbeAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await runner.RunAsync(
                "dotnet", ["--version"], Directory.GetCurrentDirectory(), cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0
                ? new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
                {
                    ["dotnet"] = FirstLine(result.StandardOutput),
                })
                : new SensorAvailability(
                    false, $"dotnet is unavailable: version probe exited with code {result.ExitCode}.");
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return new SensorAvailability(false, $"dotnet is unavailable: {exception.Message}");
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

        IReadOnlyList<string> targets;
        try
        {
            targets = ResolveTargets(root, request.Configuration);
        }
        catch (ArgumentException exception)
        {
            return Unavailable(
                request, new Dictionary<string, string>(StringComparer.Ordinal), exception.Message);
        }

        if (targets.Count == 0)
            return Available(request, [], new Dictionary<string, string>(StringComparer.Ordinal));

        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var findings = new List<ReviewFinding>();
        try
        {
            var version = await runner.RunAsync("dotnet", ["--version"], root, cancellationToken)
                .ConfigureAwait(false);
            if (version.ExitCode != 0)
                return Unavailable(request, versions,
                    $"dotnet is unavailable: version probe exited with code {version.ExitCode}.");
            versions["dotnet"] = FirstLine(version.StandardOutput);

            foreach (var target in targets)
            {
                var relativeTarget = Path.GetRelativePath(root, target);
                var restore = await runner.RunAsync(
                    "dotnet", ["restore", relativeTarget, "--nologo"], root, cancellationToken)
                    .ConfigureAwait(false);
                if (restore.ExitCode != 0)
                    return Unavailable(request, versions,
                        $"dotnet restore failed for '{Normalize(relativeTarget)}': {OutputDetail(restore)}");

                var build = await runner.RunAsync(
                    "dotnet",
                    [
                        "build", relativeTarget,
                        "--configuration", "Release",
                        "--no-restore",
                        "--nologo",
                        "-p:GenerateFullPaths=true",
                    ],
                    root,
                    cancellationToken).ConfigureAwait(false);
                var parsed = Parse(CombinedOutput(build), root);
                findings.AddRange(parsed);
                if (build.ExitCode != 0 && !parsed.Any(finding =>
                        finding.Severity is FindingSeverity.High or FindingSeverity.Critical))
                {
                    return Unavailable(request, versions,
                        $"dotnet build failed for '{Normalize(relativeTarget)}' without a parseable error: " +
                        OutputDetail(build));
                }
            }
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return Unavailable(request, versions, $"dotnet build is unavailable: {exception.Message}");
        }

        return Available(
            request,
            findings
                .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .OrderBy(finding => finding.Locations[0].Path, StringComparer.Ordinal)
                .ThenBy(finding => finding.Locations[0].Range?.Start.Line ?? 0)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToArray(),
            versions);
    }

    public static IReadOnlyList<ReviewFinding> Parse(string output, string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        return Diagnostic().Matches(StripAnsi(output)).Select(match =>
        {
            var path = NormalizeReportedPath(root, match.Groups["path"].Value);
            var line = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
            var column = int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture);
            var ruleId = match.Groups["rule"].Value;
            var message = match.Groups["message"].Value.Trim();
            var severity = string.Equals(
                match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase)
                ? FindingSeverity.High
                : FindingSeverity.Medium;
            var category = ruleId.StartsWith("CS", StringComparison.OrdinalIgnoreCase)
                ? "compiler"
                : "analyzer";
            var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"dotnet-build\0{path}\0{line}\0{column}\0{ruleId}\0{message}")));
            var producer = category == "compiler" ? "C# compiler" : "Roslyn analyzer";

            return new ReviewFinding(
                $"dotnet-{ruleId.ToLowerInvariant()}-{fingerprint[^12..]}",
                category,
                severity,
                $"{ruleId}: {Trim(message, 260)}",
                message,
                $"Correct the {producer.ToLowerInvariant()} diagnostic reported by {ruleId}.",
                [new FindingLocation(
                    path,
                    new FindingRange(
                        new FindingPosition(Math.Max(1, line), Math.Max(1, column)),
                        new FindingPosition(Math.Max(1, line), Math.Max(1, column))))],
                fingerprint,
                ruleId,
                Source: new FindingSource(
                    FindingSourceKind.Deterministic, "dotnet-build", producer, SensorVersion));
        }).DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> ResolveTargets(
        string root,
        IReadOnlyDictionary<string, string>? configuration)
    {
        if (configuration?.TryGetValue("target", out var configuredTarget) == true &&
            !string.IsNullOrWhiteSpace(configuredTarget))
        {
            var target = AnalyzerCommand.ContainedPath(root, configuredTarget);
            if (!File.Exists(target))
                throw new ArgumentException("dotnet-build target must be an existing repository file.");
            return [target];
        }

        var candidates = EnumerateBuildFiles(root).ToArray();
        var solution = candidates
            .Where(path => Path.GetExtension(path) is ".sln" or ".slnx")
            .OrderBy(path => Path.GetRelativePath(root, path).Count(character =>
                character is '/' or '\\'))
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (solution is not null) return [solution];
        return candidates
            .Where(path => string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool HasTarget(string root)
    {
        try
        {
            return ResolveTargets(Path.GetFullPath(root), null).Count > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateBuildFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFiles(directory, "*.slnx")) yield return path;
            foreach (var path in Directory.EnumerateFiles(directory, "*.sln")) yield return path;
            foreach (var path in Directory.EnumerateFiles(directory, "*.csproj")) yield return path;
            foreach (var child in Directory.EnumerateDirectories(directory)
                         .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                         .Where(path => Path.GetFileName(path) is not (
                             ".git" or ".quality" or "bin" or "obj" or "node_modules"))
                         .OrderByDescending(path => path, StringComparer.Ordinal))
                pending.Push(child);
        }
    }

    private static string NormalizeReportedPath(string root, string value)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
        return AnalyzerCommand.IsWithin(root, path)
            ? Normalize(Path.GetRelativePath(root, path))
            : "external/" + Path.GetFileName(path);
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
        new(false, reason, [], Provenance(request, versions));

    private SensorProvenance Provenance(
        SensorScanRequest request,
        IReadOnlyDictionary<string, string> versions) =>
        new(Id, Version, "repository", ".",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), versions);

    private static string CombinedOutput(SensorCommandResult result) =>
        string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string OutputDetail(SensorCommandResult result)
    {
        var detail = CombinedOutput(result).Trim();
        if (detail.Length == 0) return "The command returned no diagnostic output.";
        return detail.Length <= 1000 ? detail : detail[..1000];
    }

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "not reported";

    private static string Normalize(string path) => path.Replace('\\', '/');
    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static string StripAnsi(string value) => Ansi().Replace(value, string.Empty);

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex Ansi();

    [GeneratedRegex(
        @"^(?<path>.+?)\((?<line>\d+),(?<column>\d+)\):\s*(?<severity>error|warning)\s+(?<rule>[A-Za-z]+\d+):\s*(?<message>.+?)(?:\s+\[[^\]]+\])?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Diagnostic();
}
