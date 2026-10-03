using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// What a coverage producer run did, recorded in the snapshot next to the reports it wrote, so the
/// risk view can say where its coverage came from and whether the test run itself failed.
/// </summary>
public sealed record CoverageProduction(
    string Profile,
    int ExitCode,
    double ElapsedSeconds,
    int TimeoutSeconds,
    string OutputDirectory);

/// <summary>The outcome of one producer run: the reports to ingest, or why there are none.</summary>
public sealed record CoverageProductionResult(
    CoverageProduction? Production,
    IReadOnlyList<string> Reports,
    string? SourceBase,
    string? Refusal);

/// <summary>
/// Runs a host-owned coverage profile - <c>dotnet test --collect "Code Coverage"</c> or vitest with
/// coverage - into the project's data root, time-boxed, and hands the reports it wrote to the
/// coverage sensor. Opt-in: nothing runs unless the repository's coverage configuration selects a
/// profile, and only a profile the host declared can be selected.
/// </summary>
public sealed class CoverageProducer
{
    /// <summary>The time-box for a profile that declares none: fifteen minutes.</summary>
    public const int DefaultTimeoutSeconds = 900;

    /// <summary>Test runners are chatty; allow more output than an analyzer before refusing the run.</summary>
    public const int MaximumOutputCharacters = 8_000_000;

    /// <summary>Where produced reports live, relative to the project's data root.</summary>
    public const string ProducedDirectory = "coverage/produced";

    private readonly AnalyzerProfileCatalog profiles;
    private readonly Func<TimeSpan, ISensorCommandRunner> runnerFactory;

    public CoverageProducer(
        AnalyzerProfileCatalog? profiles = null,
        Func<TimeSpan, ISensorCommandRunner>? runnerFactory = null)
    {
        this.profiles = profiles ?? AnalyzerProfileCatalog.BuiltIn;
        this.runnerFactory = runnerFactory ??
                             (timeout => new ProcessSensorCommandRunner(timeout, MaximumOutputCharacters));
    }

    /// <summary>Whether <paramref name="configuration"/> opts into producing coverage.</summary>
    public static bool IsRequested(IReadOnlyDictionary<string, string>? configuration) =>
        configuration?.GetValueOrDefault(AnalyzerSensorConfiguration.ProfileKey) is { } profile &&
        !string.IsNullOrWhiteSpace(profile);

    public async Task<CoverageProductionResult> ProduceAsync(
        string repositoryRoot,
        IReadOnlyDictionary<string, string> configuration,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!AnalyzerInvocation.TryResolve("coverage", configuration, profiles, out var invocation, out var refusal))
            return Refused(refusal);
        if (string.IsNullOrWhiteSpace(invocation.Command))
            return Refused("Coverage production requires a host-owned profile.");
        var profileId = invocation.ProfileId ?? "inline";
        if (!Regex.IsMatch(profileId, "^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant))
            return Refused($"Coverage profile id '{profileId}' cannot name a report directory.");

        string target;
        string workingDirectory;
        try
        {
            target = configuration.GetValueOrDefault("target") is { Length: > 0 } configuredTarget
                ? AnalyzerCommand.ContainedPath(root, configuredTarget)
                : root;
            workingDirectory = AnalyzerCommand.ContainedPath(root, invocation.WorkingDirectory ?? ".");
        }
        catch (ArgumentException exception)
        {
            return Refused(exception.Message);
        }
        if (!Directory.Exists(workingDirectory))
            return Refused($"Coverage profile '{profileId}' runs in '{Relative(root, workingDirectory)}', " +
                           "which is not a directory of this repository.");

        // Each run owns its reports; overlapping scans must not delete or ingest each other's output.
        var outputDirectory = QualityDataRoot.Combine(root,
            [.. ProducedDirectory.Split('/'), profileId, Guid.NewGuid().ToString("N")]);
        Directory.CreateDirectory(outputDirectory);

        var command = AnalyzerCommand.Expand(invocation.Command, root, target, outputDirectory)
            .Select(argument => argument.Replace("{outputDirectory}", outputDirectory, StringComparison.Ordinal))
            .ToArray();
        var timeoutSeconds = Math.Clamp(invocation.TimeoutSeconds ?? DefaultTimeoutSeconds, 1,
            AnalyzerInvocation.MaximumTimeoutSeconds);
        var stopwatch = Stopwatch.StartNew();
        SensorCommandResult output;
        try
        {
            output = await runnerFactory(TimeSpan.FromSeconds(timeoutSeconds))
                .RunAsync(command[0], command[1..], workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
        {
            // Timed out, over its output bound, or not launchable; the runner has killed the tree.
            return Refused($"Coverage profile '{profileId}' did not complete: {exception.Message}");
        }

        var production = new CoverageProduction(profileId, output.ExitCode,
            Math.Round(stopwatch.Elapsed.TotalSeconds, 1), timeoutSeconds, outputDirectory);
        var pattern = string.IsNullOrWhiteSpace(invocation.ReportPath) ? "**/*" : invocation.ReportPath;
        var reports = CoverageReportGlob.Find(outputDirectory, pattern);
        if (reports.Count == 0)
            return new CoverageProductionResult(production, [], workingDirectory,
                $"Coverage profile '{profileId}' exited with code {output.ExitCode.ToString(CultureInfo.InvariantCulture)} " +
                $"and wrote no report matching '{pattern}'. " + AnalyzerCommand.OutputDetail(output));
        // Failing tests still produce coverage; the exit code is kept in the snapshot, not treated as fatal.
        return new CoverageProductionResult(production, reports, workingDirectory, null);
    }

    private static CoverageProductionResult Refused(string reason) => new(null, [], null, reason);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>Repository-style glob matching (<c>**</c>, <c>*</c>, <c>?</c>) over the files below one directory.</summary>
internal static class CoverageReportGlob
{
    public static IReadOnlyList<string> Find(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return [];
        var regex = Compile(pattern.Replace('\\', '/'));
        return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        })
            .Where(file => regex.IsMatch(Path.GetRelativePath(directory, file).Replace('\\', '/')))
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static Regex Compile(string pattern)
    {
        var expression = new System.Text.StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                index++;
                if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                {
                    index++;
                    expression.Append("(?:.*/)?");
                }
                else expression.Append(".*");
            }
            else if (pattern[index] == '*') expression.Append("[^/]*");
            else if (pattern[index] == '?') expression.Append("[^/]");
            else expression.Append(Regex.Escape(pattern[index].ToString()));
        }
        expression.Append('$');
        return new Regex(expression.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
