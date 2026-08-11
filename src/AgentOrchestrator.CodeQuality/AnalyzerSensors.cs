using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public abstract class SarifCommandAnalyzerSensor : IDeterministicEvidenceSensor
{
    private readonly ISensorCommandRunner commandRunner;
    private readonly SarifSensor sarif;
    private readonly string executable;
    private readonly string[] versionArguments;

    protected SarifCommandAnalyzerSensor(
        string id,
        string executable,
        string[] versionArguments,
        ISensorCommandRunner? commandRunner)
    {
        Id = id;
        this.executable = executable;
        this.versionArguments = versionArguments;
        this.commandRunner = commandRunner ?? new ProcessSensorCommandRunner();
        sarif = new SarifSensor(id, this.commandRunner);
    }

    public string Id { get; }
    public string Version => SarifSensor.SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes => sarif.SupportedScopes;

    public async Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await commandRunner.RunAsync(
                executable, versionArguments, Directory.GetCurrentDirectory(), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return new SensorAvailability(
                    false, $"{Id} is unavailable: version probe exited with code {result.ExitCode}.");
            var version = string.IsNullOrWhiteSpace(result.StandardOutput)
                ? result.StandardError.Trim()
                : result.StandardOutput.Trim();
            return new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
            {
                [Id] = version,
                ["sarif"] = "2.1.0",
            });
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return new SensorAvailability(false, $"{Id} is unavailable: {exception.Message}");
        }
    }

    public Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default) =>
        sarif.RunAsync(request, cancellationToken);
}

public sealed partial class RoslynAnalyzerSensor : IDeterministicEvidenceSensor
{
    public const string SensorVersion = "1.1.0";
    private readonly ISensorCommandRunner commandRunner;
    private readonly SarifSensor sarif;

    public RoslynAnalyzerSensor(ISensorCommandRunner? commandRunner = null)
    {
        this.commandRunner = commandRunner ?? new ProcessSensorCommandRunner();
        sarif = new SarifSensor("roslyn", this.commandRunner);
    }

    public string Id => "roslyn";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes => sarif.SupportedScopes;

    public async Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await commandRunner.RunAsync(
                "dotnet", ["--version"], Directory.GetCurrentDirectory(), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return new SensorAvailability(
                    false, $"roslyn is unavailable: dotnet version probe exited with code {result.ExitCode}.");
            }

            return new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
            {
                ["dotnet"] = FirstLine(result.StandardOutput, result.StandardError),
            });
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return new SensorAvailability(false, $"roslyn is unavailable: {exception.Message}");
        }
    }

    public Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default) =>
        IsBuildOutputMode(request.Configuration)
            ? RunBuildOutputAsync(request, cancellationToken)
            : sarif.RunAsync(request, cancellationToken);

    public static IReadOnlyList<ReviewFinding> ParseBuildOutput(
        string output,
        string repositoryRoot,
        string? producerVersion = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        return DotNetDiagnostic().Matches(StripAnsi(output)).Select(match =>
        {
            var rawPath = match.Groups["path"].Value;
            var hasPath = !string.IsNullOrWhiteSpace(rawPath);
            var path = hasPath ? NormalizeReportedPath(root, rawPath) : ".";
            var line = hasPath
                ? int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture)
                : 1;
            var column = hasPath
                ? int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture)
                : 1;
            var severity = string.Equals(
                match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase)
                ? FindingSeverity.High
                : FindingSeverity.Medium;
            var ruleId = match.Groups["rule"].Value;
            var message = match.Groups["message"].Value.Trim();
            var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"roslyn\0{path}\0{line}\0{column}\0{ruleId}\0{message}")));
            var title = $".NET {ruleId}: {Trim(message, 260)}";
            return new ReviewFinding(
                $"roslyn-{ruleId.ToLowerInvariant()}-{fingerprint[^12..]}",
                "analyzer",
                severity,
                title,
                message,
                $"Correct the deterministic .NET diagnostic reported by {ruleId}.",
                hasPath
                    ? [new FindingLocation(path, new FindingRange(
                        new FindingPosition(line, column), new FindingPosition(line, column)))]
                    : [new FindingLocation(path)],
                fingerprint,
                ruleId,
                Source: new FindingSource(
                    FindingSourceKind.Deterministic, "roslyn", ".NET build", producerVersion));
        }).DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
          .OrderBy(finding => finding.Locations[0].Path, StringComparer.Ordinal)
          .ThenBy(finding => finding.Locations[0].Range?.Start.Line ?? 0)
          .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
          .ToArray();
    }

    private async Task<SensorScanResult> RunBuildOutputAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        var configuration = request.Configuration ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!configuration.TryGetValue("command", out var configuredCommand) ||
            string.IsNullOrWhiteSpace(configuredCommand))
        {
            return Unavailable(request, "roslyn build-output configuration requires command.");
        }

        try
        {
            var target = request.Scope == SensorScope.Path && !string.IsNullOrWhiteSpace(request.Path)
                ? AnalyzerCommand.ContainedPath(root, request.Path)
                : root;
            var workingDirectory = configuration.TryGetValue("workingDirectory", out var configuredWorkingDirectory) &&
                                   !string.IsNullOrWhiteSpace(configuredWorkingDirectory)
                ? AnalyzerCommand.ContainedPath(root, configuredWorkingDirectory)
                : Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
            if (!Directory.Exists(workingDirectory))
                return Unavailable(request, "roslyn workingDirectory must be an existing repository directory.");

            var reportPath = configuration.TryGetValue("reportPath", out var configuredReport) &&
                             !string.IsNullOrWhiteSpace(configuredReport)
                ? AnalyzerCommand.ContainedPath(root, configuredReport)
                : Path.Combine(root, ".quality", "analyzers", "dotnet-build.txt");
            var command = AnalyzerCommand.Expand(configuredCommand, root, target, reportPath);
            var versionResult = await commandRunner.RunAsync(
                "dotnet", ["--version"], workingDirectory, cancellationToken).ConfigureAwait(false);
            if (versionResult.ExitCode != 0)
            {
                return Unavailable(
                    request, $"dotnet version probe exited with code {versionResult.ExitCode}.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            if (File.Exists(reportPath)) File.Delete(reportPath);
            var result = await commandRunner.RunAsync(
                command[0], command.Skip(1).ToArray(), workingDirectory, cancellationToken).ConfigureAwait(false);
            var output = string.Join(
                Environment.NewLine,
                new[] { result.StandardOutput, result.StandardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            await File.WriteAllTextAsync(
                reportPath, output, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var producerVersion = FirstLine(versionResult.StandardOutput, versionResult.StandardError);
            var findings = ParseBuildOutput(output, root, producerVersion);
            if (result.ExitCode != 0 && findings.Count == 0)
            {
                return Unavailable(
                    request,
                    $"dotnet build exited with code {result.ExitCode} without parseable diagnostics. " +
                    AnalyzerCommand.OutputDetail(result),
                    producerVersion);
            }

            return new SensorScanResult(
                true,
                null,
                findings,
                Provenance(request, producerVersion));
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException or ArgumentException)
        {
            return Unavailable(request, $"roslyn is unavailable: {exception.Message}");
        }
    }

    private SensorScanResult Unavailable(
        SensorScanRequest request,
        string reason,
        string? producerVersion = null) =>
        new(false, reason, [], Provenance(request, producerVersion));

    private SensorProvenance Provenance(SensorScanRequest request, string? producerVersion)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(producerVersion)) versions["dotnet"] = producerVersion;
        return new SensorProvenance(
            Id,
            Version,
            request.Scope.ToString().ToLowerInvariant(),
            request.Scope == SensorScope.Repository ? "." : request.Path ?? "(missing)",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            versions);
    }

    private static bool IsBuildOutputMode(IReadOnlyDictionary<string, string>? configuration) =>
        configuration?.TryGetValue("format", out var format) == true &&
        string.Equals(format, "dotnet-build", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(params string[] values) =>
        values.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.Length > 0) ?? "unknown";

    private static string NormalizeReportedPath(string root, string rawPath)
    {
        var candidate = Path.GetFullPath(Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(root, rawPath));
        return AnalyzerCommand.IsWithin(root, candidate)
            ? Path.GetRelativePath(root, candidate).Replace('\\', '/')
            : "external/" + Path.GetFileName(candidate);
    }

    private static string StripAnsi(string value) =>
        AnsiEscape().Replace(value.Replace("\r\n", "\n", StringComparison.Ordinal), string.Empty);

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    [GeneratedRegex(
        @"^(?:(?<path>.+?)\((?<line>[1-9]\d*),(?<column>[1-9]\d*)\)|[A-Za-z]+)\s*:\s*(?<severity>error|warning)\s+(?<rule>[A-Za-z]+\d+)\s*:\s*(?<message>.+?)(?:\s+\[[^\]]+\])?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetDiagnostic();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscape();
}

public sealed class EslintAnalyzerSensor : SarifCommandAnalyzerSensor
{
    public EslintAnalyzerSensor(ISensorCommandRunner? commandRunner = null)
        : base("eslint", "npx", ["--no-install", "eslint", "--version"], commandRunner)
    {
    }
}

public sealed partial class TypeScriptAnalyzerSensor :
    IDeterministicEvidenceSensor, IRepositoryAwareSensorAvailability
{
    public const string SensorVersion = "1.0.0";
    private readonly ISensorCommandRunner commandRunner;

    public TypeScriptAnalyzerSensor(ISensorCommandRunner? commandRunner = null) =>
        this.commandRunner = commandRunner ?? new ProcessSensorCommandRunner();

    public string Id => "tsc";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository, SensorScope.Path];

    public async Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var output = await commandRunner.RunAsync(
                "npx", ["--no-install", "tsc", "--version"], Directory.GetCurrentDirectory(), cancellationToken)
                .ConfigureAwait(false);
            return output.ExitCode == 0
                ? new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
                {
                    ["typescript"] = output.StandardOutput.Trim(),
                })
                : new SensorAvailability(
                    false, $"tsc is unavailable: version probe exited with code {output.ExitCode}.");
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return new SensorAvailability(false, $"tsc is unavailable: {exception.Message}");
        }
    }

    public async Task<SensorAvailability> ProbeAvailabilityAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        var configuration = request.Configuration ?? new Dictionary<string, string>(StringComparer.Ordinal);
        string workingDirectory;
        try
        {
            workingDirectory = configuration.TryGetValue("workingDirectory", out var configuredWorkingDirectory) &&
                               !string.IsNullOrWhiteSpace(configuredWorkingDirectory)
                ? AnalyzerCommand.ContainedPath(root, configuredWorkingDirectory)
                : root;
        }
        catch (ArgumentException exception)
        {
            return new SensorAvailability(false, $"tsc is unavailable: {exception.Message}");
        }

        var localCompiler = Path.Combine(workingDirectory, "node_modules", "typescript", "bin", "tsc");
        if (!File.Exists(localCompiler))
        {
            return new SensorAvailability(
                false,
                $"tsc is unavailable: local compiler is missing in '{Path.GetRelativePath(root, workingDirectory).Replace('\\', '/')}'.");
        }

        try
        {
            var output = await commandRunner.RunAsync(
                "node", [localCompiler, "--version"], workingDirectory, cancellationToken).ConfigureAwait(false);
            if (output.ExitCode != 0)
            {
                return new SensorAvailability(
                    false, $"tsc is unavailable: local compiler probe exited with code {output.ExitCode}.");
            }

            return new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
            {
                ["typescript"] = TypeScriptVersion(workingDirectory) ?? output.StandardOutput.Trim(),
            });
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return new SensorAvailability(false, $"tsc is unavailable: {exception.Message}");
        }
    }

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        var configuration = request.Configuration ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!configuration.TryGetValue("command", out var configuredCommand) ||
            string.IsNullOrWhiteSpace(configuredCommand))
            return Unavailable(request, "tsc analyzer configuration requires command.");
        if (!configuration.TryGetValue("reportPath", out var configuredReport) ||
            string.IsNullOrWhiteSpace(configuredReport))
            return Unavailable(request, "tsc analyzer configuration requires reportPath.");

        string reportPath;
        string target;
        string workingDirectory;
        IReadOnlyList<string> command;
        try
        {
            target = request.Scope == SensorScope.Path && !string.IsNullOrWhiteSpace(request.Path)
                ? AnalyzerCommand.ContainedPath(root, request.Path)
                : root;
            reportPath = AnalyzerCommand.ContainedPath(root, configuredReport);
            workingDirectory = configuration.TryGetValue("workingDirectory", out var configuredWorkingDirectory) &&
                               !string.IsNullOrWhiteSpace(configuredWorkingDirectory)
                ? AnalyzerCommand.ContainedPath(root, configuredWorkingDirectory)
                : Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
            if (!Directory.Exists(workingDirectory))
                return Unavailable(request, "tsc workingDirectory must be an existing repository directory.");
            command = AnalyzerCommand.Expand(configuredCommand, root, target, reportPath);
        }
        catch (ArgumentException exception)
        {
            return Unavailable(request, exception.Message);
        }

        SensorCommandResult output;
        try
        {
            output = await commandRunner.RunAsync(
                command[0], command.Skip(1).ToArray(), workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return Unavailable(request, $"tsc is unavailable: {exception.Message}");
        }

        var diagnostics = string.Join(
            Environment.NewLine,
            new[] { output.StandardOutput, output.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(
            reportPath, diagnostics, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var producerVersion = configuration.GetValueOrDefault("producerVersion") ??
                              TypeScriptVersion(workingDirectory);
        var findings = Parse(diagnostics, root, workingDirectory, producerVersion);
        if (output.ExitCode != 0 && findings.Count == 0)
        {
            return Unavailable(
                request,
                $"tsc exited with code {output.ExitCode} without parseable diagnostics. " +
                AnalyzerCommand.OutputDetail(output));
        }

        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(producerVersion)) versions["typescript"] = producerVersion;
        return new SensorScanResult(
            true,
            null,
            findings,
            Provenance(request, versions));
    }

    public static IReadOnlyList<ReviewFinding> Parse(
        string output,
        string repositoryRoot,
        string? workingDirectory = null,
        string? producerVersion = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var working = Path.GetFullPath(workingDirectory ?? root);
        var findings = new List<ReviewFinding>();
        foreach (Match match in DiagnosticLine().Matches(output))
        {
            var rawPath = match.Groups["path"].Value;
            var absolute = Path.GetFullPath(
                Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(working, rawPath));
            var path = AnalyzerCommand.IsWithin(root, absolute)
                ? Path.GetRelativePath(root, absolute).Replace('\\', '/')
                : "external/" + Path.GetFileName(absolute);
            var line = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
            var column = int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture);
            var ruleId = match.Groups["rule"].Value;
            var message = match.Groups["message"].Value.Trim();
            var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"tsc\0{path}\0{line}\0{column}\0{ruleId}\0{message}")));
            findings.Add(new ReviewFinding(
                $"tsc-{ruleId.ToLowerInvariant()}-{fingerprint[^12..]}",
                "analyzer",
                match.Groups["severity"].Value == "error" ? FindingSeverity.High : FindingSeverity.Medium,
                $"TypeScript {ruleId}: {Trim(message, 260)}",
                message,
                $"Correct the TypeScript diagnostic reported by {ruleId}.",
                [new FindingLocation(
                    path,
                    new FindingRange(
                        new FindingPosition(line, column),
                        new FindingPosition(line, column)))],
                fingerprint,
                ruleId,
                Source: new FindingSource(
                    FindingSourceKind.Deterministic, "tsc", "TypeScript", producerVersion)));
        }
        return findings
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .OrderBy(finding => finding.Locations[0].Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.Locations[0].Range!.Start.Line)
            .ToArray();
    }

    private SensorScanResult Unavailable(SensorScanRequest request, string reason) =>
        new(false, reason, [], Provenance(
            request, new Dictionary<string, string>(StringComparer.Ordinal)));

    private SensorProvenance Provenance(
        SensorScanRequest request,
        IReadOnlyDictionary<string, string> versions) =>
        new(Id, Version, request.Scope.ToString().ToLowerInvariant(),
            request.Scope == SensorScope.Repository ? "." : request.Path ?? "(missing)",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), versions);

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static string? TypeScriptVersion(string workingDirectory)
    {
        var packagePath = Path.Combine(workingDirectory, "node_modules", "typescript", "package.json");
        if (!File.Exists(packagePath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
            return document.RootElement.TryGetProperty("version", out var version) &&
                   version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(
        @"^(?<path>.+?)\((?<line>[1-9]\d*),(?<column>[1-9]\d*)\):\s+(?<severity>error|warning)\s+(?<rule>TS\d+):\s*(?<message>.+)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticLine();
}
