using System.Diagnostics;
using AgentOrchestrator.CodeQuality;

return QualityCommand.Run(args, Console.Out, Console.Error);

internal static class QualityCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            output.WriteLine("Usage: quality scan [path] [--by-level]\n       quality security scan [path] [--mode repo|range|staged] [--range <git-range>] [--config <path>] [--baseline <path>]");
            return 0;
        }

        if (StringComparer.OrdinalIgnoreCase.Equals(args[0], "security"))
        {
            return RunSecurity(args.Skip(1).ToArray(), output, error);
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(args[0], "scan"))
        {
            error.WriteLine($"Unknown command '{args[0]}'.");
            return 2;
        }

        var byLevel = args.Contains("--by-level", StringComparer.OrdinalIgnoreCase);
        var pathArgument = args.Skip(1).FirstOrDefault(value => !value.StartsWith('-')) ?? ".";
        var root = Path.GetFullPath(pathArgument);
        if (!Directory.Exists(root))
        {
            error.WriteLine($"Repository path does not exist: {root}");
            return 2;
        }

        var timer = Stopwatch.StartNew();
        try
        {
            var projects = RepositoryHierarchyBuilder.Build(root);
            ReviewMetaDiscovery.AttachDiscovered(root, projects);
            if (byLevel)
            {
                WriteByLevel(projects, output);
            }
            else
            {
                var documents = Flatten(projects).Sum(node => node.Documents.Count);
                output.WriteLine($"Scanned {projects.Count} project(s); found {documents} review document(s). Use --by-level for module summaries.");
            }

            timer.Stop();
            var moduleCount = projects.Sum(project => project.Children.Count);
            error.WriteLine($"event=quality.scan.completed projects={projects.Count} modules={moduleCount} elapsedMs={timer.ElapsedMilliseconds}");
            return 0;
        }
        catch (Exception exception)
        {
            timer.Stop();
            error.WriteLine($"event=quality.scan.failed elapsedMs={timer.ElapsedMilliseconds} error={exception.Message}");
            return 1;
        }
    }

    private static int RunSecurity(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            output.WriteLine("Usage: quality security scan [path] [--mode repo|range|staged] [--range <git-range>] [--config <path>] [--baseline <path>]");
            return 0;
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(args[0], "scan"))
        {
            error.WriteLine($"Unknown security command '{args[0]}'.");
            return 2;
        }

        try
        {
            var (path, options) = ParseSecurityArguments(args.Skip(1).ToArray());
            var timer = Stopwatch.StartNew();
            var result = new GitleaksSecurityScanner().ScanAsync(new SecurityScanRequest(path, options.Mode, options.Range, options.ConfigPath, options.BaselinePath)).GetAwaiter().GetResult();
            timer.Stop();
            output.WriteLine($"Scanned {result.Report.FilesScanned} file(s); verdict {result.Report.Verdict.ToString().ToLowerInvariant()}; new {result.Report.NewFindings}; accepted {result.Report.AcceptedFindings}; {timer.ElapsedMilliseconds} ms");
            foreach (var finding in result.Findings)
            {
                output.WriteLine($"{finding.Severity.ToString().ToLowerInvariant(),-8} {finding.Path} {finding.RuleId} {finding.Locations[0].Range!.Start.Line}-{finding.Locations[0].Range!.End.Line}" + (finding.Accepted ? " accepted" : string.Empty));
            }

            return result.Report.Verdict switch
            {
                SecurityVerdict.Unavailable => 2,
                SecurityVerdict.Block or SecurityVerdict.Warn => 1,
                _ => 0,
            };
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or SecurityScannerUnavailableException)
        {
            error.WriteLine($"security scan failed: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            error.WriteLine($"security scan failed: {exception.Message}");
            return 1;
        }
    }

    private static (string Path, SecurityCliOptions Options) ParseSecurityArguments(string[] args)
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

        return (path, new SecurityCliOptions(mode, range, configPath, baselinePath));
    }

    private static SecurityScanMode ParseSecurityMode(string value) =>
        value.ToLowerInvariant() switch
        {
            "repo" or "repository" => SecurityScanMode.Repository,
            "range" or "diff" => SecurityScanMode.Range,
            "staged" => SecurityScanMode.Staged,
            _ => throw new ArgumentException($"Unsupported security scan mode '{value}'."),
        };

    private static void WriteByLevel(IEnumerable<HierarchyNode> projects, TextWriter output)
    {
        foreach (var project in projects)
        {
            output.WriteLine($"project {project.Name}");
            foreach (var module in project.Children)
            {
                var states = HierarchyAggregation.ForAllKinds(module);
                output.WriteLine(
                    $"  module {module.Name} " +
                    string.Join(' ', Enum.GetValues<ReviewKind>().Select(kind =>
                        $"{kind.ToString().ToLowerInvariant()}={Format(states[kind].Overall)}")));
            }
        }
    }

    private static string Format(ReviewState state) => state switch
    {
        ReviewState.NotReviewed => "not-reviewed",
        ReviewState.Current => "current",
        ReviewState.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static IEnumerable<HierarchyNode> Flatten(IEnumerable<HierarchyNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children))
            {
                yield return child;
            }
        }
    }

    private sealed record SecurityCliOptions(SecurityScanMode Mode, string? Range, string? ConfigPath, string? BaselinePath);
}
