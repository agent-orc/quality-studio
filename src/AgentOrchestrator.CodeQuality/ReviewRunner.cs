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
    IReadOnlyList<string>? AggregateControls = null,
    IReadOnlyList<ScopeExclusion>? AggregateExclusions = null,
    string? ReviewRunId = null,
    IReadOnlyList<ReviewSensorConfiguration>? Sensors = null,
    IReadOnlyList<ReviewSensorConfiguration>? DeterministicSensors = null,
    IReadOnlyList<SensorScanResult>? DeterministicEvidence = null,
    RequestedReviewRoute? RequestedRoute = null);

public sealed record ReviewSubjectFile(string UnitId, string Path);
public sealed record RequestedReviewRoute(string? Model, string? ThinkingLevel, string? CliType);

public sealed record ReviewResult(
    string MetaPath,
    string ReviewedHash,
    string RunId,
    ResolvedInputs Inputs,
    ReviewUsageEntry Usage,
    ReviewObservationSnapshot? Observation = null);

/// <summary>
/// Immutable copy of the review metadata and lifecycle states observed by one sweep operation.
/// The JSON is captured while the sidecar write lock is held so a later sweep cannot be
/// accidentally attributed to this operation.
/// </summary>
public sealed record ReviewObservationSnapshot(
    string SidecarPath,
    string SidecarSha256,
    DateTimeOffset CapturedAt,
    string ReviewMetaJson,
    IReadOnlyDictionary<string, string> FindingStates);

public sealed record ReviewExecutionResult(
    bool SkippedFresh,
    ReviewResult? Review,
    ReviewObservationSnapshot? Observation = null);

public sealed record ReviewPromptMeasurement(int Characters, string Path, string Level);

public sealed class ReviewRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IReviewAgent _agent;
    private readonly ReviewPromptBuilder _promptBuilder;
    private readonly ReviewResponseParser _responseParser;
    private readonly InputResolver _inputResolver;
    private readonly Action<ReviewUsageEntry>? _usageRecorded;
    private readonly StalenessEvaluator _stalenessEvaluator;
    private readonly SensorRegistry? _sensorRegistry;

    public ReviewRunner(
        IReviewAgent? agent = null,
        ReviewPromptBuilder? promptBuilder = null,
        ReviewResponseParser? responseParser = null,
        InputResolver? inputResolver = null,
        Action<ReviewUsageEntry>? usageRecorded = null,
        SensorRegistry? sensorRegistry = null,
        StalenessEvaluator? stalenessEvaluator = null)
    {
        _agent = agent ?? new CodingAgentReviewAgent();
        _promptBuilder = promptBuilder ?? new ReviewPromptBuilder();
        _responseParser = responseParser ?? new ReviewResponseParser();
        _inputResolver = inputResolver ?? new InputResolver();
        _usageRecorded = usageRecorded;
        _stalenessEvaluator = stalenessEvaluator ?? new StalenessEvaluator();
        _sensorRegistry = sensorRegistry;
    }

    public async Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken cancellationToken = default)
    {
        var execution = await ReviewIfNeededAsync(request, force: true, cancellationToken).ConfigureAwait(false);
        return execution.Review!;
    }

    public async Task<ReviewExecutionResult> ReviewIfNeededAsync(
        ReviewRequest request,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PreparePromptAsync(request, cancellationToken).ConfigureAwait(false);
        var (root, relativePath, subjectPaths, files, fileContent, inputs, prompt, unitId, metaPath, threads,
            sensorEvidence, deterministicEvidence) = prepared;
        QualityStudioEventSource.Log.InputsResolved(relativePath, request.Kind, inputs.Inputs.Count,
            inputs.Omissions.Count, inputs.IncludedCharacters, inputs.BudgetCharacters);
        var initialSubject = await PrepareSubjectAsync(root, relativePath, unitId, request, subjectPaths, files, cancellationToken).ConfigureAwait(false);
        var reviewedHash = ReviewSubjectHasher.ComputeManifestHash(unitId, initialSubject.Inputs);
        var reviewInputsHash = inputs.EffectiveHash(ReviewPromptBuilder.TemplateHash(request.Kind));
        if (!force)
        {
            var freshness = await _stalenessEvaluator.EvaluateReviewAsync(
                metaPath, reviewedHash, reviewInputsHash, _agent.Model, cancellationToken).ConfigureAwait(false);
            if (freshness.IsFresh)
            {
                var observation = await CaptureExistingObservationAsync(root, metaPath, cancellationToken)
                    .ConfigureAwait(false);
                return new ReviewExecutionResult(true, null, observation);
            }
        }
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
                agentResult.EffectiveModel, startedAt, request, relativePath, agentResult.EffectiveThinkingLevel);
            await RecordUsageAsync(root, usage, relativePath, request.Kind).ConfigureAwait(false);
            var response = _responseParser.Parse(agentResult.Response);
            if (request.Level == ReviewLevel.Project &&
                string.Equals(request.Kind, "code", StringComparison.Ordinal) &&
                request.ProjectGuidelines?.Contains("id \"architecture\"", StringComparison.Ordinal) == true &&
                !response["aspects"]!.AsArray().OfType<JsonObject>().Any(aspect =>
                    string.Equals(aspect["id"]?.GetValue<string>(), "architecture", StringComparison.Ordinal)))
            {
                throw new ReviewResponseException(
                    "A project-level code review must include the required 'architecture' aspect.");
            }
            var finalSubject = await PrepareSubjectAsync(root, relativePath, unitId, request, subjectPaths, files, cancellationToken).ConfigureAwait(false);
            if (!initialSubject.Inputs.SequenceEqual(finalSubject.Inputs))
            {
                throw new ReviewRunException("The review target changed while the agent was reviewing it; no metadata was written.");
            }

            var subjectContents = await ReadSubjectContentsAsync(subjectPaths, files, cancellationToken).ConfigureAwait(false);
            if (request.Kind == "security")
            {
                SecurityReviewCombiner.PrepareAgentResponse(response, sensorEvidence, request.Level);
            }
            var findingIdentities = FindingIdentity.Assign(response, subjectContents).ToList();
            var promptHash = ReviewPromptBuilder.TemplateHash(request.Kind);
            var reviewInputHash = inputs.EffectiveHash(promptHash);
            var requestedModel = request.RequestedRoute is null ? _agent.Model : request.RequestedRoute.Model;
            var requestedThinkingLevel = request.RequestedRoute is null ? _agent.ThinkingLevel : request.RequestedRoute.ThinkingLevel;
            var findingOrigin = new FindingOriginContext(
                request.ReviewRunId,
                agentResult.RunId,
                requestedModel,
                requestedThinkingLevel,
                _agent.CliType,
                usage.Model,
                agentResult.EffectiveThinkingLevel ?? _agent.ThinkingLevel,
                $"file-{request.Kind}-review",
                "1.0.0",
                promptHash,
                reviewInputHash,
                reviewedHash,
                SourceRevision(root),
                DateTimeOffset.UtcNow);
            if (request.Kind == "security")
            {
                findingIdentities.AddRange(SecurityReviewCombiner.AppendSensorFindings(response, sensorEvidence));
            }
            FindingEvidenceCapture.EnrichFindings(response, subjectContents, findingOrigin);

            var adapter = AdapterFromUnitId(unitId);
            ReviewObservationSnapshot observation;
            var writeLock = ReviewThreadManager.GetWriteLock(metaPath);
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var previousFindings = LoadFindingIdentities(metaPath);
                var findingStates = await new FindingStateStore(root).MergeReviewAsync(
                    findingIdentities, previousFindings, _agent.AgentName, cancellationToken).ConfigureAwait(false);
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
                    initialSubject.Exclusions,
                    reviewedHash,
                    agentResult.RunId,
                    inputs,
                    request.Level,
                    request.DisplayName,
                    usage,
                    threads,
                    sensorEvidence,
                    deterministicEvidence);
                Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
                var temporaryPath = metaPath + ".tmp-" + Guid.NewGuid().ToString("N");
                var metadataJson = meta.ToJsonString(JsonOptions) + Environment.NewLine;
                await File.WriteAllTextAsync(
                    temporaryPath,
                    metadataJson,
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, metaPath, true);
                observation = CreateObservationSnapshot(root, metaPath, metadataJson, findingStates);
            }
            finally
            {
                writeLock.Release();
            }
            QualityStudioEventSource.Log.ReviewCompleted(relativePath, request.Kind, agentResult.RunId, stopwatch.ElapsedMilliseconds);
            return new ReviewExecutionResult(
                false,
                new ReviewResult(metaPath, reviewedHash, agentResult.RunId, inputs, usage, observation),
                observation);
        }
        catch (Exception exception)
        {
            QualityStudioEventSource.Log.ReviewFailed(relativePath, request.Kind, exception.GetType().Name, exception.Message);
            throw;
        }
    }

    private static async Task<ReviewObservationSnapshot> CaptureExistingObservationAsync(
        string root,
        string metaPath,
        CancellationToken cancellationToken)
    {
        var writeLock = ReviewThreadManager.GetWriteLock(metaPath);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadataJson = await File.ReadAllTextAsync(metaPath, cancellationToken).ConfigureAwait(false);
            var states = await new FindingStateStore(root).ReadAsync(cancellationToken).ConfigureAwait(false);
            return CreateObservationSnapshot(root, metaPath, metadataJson, states);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static ReviewObservationSnapshot CreateObservationSnapshot(
        string root,
        string metaPath,
        string metadataJson,
        IReadOnlyDictionary<string, FindingStateRecord> states)
    {
        var bytes = Encoding.UTF8.GetBytes(metadataJson);
        var findingStates = states.ToDictionary(
            pair => pair.Key,
            pair => FindingStateStore.StateName(pair.Value.State),
            StringComparer.Ordinal);
        return new ReviewObservationSnapshot(
            NormalizeRelativePath(root, metaPath),
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
            DateTimeOffset.UtcNow,
            metadataJson,
            findingStates);
    }

    public async Task<ReviewPromptMeasurement> MeasurePromptAsync(
        ReviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PreparePromptAsync(request, cancellationToken).ConfigureAwait(false);
        return new ReviewPromptMeasurement(prepared.Prompt.Length, prepared.RelativePath,
            request.Level.ToString().ToLowerInvariant());
    }

    private async Task<PreparedPrompt> PreparePromptAsync(ReviewRequest request, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(request.RepositoryRoot ?? Directory.GetCurrentDirectory());
        var relativePath = NormalizeRelativePath(root, request.FilePath);
        string[] subjectPaths = request.Level == ReviewLevel.File
            ? [relativePath]
            : request.SubjectFiles?.Select(path => NormalizeRelativePath(root, path)).Distinct(StringComparer.Ordinal).ToArray()
              ?? [];
        if (subjectPaths.Length == 0)
            throw new ArgumentException("An aggregate review requires at least one descendant file.", nameof(request));

        var files = subjectPaths.Select(path => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))).ToArray();
        foreach (var file in files)
        {
            EnsureContained(root, file);
            if (!File.Exists(file)) throw new FileNotFoundException("Review target does not exist.", file);
        }

        var scope = RepositoryScope.Load(root);
        for (var index = 0; index < files.Length; index++)
        {
            var decision = scope.Evaluate(subjectPaths[index], files[index]);
            if (!decision.Included)
                throw new ArgumentException(
                    $"Review target '{subjectPaths[index]}' is excluded: {decision.Reason}", nameof(request));
        }

        var fileContent = await BuildSubjectContentAsync(subjectPaths, files, request.Level, cancellationToken).ConfigureAwait(false);
        var inputs = _inputResolver.Resolve(root, request.Kind, request.Level,
            request.GlobalInputsDirectory, request.InputBudgetCharacters);
        var globalGuidelines = Combine(inputs.Guidelines("global"), request.GlobalGuidelines);
        var projectGuidelines = Combine(inputs.Guidelines("project"), request.ProjectGuidelines);
        var unitId = request.UnitId ?? ResolveUnitId(root, relativePath, request.Level)
            ?? $"qs-v1/{GetAdapter(files[0])}/{request.Level.ToString().ToLowerInvariant()}/{Sha256($"{GetAdapter(files[0])}\0{relativePath}")}";
        var metaPath = GetMetaPath(root, files[0], request.Kind, relativePath, request.Level);
        var threads = ReviewThreadManager.LoadAndHeal(metaPath, relativePath, fileContent);
        var openThreads = new JsonArray(threads.OfType<JsonObject>()
            .Where(thread => thread["status"]?.GetValue<string>() == "open")
            .Select(thread => (JsonNode)thread.DeepClone()).ToArray());
        var sensorEvidence = await CollectSensorEvidenceAsync(
            request, root, subjectPaths, cancellationToken).ConfigureAwait(false);
        var deterministicEvidence = DeterministicEvidenceProjection.ForSubjects(
            request.DeterministicEvidence ??
            await CollectDeterministicEvidenceAsync(request, root, cancellationToken).ConfigureAwait(false),
            subjectPaths);
        var coverageEvidence = CoverageProjection.Evidence(
            CoverageSnapshot.Load(root),
            CoverageSensor.GitValue(root, "rev-parse", "--verify", "HEAD"),
            subjectPaths);
        var prompt = _promptBuilder.Build(relativePath, request.Kind, globalGuidelines,
            projectGuidelines, fileContent, openThreads,
            request.Kind == "security" ? sensorEvidence.ToPromptJson() : null,
            request.Level,
            coverageEvidence,
            DeterministicEvidenceProjection.ToPromptJson(deterministicEvidence));
        return new PreparedPrompt(root, relativePath, subjectPaths, files, fileContent, inputs,
            prompt, unitId, metaPath, threads, sensorEvidence, deterministicEvidence);
    }

    private async Task<SecurityEvidenceBundle> CollectSensorEvidenceAsync(
        ReviewRequest request,
        string root,
        IReadOnlyList<string> subjectPaths,
        CancellationToken cancellationToken)
    {
        if (request.Kind != "security" || request.Sensors is not { Count: > 0 })
            return SecurityEvidenceBundle.Empty;
        if (_sensorRegistry is null)
            throw new InvalidOperationException("Security sensors were configured for the review, but no sensor registry is available.");
        return await new SecurityEvidenceCollector(_sensorRegistry)
            .CollectAsync(root, subjectPaths, request.Sensors, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SensorScanResult>> CollectDeterministicEvidenceAsync(
        ReviewRequest request,
        string root,
        CancellationToken cancellationToken)
    {
        if (request.DeterministicSensors is not { Count: > 0 }) return [];
        if (_sensorRegistry is null)
            throw new InvalidOperationException(
                "Deterministic analyzer sensors were configured for the review, but no sensor registry is available.");
        return await new DeterministicEvidenceCollector(_sensorRegistry)
            .CollectAsync(root, request.DeterministicSensors, cancellationToken).ConfigureAwait(false);
    }

    private ReviewUsageEntry CreateUsage(string runId, TokenUsage tokens, string? effectiveModel,
        DateTimeOffset startedAt, ReviewRequest request, string relativePath, string? effectiveThinkingLevel = null) =>
        new(runId, startedAt,
            string.IsNullOrWhiteSpace(effectiveModel) ? (string.IsNullOrWhiteSpace(_agent.Model) ? "runner-default" : _agent.Model) : effectiveModel,
            _agent.AgentName, tokens, request.Kind, request.Level.ToString().ToLowerInvariant(), relativePath,
            request.ReviewRunId,
            request.ReviewRunId is null ? 1 : UsageLedger.CurrentSchemaVersion,
            request.RequestedRoute is null ? _agent.Model : request.RequestedRoute.Model,
            effectiveThinkingLevel ?? _agent.ThinkingLevel,
            request.RequestedRoute is null ? _agent.ThinkingLevel : request.RequestedRoute.ThinkingLevel);

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
        IReadOnlyList<ScopeExclusion>? aggregateExclusions,
        string reviewedHash,
        string runId,
        ResolvedInputs inputs,
        ReviewLevel level,
        string? displayName,
        ReviewUsageEntry usage,
        JsonArray threads,
        SecurityEvidenceBundle sensorEvidence,
        IReadOnlyList<SensorScanResult> deterministicEvidence)
    {
        var promptHash = ReviewPromptBuilder.TemplateHash(kind);
        var effectiveHash = inputs.EffectiveHash(promptHash);
        var reviewer = new JsonObject
        {
            ["agent"] = _agent.AgentName,
            ["model"] = usage.Model,
            ["runId"] = runId,
            ["requested"] = new JsonObject
            {
                ["model"] = usage.RequestedModel,
                ["thinkingLevel"] = usage.RequestedThinkingLevel,
            },
            ["executed"] = new JsonObject
            {
                ["cli"] = usage.CliType,
                ["model"] = usage.Model,
                ["thinkingLevel"] = usage.ThinkingLevel,
            },
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
            ["$schema"] = ReviewMetaV3.SchemaId,
            ["schemaVersion"] = ReviewMetaV3.SchemaVersion,
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
            ["deterministicEvidence"] = JsonSerializer.SerializeToNode(
                deterministicEvidence, ReviewMetaJson.Options),
        };
        if (kind == "security" && sensorEvidence.Sensors.Count > 0)
        {
            reviewer["sensors"] = new JsonArray(sensorEvidence.Sensors.Select(sensor => (JsonNode)new JsonObject
            {
                ["id"] = sensor.SensorId,
                ["version"] = sensor.SensorVersion,
                ["resultHash"] = sensor.ResultHash,
            }).ToArray());
            meta["security"] = SecurityReviewCombiner.Metadata(sensorEvidence);
        }
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
                ["excluded"] = new JsonArray((aggregateExclusions ?? []).Distinct()
                    .OrderBy(item => item.Path, StringComparer.Ordinal)
                    .ThenBy(item => item.Reason, StringComparer.Ordinal)
                    .Select(item => (JsonNode)new JsonObject
                    {
                        ["path"] = item.Path,
                        ["reason"] = item.Reason,
                    }).ToArray()),
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

    private static async Task<IReadOnlyDictionary<string, string>> ReadSubjectContentsAsync(
        IReadOnlyList<string> paths, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < files.Count; index++)
            result[paths[index]] = await File.ReadAllTextAsync(files[index], cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static IReadOnlyList<FindingIdentityRecord> LoadFindingIdentities(string metaPath)
    {
        if (!File.Exists(metaPath)) return [];
        var root = JsonNode.Parse(File.ReadAllText(metaPath))?.AsObject();
        return root?["findings"]?.AsArray().OfType<JsonObject>().Select(finding => new FindingIdentityRecord(
            finding["fingerprint"]?.GetValue<string>() ?? string.Empty,
            finding["id"]?.GetValue<string>() ?? string.Empty,
            finding["locations"]?.AsArray().OfType<JsonObject>().FirstOrDefault()?["path"]?.GetValue<string>() ?? string.Empty,
            finding["ruleId"]?.GetValue<string>() ?? string.Empty))
            .Where(finding => !string.IsNullOrWhiteSpace(finding.Fingerprint)).ToArray() ?? [];
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
        if (request.Level == ReviewLevel.File) return new PreparedSubject(fileInputs, null, null);

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
            new(relativePath, "aggregate-members", ReviewSubjectHasher.ComputeAggregateMembersHash(members, request.AggregateExclusions)),
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
        return new PreparedSubject(aggregateInputs, members, request.AggregateExclusions ?? []);
    }

    private static string NormalizeRelativePath(string root, string path)
    {
        var absolute = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        EnsureContained(root, absolute, allowRoot: true);
        return Path.GetRelativePath(root, absolute).Replace('\\', '/');
    }

    private static string GetAdapter(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".cs" or ".fs" or ".vb" ? "dotnet" : "generic";

    private static string AdapterFromUnitId(string unitId)
    {
        var segments = unitId.Split('/');
        return segments.Length == 4 && segments[0] == "qs-v1" &&
               segments[1] is "angular" or "dotnet" or "generic"
            ? segments[1]
            : throw new ArgumentException($"Unit ID '{unitId}' has no supported adapter.");
    }

    private static string? ResolveUnitId(string root, string relativePath, ReviewLevel level) =>
        FlattenHierarchy(RepositoryHierarchyBuilder.Build(root))
            .Where(node => node.Level == level && StringComparer.Ordinal.Equals(node.Path, relativePath))
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .Select(node => node.Id)
            .FirstOrDefault();

    private static IEnumerable<HierarchyNode> FlattenHierarchy(IEnumerable<HierarchyNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in FlattenHierarchy(node.Children)) yield return child;
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string SourceRevision(string root)
    {
        var commit = CoverageSensor.GitValue(root, "rev-parse", "--verify", "HEAD");
        return string.IsNullOrWhiteSpace(commit) ? "unknown" : "git:" + commit.Trim();
    }

    private static string Combine(string resolved, string? supplied) =>
        string.IsNullOrWhiteSpace(supplied)
            ? resolved
            : resolved == "(none supplied)" ? supplied.Trim() : resolved + "\n\n" + supplied.Trim();

    private static void EnsureContained(string root, string file, bool allowRoot = false)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFile = Path.GetFullPath(file).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (allowRoot && string.Equals(normalizedRoot, normalizedFile, comparison)) return;
        if (!normalizedFile.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
        {
            throw new ArgumentException("Review target must be inside the repository root.");
        }
        var current = normalizedRoot;
        foreach (var segment in Path.GetRelativePath(normalizedRoot, normalizedFile).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Review targets cannot traverse symbolic links or junctions.");
        }
    }

    private sealed record PreparedSubject(
        IReadOnlyList<SubjectInputHash> Inputs,
        IReadOnlyList<AggregateMemberHash>? Members,
        IReadOnlyList<ScopeExclusion>? Exclusions);

    private sealed record PreparedPrompt(
        string Root,
        string RelativePath,
        string[] SubjectPaths,
        string[] Files,
        string FileContent,
        ResolvedInputs Inputs,
        string Prompt,
        string UnitId,
        string MetaPath,
        JsonArray Threads,
        SecurityEvidenceBundle SensorEvidence,
        IReadOnlyList<SensorScanResult> DeterministicEvidence);
}

public sealed class ReviewRunException(string message) : Exception(message);
