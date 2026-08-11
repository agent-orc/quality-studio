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
            var results = await new ChangeSetReviewService().ReviewAsync(
                new GitMergeRangeChangeSetProvider(),
                query,
                new ChangeReviewOptions(!options.NoWrite, reviewer),
                cancellationToken).ConfigureAwait(false);
            if (options.Format == ChangeDiffFormat.Json)
            {
                var repository = options.Repository ?? new DirectoryInfo(GitPlumbing.RequireRepository(options.Path)).Name;
                var policyHash = options.ReviewPolicyHash ?? ChangeReviewEvidenceDocument.ProviderPolicy.ContentHash;
                var artifact = ChangeReviewEvidenceJson.Create(repository, policyHash, results);
                await ChangeReviewEvidenceJson.SaveAsync(options.Output!, artifact, cancellationToken).ConfigureAwait(false);
                output.WriteLine($"quality diff: wrote {Path.GetFullPath(options.Output!)}");
            }
            if (results.Count == 0)
            {
                output.WriteLine("quality diff: no integration transitions found.");
                return SuccessExitCode;
            }

            foreach (var result in results.Where(_ => options.Format == ChangeDiffFormat.Text))
            {
                WriteResult(output, options.Path, result);
            }
            var regressions = results.Count(result => result.Document.Verdict == ChangeReviewVerdict.Regression);
            output.WriteLine(
                $"quality diff: {results.Count} change review(s) | regressions {regressions} | " +
                $"trajectory {string.Join(" -> ", results.Select(result => FormatVerdict(result.Document.Verdict)))}");
            return options.FailOnRegression && regressions > 0 ? RegressionExitCode : SuccessExitCode;
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or
                                              ChangeReviewException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"quality diff failed: {exception.Message}");
            return ErrorExitCode;
        }
    }

    public static void WriteUsage(TextWriter output) =>
        output.WriteLine(
            """
            Usage:
              quality diff [path] --base <commit> [--head <commit>] [--fail-on-regression] [--no-write] [--agent] [--cli <adapter>] [--format json --output <path>]
              quality diff [path] --last <N> [--branch <integration-ref>] [--fail-on-regression] [--no-write] [--agent] [--cli <adapter>] [--format json --output <path>]

            Portable JSON options:
              --repository <identity>          stable repository identity (defaults to directory name)
              --review-policy-hash <sha256>    caller policy binding (defaults to the QS provider policy)

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
        var format = ChangeDiffFormat.Text;
        string? output = null;
        string? repository = null;
        string? reviewPolicyHash = null;
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
                case "--format":
                    format = Value(args, ref index).ToLowerInvariant() switch
                    {
                        "text" => ChangeDiffFormat.Text,
                        "json" => ChangeDiffFormat.Json,
                        var value => throw new ArgumentException($"Unsupported diff format '{value}'. Use text or json."),
                    };
                    break;
                case "--output":
                    output = Value(args, ref index);
                    break;
                case "--repository":
                    repository = Value(args, ref index);
                    break;
                case "--review-policy-hash":
                    reviewPolicyHash = Value(args, ref index);
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
        if ((format == ChangeDiffFormat.Json) != (output is not null))
            throw new ArgumentException("--format json and --output must be specified together.");
        if (reviewPolicyHash is not null &&
            (reviewPolicyHash.Length != 71 || !reviewPolicyHash.StartsWith("sha256:", StringComparison.Ordinal) ||
             !reviewPolicyHash[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("--review-policy-hash must be a lowercase sha256 value.");
        if (string.IsNullOrWhiteSpace(repository) && repository is not null)
            throw new ArgumentException("--repository must not be empty.");
        return new CliOptions(path, @base, head, branch, last, fail, noWrite, agent, cliType,
            format, output, repository, reviewPolicyHash, help);
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
        ChangeDiffFormat Format,
        string? Output,
        string? Repository,
        string? ReviewPolicyHash,
        bool Help);

    private enum ChangeDiffFormat
    {
        Text,
        Json,
    }
}
