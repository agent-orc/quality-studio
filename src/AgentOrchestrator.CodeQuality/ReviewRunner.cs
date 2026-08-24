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
    string? ModelSource = null,
    IReadOnlyList<ReviewSubjectGroup>? SubjectGroups = null,
    string? OperationId = null,
    int? ReviewAttempt = null);

public sealed record ReviewSubjectFile(string UnitId, string Path);

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
    private readonly IReviewAgent _agent;
    private readonly ReviewPromptBuilder _promptBuilder;
    private readonly ReviewResponseParser _responseParser;
    private readonly InputResolver _inputResolver;
    private readonly Action<ReviewUsageEntry>? _usageRecorded;
    private readonly StalenessEvaluator _stalenessEvaluator;
    private readonly SensorRegistry? _sensorRegistry;
    private readonly HierarchyUnitResolver _unitResolver;
    private readonly ReviewExecutionPipeline _pipeline;

    public ReviewRunner(
        IReviewAgent? agent = null,
        ReviewPromptBuilder? promptBuilder = null,
        ReviewResponseParser? responseParser = null,
        InputResolver? inputResolver = null,
        Action<ReviewUsageEntry>? usageRecorded = null,
        SensorRegistry? sensorRegistry = null,
        StalenessEvaluator? stalenessEvaluator = null,
        HierarchyUnitResolver? unitResolver = null)
    {
        _agent = agent ?? CodingAgentReviewAgent.CreateDefault();
        _promptBuilder = promptBuilder ?? new ReviewPromptBuilder();
        _responseParser = responseParser ?? new ReviewResponseParser();
        _inputResolver = inputResolver ?? new InputResolver();
        _usageRecorded = usageRecorded;
        _stalenessEvaluator = stalenessEvaluator ?? new StalenessEvaluator();
        _sensorRegistry = sensorRegistry;
        _unitResolver = unitResolver ?? HierarchyUnitResolver.Shared;
        _pipeline = new ReviewExecutionPipeline(_agent);
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
        var (root, relativePath, subjectPaths, files, fileContent, memberFindings, inputs, prompt, unitId,
            metaPath, threads, sensorEvidence, deterministicEvidence) = prepared;
        QualityStudioEventSource.Log.InputsResolved(relativePath, request.Kind, inputs.Inputs.Count,
            inputs.Omissions.Count, inputs.IncludedCharacters, inputs.BudgetCharacters);
        var initialSubject = await PrepareSubjectAsync(root, relativePath, unitId, request, subjectPaths, files, cancellationToken).ConfigureAwait(false);
        var reviewedHash = ReviewSubjectHasher.ComputeManifestHash(unitId, initialSubject.Inputs);
        var reviewInputsHash = inputs.EffectiveHash(ReviewPromptBuilder.TemplateHash(request.Level, request.Kind));
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
            ReviewUsageEntry usage = null!;
            JsonObject response = null!;
            // Every resolved input id is citable, whether or not the budget included its body: the
            // agent can only have seen the included ones, and accepting the rest costs nothing.
            var rulePolicy = new RuleIdPolicy(inputs.Inputs.Select(input => input.Id), request.Kind);
            return await _pipeline.ExecuteAsync(new ReviewExecution<ReviewExecutionResult>(
                prompt,
                root,
                () => new TokenUsage(null, null, null, null, stopwatch.ElapsedMilliseconds),
                async reported =>
                {
                    usage = CreateUsage(reported.RunId, reported.Usage, reported.EffectiveModel,
                        startedAt, request, relativePath);
                    await RecordUsageAsync(root, usage, relativePath, request.Kind).ConfigureAwait(false);
                },
                outcome =>
                {
                    response = _responseParser.Parse(outcome.Response, rulePolicy);
                    RequireArchitectureAspect(response, request);
                },
                async token =>
                {
                    var finalSubject = await PrepareSubjectAsync(
                        root, relativePath, unitId, request, subjectPaths, files, token).ConfigureAwait(false);
                    return initialSubject.Inputs.SequenceEqual(finalSubject.Inputs)
                        ? null
                        : "The review target changed while the agent was reviewing it; no metadata was written.";
                },
                async (outcome, token) =>
                {
                    var subjectContents = await ReadSubjectContentsAsync(subjectPaths, files, token).ConfigureAwait(false);
                    if (request.Kind == "security")
                    {
                        SecurityReviewCombiner.PrepareAgentResponse(response, sensorEvidence, request.Level);
                    }
                    var findingIdentities = FindingIdentity.Assign(response, subjectContents).ToList();
                    AggregateFindingRollup.Apply(response, request.Level, subjectContents, memberFindings);
                    if (request.Kind == "security")
                    {
                        findingIdentities.AddRange(SecurityReviewCombiner.AppendSensorFindings(response, sensorEvidence));
                    }

                    var adapter = AdapterFromUnitId(unitId);
                    ReviewObservationSnapshot observation;
                    var writeLock = ReviewThreadManager.GetWriteLock(metaPath);
                    await writeLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        var previousFindings = LoadFindingIdentities(metaPath);
                        var findingStates = await new FindingStateStore(root).MergeReviewAsync(
                            findingIdentities, previousFindings, _agent.AgentName, token).ConfigureAwait(false);
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
                            outcome.RunId,
                            inputs,
                            request.Level,
                            request.DisplayName,
                            usage,
                            threads,
                            sensorEvidence,
                            deterministicEvidence,
                            ResolveSourceRevision(root));
                        var metadataJson = ReviewMetaJson.Serialize(meta) + Environment.NewLine;
                        await AtomicFile.WriteAllTextAsync(metaPath, metadataJson, token).ConfigureAwait(false);
                        observation = CreateObservationSnapshot(root, metaPath, metadataJson, findingStates);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                    QualityStudioEventSource.Log.ReviewCompleted(relativePath, request.Kind, outcome.RunId, stopwatch.ElapsedMilliseconds);
                    return new ReviewExecutionResult(
                        false,
                        new ReviewResult(metaPath, reviewedHash, outcome.RunId, inputs, usage, observation),
                        observation);
                }), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            QualityStudioEventSource.Log.ReviewFailed(relativePath, request.Kind, exception.GetType().Name, exception.Message);
            throw;
        }
    }

    private static void RequireArchitectureAspect(JsonObject response, ReviewRequest request)
    {
        if (request.Level == ReviewLevel.Project &&
            string.Equals(request.Kind, "code", StringComparison.Ordinal) &&
            request.ProjectGuidelines?.Contains("id \"architecture\"", StringComparison.Ordinal) == true &&
            !response["aspects"]!.AsArray().OfType<JsonObject>().Any(aspect =>
                string.Equals(aspect["id"]?.GetValue<string>(), "architecture", StringComparison.Ordinal)))
        {
            throw new ReviewResponseException(
                "A project-level code review must include the required 'architecture' aspect.");
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

        var subject = await BuildSubjectContentAsync(
            root, relativePath, request, subjectPaths, files, cancellationToken).ConfigureAwait(false);
        var fileContent = subject.Text;
        // The unit identifies the technology, and the technology selects the named rules that
        // reach this review, so it is resolved before the inputs rather than with the metadata.
        var unitId = request.UnitId ?? _unitResolver.ResolveUnitId(root, relativePath, request.Level)
            ?? $"qs-v1/{GetAdapter(files[0])}/{request.Level.ToString().ToLowerInvariant()}/{Sha256($"{GetAdapter(files[0])}\0{relativePath}")}";
        var inputs = _inputResolver.Resolve(root, request.Kind, request.Level,
            request.GlobalInputsDirectory, request.InputBudgetCharacters, AdapterFromUnitId(unitId));
        var globalGuidelines = Combine(inputs.Guidelines("global"), request.GlobalGuidelines);
        var projectGuidelines = Combine(inputs.Guidelines("project"), request.ProjectGuidelines);
        var metaPath = ReviewMetaPath.For(root, files[0], relativePath, request.Level, request.Kind);
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
        return new PreparedPrompt(root, relativePath, subjectPaths, files, fileContent, subject.MemberFindings,
            inputs, prompt, unitId, metaPath, threads, sensorEvidence, deterministicEvidence);
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
        DateTimeOffset startedAt, ReviewRequest request, string relativePath)
    {
        var model = !string.IsNullOrWhiteSpace(effectiveModel) ? effectiveModel
            : !string.IsNullOrWhiteSpace(_agent.Model) ? _agent.Model
            : ReviewModelSource.RunnerDefault;
        // The CLI reports the model it was asked to run, so a known model keeps the source the
        // caller recorded; only a run nobody named a model for is attributed to the runner default.
        var modelSource = string.Equals(model, ReviewModelSource.RunnerDefault, StringComparison.Ordinal)
            ? ReviewModelSource.RunnerDefault
            : request.ModelSource ?? _agent.ModelSource ?? ReviewModelSource.Explicit;
        return new ReviewUsageEntry(runId, startedAt, model, _agent.AgentName, tokens, request.Kind,
            request.Level.ToString().ToLowerInvariant(), relativePath, request.ReviewRunId,
            UsageLedger.CurrentSchemaVersion, modelSource, UsageLedger.EstimateCost(model, tokens, startedAt),
            request.OperationId, request.ReviewAttempt);
    }

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

    private ReviewMetaDocument CreateMeta(
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
        IReadOnlyList<SensorScanResult> deterministicEvidence,
        string? sourceRevision)
    {
        var promptHash = ReviewPromptBuilder.TemplateHash(level, kind);
        var withSensors = kind == "security" && sensorEvidence.Sensors.Count > 0;
        return new ReviewMetaDocument
        {
            Unit = new ReviewUnit(unitId, ParseAdapter(adapter), level, relativePath,
                displayName ?? Path.GetFileName(relativePath)),
            ReviewedAt = DateTimeOffset.UtcNow,
            Kind = ParseKind(kind),
            Reviewer = new ReviewerIdentity(
                _agent.AgentName,
                usage.Model,
                RunId: runId,
                Usage: new ReviewerUsage(usage.CliType, usage.Tokens.InputTokens, usage.Tokens.OutputTokens,
                    usage.Tokens.CachedInputTokens, usage.Tokens.ReasoningOutputTokens, usage.Tokens.DurationMs),
                Sensors: withSensors
                    ? sensorEvidence.Sensors.Select(sensor => new ReviewerSensorReference(
                        sensor.SensorId, sensor.SensorVersion, sensor.ResultHash)).ToArray()
                    : null,
                // Requested route, as distinct from the CLI-resolved model above — the model the
                // review agent was asked to use may differ from what actually served the run.
                RequestedModel: Trimmed(_agent.Model),
                RequestedThinkingLevel: Trimmed(_agent.ThinkingLevel)),
            ReviewedHash = ManifestHash.Subject(reviewedHash),
            SubjectInputs = subjectInputs,
            ReviewInputs = new ReviewInputs(
                ManifestHash.ReviewInput(inputs.EffectiveHash(promptHash)),
                inputs.Complete,
                inputs.Inputs.Where(input => input.IncludedContent.Length > 0)
                    .Select(input => new StandardReference(
                        input.Id, ParseScope(input.Scope), input.Version, "sha256:" + Sha256(input.Content)))
                    .ToArray(),
                inputs.Omissions.Select(omission => omission.Id).Distinct(StringComparer.Ordinal).ToArray(),
                new PromptReference(ReviewPromptBuilder.TemplateId(level, kind), "1.0.0", promptHash)),
            Grade = Read<ReviewGrade>(response, "grade"),
            Summary = response["summary"]!.GetValue<string>(),
            Aspects = Read<ReviewAspect[]>(response, "aspects"),
            Findings = Read<ReviewFinding[]>(response, "findings"),
            Threads = threads.Deserialize<ReviewThread[]>(ReviewMetaJson.Options) ?? [],
            Aggregate = aggregateMembers is null
                ? null
                : new ReviewAggregate(
                    aggregateMembers.OrderBy(member => member.UnitId, StringComparer.Ordinal)
                        .Select(member => new AggregateMember(member.UnitId, member.Path, member.SubjectHash))
                        .ToArray(),
                    (aggregateExclusions ?? []).Distinct()
                        .OrderBy(item => item.Path, StringComparer.Ordinal)
                        .ThenBy(item => item.Reason, StringComparer.Ordinal)
                        .Select(item => new AggregateExclusion(item.Path, item.Reason))
                        .ToArray()),
            Security = withSensors ? SecurityReviewCombiner.Metadata(sensorEvidence) : null,
            DeterministicEvidence = deterministicEvidence,
            SourceRevision = string.IsNullOrWhiteSpace(sourceRevision) ? null : sourceRevision,
        };
    }

    // The parser has already validated the agent's response against the review response schema, so
    // a shape the contract cannot bind is a defect in this build rather than agent output.
    private static T Read<T>(JsonObject response, string property) =>
        response[property].Deserialize<T>(ReviewMetaJson.Options)
        ?? throw new ReviewResponseException($"The review response has no usable '{property}'.");

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string AdapterFromUnitId(string unitId)
    {
        var segments = unitId.Split('/');
        return segments.Length == 4 && segments[0] == "qs-v1" &&
               segments[1] is "angular" or "dotnet" or "generic"
            ? segments[1]
            : throw new ArgumentException($"Unit ID '{unitId}' has no supported adapter.");
    }

    private static ReviewAdapter ParseAdapter(string adapter) => adapter switch
    {
        "angular" => ReviewAdapter.Angular,
        "dotnet" => ReviewAdapter.Dotnet,
        "generic" => ReviewAdapter.Generic,
        _ => throw new ArgumentException($"Adapter '{adapter}' is not part of the metadata contract.", nameof(adapter)),
    };

    private static ReviewKind ParseKind(string kind) =>
        Enum.TryParse<ReviewKind>(kind, true, out var parsed)
            ? parsed
            : throw new ArgumentException($"Review kind '{kind}' is not part of the metadata contract.", nameof(kind));

    private static StandardScope ParseScope(string scope) => scope switch
    {
        "built-in" => StandardScope.BuiltIn,
        "global" => StandardScope.Global,
        "project" => StandardScope.Project,
        _ => throw new ArgumentException($"Review input scope '{scope}' is not part of the metadata contract.", nameof(scope)),
    };

    /// <summary>
    /// A file review sees its file. An aggregate review sees a digest of its members - their sizes,
    /// their own review state and findings, the derived structure, the boundary inventory for a
    /// security pass, and a budgeted sample of real source - because concatenating every member
    /// asks the file question N times over and does not fit. The subject hash is unaffected: it
    /// stays the manifest of member hashes, and only the prompt content changes.
    /// </summary>
    private static async Task<SubjectContent> BuildSubjectContentAsync(
        string root,
        string relativePath,
        ReviewRequest request,
        IReadOnlyList<string> paths,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        if (request.Level == ReviewLevel.File)
        {
            return new SubjectContent(
                await File.ReadAllTextAsync(files[0], cancellationToken).ConfigureAwait(false),
                EmptyMemberFindings);
        }

        var digest = await AggregateSubjectDigest.BuildAsync(new AggregateDigestRequest(
            root,
            relativePath,
            request.DisplayName ?? Path.GetFileName(relativePath),
            request.Level,
            request.Kind,
            paths,
            request.AggregateExclusions ?? [],
            request.SubjectGroups ?? []), cancellationToken).ConfigureAwait(false);
        return new SubjectContent(digest.Text, digest.MemberFindings);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMemberFindings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static async Task<IReadOnlyDictionary<string, string>> ReadSubjectContentsAsync(
        IReadOnlyList<string> paths, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < files.Count; index++)
            result[paths[index]] = await File.ReadAllTextAsync(files[index], cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// The findings the previous review recorded, which the lifecycle compares against this run.
    /// A sidecar that cannot be read yields none: the reader has reported the fault, and claiming
    /// no previous findings resolves nothing, where a guessed list would resolve the wrong ones.
    /// </summary>
    private static IReadOnlyList<FindingIdentityRecord> LoadFindingIdentities(string metaPath) =>
        ReviewMetaReader.TryLoad(metaPath, out var sidecar, out _)
            ? sidecar.Document.Findings.Select(finding => new FindingIdentityRecord(
                finding.Fingerprint,
                finding.Id,
                finding.Locations.FirstOrDefault()?.Path ?? string.Empty,
                finding.RuleId)).ToArray()
            : [];

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


    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? ResolveSourceRevision(string root)
    {
        var commit = CoverageSensor.GitValue(root, "rev-parse", "--verify", "HEAD");
        if (commit is null) return null;
        var dirty = CoverageSensor.GitValue(root, "status", "--porcelain");
        return string.IsNullOrEmpty(dirty) ? $"git:{commit}" : $"git:{commit}-dirty";
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

    private sealed record SubjectContent(string Text, IReadOnlyDictionary<string, string> MemberFindings);

    private sealed record PreparedPrompt(
        string Root,
        string RelativePath,
        string[] SubjectPaths,
        string[] Files,
        string FileContent,
        IReadOnlyDictionary<string, string> MemberFindings,
        ResolvedInputs Inputs,
        string Prompt,
        string UnitId,
        string MetaPath,
        JsonArray Threads,
        SecurityEvidenceBundle SensorEvidence,
        IReadOnlyList<SensorScanResult> DeterministicEvidence);
}

public sealed class ReviewRunException(string message) : Exception(message);
