using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Runs the repository-local Angular AOT compiler and normalizes template diagnostics.
/// </summary>
public sealed partial class AngularCompilerSensor : IDeterministicEvidenceSensor
{
    public const string SensorVersion = "1.0.0";
    private const string ProjectPath = "frontend/tsconfig.app.json";
    private const string CompilerPath =
        "frontend/node_modules/@angular/compiler-cli/bundles/src/bin/ngc.js";
    private const string CompilerManifestPath =
        "frontend/node_modules/@angular/compiler-cli/package.json";
    private const int MaximumReasonLength = 1000;
    private readonly ISensorCommandRunner runner;
    private readonly string? probeRepositoryRoot;

    public AngularCompilerSensor(
        ISensorCommandRunner? commandRunner = null,
        string? probeRepositoryRoot = null)
    {
        runner = commandRunner ?? new BoundedProcessSensorCommandRunner();
        this.probeRepositoryRoot = probeRepositoryRoot;
    }

    public string Id => "angular-compiler";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public async Task<SensorAvailability> ProbeAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(probeRepositoryRoot ?? Directory.GetCurrentDirectory());
        if (!TryResolve(root, out var compiler, out var reason))
            return new SensorAvailability(false, reason);

        try
        {
            var probe = await runner.RunAsync(
                "node", [Normalize(Path.GetRelativePath(root, compiler)), "--version"], root,
                cancellationToken).ConfigureAwait(false);
            if (probe.ExitCode != 0)
                return new SensorAvailability(false,
                    Bounded($"Angular compiler is unavailable: version probe exited with code {probe.ExitCode}. " +
                            OutputDetail(probe)));

            return new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
            {
                ["angularCompiler"] = ReadCompilerVersion(root, probe.StandardOutput),
            });
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
        if (!TryResolve(root, out var compiler, out var reason))
            return Unavailable(
                request, new Dictionary<string, string>(StringComparer.Ordinal), reason!);

        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var compilerArgument = Normalize(Path.GetRelativePath(root, compiler));
            var probe = await runner.RunAsync(
                "node", [compilerArgument, "--version"], root, cancellationToken)
                .ConfigureAwait(false);
            if (probe.ExitCode != 0)
                return Unavailable(request, versions,
                    Bounded($"Angular compiler is unavailable: version probe exited with code {probe.ExitCode}. " +
                            OutputDetail(probe)));
            versions["angularCompiler"] = ReadCompilerVersion(root, probe.StandardOutput);

            var compilation = await runner.RunAsync(
                "node", [compilerArgument, "-p", ProjectPath], root, cancellationToken)
                .ConfigureAwait(false);
            var diagnostics = CombinedOutput(compilation);
            var findings = Parse(diagnostics, root, versions["angularCompiler"]);
            if (compilation.ExitCode != 0 && findings.Count == 0)
                return Unavailable(request, versions,
                    Bounded($"Angular compiler exited with code {compilation.ExitCode} without parseable " +
                            $"diagnostics. {OutputDetail(compilation)}"));

            return Available(request, findings, versions);
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return Unavailable(request, versions,
                Bounded($"Angular compiler is unavailable: {exception.Message}"));
        }
    }

    public static IReadOnlyList<ReviewFinding> Parse(
        string output,
        string repositoryRoot,
        string? producerVersion = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var projectDirectory = Path.GetDirectoryName(Path.Combine(root,
            ProjectPath.Replace('/', Path.DirectorySeparatorChar)))!;
        var clean = StripAnsi(output);
        var matches = Diagnostic().Matches(clean);
        var findings = new List<ReviewFinding>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var path = NormalizeReportedPath(root, projectDirectory, match.Groups["path"].Value);
            var line = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
            var column = int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture);
            var ruleId = match.Groups["rule"].Value.ToUpperInvariant();
            var message = match.Groups["message"].Value.Trim();
            var severity = string.Equals(
                match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase)
                ? FindingSeverity.High
                : FindingSeverity.Medium;
            var nextMatch = index + 1 < matches.Count ? matches[index + 1].Index : clean.Length;
            var diagnosticBody = clean[match.Index..nextMatch];
            var marker = Marker().Match(diagnosticBody);
            var endColumn = marker.Success ? column + marker.Length - 1 : column;
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
            var fullRoot = Path.GetFullPath(root);
            var project = Path.GetFullPath(Path.Combine(
                fullRoot, ProjectPath.Replace('/', Path.DirectorySeparatorChar)));
            return AnalyzerCommand.IsWithin(fullRoot, project) && File.Exists(project);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryResolve(string root, out string compiler, out string? reason)
    {
        var project = Path.GetFullPath(Path.Combine(
            root, ProjectPath.Replace('/', Path.DirectorySeparatorChar)));
        compiler = Path.GetFullPath(Path.Combine(
            root, CompilerPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!AnalyzerCommand.IsWithin(root, project) || !AnalyzerCommand.IsWithin(root, compiler))
        {
            reason = "Angular compiler paths must remain inside the repository.";
            return false;
        }
        if (!File.Exists(project))
        {
            reason = $"Angular compiler is unavailable: '{ProjectPath}' was not found.";
            return false;
        }
        if (!File.Exists(compiler))
        {
            reason = $"Angular compiler is unavailable: repository-local '{CompilerPath}' was not found.";
            return false;
        }

        reason = null;
        return true;
    }

    private static string ReadCompilerVersion(string root, string fallback)
    {
        var manifest = Path.Combine(
            root, CompilerManifestPath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var version = document.RootElement.GetProperty("version").GetString();
            if (!string.IsNullOrWhiteSpace(version)) return Trim(version, 100);
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // A successful executable probe still supplies a bounded fallback version.
        }
        return Trim(FirstLine(fallback), 100);
    }

    private static string NormalizeReportedPath(string root, string projectDirectory, string value)
    {
        string path;
        if (Path.IsPathRooted(value))
        {
            path = Path.GetFullPath(value);
        }
        else
        {
            path = Normalize(value).StartsWith("frontend/", StringComparison.Ordinal)
                ? Path.GetFullPath(Path.Combine(root, value))
                : Path.GetFullPath(Path.Combine(projectDirectory, value));
        }
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
        new(false, Bounded(reason), [], Provenance(request, versions));

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
        return string.IsNullOrEmpty(detail)
            ? "The command returned no diagnostic output."
            : Bounded(detail);
    }

    private static string Bounded(string value) =>
        value.Length <= MaximumReasonLength ? value : value[..MaximumReasonLength];
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
        @"^(?<path>.+?)(?::(?<line>[1-9]\d*):(?<column>[1-9]\d*)\s+-|" +
        @"\((?<line>[1-9]\d*),(?<column>[1-9]\d*)\):)\s+" +
        @"(?<severity>error|warning)\s+(?<rule>(?:NG|TS)\d+):\s*(?<message>.+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Diagnostic();

    [GeneratedRegex(@"~+", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();
}

internal sealed class BoundedProcessSensorCommandRunner : ISensorCommandRunner
{
    private const int MaximumOutputCharacters = 1_000_000;

    public async Task<SensorCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start())
                throw new SecurityScannerUnavailableException($"{executable} did not start.");
        }
        catch (Win32Exception exception)
        {
            throw new SecurityScannerUnavailableException(
                $"{executable} could not be launched.", exception);
        }

        var standardOutput = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var standardError = ReadBoundedAsync(process.StandardError, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new SensorCommandResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(MaximumOutputCharacters, 4096));
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var remaining = MaximumOutputCharacters - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(remaining, read));
        }
        return result.ToString();
    }
}
