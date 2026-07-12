using System.Diagnostics;
using AgentOrchestrator.CodeQuality;

return await QualityCli.RunAsync(args);

internal static class QualityCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        if (string.Equals(args[0], "review", StringComparison.Ordinal))
        {
            return await RunReviewAsync(args[1..]);
        }

        if (string.Equals(args[0], "security", StringComparison.Ordinal))
        {
            return await RunSecurityAsync(args[1..]);
        }

        if (!string.Equals(args[0], "scan", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            PrintUsage();
            return 2;
        }

        try
        {
            var (path, options) = ParseScanArguments(args[1..]);
            var stopwatch = Stopwatch.StartNew();
            var report = await new StalenessEvaluator().ScanAsync(path, options);

            Console.WriteLine(
                $"quality scan: {report.Files.Count} files | fresh {report.FreshCount} | stale {report.StaleCount} | missing {report.MissingCount} | {stopwatch.ElapsedMilliseconds} ms");
            foreach (var file in report.Files.Where(file => file.State != StalenessState.Fresh))
            {
                Console.WriteLine($"{file.State.ToString().ToLowerInvariant(),-7} {file.RelativePath}");
            }

            return report.StaleCount > 0 ? 1 : 0;
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or StalenessScanException)
        {
            Console.Error.WriteLine($"quality scan failed: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> RunReviewAsync(string[] args)
    {
        try
        {
            var options = ParseReviewArguments(args);
            var globalInputs = options.GlobalInputsDirectory ?? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS");
            if (options.ExplainInputs)
            {
                var resolved = new InputResolver().Resolve(Directory.GetCurrentDirectory(), options.Kind,
                    ReviewLevel.File, globalInputs, options.BudgetCharacters);
                PrintInputExplanation(resolved);
                return 0;
            }

            var stopwatch = Stopwatch.StartNew();
            var result = await new ReviewRunner().ReviewAsync(new ReviewRequest(
                options.File, options.Kind, GlobalInputsDirectory: globalInputs,
                InputBudgetCharacters: options.BudgetCharacters));
            Console.WriteLine($"quality review: wrote {Path.GetRelativePath(Directory.GetCurrentDirectory(), result.MetaPath)} | {stopwatch.ElapsedMilliseconds} ms");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or InputFormatException or ReviewResponseException or ReviewRunException)
        {
            Console.Error.WriteLine($"quality review failed: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> RunSecurityAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintSecurityUsage();
            return args.Length == 0 ? 2 : 0;
        }

        if (!string.Equals(args[0], "scan", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Unknown security command: {args[0]}");
            PrintSecurityUsage();
            return 2;
        }

        try
        {
            var options = ParseSecurityArguments(args[1..]);
            var stopwatch = Stopwatch.StartNew();
            var result = await new GitleaksSecurityScanner().ScanAsync(new SecurityScanRequest(
                options.Path,
                options.Mode,
                options.Range,
                options.ConfigPath,
                options.BaselinePath));

            Console.WriteLine(
                $"quality security scan: {result.Report.Verdict.ToString().ToLowerInvariant()} | files {result.Report.FilesScanned} | new {result.Report.NewFindings} | accepted {result.Report.AcceptedFindings} | block {result.Report.BlockFindings} | warn {result.Report.WarnFindings} | {stopwatch.ElapsedMilliseconds} ms");
            Console.WriteLine(
                $"scanner {result.Provenance.Scanner} {result.Provenance.Version} | mode {result.Provenance.Mode} | scanned {result.Provenance.ScannedAt}");
            foreach (var finding in result.Findings)
            {
                Console.WriteLine(
                    $"{finding.Severity.ToString().ToLowerInvariant(),-8} {finding.Path} {finding.RuleId} {finding.Locations[0].Range!.Start.Line}-{finding.Locations[0].Range!.End.Line}" +
                    (finding.Accepted ? " accepted" : string.Empty));
            }

            return result.Report.Verdict is SecurityVerdict.Block or SecurityVerdict.Warn ? 1 : 0;
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or SecurityScannerUnavailableException)
        {
            Console.Error.WriteLine($"quality security scan failed: {exception.Message}");
            return 2;
        }
    }

    private static ReviewCliOptions ParseReviewArguments(string[] args)
    {
        string? file = null;
        var kind = "code";
        string? globalInputsDirectory = null;
        var budgetCharacters = InputResolver.DefaultBudgetCharacters;
        var explainInputs = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--kind" && index + 1 < args.Length)
            {
                kind = args[++index];
            }
            else if (args[index] == "--global-inputs" && index + 1 < args.Length)
            {
                globalInputsDirectory = args[++index];
            }
            else if (args[index] == "--input-budget" && index + 1 < args.Length &&
                     int.TryParse(args[++index], out var parsedBudget))
            {
                budgetCharacters = parsedBudget;
            }
            else if (args[index] == "--explain-inputs")
            {
                explainInputs = true;
            }
            else if (args[index] is "--kind" or "--global-inputs" or "--input-budget")
            {
                throw new ArgumentException($"Missing or invalid value for {args[index]}.");
            }
            else if (args[index].StartsWith("-", StringComparison.Ordinal) || file is not null)
            {
                throw new ArgumentException($"Unexpected argument: {args[index]}");
            }
            else
            {
                file = args[index];
            }
        }

        return new ReviewCliOptions(file ?? throw new ArgumentException("A review file is required."), kind,
            globalInputsDirectory, budgetCharacters, explainInputs);
    }

    private static void PrintInputExplanation(ResolvedInputs resolved)
    {
        Console.WriteLine($"quality review inputs: kind {resolved.Kind} | level {resolved.Level} | budget {resolved.IncludedCharacters}/{resolved.BudgetCharacters} characters");
        foreach (var input in resolved.Inputs)
        {
            var reason = input.Truncated ? "applicable; truncated to budget" : "applicable";
            Console.WriteLine($"inject   {input.Scope,-7} {input.Id} | priority {input.Priority} | {input.IncludedContent.Length}/{input.Content.Length} chars | {reason} | {input.Source}");
        }
        foreach (var omission in resolved.Omissions)
        {
            Console.WriteLine($"omit     {omission.Id} | {omission.Reason} | {omission.OmittedCharacters} chars | {omission.Source}");
        }
    }

    private static (string Path, StalenessEvaluatorOptions Options) ParseScanArguments(string[] args)
    {
        var path = ".";
        var kind = "code";
        var globs = new List<string>();
        var pathSet = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--kind" when index + 1 < args.Length:
                    kind = args[++index];
                    break;
                case "--include" when index + 1 < args.Length:
                    globs.Add(args[++index]);
                    break;
                case "--kind" or "--include":
                    throw new ArgumentException($"Missing value for {args[index]}.");
                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal) || pathSet)
                    {
                        throw new ArgumentException($"Unexpected argument: {args[index]}");
                    }

                    path = args[index];
                    pathSet = true;
                    break;
            }
        }

        var options = globs.Count == 0
            ? new StalenessEvaluatorOptions { ReviewKind = kind }
            : new StalenessEvaluatorOptions
        {
            ReviewKind = kind,
            IncludeGlobs = globs,
        };
        return (path, options);
    }

    private static SecurityCliOptions ParseSecurityArguments(string[] args)
    {
        var path = ".";
        var mode = SecurityScanMode.Repository;
        string? range = null;
        string? configPath = null;
        string? baselinePath = null;
        var pathSet = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--mode" when index + 1 < args.Length:
                    mode = ParseSecurityMode(args[++index]);
                    break;
                case "--range" when index + 1 < args.Length:
                    range = args[++index];
                    break;
                case "--config" when index + 1 < args.Length:
                    configPath = args[++index];
                    break;
                case "--baseline" when index + 1 < args.Length:
                    baselinePath = args[++index];
                    break;
                case "--mode" or "--range" or "--config" or "--baseline":
                    throw new ArgumentException($"Missing value for {args[index]}.");
                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal) || pathSet)
                    {
                        throw new ArgumentException($"Unexpected argument: {args[index]}");
                    }

                    path = args[index];
                    pathSet = true;
                    break;
            }
        }

        if (mode == SecurityScanMode.Range && string.IsNullOrWhiteSpace(range))
        {
            throw new ArgumentException("A git range is required for range scans.");
        }

        return new SecurityCliOptions(path, mode, range, configPath, baselinePath);
    }

    private static SecurityScanMode ParseSecurityMode(string value) =>
        value.ToLowerInvariant() switch
        {
            "repo" or "repository" => SecurityScanMode.Repository,
            "range" or "diff" => SecurityScanMode.Range,
            "staged" => SecurityScanMode.Staged,
            _ => throw new ArgumentException($"Unsupported security scan mode '{value}'."),
        };

    private static void PrintUsage() => Console.WriteLine(
        "Usage:\n  quality scan [path] [--kind code] [--include <glob>]...\n  quality review <file> [--kind code|security|performance] [--global-inputs <directory>] [--input-budget <characters>] [--explain-inputs]\n  quality security scan [path] [--mode repo|range|staged] [--range <git-range>] [--config <path>] [--baseline <path>]");

    private static void PrintSecurityUsage() => Console.WriteLine(
        "Usage:\n  quality security scan [path] [--mode repo|range|staged] [--range <git-range>] [--config <path>] [--baseline <path>]");

    private sealed record ReviewCliOptions(string File, string Kind, string? GlobalInputsDirectory,
        int BudgetCharacters, bool ExplainInputs);

    private sealed record SecurityCliOptions(string Path, SecurityScanMode Mode, string? Range,
        string? ConfigPath, string? BaselinePath);
}
