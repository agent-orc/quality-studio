using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Runs the Angular production build and normalizes bundle/style budget breaches reported by the CLI.
/// Scoped to the budget line item only; compiler and template diagnostics belong to a separate sensor.
/// </summary>
public sealed partial class AngularBudgetSensor(ISensorCommandRunner? commandRunner = null)
    : IDeterministicEvidenceSensor
{
    public const string SensorVersion = "1.0.0";
    private readonly ISensorCommandRunner commandRunner = commandRunner ?? new ProcessSensorCommandRunner();

    public string Id => "ng-budget";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public async Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var output = await commandRunner.RunAsync(
                "node", ["--version"], Directory.GetCurrentDirectory(), cancellationToken).ConfigureAwait(false);
            return output.ExitCode == 0
                ? new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
                {
                    ["node"] = output.StandardOutput.Trim(),
                })
                : new SensorAvailability(
                    false, $"ng-budget is unavailable: version probe exited with code {output.ExitCode}.");
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            return new SensorAvailability(false, $"ng-budget is unavailable: {exception.Message}");
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
            return Unavailable(request, "ng-budget configuration requires command.");
        if (!configuration.TryGetValue("reportPath", out var configuredReport) ||
            string.IsNullOrWhiteSpace(configuredReport))
            return Unavailable(request, "ng-budget configuration requires reportPath.");

        string reportPath;
        string workingDirectory;
        IReadOnlyList<string> command;
        try
        {
            reportPath = AnalyzerCommand.ContainedPath(root, configuredReport);
            workingDirectory = configuration.TryGetValue("workingDirectory", out var configuredWorkingDirectory) &&
                               !string.IsNullOrWhiteSpace(configuredWorkingDirectory)
                ? AnalyzerCommand.ContainedPath(root, configuredWorkingDirectory)
                : root;
            if (!Directory.Exists(workingDirectory))
                return Unavailable(request, "ng-budget workingDirectory must be an existing repository directory.");
            command = AnalyzerCommand.Expand(configuredCommand, root, root, reportPath);
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
            return Unavailable(request, $"ng-budget is unavailable: {exception.Message}");
        }

        var combined = string.Join(
            Environment.NewLine,
            new[] { output.StandardOutput, output.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, combined, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        var producerVersion = configuration.GetValueOrDefault("producerVersion");
        var findings = Parse(combined, root, workingDirectory, producerVersion);
        if (output.ExitCode != 0 && findings.Count == 0)
        {
            return Unavailable(
                request,
                $"ng build exited with code {output.ExitCode} without a parseable budget diagnostic. " +
                AnalyzerCommand.OutputDetail(output));
        }

        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(producerVersion)) versions["angular-cli"] = producerVersion;
        return new SensorScanResult(true, null, findings, Provenance(request, versions));
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
        foreach (Match match in BudgetLine().Matches(output))
        {
            var subject = match.Groups["subject"].Value.Trim();
            var budget = match.Groups["budget"].Value.Trim();
            var over = match.Groups["over"].Value.Trim();
            var total = match.Groups["total"].Value.Trim();
            var isFile = subject.Contains('/', StringComparison.Ordinal);
            var path = ToRepoPath(root, working, isFile ? subject : "angular.json");
            var severity = string.Equals(match.Groups["severity"].Value, "ERROR", StringComparison.Ordinal)
                ? FindingSeverity.High
                : FindingSeverity.Medium;
            var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"ng-budget\0{path}\0{subject}")));
            var title = isFile
                ? $"Component style '{subject}' exceeds its production budget by {over} (total {total}, budget {budget})"
                : $"Initial bundle exceeds its production budget by {over} (total {total}, budget {budget})";
            findings.Add(new ReviewFinding(
                $"ng-budget-{(isFile ? "style" : "initial")}-{fingerprint[^12..]}",
                "performance",
                severity,
                title,
                $"{subject} exceeded maximum budget. Budget {budget} was not met by {over} with a total of {total}.",
                "Reduce bundle or stylesheet size, or adjust the declared budget in frontend/angular.json " +
                "after a deliberate decision.",
                [new FindingLocation(path)],
                fingerprint,
                "ng-budget",
                Source: new FindingSource(
                    FindingSourceKind.Deterministic, "ng-budget", "Angular CLI", producerVersion)));
        }
        return findings
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .OrderBy(finding => finding.Locations[0].Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ToRepoPath(string root, string workingDirectory, string relative)
    {
        var absolute = Path.GetFullPath(Path.Combine(workingDirectory, relative));
        return AnalyzerCommand.IsWithin(root, absolute)
            ? Path.GetRelativePath(root, absolute).Replace('\\', '/')
            : "external/" + Path.GetFileName(absolute);
    }

    private SensorScanResult Unavailable(SensorScanRequest request, string reason) =>
        new(false, reason, [], Provenance(
            request, new Dictionary<string, string>(StringComparer.Ordinal)));

    private SensorProvenance Provenance(
        SensorScanRequest request,
        IReadOnlyDictionary<string, string> versions) =>
        new(Id, Version, "repository", ".",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), versions);

    [GeneratedRegex(
        @"[▲✘]\s*\[(?<severity>WARNING|ERROR)\]\s*(?<subject>.+?)\s+exceeded maximum budget\.\s*" +
        @"Budget\s+(?<budget>[\d.]+\s*kB)\s+was not met by\s+(?<over>[\d.]+\s*kB)\s+with a total of\s+" +
        @"(?<total>[\d.]+\s*kB)\.",
        RegexOptions.CultureInvariant)]
    private static partial Regex BudgetLine();
}
