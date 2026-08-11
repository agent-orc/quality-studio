namespace AgentOrchestrator.CodeQuality;

public static class ChangeDiffCommand
{
    public const int SuccessExitCode = 0;
    public const int RegressionExitCode = 1;
    public const int ErrorExitCode = 2;

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = Parse(args);
            if (options.Help)
            {
                WriteUsage(output);
                return SuccessExitCode;
            }

            IChangeDeltaReviewer? reviewer = options.Agent
                ? new AgentChangeDeltaReviewer(new CodingAgentReviewAgent(options.CliType))
                : null;
            var query = new ChangeSetQuery(
                options.Path,
                options.Base,
                options.Head,
                options.Branch,
                options.Last);
            var results = await new ChangeSetReviewService(
                    qualityTaxonomyOptions: QualityTaxonomyOptions.FromEnvironment())
                .ReviewAsync(
                new GitMergeRangeChangeSetProvider(),
                query,
                new ChangeReviewOptions(!options.NoWrite, reviewer),
                cancellationToken).ConfigureAwait(false);
            if (results.Count == 0)
            {
                output.WriteLine("quality diff: no integration transitions found.");
                return SuccessExitCode;
            }

            foreach (var result in results)
            {
                WriteResult(output, options.Path, result);
            }
            var regressions = results.Count(result => result.Document.Verdict == ChangeReviewVerdict.Regression);
            output.WriteLine(
                $"quality diff: {results.Count} change review(s) | regressions {regressions} | " +
                $"trajectory {string.Join(" -> ", results.Select(result => FormatVerdict(result.Document.Verdict)))}");
            return options.FailOnRegression && regressions > 0 ? RegressionExitCode : SuccessExitCode;
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or ChangeReviewException)
        {
            error.WriteLine($"quality diff failed: {exception.Message}");
            return ErrorExitCode;
        }
    }

    public static void WriteUsage(TextWriter output) =>
        output.WriteLine(
            """
            Usage:
              quality diff [path] --base <commit> [--head <commit>] [--fail-on-regression] [--no-write] [--agent] [--cli <adapter>]
              quality diff [path] --last <N> [--branch <integration-ref>] [--fail-on-regression] [--no-write] [--agent] [--cli <adapter>]

            Exit codes:
              0  review completed (and no regression when --fail-on-regression is set)
              1  at least one deterministic regression was found with --fail-on-regression
              2  invalid invocation, unavailable Git data, or review failure
            """);

    private static void WriteResult(TextWriter output, string requestedRoot, ChangeReviewResult result)
    {
        var document = result.Document;
        var key = document.ChangeSet.MergeCommit ?? document.ChangeSet.HeadCommit;
        output.WriteLine($"{key[..Math.Min(12, key.Length)]} {FormatVerdict(document.Verdict),-16} {document.Summary}");
        foreach (var grade in document.Delta.Grades.Where(grade => grade.Regression))
            output.WriteLine(
                $"  grade      {grade.UnitPath} ({grade.Kind}) {grade.Before!.Band}/{grade.Before.Score} -> {grade.After!.Band}/{grade.After.Score}");
        foreach (var boundary in document.Delta.Boundaries.New)
            output.WriteLine($"  boundary   new {boundary.Name} | {boundary.Path}:{boundary.Line} | {boundary.UnitId}");
        foreach (var boundary in document.Delta.Boundaries.Changed)
            output.WriteLine($"  boundary   changed {boundary.Name} | {boundary.Path}:{boundary.Line} | {boundary.UnitId}");
        foreach (var stale in document.Delta.NewlyStale)
            output.WriteLine($"  stale      {stale.UnitPath} ({stale.Kind}) | {string.Join("; ", stale.Reasons)}");
        output.WriteLine(
            $"  economy    diff {document.Economy.DiffCharacters} chars / full sweep {document.Economy.FullSweepCharacters} chars | saved {document.Economy.SavedPercent:0.##}%");
        if (File.Exists(result.Path))
        {
            var root = Path.GetFullPath(requestedRoot);
            output.WriteLine($"  wrote      {Path.GetRelativePath(root, result.Path).Replace('\\', '/')}");
        }
    }

    private static string FormatVerdict(ChangeReviewVerdict verdict) => verdict switch
    {
        ChangeReviewVerdict.NoQualityDelta => "no-quality-delta",
        ChangeReviewVerdict.Improved => "improved",
        ChangeReviewVerdict.Neutral => "neutral",
        ChangeReviewVerdict.Regression => "regression",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    private static CliOptions Parse(string[] args)
    {
        var path = ".";
        string? @base = null;
        var head = "HEAD";
        string? branch = null;
        var last = 1;
        var pathSet = false;
        var fail = false;
        var noWrite = false;
        var agent = false;
        var cliType = "codex";
        var help = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-h" or "--help":
                    help = true;
                    break;
                case "--base":
                    @base = Value(args, ref index);
                    break;
                case "--head":
                    head = Value(args, ref index);
                    break;
                case "--branch":
                    branch = Value(args, ref index);
                    break;
                case "--last":
                    if (!int.TryParse(Value(args, ref index), out last) || last < 1)
                        throw new ArgumentException("--last must be a positive integer.");
                    break;
                case "--fail-on-regression":
                    fail = true;
                    break;
                case "--no-write":
                    noWrite = true;
                    break;
                case "--agent":
                    agent = true;
                    break;
                case "--cli":
                    cliType = Value(args, ref index);
                    break;
                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal) || pathSet)
                        throw new ArgumentException($"Unexpected argument: {args[index]}");
                    path = args[index];
                    pathSet = true;
                    break;
            }
        }
        if (@base is not null && branch is not null)
            throw new ArgumentException("--base and --branch describe different provider modes and cannot be combined.");
        if (@base is not null && last != 1)
            throw new ArgumentException("--last cannot be combined with an explicit --base.");
        return new CliOptions(path, @base, head, branch, last, fail, noWrite, agent, cliType, help);
    }

    private static string Value(string[] args, ref int index)
    {
        if (++index >= args.Length || args[index].StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for {args[index - 1]}.");
        return args[index];
    }

    private sealed record CliOptions(
        string Path,
        string? Base,
        string Head,
        string? Branch,
        int Last,
        bool FailOnRegression,
        bool NoWrite,
        bool Agent,
        string CliType,
        bool Help);
}
