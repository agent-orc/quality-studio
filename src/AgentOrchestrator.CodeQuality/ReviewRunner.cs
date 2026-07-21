using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed record ReviewRequest(
    string FilePath,
    string Kind = "code",
    ReviewLevel Level = ReviewLevel.File,
    string? GlobalGuidelines = null,
    string? ProjectGuidelines = null,
    string? RepositoryRoot = null,
    string? GlobalInputsDirectory = null,
    int InputBudgetCharacters = InputResolver.DefaultBudgetCharacters,
    string? UnitId = null,
    IReadOnlyList<string>? SubjectFiles = null,
    string? DisplayName = null,
    IReadOnlyList<ReviewSubjectFile>? SubjectUnits = null,
    IReadOnlyList<string>? AggregateControls = null);

public sealed record ReviewSubjectFile(string UnitId, string Path);

public sealed record ReviewResult(string MetaPath, string ReviewedHash, string RunId, ResolvedInputs Inputs, ReviewUsageEntry Usage);

public sealed class ReviewRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IReviewAgent _agent;
    private readonly ReviewPromptBuilder _promptBuilder;
    private readonly ReviewResponseParser _responseParser;
    private readonly InputResolver _inputResolver;
    private readonly Action<ReviewUsageEntry>? _usageRecorded;

    public ReviewRunner(
        IReviewAgent? agent = null,
        ReviewPromptBuilder? promptBuilder = null,
        ReviewResponseParser? responseParser = null,
        InputResolver? inputResolver = null,
        Action<ReviewUsageEntry>? usageRecorded = null)
    {
        _agent = agent ?? new CodingAgentReviewAgent();
        _promptBuilder = promptBuilder ?? new ReviewPromptBuilder();
        _responseParser = responseParser ?? new ReviewResponseParser();
        _inputResolver = inputResolver ?? new InputResolver();
        _usageRecorded = usageRecorded;
    }

    public async Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot ?? Directory.GetCurrentDirectory());
        var relativePath = NormalizeRelativePath(root, request.FilePath);
        string[] subjectPaths = request.Level == ReviewLevel.File
            ? [relativePath]
            : request.SubjectFiles?.Select(path => NormalizeRelativePath(root, path)).Distinct(StringComparer.Ordinal).ToArray()
              ?? [];
        if (subjectPaths.Length == 0)
        {
            throw new ArgumentException("An aggregate review requires at least one descendant file.", nameof(request));
        }
        var files = subjectPaths.Select(path => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))).ToArray();
        foreach (var file in files)
        {
            EnsureContained(root, file);
            if (!File.Exists(file)) throw new FileNotFoundException("Review target does not exist.", file);
        }

        var fileContent = await BuildSubjectContentAsync(subjectPaths, files, request.Level, cancellationToken).ConfigureAwait(false);
        var inputs = _inputResolver.Resolve(root, request.Kind, request.Level,
            request.GlobalInputsDirectory, request.InputBudgetCharacters);
        QualityStudioEventSource.Log.InputsResolved(relativePath, request.Kind, inputs.Inputs.Count,
            inputs.Omissions.Count, inputs.IncludedCharacters, inputs.BudgetCharacters);
        var globalGuidelines = Combine(inputs.Guidelines("global"), request.GlobalGuidelines);
        var projectGuidelines = Combine(inputs.Guidelines("project"), request.ProjectGuidelines);
        var unitId = request.UnitId ?? $"qs-v1/{GetAdapter(files[0])}/{request.Level.ToString().ToLowerInvariant()}/{Sha256($"{GetAdapter(files[0])}\0{relativePath}")}";
        var metaPath = GetMetaPath(root, files[0], request.Kind, relativePath, request.Level);
        var threads = ReviewThreadManager.LoadAndHeal(metaPath, relativePath, fileContent);
        var openThreads = new JsonArray(threads.OfType<JsonObject>()
            .Where(thread => thread["status"]?.GetValue<string>() == "open")
            .Select(thread => (JsonNode)thread.DeepClone()).ToArray());
        var prompt = _promptBuilder.Build(
            relativePath,
            request.Kind,
            globalGuidelines,
            projectGuidelines,
            fileContent,
            openThreads);
        var initialSubject = await PrepareSubjectAsync(root, relativePath, unitId, request, subjectPaths, files, cancellationToken).ConfigureAwait(false);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        QualityStudioEventSource.Log.ReviewStarted(relativePath, request.Kind, _agent.AgentName);
        try
        {
            ReviewAgentResult agentResult;
            try
            {
                agentResult = await _agent.RunAsync(prompt, root, cancellationToken).ConfigureAwait(false);
            }
            catch (ReviewAgentRunCanceledException exception)
            {
                await RecordUsageAsync(root, CreateUsage(exception.RunId, exception.Usage, exception.EffectiveModel,
                    startedAt, request, relativePath), relativePath, request.Kind).ConfigureAwait(false);
                throw;
            }
            catch (ReviewAgentRunException exception)
            {
                await RecordUsageAsync(root, CreateUsage(exception.RunId, exception.Usage, exception.EffectiveModel,
                    startedAt, request, relativePath), relativePath, request.Kind).ConfigureAwait(false);
                throw;
            }

            var usage = CreateUsage(agentResult.RunId,
                agentResult.Usage ?? new TokenUsage(null, null, null, null, stopwatch.ElapsedMilliseconds),
                agentResult.EffectiveModel, startedAt, request, relativePath);
            await RecordUsageAsync(root, usage, relativePath, request.Kind).ConfigureAwait(false);
            var response = _responseParser.Parse(agentResult.Response);
            var finalSubject = await PrepareSubjectAsync(root, relativePath, unitId, request, subjectPaths, files, cancellationToken).ConfigureAwait(false);
            if (!initialSubject.Inputs.SequenceEqual(finalSubject.Inputs))
            {
                throw new ReviewRunException("The review target changed while the agent was reviewing it; no metadata was written.");
            }

            var adapter = GetAdapter(files[0]);
            var reviewedHash = ReviewSubjectHasher.ComputeManifestHash(unitId, initialSubject.Inputs);
            var writeLock = ReviewThreadManager.GetWriteLock(metaPath);
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            threads = ReviewThreadManager.MergeLatest(threads, metaPath, relativePath, fileContent);
            ReviewThreadManager.HealFromFindingFingerprints(threads, response, relativePath, fileContent);
            ReviewThreadManager.AppendAgentUpdates(threads, response, _agent.AgentName, usage.Model, DateTimeOffset.UtcNow);
            var meta = CreateMeta(
                response,
                relativePath,
                request.Kind,
                adapter,
                unitId,
                initialSubject.Inputs,
                initialSubject.Members,
                reviewedHash,
                prompt,
                agentResult.RunId,
                inputs,
                request.Level,
                request.DisplayName,
                usage,
                threads);
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            var temporaryPath = metaPath + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(
                temporaryPath,
                meta.ToJsonString(JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, metaPath, true);
            }
            finally
            {
                writeLock.Release();
            }
            QualityStudioEventSource.Log.ReviewCompleted(relativePath, request.Kind, agentResult.RunId, stopwatch.ElapsedMilliseconds);
            return new ReviewResult(metaPath, reviewedHash, agentResult.RunId, inputs, usage);
        }
        catch (Exception exception)
        {
            QualityStudioEventSource.Log.ReviewFailed(relativePath, request.Kind, exception.GetType().Name, exception.Message);
            throw;
        }
    }

    private ReviewUsageEntry CreateUsage(string runId, TokenUsage tokens, string? effectiveModel,
        DateTimeOffset startedAt, ReviewRequest request, string relativePath) =>
        new(runId, startedAt,
            string.IsNullOrWhiteSpace(effectiveModel) ? (string.IsNullOrWhiteSpace(_agent.Model) ? "runner-default" : _agent.Model) : effectiveModel,
            _agent.AgentName, tokens, request.Kind, request.Level.ToString().ToLowerInvariant(), relativePath);

    private async Task RecordUsageAsync(string root, ReviewUsageEntry usage, string relativePath, string kind)
    {
        // The agent has already consumed the tokens; persist that fact even if the caller
        // cancels while response validation or metadata writing is finishing.
        await UsageLedger.AppendAsync(root, usage, CancellationToken.None).ConfigureAwait(false);
        QualityStudioEventSource.Log.UsageRecorded(usage.RunId, relativePath, kind,
            usage.Tokens.InputTokens ?? -1, usage.Tokens.OutputTokens ?? -1,
            usage.Tokens.CachedInputTokens ?? -1, usage.Tokens.DurationMs);
        _usageRecorded?.Invoke(usage);
    }

    private JsonObject CreateMeta(
        JsonObject response,
        string relativePath,
        string kind,
        string adapter,
        string unitId,
        IReadOnlyList<SubjectInputHash> subjectInputs,
        IReadOnlyList<AggregateMemberHash>? aggregateMembers,
        string reviewedHash,
        string prompt,
        string runId,
        ResolvedInputs inputs,
        ReviewLevel level,
        string? displayName,
        ReviewUsageEntry usage,
        JsonArray threads)
    {
        var promptHash = "sha256:" + Sha256(prompt);
        var effectiveHash = Sha256($"quality-studio-review-inputs-v1\0{kind}\0{promptHash}");
        var reviewer = new JsonObject
        {
            ["agent"] = _agent.AgentName,
            ["model"] = usage.Model,
            ["runId"] = runId,
            ["usage"] = new JsonObject
            {
                ["cliType"] = usage.CliType,
                ["inputTokens"] = usage.Tokens.InputTokens,
                ["outputTokens"] = usage.Tokens.OutputTokens,
                ["cachedInputTokens"] = usage.Tokens.CachedInputTokens,
                ["reasoningOutputTokens"] = usage.Tokens.ReasoningOutputTokens,
                ["durationMs"] = usage.Tokens.DurationMs,
            },
        };

        var meta = new JsonObject
        {
            ["$schema"] = "https://agent-orchestrator.dev/quality/schemas/review-meta.v1.schema.json",
            ["schemaVersion"] = 1,
            ["unit"] = new JsonObject
            {
                ["id"] = unitId,
                ["adapter"] = adapter,
                ["level"] = level.ToString().ToLowerInvariant(),
                ["path"] = relativePath,
                ["displayName"] = displayName ?? Path.GetFileName(relativePath),
            },
            ["reviewedAt"] = DateTime.UtcNow.ToString("O"),
            ["kind"] = kind,
            ["reviewer"] = reviewer,
            ["reviewedHash"] = new JsonObject
            {
                ["algorithm"] = "sha256",
                ["canonicalization"] = "quality-studio-subject-manifest-v1",
                ["value"] = reviewedHash,
            },
            ["subjectInputs"] = new JsonArray(subjectInputs.Select(input => (JsonNode)new JsonObject
            {
                ["path"] = input.Path,
                ["selector"] = input.Selector,
                ["contentHash"] = input.ContentHash,
            }).ToArray()),
            ["reviewInputs"] = new JsonObject
            {
                ["effectiveHash"] = new JsonObject
                {
                    ["algorithm"] = "sha256",
                    ["canonicalization"] = "quality-studio-review-inputs-v1",
                    ["value"] = effectiveHash,
                },
                ["complete"] = inputs.Complete,
                ["standards"] = new JsonArray(inputs.Inputs.Where(input => input.IncludedContent.Length > 0).Select(input => (JsonNode)new JsonObject
                {
                    ["id"] = input.Id,
                    ["scope"] = input.Scope,
                    ["version"] = "unversioned",
                    ["contentHash"] = "sha256:" + Sha256(input.Content),
                }).ToArray()),
                ["omitted"] = new JsonArray(inputs.Omissions.Select(omission => omission.Id).Distinct(StringComparer.Ordinal).Select(id => (JsonNode)id).ToArray()),
                ["prompt"] = new JsonObject
                {
                    ["id"] = $"file-{kind}-review",
                    ["version"] = "1.0.0",
                    ["contentHash"] = promptHash,
                },
            },
            ["grade"] = response["grade"]!.DeepClone(),
            ["summary"] = response["summary"]!.DeepClone(),
            ["aspects"] = response["aspects"]!.DeepClone(),
            ["findings"] = response["findings"]!.DeepClone(),
            ["threads"] = threads.DeepClone(),
        };
        if (aggregateMembers is not null)
        {
            meta["aggregate"] = new JsonObject
            {
                ["members"] = new JsonArray(aggregateMembers.OrderBy(member => member.UnitId, StringComparer.Ordinal).Select(member => (JsonNode)new JsonObject
                {
                    ["unitId"] = member.UnitId,
                    ["path"] = member.Path,
                    ["subjectHash"] = member.SubjectHash,
                }).ToArray()),
                ["excluded"] = new JsonArray(),
            };
        }
        return meta;
    }

    private static string GetMetaPath(string root, string firstFile, string kind, string relativePath, ReviewLevel level)
    {
        var key = Sha256(relativePath);
        var directory = level switch
        {
            ReviewLevel.Project => root,
            ReviewLevel.Module when File.Exists(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)))
                => Path.GetDirectoryName(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)))!,
            _ => Path.GetDirectoryName(firstFile)!,
        };
        var lane = level switch
        {
            ReviewLevel.File => "files",
            ReviewLevel.Namespace => "namespaces",
            _ => string.Empty,
        };
        var prefix = level.ToString().ToLowerInvariant();
        return Path.Combine(directory, ".quality", "reviews", lane, $"{prefix}.{key}.review-meta.{kind}.json");
    }

    private static async Task<string> BuildSubjectContentAsync(
        IReadOnlyList<string> paths, IReadOnlyList<string> files, ReviewLevel level, CancellationToken cancellationToken)
    {
        if (level == ReviewLevel.File) return await File.ReadAllTextAsync(files[0], cancellationToken).ConfigureAwait(false);
        var builder = new StringBuilder();
        for (var index = 0; index < files.Count; index++)
        {
            builder.AppendLine($"\n--- {paths[index]} ---");
            builder.AppendLine(await File.ReadAllTextAsync(files[index], cancellationToken).ConfigureAwait(false));
        }
        return builder.ToString();
    }

    private static async Task<IReadOnlyList<SubjectInputHash>> HashInputsAsync(
        IReadOnlyList<string> paths, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var result = new SubjectInputHash[files.Count];
        for (var index = 0; index < files.Count; index++)
        {
            result[index] = new SubjectInputHash(paths[index], "file",
                await ReviewSubjectHasher.ComputeFileContentHashAsync(files[index], cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    private static async Task<PreparedSubject> PrepareSubjectAsync(
        string root, string relativePath, string unitId, ReviewRequest request,
        IReadOnlyList<string> paths, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var fileInputs = await HashInputsAsync(paths, files, cancellationToken).ConfigureAwait(false);
        if (request.Level == ReviewLevel.File) return new PreparedSubject(fileInputs, null);

        var units = request.SubjectUnits?.ToDictionary(unit => unit.Path, StringComparer.Ordinal);
        var members = fileInputs.Select(input =>
        {
            var memberId = units?.GetValueOrDefault(input.Path)?.UnitId
                ?? $"qs-v1/{GetAdapter(Path.Combine(root, input.Path.Replace('/', Path.DirectorySeparatorChar)))}/file/{Sha256($"{GetAdapter(Path.Combine(root, input.Path.Replace('/', Path.DirectorySeparatorChar)))}\0{input.Path}")}";
            var subjectHash = "sha256:" + ReviewSubjectHasher.ComputeManifestHash(memberId, [input]);
            return new AggregateMemberHash(memberId, input.Path, subjectHash);
        }).OrderBy(member => member.UnitId, StringComparer.Ordinal).ToArray();
        var aggregateInputs = new List<SubjectInputHash>
        {
            new(relativePath, "aggregate-members", ReviewSubjectHasher.ComputeAggregateMembersHash(members)),
        };
        var controls = request.AggregateControls?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            ?? Enumerable.Empty<string>();
        foreach (var controlPath in controls)
        {
            var normalized = NormalizeRelativePath(root, controlPath);
            var controlFile = Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(controlFile))
                aggregateInputs.Add(new(normalized, "aggregate-control", await ReviewSubjectHasher.ComputeFileContentHashAsync(controlFile, cancellationToken).ConfigureAwait(false)));
        }
        return new PreparedSubject(aggregateInputs, members);
    }

    private static string NormalizeRelativePath(string root, string path)
    {
        var absolute = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        EnsureContained(root, absolute, allowRoot: true);
        return Path.GetRelativePath(root, absolute).Replace('\\', '/');
    }

    private static string GetAdapter(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".cs" or ".fs" or ".vb" ? "dotnet" : "angular";

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Combine(string resolved, string? supplied) =>
        string.IsNullOrWhiteSpace(supplied)
            ? resolved
            : resolved == "(none supplied)" ? supplied.Trim() : resolved + "\n\n" + supplied.Trim();

    private static void EnsureContained(string root, string file, bool allowRoot = false)
    {
        if (allowRoot && string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), file.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!file.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Review target must be inside the repository root.");
        }
    }

    private sealed record PreparedSubject(IReadOnlyList<SubjectInputHash> Inputs, IReadOnlyList<AggregateMemberHash>? Members);
}

public sealed class ReviewRunException(string message) : Exception(message);
