using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodingAgentRunner.Pricing;
using PricingTokenUsage = CodingAgentRunner.Pricing.TokenUsage;

namespace AgentOrchestrator.CodeQuality;

public sealed class FlowReviewRunner
{
    public const string PromptId = "flow-business-logic-review";
    public const string PromptVersion = "1.0.0";
    public const string ReportSchema = "https://quality.studio/schemas/flow-review.v1.schema.json";
    public const string UsageKind = "deep-flow-security";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly IReviewAgent agent;
    private readonly FlowReviewResponseParser responseParser;
    private readonly ModelPriceCatalog prices;
    private readonly Func<DateTimeOffset> clock;

    public FlowReviewRunner(
        IReviewAgent? agent = null,
        ModelPriceCatalog? prices = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.agent = agent ?? new CodingAgentReviewAgent();
        responseParser = new FlowReviewResponseParser();
        this.prices = prices ?? ModelPriceCatalog.Default;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<FlowReviewResult> ReviewAsync(
        FlowReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        var startedAt = clock().ToUniversalTime();
        ReviewAgentResult agentResult;
        try
        {
            agentResult = await agent.RunAsync(prepared.Prompt, prepared.Root, cancellationToken).ConfigureAwait(false);
        }
        catch (ReviewAgentRunCanceledException exception)
        {
            await RecordUsageAsync(prepared.Root, request.Flow.Id, exception.RunId, exception.Usage,
                exception.EffectiveModel, startedAt).ConfigureAwait(false);
            throw;
        }
        catch (ReviewAgentRunException exception)
        {
            await RecordUsageAsync(prepared.Root, request.Flow.Id, exception.RunId, exception.Usage,
                exception.EffectiveModel, startedAt).ConfigureAwait(false);
            throw;
        }

        var usage = agentResult.Usage ?? new TokenUsage(null, null, null, null, 0);
        var model = EffectiveModel(agentResult.EffectiveModel);
        await RecordUsageAsync(prepared.Root, request.Flow.Id, agentResult.RunId, usage, model, startedAt)
            .ConfigureAwait(false);
        var response = responseParser.Parse(agentResult.Response);

        // A source or catalogue change during an expensive review invalidates the conclusion.
        var finalPrepared = await PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(prepared.InputHash, finalPrepared.InputHash, StringComparison.Ordinal))
            throw new ReviewRunException("The flow evidence changed while it was being reviewed; no flow report was written.");

        var findings = CreateFindings(response, prepared.SubjectContents);
        var reportPath = GetReportPath(prepared.Root, request.Flow.Id);
        var previous = await LoadPreviousFindingsAsync(reportPath, cancellationToken).ConfigureAwait(false);
        var identities = findings.Select(finding =>
            new FindingIdentityRecord(finding.Fingerprint, finding.Id,
                finding.FlowPath[finding.WeakestPointIndex].Path, finding.RuleId)).ToArray();
        var states = await new FindingStateStore(prepared.Root).MergeReviewAsync(
            identities, previous, agent.AgentName, cancellationToken).ConfigureAwait(false);
        findings = findings.Select(finding => finding with { State = states[finding.Fingerprint].State }).ToArray();

        var reviewedAt = clock().ToUniversalTime();
        var report = new FlowReviewReport(
            ReportSchema,
            1,
            request.Flow,
            ParseVerdict(response["verdict"]!.GetValue<string>()),
            response["summary"]!.GetValue<string>().Trim(),
            response["undeterminedReason"]?.GetValue<string>()?.Trim(),
            findings,
            Count(findings),
            new FlowReviewProvenance(
                agent.AgentName,
                model,
                agentResult.RunId,
                PromptId,
                PromptVersion,
                TemplateHash(),
                prepared.InputHash,
                prepared.BoundaryCatalogueHash,
                reviewedAt,
                usage,
                ComputeCost(model, usage, startedAt)));

        if (!request.PersistMetadata) return new FlowReviewResult(null, report);
        await SaveAsync(reportPath, report, cancellationToken).ConfigureAwait(false);
        await QualityObservationLedger.AppendAsync(
            prepared.Root,
            QualityDomainObservationAdapters.FromFlow(
                report, Path.GetRelativePath(prepared.Root, reportPath).Replace('\\', '/')),
            CancellationToken.None).ConfigureAwait(false);
        return new FlowReviewResult(reportPath, report);
    }

    public async Task<int> MeasurePromptAsync(
        FlowReviewRequest request,
        CancellationToken cancellationToken = default) =>
        (await PrepareAsync(request, cancellationToken).ConfigureAwait(false)).Prompt.Length;

    public async Task<FlowReviewStaleness> EvaluateStalenessAsync(
        FlowReviewRequest request,
        FlowReviewReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var current = await PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        var reasons = new List<string>();
        if (!string.Equals(report.Provenance.InputHash, current.InputHash, StringComparison.Ordinal))
            reasons.Add("Flow source, data model, call graph, definition, or review prompt changed.");
        if (!string.Equals(report.Provenance.BoundaryCatalogueHash, current.BoundaryCatalogueHash, StringComparison.Ordinal))
            reasons.Add("Boundary catalogue changed.");
        return new FlowReviewStaleness(reasons.Count > 0, reasons);
    }

    public static string GetReportPath(string repositoryRoot, string flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        return Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "flows",
            Sha256("quality-studio-flow-report-v1\0" + flowId.Trim()) + ".flow-review.json");
    }

    public static string TemplateHash() =>
        "sha256:" + Sha256(LoadTemplate());

    private async Task<PreparedFlowReview> PrepareAsync(
        FlowReviewRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        var subjectContents = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var requestedPath in request.SubjectFiles.Distinct(StringComparer.Ordinal))
        {
            var path = NormalizeRelativePath(requestedPath);
            var fullPath = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            EnsureContained(root, fullPath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Flow source file does not exist.", fullPath);
            subjectContents[path] = Normalize(await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false));
        }

        var flowJson = SerializeCanonical(request.Flow);
        var catalogueJson = SerializeCanonical(request.BoundaryInventory);
        var boundaryHash = "sha256:" + Sha256("quality-studio-boundary-catalogue-v1\0" + catalogueJson);
        var sources = string.Join("\n\n", subjectContents.Select(pair =>
            $"### {pair.Key}\n\n```text\n{pair.Value}\n```"));
        var prompt = LoadTemplate()
            .Replace("{{FLOW}}", flowJson, StringComparison.Ordinal)
            .Replace("{{BOUNDARY_INVENTORY}}", catalogueJson, StringComparison.Ordinal)
            .Replace("{{DATA_MODEL}}", Normalize(request.DataModel), StringComparison.Ordinal)
            .Replace("{{CALL_GRAPH}}", Normalize(request.CallGraph), StringComparison.Ordinal)
            .Replace("{{SOURCE_EVIDENCE}}", sources, StringComparison.Ordinal);
        var inputHash = "sha256:" + Sha256("quality-studio-flow-review-input-v1\0" + prompt);
        return new PreparedFlowReview(root, prompt, inputHash, boundaryHash, subjectContents);
    }

    private static void ValidateRequest(FlowReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Flow);
        ArgumentNullException.ThrowIfNull(request.BoundaryInventory);
        if (string.IsNullOrWhiteSpace(request.RepositoryRoot))
            throw new ArgumentException("A repository root is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Flow.Id) || string.IsNullOrWhiteSpace(request.Flow.Name) ||
            string.IsNullOrWhiteSpace(request.Flow.Description))
            throw new ArgumentException("A flow id, name, and description are required.", nameof(request));
        if (request.Flow.EntryBoundaryIds is not { Count: > 0 })
            throw new ArgumentException("A flow requires at least one entry boundary.", nameof(request));
        var knownBoundaries = request.BoundaryInventory.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var missing = request.Flow.EntryBoundaryIds.Where(id => !knownBoundaries.Contains(id)).ToArray();
        if (missing.Length > 0)
            throw new ArgumentException($"Flow references unknown boundary id(s): {string.Join(", ", missing)}.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.DataModel))
            throw new ArgumentException("Flow review requires data-model evidence.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.CallGraph))
            throw new ArgumentException("Flow review requires call-graph evidence.", nameof(request));
        if (request.SubjectFiles is not { Count: > 0 })
            throw new ArgumentException("Flow review requires at least one source file.", nameof(request));
    }

    private static IReadOnlyList<FlowFinding> CreateFindings(
        JsonObject response,
        IReadOnlyDictionary<string, string> subjects)
    {
        var findings = new List<FlowFinding>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in response["findings"]!.AsArray().OfType<JsonObject>())
        {
            var path = node["flowPath"]!.AsArray().OfType<JsonObject>().Select(step =>
            {
                var sourcePath = NormalizeRelativePath(step["path"]!.GetValue<string>());
                var stage = ParseStage(step["stage"]!.GetValue<string>());
                if (stage != FlowPathStage.External)
                {
                    if (!subjects.TryGetValue(sourcePath, out var content))
                        throw new ReviewResponseException($"Flow path location '{sourcePath}' is not part of the reviewed subject.");
                    ValidateLine(content, sourcePath, step["line"]!.GetValue<int>());
                }
                return new FlowPathStep(
                    step["order"]!.GetValue<int>(),
                    stage,
                    sourcePath,
                    step["line"]!.GetValue<int>(),
                    step["symbol"]!.GetValue<string>().Trim(),
                    step["action"]!.GetValue<string>().Trim());
            }).ToArray();
            var weakest = node["weakestPointIndex"]!.GetValue<int>();
            var weakestStep = path[weakest];
            if (weakestStep.Stage == FlowPathStage.External || !subjects.TryGetValue(weakestStep.Path, out var content))
                throw new ReviewResponseException("A proven finding's weakest point must be reviewable repository source.");
            var snippet = content.Split('\n')[weakestStep.Line - 1];
            var findingClass = ParseClass(node["class"]!.GetValue<string>());
            var ruleId = "deep-flow/" + node["class"]!.GetValue<string>();
            var fingerprint = FindingIdentity.Compute(weakestStep.Path, FindingIdentity.NormalizeSnippet(snippet), ruleId);
            if (!fingerprints.Add(fingerprint))
                throw new ReviewResponseException($"The agent returned duplicate flow finding identity '{fingerprint}'.");
            findings.Add(new FlowFinding(
                "finding-" + fingerprint[7..],
                fingerprint,
                ruleId,
                findingClass,
                ParseSeverity(node["severity"]!.GetValue<string>()),
                node["title"]!.GetValue<string>().Trim(),
                node["description"]!.GetValue<string>().Trim(),
                node["recommendation"]!.GetValue<string>().Trim(),
                weakest,
                path));
        }
        return findings;
    }

    private async Task RecordUsageAsync(
        string root,
        string flowId,
        string runId,
        TokenUsage usage,
        string? effectiveModel,
        DateTimeOffset timestamp)
    {
        await UsageLedger.AppendAsync(root, new ReviewUsageEntry(
            runId,
            timestamp,
            EffectiveModel(effectiveModel),
            agent.AgentName,
            usage,
            UsageKind,
            "flow",
            flowId), CancellationToken.None).ConfigureAwait(false);
    }

    private FlowReviewCost ComputeCost(string model, TokenUsage usage, DateTimeOffset timestamp)
    {
        if (usage.InputTokens is null || usage.OutputTokens is null)
            return new FlowReviewCost("usageUnavailable", null, null);
        var input = Math.Max(0, usage.InputTokens.Value);
        var cached = Math.Clamp(usage.CachedInputTokens ?? 0, 0, input);
        var cost = prices.ComputeCost(model, new PricingTokenUsage(
            input - cached, Math.Max(0, usage.OutputTokens.Value), cached, 0), timestamp.UtcDateTime);
        return new FlowReviewCost(Camel(cost.Status.ToString()), cost.Total, cost.Currency);
    }

    private string EffectiveModel(string? effectiveModel) =>
        string.IsNullOrWhiteSpace(effectiveModel)
            ? string.IsNullOrWhiteSpace(agent.Model) ? "runner-default" : agent.Model
            : effectiveModel;

    private static async Task<IReadOnlyList<FindingIdentityRecord>> LoadPreviousFindingsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            var report = await JsonSerializer.DeserializeAsync<FlowReviewReport>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return report?.Findings.Select(finding => new FindingIdentityRecord(
                finding.Fingerprint, finding.Id,
                finding.FlowPath[finding.WeakestPointIndex].Path, finding.RuleId)).ToArray() ?? [];
        }
        catch (JsonException exception)
        {
            throw new ReviewRunException($"Existing flow report '{path}' is invalid: {exception.Message}");
        }
    }

    private static async Task SaveAsync(
        string path,
        FlowReviewReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static FlowFindingCounts Count(IReadOnlyList<FlowFinding> findings) =>
        new(
            findings.Count,
            findings.Count(item => item.State == FindingState.Open),
            findings.Count(item => item.State == FindingState.Accepted),
            findings.Count(item => item.State == FindingState.Waived),
            findings.Count(item => item.State == FindingState.FalsePositive),
            findings.Count(item => item.State == FindingState.Resolved));

    private static void ValidateLine(string content, string path, int line)
    {
        if (line > content.Split('\n').Length)
            throw new ReviewResponseException($"Flow path line {line} is outside reviewed file '{path}'.");
    }

    private static FlowReviewVerdict ParseVerdict(string value) => value switch
    {
        "pass" => FlowReviewVerdict.Pass,
        "fail" => FlowReviewVerdict.Fail,
        "undetermined" => FlowReviewVerdict.Undetermined,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static FlowPathStage ParseStage(string value) => value switch
    {
        "entry" => FlowPathStage.Entry,
        "authentication" => FlowPathStage.Authentication,
        "authorization" => FlowPathStage.Authorization,
        "stateTransition" => FlowPathStage.StateTransition,
        "persistence" => FlowPathStage.Persistence,
        "response" => FlowPathStage.Response,
        "external" => FlowPathStage.External,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static BusinessLogicClass ParseClass(string value) => value switch
    {
        "sessionLifecycle" => BusinessLogicClass.SessionLifecycle,
        "horizontalPrivilegeEscalation" => BusinessLogicClass.HorizontalPrivilegeEscalation,
        "verticalPrivilegeEscalation" => BusinessLogicClass.VerticalPrivilegeEscalation,
        "objectOwnership" => BusinessLogicClass.ObjectOwnership,
        "flowBypass" => BusinessLogicClass.FlowBypass,
        "replay" => BusinessLogicClass.Replay,
        "raceCondition" => BusinessLogicClass.RaceCondition,
        "quotaAbuse" => BusinessLogicClass.QuotaAbuse,
        "unenforcedInvariant" => BusinessLogicClass.UnenforcedInvariant,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static FindingSeverity ParseSeverity(string value) => value switch
    {
        "critical" => FindingSeverity.Critical,
        "high" => FindingSeverity.High,
        "medium" => FindingSeverity.Medium,
        "low" => FindingSeverity.Low,
        "info" => FindingSeverity.Info,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string NormalizeRelativePath(string value)
    {
        var path = value.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
            path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException($"Flow source path '{value}' must be repository-relative.");
        return path;
    }

    private static void EnsureContained(string root, string path)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new ArgumentException("Flow source must remain inside the repository.");
    }

    private static string SerializeCanonical<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static string LoadTemplate()
    {
        var assembly = typeof(FlowReviewRunner).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource =>
            resource.EndsWith("prompts.flow-business-logic-review.v1.md", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Normalize(reader.ReadToEnd());
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Camel(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PreparedFlowReview(
        string Root,
        string Prompt,
        string InputHash,
        string BoundaryCatalogueHash,
        IReadOnlyDictionary<string, string> SubjectContents);
}
