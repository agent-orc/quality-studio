using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Ensures lockfile-pinned Node dependencies exist before Angular checks execute.</summary>
public sealed class NpmCiSensor(ISensorCommandRunner? commandRunner = null)
    : IDeterministicEvidenceSensor, IPreflightPrerequisiteSensor
{
    public const string SensorVersion = "1.0.0";
    private readonly ISensorCommandRunner runner = commandRunner ?? new ProcessSensorCommandRunner();

    public string Id => "npm-ci";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public async Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        await BuildPreflightSupport.ProbeAsync(runner, "npm", cancellationToken).ConfigureAwait(false);

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        var root = BuildPreflightSupport.RepositoryRoot(request);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var probe = await runner.RunAsync("npm", ["--version"], root, cancellationToken).ConfigureAwait(false);
        if (probe.ExitCode != 0)
            return Unavailable(request, versions, $"npm availability probe exited with code {probe.ExitCode}.");
        versions["npm"] = BuildPreflightSupport.FirstLine(probe.StandardOutput);
        var lockFiles = BuildPreflightSupport.FindFiles(root, "package-lock.json");
        versions["lockSetHash"] = BuildPreflightSupport.FileSetHash(root, lockFiles);

        foreach (var lockFile in lockFiles)
        {
            var workingDirectory = Path.GetDirectoryName(lockFile)!;
            var nodeModules = Path.Combine(workingDirectory, "node_modules");
            var marker = Path.Combine(nodeModules, ".quality-studio-lock-hash");
            var lockHash = "sha256:" + Convert.ToHexStringLower(
                SHA256.HashData(await File.ReadAllBytesAsync(lockFile, cancellationToken).ConfigureAwait(false)));
            if (File.Exists(marker) &&
                string.Equals(await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false), lockHash,
                    StringComparison.Ordinal))
                continue;

            var install = await runner.RunAsync("npm", ["ci"], workingDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (install.ExitCode != 0)
                return Unavailable(request, versions,
                    $"npm ci failed in '{BuildPreflightSupport.Relative(root, workingDirectory)}': " +
                    BuildPreflightSupport.OutputDetail(install));
            Directory.CreateDirectory(nodeModules);
            await File.WriteAllTextAsync(marker, lockHash, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
        }

        return Available(request, versions);
    }

    private SensorScanResult Available(SensorScanRequest request, IReadOnlyDictionary<string, string> versions) =>
        new(true, null, [], BuildPreflightSupport.Provenance(Id, Version, request, versions));

    private SensorScanResult Unavailable(SensorScanRequest request, IReadOnlyDictionary<string, string> versions, string reason) =>
        new(false, reason, [], BuildPreflightSupport.Provenance(Id, Version, request, versions));
}

/// <summary>Runs the repository's Release compiler gate and normalizes MSBuild diagnostics.</summary>
public sealed partial class DotNetBuildSensor(ISensorCommandRunner? commandRunner = null)
    : IDeterministicEvidenceSensor, ISelectivePreflightGateSensor
{
    public const string SensorVersion = "1.0.0";
    private readonly ISensorCommandRunner runner = commandRunner ?? new ProcessSensorCommandRunner();

    public string Id => "dotnet-build";
    public string Version => SensorVersion;
    public PreflightGateDisposition GateDisposition => PreflightGateDisposition.BlockAffectedSubjects;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public bool HasBlockingFindings(SensorScanResult result) =>
        result.Findings.Any(finding => finding.Severity is FindingSeverity.High or FindingSeverity.Critical);

    public async Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        await BuildPreflightSupport.ProbeAsync(runner, "dotnet", cancellationToken).ConfigureAwait(false);

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        var root = BuildPreflightSupport.RepositoryRoot(request);
        var target = BuildPreflightSupport.FindDotNetTarget(root);
        if (target is null) return Available(request, [], new Dictionary<string, string>());

        var version = await runner.RunAsync("dotnet", ["--version"], root, cancellationToken).ConfigureAwait(false);
        if (version.ExitCode != 0)
            return Unavailable(request, new Dictionary<string, string>(),
                $"dotnet availability probe exited with code {version.ExitCode}.");
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dotnet"] = BuildPreflightSupport.FirstLine(version.StandardOutput),
            ["configurationHash"] = BuildPreflightSupport.FileSetHash(root,
                BuildPreflightSupport.FindFiles(root, "CodeMetricsConfig.txt")
                    .Concat(BuildPreflightSupport.FindFiles(root, "Directory.Build.props"))
                    .Concat(BuildPreflightSupport.FindFiles(root, "Directory.Build.targets"))
                    .Concat(BuildPreflightSupport.FindFiles(root, ".editorconfig"))),
        };
        var relativeTarget = BuildPreflightSupport.Relative(root, target);
        var restore = await runner.RunAsync("dotnet", ["restore", relativeTarget], root, cancellationToken)
            .ConfigureAwait(false);
        if (restore.ExitCode != 0)
            return Unavailable(request, versions, "dotnet restore failed: " + BuildPreflightSupport.OutputDetail(restore));

        var build = await runner.RunAsync("dotnet",
            ["build", relativeTarget, "--configuration", "Release", "--no-restore", "--nologo", "-p:GenerateFullPaths=true"],
            root,
            cancellationToken).ConfigureAwait(false);
        var findings = Parse(BuildPreflightSupport.CombinedOutput(build), root);
        if (build.ExitCode != 0 && !findings.Any(finding =>
                finding.Severity is FindingSeverity.High or FindingSeverity.Critical))
            return Unavailable(request, versions,
                $"dotnet build exited with code {build.ExitCode} without parseable diagnostics: " +
                BuildPreflightSupport.OutputDetail(build));
        return Available(request, findings, versions);
    }

    public static IReadOnlyList<ReviewFinding> Parse(string output, string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        return Diagnostic().Matches(BuildPreflightSupport.StripAnsi(output)).Select(match =>
        {
            var path = BuildPreflightSupport.NormalizeReportedPath(root, match.Groups["path"].Value);
            var line = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
            var column = int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture);
            var severity = string.Equals(match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase)
                ? FindingSeverity.High
                : FindingSeverity.Medium;
            return BuildPreflightSupport.Finding(
                "dotnet-build", Version: SensorVersion, path, line, column,
                match.Groups["rule"].Value, severity, match.Groups["message"].Value.Trim(), "compiler");
        }).DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal).ToArray();
    }

    private SensorScanResult Available(
        SensorScanRequest request,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyDictionary<string, string> versions) =>
        new(true, null, findings, BuildPreflightSupport.Provenance(Id, Version, request, versions));

    private SensorScanResult Unavailable(
        SensorScanRequest request,
        IReadOnlyDictionary<string, string> versions,
        string reason) =>
        new(false, reason, [], BuildPreflightSupport.Provenance(Id, Version, request, versions));

    [GeneratedRegex(@"^(?<path>.+?)\((?<line>\d+),(?<column>\d+)\):\s*(?<severity>error|warning)\s+(?<rule>[A-Za-z]+\d+):\s*(?<message>.+?)(?:\s+\[[^\]]+\])?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Diagnostic();
}

/// <summary>Runs both TypeScript and Angular template compilers from declared local binaries.</summary>
public sealed partial class AngularCompilerSensor(ISensorCommandRunner? commandRunner = null)
    : IDeterministicEvidenceSensor, ISelectivePreflightGateSensor
{
    public const string SensorVersion = "1.0.0";
    private readonly ISensorCommandRunner runner = commandRunner ?? new ProcessSensorCommandRunner();

    public string Id => "angular-compiler";
    public string Version => SensorVersion;
    public PreflightGateDisposition GateDisposition => PreflightGateDisposition.BlockAffectedSubjects;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public bool HasBlockingFindings(SensorScanResult result) =>
        result.Findings.Any(finding => finding.Severity is FindingSeverity.High or FindingSeverity.Critical);

    public async Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        await BuildPreflightSupport.ProbeAsync(runner, "node", cancellationToken).ConfigureAwait(false);

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        var root = BuildPreflightSupport.RepositoryRoot(request);
        var angularRoots = BuildPreflightSupport.FindFiles(root, "angular.json")
            .Select(Path.GetDirectoryName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (angularRoots.Length == 0) return Available(request, [], new Dictionary<string, string>());

        var node = await runner.RunAsync("node", ["--version"], root, cancellationToken).ConfigureAwait(false);
        if (node.ExitCode != 0)
            return Unavailable(request, new Dictionary<string, string>(),
                $"node availability probe exited with code {node.ExitCode}.");
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["node"] = BuildPreflightSupport.FirstLine(node.StandardOutput),
        };
        var findings = new List<ReviewFinding>();
        foreach (var angularRoot in angularRoots)
        {
            var tsc = Path.Combine(angularRoot!, "node_modules", "typescript", "bin", "tsc");
            var ngc = Path.Combine(angularRoot!, "node_modules", "@angular", "compiler-cli", "bundles", "src", "bin", "ngc.js");
            var config = Path.Combine(angularRoot!, "tsconfig.app.json");
            if (!File.Exists(tsc) || !File.Exists(ngc) || !File.Exists(config))
                return Unavailable(request, versions,
                    $"Angular compiler binaries or tsconfig.app.json are missing in '{BuildPreflightSupport.Relative(root, angularRoot!)}'; run npm ci.");

            versions["typescript"] = BuildPreflightSupport.PackageVersion(angularRoot!, "typescript") ?? "unknown";
            versions["angularCompiler"] = BuildPreflightSupport.PackageVersion(angularRoot!, "@angular/compiler-cli") ?? "unknown";
            var tscOutput = await runner.RunAsync("node", [tsc, "--noEmit", "-p", config], angularRoot!, cancellationToken)
                .ConfigureAwait(false);
            var tscText = BuildPreflightSupport.CombinedOutput(tscOutput);
            var typeScriptFindings = TypeScriptAnalyzerSensor.Parse(
                tscText, root, angularRoot!, versions["typescript"]);
            findings.AddRange(typeScriptFindings);
            if (tscOutput.ExitCode != 0 && typeScriptFindings.Count == 0)
                return Unavailable(request, versions,
                    $"tsc exited with code {tscOutput.ExitCode} without parseable diagnostics: " +
                    BuildPreflightSupport.OutputDetail(tscOutput));

            var ngcOutput = await runner.RunAsync("node", [ngc, "-p", config], angularRoot!, cancellationToken)
                .ConfigureAwait(false);
            var angularFindings = ParseAngular(BuildPreflightSupport.CombinedOutput(ngcOutput), root, versions["angularCompiler"]);
            findings.AddRange(angularFindings);
            if (ngcOutput.ExitCode != 0 && angularFindings.Count == 0)
                return Unavailable(request, versions,
                    $"ngc exited with code {ngcOutput.ExitCode} without parseable diagnostics: " +
                    BuildPreflightSupport.OutputDetail(ngcOutput));
        }
        return Available(request,
            findings.DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal).ToArray(), versions);
    }

    public static IReadOnlyList<ReviewFinding> ParseAngular(string output, string repositoryRoot, string? producerVersion = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        return AngularDiagnostic().Matches(BuildPreflightSupport.StripAnsi(output)).Select(match =>
        {
            var path = BuildPreflightSupport.NormalizeReportedPath(root, match.Groups["path"].Value);
            var severity = string.Equals(match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase)
                ? FindingSeverity.High
                : FindingSeverity.Medium;
            return BuildPreflightSupport.Finding(
                "angular-compiler", producerVersion ?? SensorVersion, path,
                int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture),
                match.Groups["rule"].Value, severity, match.Groups["message"].Value.Trim(), "compiler");
        }).DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal).ToArray();
    }

    private SensorScanResult Available(SensorScanRequest request, IReadOnlyList<ReviewFinding> findings,
        IReadOnlyDictionary<string, string> versions) =>
        new(true, null, findings, BuildPreflightSupport.Provenance(Id, Version, request, versions));

    private SensorScanResult Unavailable(SensorScanRequest request, IReadOnlyDictionary<string, string> versions,
        string reason) =>
        new(false, reason, [], BuildPreflightSupport.Provenance(Id, Version, request, versions));

    [GeneratedRegex(@"^(?<path>.+?):(?<line>\d+):(?<column>\d+)\s+-\s+(?<severity>error|warning)\s+(?<rule>NG\d+):\s*(?<message>.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AngularDiagnostic();
}

/// <summary>Runs Angular's checked-in production budgets and records build warnings separately.</summary>
public sealed partial class AngularBudgetSensor(ISensorCommandRunner? commandRunner = null)
    : IDeterministicEvidenceSensor, ISelectivePreflightGateSensor
{
    public const string SensorVersion = "1.0.0";
    private readonly ISensorCommandRunner runner = commandRunner ?? new ProcessSensorCommandRunner();

    public string Id => "angular-budget";
    public string Version => SensorVersion;
    public PreflightGateDisposition GateDisposition => PreflightGateDisposition.BlockProjectPerformance;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public bool HasBlockingFindings(SensorScanResult result) => result.Findings.Any(finding =>
        finding.RuleId == "angular-budget" &&
        finding.Severity is FindingSeverity.High or FindingSeverity.Critical);

    public async Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        await BuildPreflightSupport.ProbeAsync(runner, "node", cancellationToken).ConfigureAwait(false);

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        var root = BuildPreflightSupport.RepositoryRoot(request);
        var angularRoots = BuildPreflightSupport.FindFiles(root, "angular.json")
            .Select(Path.GetDirectoryName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (angularRoots.Length == 0) return Available(request, [], new Dictionary<string, string>());
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var findings = new List<ReviewFinding>();
        foreach (var angularRoot in angularRoots)
        {
            var ng = Path.Combine(angularRoot!, "node_modules", "@angular", "cli", "bin", "ng.js");
            if (!File.Exists(ng))
                return Unavailable(request, versions,
                    $"Angular CLI is missing in '{BuildPreflightSupport.Relative(root, angularRoot!)}'; run npm ci.");
            versions["angularCli"] = BuildPreflightSupport.PackageVersion(angularRoot!, "@angular/cli") ?? "unknown";
            var output = await runner.RunAsync("node",
                [ng, "build", "--configuration", "production", "--no-progress"], angularRoot!, cancellationToken)
                .ConfigureAwait(false);
            var parsed = Parse(BuildPreflightSupport.CombinedOutput(output), root,
                BuildPreflightSupport.Relative(root, Path.Combine(angularRoot!, "angular.json")), versions["angularCli"]);
            findings.AddRange(parsed);
            if (output.ExitCode != 0 && !parsed.Any(finding =>
                    finding.Severity is FindingSeverity.High or FindingSeverity.Critical))
                return Unavailable(request, versions,
                    $"Angular production build exited with code {output.ExitCode} without parseable diagnostics: " +
                    BuildPreflightSupport.OutputDetail(output));
        }
        return Available(request,
            findings.DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal).ToArray(), versions);
    }

    public static IReadOnlyList<ReviewFinding> Parse(
        string output,
        string repositoryRoot,
        string angularJsonPath,
        string? producerVersion = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        return BuildMessage().Matches(BuildPreflightSupport.StripAnsi(output)).Select(match =>
        {
            var message = match.Groups["message"].Value.Trim();
            var isBudget = message.Contains("exceeded maximum budget", StringComparison.OrdinalIgnoreCase);
            var pathMatch = SourcePath().Match(message);
            var path = pathMatch.Success
                ? BuildPreflightSupport.NormalizeReportedPath(root, pathMatch.Groups["path"].Value)
                : angularJsonPath;
            var severity = string.Equals(match.Groups["severity"].Value, "ERROR", StringComparison.OrdinalIgnoreCase)
                ? FindingSeverity.High
                : FindingSeverity.Medium;
            return BuildPreflightSupport.Finding(
                "angular-budget", producerVersion ?? SensorVersion, path, 1, 1,
                isBudget ? "angular-budget" : "angular-build", severity, message,
                isBudget ? "performance" : "build");
        }).DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal).ToArray();
    }

    private SensorScanResult Available(SensorScanRequest request, IReadOnlyList<ReviewFinding> findings,
        IReadOnlyDictionary<string, string> versions) =>
        new(true, null, findings, BuildPreflightSupport.Provenance(Id, Version, request, versions));

    private SensorScanResult Unavailable(SensorScanRequest request, IReadOnlyDictionary<string, string> versions,
        string reason) =>
        new(false, reason, [], BuildPreflightSupport.Provenance(Id, Version, request, versions));

    [GeneratedRegex(@"^[▲✘]?\s*\[(?<severity>WARNING|ERROR)\]\s*(?<message>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BuildMessage();

    [GeneratedRegex(@"^(?<path>[^\s]+\.(?:css|scss|sass|less|ts|html))\s", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SourcePath();
}

internal static partial class BuildPreflightSupport
{
    public static string RepositoryRoot(SensorScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scope != SensorScope.Repository)
            throw new ArgumentException("Build preflight sensors support repository scope only.");
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        return root;
    }

    public static async Task<SensorAvailability> ProbeAsync(
        ISensorCommandRunner runner,
        string executable,
        CancellationToken cancellationToken)
    {
        try
        {
            var output = await runner.RunAsync(executable, ["--version"], Directory.GetCurrentDirectory(), cancellationToken)
                .ConfigureAwait(false);
            return output.ExitCode == 0
                ? new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
                {
                    [executable] = FirstLine(output.StandardOutput),
                })
                : new SensorAvailability(false, $"{executable} availability probe exited with code {output.ExitCode}.");
        }
        catch (Exception exception) when (exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return new SensorAvailability(false, $"{executable} is unavailable: {exception.Message}");
        }
    }

    public static SensorProvenance Provenance(string id, string version, SensorScanRequest request,
        IReadOnlyDictionary<string, string> versions) =>
        new(id, version, "repository", ".", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), versions);

    public static string? FindDotNetTarget(string root) =>
        Directory.EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault()
        ?? FindFiles(root, "*.csproj").FirstOrDefault();

    public static IReadOnlyList<string> FindFiles(string root, string pattern)
    {
        var matches = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            matches.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly));
            foreach (var child in Directory.EnumerateDirectories(directory).OrderByDescending(path => path, StringComparer.Ordinal))
            {
                if (Path.GetFileName(child) is not (".git" or "bin" or "obj" or "node_modules" or "dist"))
                    pending.Push(child);
            }
        }
        return matches.Order(StringComparer.Ordinal).ToArray();
    }

    public static string PackageVersion(string projectRoot, string packageName)
    {
        var packagePath = Path.Combine(projectRoot, "node_modules", packageName.Replace('/', Path.DirectorySeparatorChar), "package.json");
        if (!File.Exists(packagePath)) return "unknown";
        using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
        return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() ?? "unknown" : "unknown";
    }

    public static ReviewFinding Finding(
        string sensorId,
        string Version,
        string path,
        int line,
        int column,
        string ruleId,
        FindingSeverity severity,
        string message,
        string aspect)
    {
        var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{sensorId}\0{path}\0{line}\0{column}\0{ruleId}\0{message}")));
        return new ReviewFinding(
            $"{sensorId}-{ruleId.ToLowerInvariant()}-{fingerprint[^12..]}",
            aspect,
            severity,
            $"{ruleId}: {Trim(message, 220)}",
            Trim(message, 2_000),
            $"Correct the {ruleId} diagnostic before rerunning the preflight.",
            [new FindingLocation(path, new FindingRange(
                new FindingPosition(Math.Max(1, line), Math.Max(1, column)),
                new FindingPosition(Math.Max(1, line), Math.Max(1, column))))],
            fingerprint,
            ruleId,
            Source: new FindingSource(FindingSourceKind.Deterministic, sensorId, sensorId, Version));
    }

    public static string NormalizeReportedPath(string root, string path)
    {
        var trimmed = path.Trim().Trim('"');
        var absolute = Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(root, trimmed));
        return AnalyzerCommand.IsWithin(root, absolute)
            ? Relative(root, absolute)
            : "external/" + Path.GetFileName(absolute);
    }

    public static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    public static string FileSetHash(string root, IEnumerable<string> paths)
    {
        var canonical = string.Join('\n', paths.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(path => $"{Relative(root, path)}\0{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))}"));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string CombinedOutput(SensorCommandResult result) =>
        string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    public static string OutputDetail(SensorCommandResult result) =>
        Trim(FirstLine(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError), 500);

    public static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown";

    public static string StripAnsi(string value) => Ansi().Replace(value.Replace("\r\n", "\n", StringComparison.Ordinal), string.Empty);

    private static string Trim(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex Ansi();
}
