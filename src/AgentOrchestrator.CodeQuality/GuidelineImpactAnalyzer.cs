using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed record GuidelineImpactRequest(
    GuidelineDraft Guideline,
    IReadOnlyList<string> SamplePaths,
    string Kind = "code",
    string? GlobalInputsDirectory = null,
    int InputBudgetCharacters = InputResolver.DefaultBudgetCharacters);

public sealed record ImpactFinding(
    string Id,
    string RuleId,
    string Severity,
    string Title,
    string Path,
    int? Line);

public sealed record FileGuidelineImpact(
    string Path,
    IReadOnlyList<ImpactFinding> Before,
    IReadOnlyList<ImpactFinding> After,
    IReadOnlyList<ImpactFinding> Added,
    IReadOnlyList<ImpactFinding> Removed);

public sealed record GuidelineImpactResult(
    string GuidelineId,
    string Kind,
    IReadOnlyList<FileGuidelineImpact> Files,
    int AddedCount,
    int RemovedCount,
    bool Changed);

/// <summary>Runs the current and draft policy against a bounded file sample without writing review metadata.</summary>
public class GuidelineImpactAnalyzer
{
    private readonly IReviewAgent agent;
    private readonly InputResolver resolver;
    private readonly ReviewPromptBuilder promptBuilder;
    private readonly ReviewResponseParser parser;

    public GuidelineImpactAnalyzer(IReviewAgent? agent = null, InputResolver? resolver = null,
        ReviewPromptBuilder? promptBuilder = null, ReviewResponseParser? parser = null)
    {
        this.agent = agent ?? new CodingAgentReviewAgent();
        this.resolver = resolver ?? new InputResolver();
        this.promptBuilder = promptBuilder ?? new ReviewPromptBuilder();
        this.parser = parser ?? new ReviewResponseParser();
    }

    public virtual async Task<GuidelineImpactResult> AnalyzeAsync(
        string repositoryRoot,
        GuidelineImpactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = GuidelineStore.Serialize(request.Guideline); // applies the same validation as persistence
        if (request.SamplePaths is null || request.SamplePaths.Count is < 1 or > 10)
            throw new ArgumentException("Dry-run impact requires between one and ten sample files.");
        var root = Path.GetFullPath(repositoryRoot);
        var current = resolver.Resolve(root, request.Kind, ReviewLevel.File,
            request.GlobalInputsDirectory, request.InputBudgetCharacters);
        var draft = ApplyDraft(current, request.Guideline);
        var results = new List<FileGuidelineImpact>();
        foreach (var requestedPath in request.SamplePaths.Distinct(StringComparer.Ordinal))
        {
            var relative = requestedPath.Replace('\\', '/').TrimStart('/');
            var absolute = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(absolute))
                throw new FileNotFoundException($"Dry-run sample '{relative}' is not a repository file.", relative);
            var content = await File.ReadAllTextAsync(absolute, cancellationToken).ConfigureAwait(false);
            var before = await RunAsync(root, relative, content, request.Kind, current, cancellationToken).ConfigureAwait(false);
            var after = await RunAsync(root, relative, content, request.Kind, draft, cancellationToken).ConfigureAwait(false);
            var beforeKeys = before.ToDictionary(Key, StringComparer.Ordinal);
            var afterKeys = after.ToDictionary(Key, StringComparer.Ordinal);
            results.Add(new FileGuidelineImpact(relative, before, after,
                after.Where(value => !beforeKeys.ContainsKey(Key(value))).ToArray(),
                before.Where(value => !afterKeys.ContainsKey(Key(value))).ToArray()));
        }
        return new GuidelineImpactResult(request.Guideline.Id, request.Kind, results,
            results.Sum(value => value.Added.Count), results.Sum(value => value.Removed.Count),
            results.Any(value => value.Added.Count > 0 || value.Removed.Count > 0));
    }

    private async Task<IReadOnlyList<ImpactFinding>> RunAsync(string root, string path, string content, string kind,
        ResolvedInputs inputs, CancellationToken cancellationToken)
    {
        var prompt = promptBuilder.Build(path, kind, inputs.Guidelines("global"), inputs.Guidelines("project"), content);
        var response = parser.Parse((await agent.RunAsync(prompt, root, cancellationToken).ConfigureAwait(false)).Response);
        return response["findings"]!.AsArray().Select(node => Map(node!.AsObject())).ToArray();
    }

    private static ImpactFinding Map(JsonObject finding)
    {
        var location = finding["locations"]!.AsArray()[0]!.AsObject();
        var line = location["range"]?["start"]?["line"]?.GetValue<int>();
        return new ImpactFinding(
            finding["id"]!.GetValue<string>(), finding["ruleId"]!.GetValue<string>(),
            finding["severity"]!.GetValue<string>(), finding["title"]!.GetValue<string>(),
            location["path"]!.GetValue<string>(), line);
    }

    private static string Key(ImpactFinding finding) =>
        $"{finding.RuleId}\0{finding.Id}\0{finding.Path}\0{finding.Line}\0{finding.Severity}\0{finding.Title}";

    private static ResolvedInputs ApplyDraft(ResolvedInputs current, GuidelineDraft draft)
    {
        var level = current.Level;
        var applies = draft.Enabled && Applies(draft.Kinds, current.Kind) && Applies(draft.Levels, level);
        var raw = current.Inputs.Where(value => !StringComparer.OrdinalIgnoreCase.Equals(value.Id, draft.Id)).ToList();
        if (applies)
        {
            raw.Add(new ReviewInput(draft.Id, $"draft:{draft.Id}", "project", draft.Priority,
                draft.Kinds, draft.Levels, true, draft.Content.Trim(), string.Empty, false));
        }
        var ordered = raw.OrderBy(value => value.Scope == "global" ? 0 : 1)
            .ThenByDescending(value => value.Priority).ThenBy(value => value.Id, StringComparer.Ordinal).ToArray();
        var remaining = current.BudgetCharacters;
        var included = new List<ReviewInput>();
        var omissions = current.Omissions.Where(value => !StringComparer.OrdinalIgnoreCase.Equals(value.Id, draft.Id)).ToList();
        foreach (var input in ordered)
        {
            var take = Math.Min(remaining, input.Content.Length);
            included.Add(input with { IncludedContent = input.Content[..take], Truncated = take < input.Content.Length });
            if (take < input.Content.Length)
                omissions.Add(new InputOmission(input.Id, input.Source, take == 0 ? "budget-exhausted" : "truncated-to-budget", input.Content.Length - take));
            remaining -= take;
        }
        return new ResolvedInputs(current.Kind, current.Level, current.BudgetCharacters,
            current.BudgetCharacters - remaining, included, omissions);
    }

    private static bool Applies(IReadOnlyList<string> values, string value) =>
        values.Any(candidate => candidate is "all" or "*" || StringComparer.OrdinalIgnoreCase.Equals(candidate, value));
}
