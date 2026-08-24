using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>One measured slice of the repository, recorded in the committed baseline.</summary>
public sealed record CoverageRatchetArea(string Id, string PathPrefix, int CoveredLines, int TotalLines)
{
    public decimal LinePercent => Percent(CoveredLines, TotalLines);

    public static decimal Percent(int covered, int total) =>
        total == 0 ? 0m : Math.Round(covered * 100m / total, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The committed measurement the gate ratchets against. The dossier is explicit that no
/// percentage may be asserted before it has been measured, so the baseline is produced by
/// <c>--update</c> from real reports and never hand-written.
/// </summary>
public sealed record CoverageRatchetBaseline(
    int SchemaVersion,
    string MeasuredAt,
    string? Commit,
    decimal TolerancePercent,
    IReadOnlyList<CoverageRatchetArea> Areas)
{
    public const string RelativePath = ".quality/coverage-baseline.json";
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Slices used when no baseline exists yet. The trailing <c>repository</c> entry has an
    /// empty prefix so every measured file lands in at least one area and nothing can be
    /// silently dropped by a prefix that stops matching.
    /// </summary>
    public static IReadOnlyList<(string Id, string PathPrefix)> DefaultAreas { get; } =
    [
        ("core", "src/AgentOrchestrator.CodeQuality/"),
        ("api", "src/QualityStudio.Api/"),
        ("cli", "src/quality-cli/"),
        ("frontend", "frontend/src/"),
        ("repository", ""),
    ];
}

/// <summary>
/// <c>quality coverage</c> — measures line coverage per area from generated reports and
/// compares it with the committed baseline.
/// </summary>
public static class CoverageRatchetCommand
{
    public const int SuccessExitCode = 0;
    public const int RegressionExitCode = 1;
    public const int ErrorExitCode = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        Options options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException exception)
        {
            error.WriteLine($"quality coverage failed: {exception.Message}");
            WriteUsage(error);
            return ErrorExitCode;
        }

        if (options.Help)
        {
            WriteUsage(output);
            return SuccessExitCode;
        }

        var root = Path.GetFullPath(options.Path);
        var baselinePath = Path.GetFullPath(options.BaselinePath ??
            Path.Combine(root, CoverageRatchetBaseline.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

        IReadOnlyList<CoverageFile> files;
        try
        {
            var reports = ExpandReports(options.Reports);
            files = new CoverageReportParser().Parse(root, reports);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine($"quality coverage failed: {exception.Message}");
            return ErrorExitCode;
        }

        if (files.Count == 0)
        {
            error.WriteLine(
                "quality coverage failed: the reports parsed but contained no files. " +
                "Check that the collector ran and that the report paths are inside the repository.");
            return ErrorExitCode;
        }

        CoverageRatchetBaseline? previous;
        try
        {
            previous = await LoadBaselineAsync(baselinePath, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            // A baseline that exists but cannot be read is never silently replaced, not
            // even by --update: that would discard the recorded areas and tolerance and
            // report success while doing it.
            error.WriteLine($"quality coverage failed: {exception.Message}");
            return ErrorExitCode;
        }

        var definitions = previous is null
            ? CoverageRatchetBaseline.DefaultAreas
            : previous.Areas.Select(area => (area.Id, area.PathPrefix)).ToArray();
        var measured = Measure(files, definitions);

        foreach (var area in measured)
        {
            output.WriteLine(
                $"quality coverage: {area.Id,-12} {area.LinePercent,6:0.00}% lines ({area.CoveredLines}/{area.TotalLines})");
        }

        if (options.Update)
        {
            var tolerance = options.Tolerance ?? previous?.TolerancePercent ?? 0.5m;
            var baseline = new CoverageRatchetBaseline(
                CoverageRatchetBaseline.CurrentSchemaVersion,
                DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                CoverageSensor.GitValue(root, "rev-parse", "HEAD")?.Trim(),
                tolerance,
                measured);
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            await File.WriteAllTextAsync(
                baselinePath,
                JsonSerializer.Serialize(baseline, JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            output.WriteLine($"quality coverage: recorded baseline at {baselinePath}");
            return SuccessExitCode;
        }

        if (previous is null)
        {
            error.WriteLine(
                $"quality coverage failed: no baseline at '{baselinePath}'. " +
                "Record the first measured baseline with --update before enforcing the ratchet.");
            return ErrorExitCode;
        }

        return Compare(previous, measured, output, error);
    }

    /// <summary>
    /// Resolves each <c>--report</c> argument to concrete files. Directories are expanded so
    /// the workflow can point at a results directory whose per-assembly subdirectory names are
    /// generated. A missing or empty argument is an error rather than an empty measurement:
    /// treating a collector that never ran as "no coverage" would let it look like a pass.
    /// </summary>
    public static IReadOnlyList<string> ExpandReports(IReadOnlyList<string> reports)
    {
        var resolved = new List<string>();
        foreach (var report in reports)
        {
            if (File.Exists(report))
            {
                resolved.Add(report);
                continue;
            }

            if (!Directory.Exists(report))
                throw new FileNotFoundException($"coverage report '{report}' does not exist.", report);

            var discovered = ReportExtensions
                .SelectMany(pattern => Directory.EnumerateFiles(report, pattern, SearchOption.AllDirectories))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (discovered.Length == 0)
                throw new FileNotFoundException(
                    $"coverage report directory '{report}' contains no {string.Join(", ", ReportExtensions)} file.",
                    report);
            resolved.AddRange(discovered);
        }

        return resolved;
    }

    private static readonly string[] ReportExtensions = ["*.cobertura.xml", "*.info", "*.lcov"];

    public static IReadOnlyList<CoverageRatchetArea> Measure(
        IReadOnlyList<CoverageFile> files,
        IReadOnlyList<(string Id, string PathPrefix)> definitions)
    {
        return definitions.Select(definition =>
        {
            var matching = files.Where(file =>
                definition.PathPrefix.Length == 0 ||
                file.Path.StartsWith(definition.PathPrefix, StringComparison.Ordinal)).ToArray();
            return new CoverageRatchetArea(
                definition.Id,
                definition.PathPrefix,
                matching.Sum(file => file.CoveredLines),
                matching.Sum(file => file.TotalLines));
        }).ToArray();
    }

    private static int Compare(
        CoverageRatchetBaseline previous,
        IReadOnlyList<CoverageRatchetArea> measured,
        TextWriter output,
        TextWriter error)
    {
        var regressed = false;
        foreach (var baseline in previous.Areas)
        {
            var current = measured.FirstOrDefault(area => string.Equals(area.Id, baseline.Id, StringComparison.Ordinal));
            if (current is null)
            {
                error.WriteLine($"quality coverage: area '{baseline.Id}' disappeared from the reports.");
                regressed = true;
                continue;
            }

            // An area that has no measured lines at all means its report stopped being
            // produced, which is a gate failure rather than a coverage result.
            if (current.TotalLines == 0 && baseline.TotalLines > 0)
            {
                error.WriteLine(
                    $"quality coverage: area '{baseline.Id}' measured 0 lines but the baseline recorded " +
                    $"{baseline.TotalLines}. Its report is missing from this run.");
                regressed = true;
                continue;
            }

            var delta = current.LinePercent - baseline.LinePercent;
            if (delta < -previous.TolerancePercent)
            {
                error.WriteLine(
                    $"quality coverage: area '{baseline.Id}' fell from {baseline.LinePercent:0.00}% to " +
                    $"{current.LinePercent:0.00}% ({delta:+0.00;-0.00}, tolerance {previous.TolerancePercent:0.00}).");
                regressed = true;
            }
        }

        if (regressed)
        {
            error.WriteLine("quality coverage: coverage decreased against the committed baseline.");
            return RegressionExitCode;
        }

        output.WriteLine($"quality coverage: no area fell below the baseline recorded at {previous.MeasuredAt}.");
        return SuccessExitCode;
    }

    /// <summary>
    /// Returns null only when no baseline has been recorded yet. A baseline that exists but
    /// cannot be used throws, so callers cannot confuse "not measured yet" with "corrupt".
    /// </summary>
    private static async Task<CoverageRatchetBaseline?> LoadBaselineAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;

        CoverageRatchetBaseline? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<CoverageRatchetBaseline>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"baseline '{path}' is unreadable: {exception.Message}", exception);
        }

        if (baseline is null || baseline.Areas.Count == 0)
            throw new InvalidDataException($"baseline '{path}' is empty.");
        if (baseline.SchemaVersion != CoverageRatchetBaseline.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"baseline '{path}' has schema version {baseline.SchemaVersion}, " +
                $"expected {CoverageRatchetBaseline.CurrentSchemaVersion}.");

        return baseline;
    }

    private static Options Parse(string[] args)
    {
        var path = ".";
        var reports = new List<string>();
        string? baselinePath = null;
        decimal? tolerance = null;
        var update = false;
        var positionalTaken = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-h" or "--help":
                    return new Options(path, reports, baselinePath, tolerance, update, Help: true);
                case "--report" when HasValue(args, index):
                    reports.Add(Path.GetFullPath(args[++index]));
                    break;
                case "--baseline" when HasValue(args, index):
                    baselinePath = args[++index];
                    break;
                case "--tolerance" when HasValue(args, index):
                    if (!decimal.TryParse(args[index + 1], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                        throw new ArgumentException($"'--tolerance' expects a number, not '{args[index + 1]}'.");
                    // A negative tolerance would invert the comparison and demand that every
                    // area gain coverage, which is not what a ratchet means.
                    if (parsed < 0) throw new ArgumentException("'--tolerance' must not be negative.");
                    tolerance = parsed;
                    index++;
                    break;
                case "--update":
                    update = true;
                    break;
                case "--report" or "--baseline" or "--tolerance":
                    throw new ArgumentException($"'{args[index]}' requires a value.");
                default:
                    if (args[index].StartsWith('-')) throw new ArgumentException($"unknown option '{args[index]}'.");
                    if (positionalTaken) throw new ArgumentException("only one repository path may be given.");
                    path = args[index];
                    positionalTaken = true;
                    break;
            }
        }

        if (reports.Count == 0) throw new ArgumentException("at least one --report is required.");
        return new Options(path, reports, baselinePath, tolerance, update, Help: false);
    }

    /// <summary>
    /// An option's value must not itself look like an option: <c>--report --update</c> would
    /// otherwise swallow the flag and fail much later with a confusing missing-file error.
    /// </summary>
    private static bool HasValue(string[] args, int index) =>
        index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal);

    private static void WriteUsage(TextWriter writer) => writer.WriteLine(
        "Usage:\n  quality coverage [path] --report <file> [--report <file>]... [--baseline <file>] [--tolerance <percent>] [--update]");

    private sealed record Options(
        string Path,
        IReadOnlyList<string> Reports,
        string? BaselinePath,
        decimal? Tolerance,
        bool Update,
        bool Help);
}
