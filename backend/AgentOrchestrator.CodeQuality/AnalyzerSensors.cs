using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public abstract class SarifCommandAnalyzerSensor : IDeterministicEvidenceSensor, IRepositoryProbedSensor
{
    private readonly ISensorCommandRunner commandRunner;
    private readonly SarifSensor sarif;
    private readonly AnalyzerProfileCatalog profiles;
    private readonly string executable;
    private readonly string[] versionArguments;
    private readonly string toolVersionKey;

    protected SarifCommandAnalyzerSensor(
        string id,
        string executable,
        string[] versionArguments,
        ISensorCommandRunner? commandRunner,
        string? toolVersionKey = null,
        AnalyzerProfileCatalog? profiles = null)
    {
        Id = id;
        this.executable = executable;
        this.versionArguments = versionArguments;
        this.toolVersionKey = toolVersionKey ?? id;
        this.commandRunner = commandRunner ?? new ProcessSensorCommandRunner();
        this.profiles = profiles ?? AnalyzerProfileCatalog.BuiltIn;
        sarif = new SarifSensor(id, this.commandRunner, this.profiles);
    }

    public string Id { get; }
    public string Version => SarifSensor.SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes => sarif.SupportedScopes;

    public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        ProbeInAsync(Directory.GetCurrentDirectory(), cancellationToken);

    /// <summary>
    /// Probes the executable from the directory the profile would run in, and checks that every tool
    /// the profile asks the repository for is installed, so a missing <c>npm ci</c> shows up as an
    /// unavailable sensor before a scan rather than as a failed one.
    /// </summary>
    public async Task<SensorAvailability> ProbeAvailabilityAsync(
        string repositoryRoot,
        IReadOnlyDictionary<string, string>? configuration,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
            return new SensorAvailability(false, $"{Id} is unavailable: the repository path does not exist.");
        var workingDirectory = root;
        if (configuration is not null &&
            AnalyzerInvocation.TryResolve(Id, configuration, profiles, out var invocation, out _))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(invocation.WorkingDirectory))
                    workingDirectory = AnalyzerCommand.ContainedPath(root, invocation.WorkingDirectory);
                if (!Directory.Exists(workingDirectory))
                    return new SensorAvailability(false,
                        $"{Id} is unavailable: working directory '{invocation.WorkingDirectory}' does not exist.");
                if (!string.IsNullOrWhiteSpace(invocation.Command))
                    AnalyzerCommand.CheckTools(invocation.Command, root, workingDirectory);
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return new SensorAvailability(false, $"{Id} is unavailable: {exception.Message}");
            }
        }
        return await ProbeInAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SensorAvailability> ProbeInAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            var result = await commandRunner.RunAsync(
                executable, versionArguments, directory, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return new SensorAvailability(
                    false, $"{Id} is unavailable: version probe exited with code {result.ExitCode}.");
            var version = string.IsNullOrWhiteSpace(result.StandardOutput)
                ? result.StandardError.Trim()
                : result.StandardOutput.Trim();
            return new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
            {
                [toolVersionKey] = version,
                ["sarif"] = "2.1.0",
            });
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or UnauthorizedAccessException or
                InvalidOperationException)
        {
            return new SensorAvailability(false, $"{Id} is unavailable: {exception.Message}");
        }
    }

    public Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default) =>
        sarif.RunAsync(request, cancellationToken);
}

public sealed class RoslynAnalyzerSensor : SarifCommandAnalyzerSensor
{
    public RoslynAnalyzerSensor(ISensorCommandRunner? commandRunner = null, AnalyzerProfileCatalog? profiles = null)
        : base("roslyn", "dotnet", ["--version"], commandRunner, "dotnet", profiles)
    {
    }
}

public sealed class EslintAnalyzerSensor : SarifCommandAnalyzerSensor
{
    public EslintAnalyzerSensor(ISensorCommandRunner? commandRunner = null, AnalyzerProfileCatalog? profiles = null)
        : base("eslint", "node", ["--version"], commandRunner, "node", profiles)
    {
    }
}

public sealed partial class TypeScriptAnalyzerSensor : IDeterministicEvidenceSensor, IRepositoryProbedSensor
{
    public const string SensorVersion = "1.1.0";
    private const string CompilerModule = "typescript/bin/tsc";
    private readonly ISensorCommandRunner commandRunner;
    private readonly AnalyzerProfileCatalog profiles;

    public TypeScriptAnalyzerSensor(ISensorCommandRunner? commandRunner = null, AnalyzerProfileCatalog? profiles = null)
    {
        this.commandRunner = commandRunner ?? new ProcessSensorCommandRunner();
        this.profiles = profiles ?? AnalyzerProfileCatalog.BuiltIn;
    }

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
            return VersionAvailability(output);
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or UnauthorizedAccessException or
                InvalidOperationException)
        {
            return new SensorAvailability(false, $"tsc is unavailable: {exception.Message}");
        }
    }

    /// <summary>
    /// Probes the TypeScript compiler the repository installed for the profile's working directory,
    /// and the project the profile would check, instead of whatever <c>tsc</c> the host can reach.
    /// </summary>
    public async Task<SensorAvailability> ProbeAvailabilityAsync(
        string repositoryRoot,
        IReadOnlyDictionary<string, string>? configuration,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
            return new SensorAvailability(false, "tsc is unavailable: the repository path does not exist.");
        string compiler;
        try
        {
            var workingDirectory = root;
            if (configuration is not null &&
                AnalyzerInvocation.TryResolve(Id, configuration, profiles, out var invocation, out _))
            {
                if (!string.IsNullOrWhiteSpace(invocation.WorkingDirectory))
                    workingDirectory = AnalyzerCommand.ContainedPath(root, invocation.WorkingDirectory);
                if (!Directory.Exists(workingDirectory))
                    return new SensorAvailability(false,
                        $"tsc is unavailable: working directory '{invocation.WorkingDirectory}' does not exist.");
                if (invocation.Command?.Contains(TsconfigPlaceholder, StringComparison.Ordinal) == true)
                    TypeScriptProjects.Resolve(root, workingDirectory);
            }
            compiler = AnalyzerCommand.NodeModule(root, workingDirectory, CompilerModule);
            var output = await commandRunner.RunAsync(
                "node", [compiler, "--version"], workingDirectory, cancellationToken).ConfigureAwait(false);
            return VersionAvailability(output);
        }
        catch (ArgumentException exception)
        {
            return new SensorAvailability(false, $"tsc is unavailable: {exception.Message}");
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or UnauthorizedAccessException or
                InvalidOperationException)
        {
            return new SensorAvailability(false, $"tsc is unavailable: {exception.Message}");
        }
    }

    private static SensorAvailability VersionAvailability(SensorCommandResult output) =>
        output.ExitCode == 0
            ? new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
            {
                ["typescript"] = output.StandardOutput.Trim(),
            })
            : new SensorAvailability(
                false, $"tsc is unavailable: version probe exited with code {output.ExitCode}.");

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        var configuration = request.Configuration ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!AnalyzerInvocation.TryResolve(Id, configuration, profiles, out var invocation, out var refusal))
            return Unavailable(request, refusal);
        var configuredCommand = invocation.Command;
        if (string.IsNullOrWhiteSpace(configuredCommand))
            return Unavailable(request, "tsc analyzer configuration requires a host-owned profile.");
        var configuredReport = invocation.ReportPath;
        if (string.IsNullOrWhiteSpace(configuredReport))
            return Unavailable(request, "tsc analyzer configuration requires reportPath.");

        string reportPath;
        string target;
        string workingDirectory;
        IReadOnlyList<IReadOnlyList<string>> commands;
        IReadOnlyList<string?> projects = [null];
        try
        {
            target = request.Scope == SensorScope.Path && !string.IsNullOrWhiteSpace(request.Path)
                ? AnalyzerCommand.ContainedPath(root, request.Path)
                : root;
            reportPath = AnalyzerCommand.ContainedPath(root, configuredReport);
            workingDirectory = !string.IsNullOrWhiteSpace(invocation.WorkingDirectory)
                ? AnalyzerCommand.ContainedPath(root, invocation.WorkingDirectory)
                : Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
            if (!Directory.Exists(workingDirectory))
                return Unavailable(request, "tsc workingDirectory must be an existing repository directory.");
            // One invocation per checked project: `tsc -p` on a solution-style tsconfig checks nothing.
            if (configuredCommand.Contains(TsconfigPlaceholder, StringComparison.Ordinal))
                projects = [.. TypeScriptProjects.Resolve(root, workingDirectory)];
            commands = projects
                .Select(project => AnalyzerCommand.Expand(
                    project is null
                        ? configuredCommand
                        : configuredCommand.Replace(TsconfigPlaceholder, Quote(project), StringComparison.Ordinal),
                    root, target, reportPath, workingDirectory))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Unavailable(request, exception.Message);
        }

        var outputs = new List<SensorCommandResult>();
        try
        {
            foreach (var command in commands)
            {
                outputs.Add(await commandRunner.RunAsync(
                    command[0], command.Skip(1).ToArray(), workingDirectory, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or UnauthorizedAccessException or
                InvalidOperationException)
        {
            return Unavailable(request, $"tsc is unavailable: {exception.Message}");
        }
        var output = new SensorCommandResult(
            outputs.Select(result => result.ExitCode).FirstOrDefault(code => code != 0),
            string.Join(Environment.NewLine, outputs.Select(result => result.StandardOutput)
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            string.Join(Environment.NewLine, outputs.Select(result => result.StandardError)
                .Where(value => !string.IsNullOrWhiteSpace(value))));

        var diagnostics = string.Join(
            Environment.NewLine,
            new[] { output.StandardOutput, output.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(
                reportPath, diagnostics, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Unavailable(request, $"tsc report '{configuredReport}' could not be written: {exception.Message}");
        }
        var producerVersion = configuration.GetValueOrDefault("producerVersion");
        // Judge every project on its own output: diagnostics from one project must not hide another that failed.
        for (var index = 0; index < outputs.Count; index++)
        {
            var projectOutput = outputs[index];
            if (projectOutput.ExitCode == 0) continue;
            var projectDiagnostics = string.Join(
                Environment.NewLine,
                new[] { projectOutput.StandardOutput, projectOutput.StandardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (Parse(projectDiagnostics, root, workingDirectory, producerVersion).Count > 0) continue;
            var project = projects[index] is { } path
                ? $" for project '{Path.GetRelativePath(root, path).Replace('\\', '/')}'"
                : string.Empty;
            return Unavailable(
                request,
                $"tsc exited with code {projectOutput.ExitCode}{project} without parseable diagnostics. " +
                AnalyzerCommand.OutputDetail(projectOutput));
        }
        var findings = Parse(diagnostics, root, workingDirectory, producerVersion);

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

    /// <summary>The placeholder a tsc profile uses for the project file it checks.</summary>
    public const string TsconfigPlaceholder = "{tsconfig}";

    // Split() treats a quoted segment as one argument, so a project path with spaces survives.
    private static string Quote(string path) => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

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

    [GeneratedRegex(
        @"^(?<path>.+?)\((?<line>[1-9]\d*),(?<column>[1-9]\d*)\):\s+(?<severity>error|warning)\s+(?<rule>TS\d+):\s*(?<message>.+)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticLine();
}
