using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationLedgerTests
{
    private static readonly QualityTaxonomyOptions WriteEnabled = new(ObservationWriteEnabled: true);

    [Fact]
    public void TheRolloutGatesUseTheirDocumentedConfigurationKeys()
    {
        Assert.Equal("QualityTaxonomy:ObservationWriteEnabled", QualityTaxonomyOptions.ObservationWriteKey);
        Assert.Equal("QualityTaxonomy:ObservationReadEnabled", QualityTaxonomyOptions.ObservationReadKey);

        var options = QualityTaxonomyOptions.FromEnvironment(name => name switch
        {
            "QualityTaxonomy__ObservationWriteEnabled" => "true",
            _ => null,
        });

        Assert.True(options.ObservationWriteEnabled);
        Assert.False(options.ObservationReadEnabled);
        Assert.False(QualityTaxonomyOptions.FromEnvironment(_ => null).ObservationWriteEnabled);
        Assert.False(QualityTaxonomyOptions.FromEnvironment(_ => "yes").ObservationWriteEnabled);
    }

    [Fact]
    public void ObservationIdentityIsDerivedFromTheExactRunSubjectAndTaxonomy()
    {
        var first = ObservationIdentity.Compute("run-1", "unit-1", "code", "sha256:a", "sha256:b", "sha256:c");
        var replay = ObservationIdentity.Compute("run-1", "unit-1", "code", "sha256:a", "sha256:b", "sha256:c");
        var otherRun = ObservationIdentity.Compute("run-2", "unit-1", "code", "sha256:a", "sha256:b", "sha256:c");
        var otherTaxonomy = ObservationIdentity.Compute("run-1", "unit-1", "code", "sha256:a", "sha256:b", "sha256:z");

        Assert.Equal(first, replay);
        Assert.NotEqual(first, otherRun);
        Assert.NotEqual(first, otherTaxonomy);
        Assert.Matches("^observation-sha256:[0-9a-f]{64}$", first);
    }

    [Fact]
    public async Task ReplayingTheSameObservationDoesNotDuplicateALine()
    {
        using var directory = new TempRepository();
        var observation = QualityObservationContractTests.CreateObservation();

        Assert.True(await QualityObservationLedger.AppendAsync(directory.Path, observation, Token));
        Assert.False(await QualityObservationLedger.AppendAsync(directory.Path, observation, Token));

        var path = QualityObservationLedger.GetLedgerPath(directory.Path, observation.RecordedAt);
        Assert.Single(await File.ReadAllLinesAsync(path, Token), line => line.Length > 0);
        Assert.EndsWith(Path.Combine(".quality", "observations", "2026-08.jsonl"), path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APartialHistoricalLineDoesNotHideTheRestOfTheLedger()
    {
        using var directory = new TempRepository();
        var observation = QualityObservationContractTests.CreateObservation();
        var path = QualityObservationLedger.GetLedgerPath(directory.Path, observation.RecordedAt);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"observationId\": \n", Token);

        await QualityObservationLedger.AppendAsync(directory.Path, observation, Token);
        var records = await QualityObservationLedger.ReadAsync(directory.Path, cancellationToken: Token);

        Assert.Equal(2, records.Count);
        Assert.Equal(ObservationSupport.Malformed, records[0].Support);
        Assert.True(records[1].IsSupported);
        Assert.Equal(observation.ObservationId, records[1].Observation!.ObservationId);
    }

    [Fact]
    public async Task AnUnsupportedTaxonomyMajorStaysReadableAndUninterpreted()
    {
        using var directory = new TempRepository();
        var future = QualityObservationContractTests.CreateObservation() with
        {
            ObservationId = "observation-sha256:" + new string('9', 64),
            Taxonomy = new("quality-studio/core", "2.0.0", QualityTaxonomyCatalogue.Core.Digest),
        };
        await QualityObservationLedger.AppendAsync(directory.Path, future, Token);

        var records = await QualityObservationLedger.ReadAsync(directory.Path, cancellationToken: Token);

        var record = Assert.Single(records);
        Assert.Equal(ObservationSupport.UnsupportedTaxonomyMajor, record.Support);
        Assert.Contains("\"assessment\":\"fail\"", record.RawJson, StringComparison.Ordinal);
        Assert.Null(record.Observation);
    }

    [Fact]
    public async Task TwoModelsOnTheSameSubjectLeaveTwoObservationsAndOneCurrentSidecar()
    {
        using var directory = new TempRepository();
        await WriteSubjectAsync(directory.Path);
        var request = new ReviewRequest("src/Small.cs", RepositoryRoot: directory.Path, ReviewRunId: "review-1");

        var first = await Runner("model-a", "high").ReviewAsync(request, Token);
        var second = await Runner("model-b", "medium").ReviewAsync(request, Token);

        var records = await QualityObservationLedger.ReadAsync(directory.Path, cancellationToken: Token);
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.True(record.IsSupported));
        Assert.Equal(["model-a", "model-b"],
            records.Select(record => record.Observation!.Producer.EffectiveModel).Order(StringComparer.Ordinal));
        Assert.Equal(["high", "medium"],
            records.Select(record => record.Observation!.Producer.ThinkingLevel).Order(StringComparer.Ordinal));
        Assert.Equal(first.MetaPath, second.MetaPath);
        Assert.Single(Directory.EnumerateFiles(
            Path.GetDirectoryName(second.MetaPath)!, "*.review-meta.code.json"));
    }

    [Fact]
    public async Task EachObservationJoinsItsUsageEntryByExactRunId()
    {
        using var directory = new TempRepository();
        await WriteSubjectAsync(directory.Path);
        var request = new ReviewRequest("src/Small.cs", RepositoryRoot: directory.Path, ReviewRunId: "review-7");

        var result = await Runner("model-a", "high").ReviewAsync(request, Token);

        var usage = await UsageLedger.QueryAsync(directory.Path, cancellationToken: Token);
        var observation = Assert.Single(await QualityObservationLedger.ReadAsync(
            directory.Path, cancellationToken: Token)).Observation!;
        Assert.Equal(result.RunId, observation.Producer.RunId);
        Assert.Equal("review-7", observation.Producer.ReviewRunId);
        Assert.Contains(usage.Recent, entry =>
            entry.RunId == observation.Producer.RunId && entry.ReviewRunId == observation.Producer.ReviewRunId);
    }

    [Fact]
    public async Task AnUnreportableRouteFactIsRecordedAsUnknownAndNeverInferred()
    {
        using var directory = new TempRepository();
        await WriteSubjectAsync(directory.Path);

        await new ReviewRunner(new RouteBlindAgent(), taxonomyOptions: WriteEnabled)
            .ReviewAsync(new ReviewRequest("src/Small.cs", RepositoryRoot: directory.Path), Token);

        var producer = Assert.Single(await QualityObservationLedger.ReadAsync(
            directory.Path, cancellationToken: Token)).Observation!.Producer;
        Assert.Equal("unknown", producer.ThinkingLevel);
        Assert.Equal("unknown", producer.Provider);
        Assert.Equal("unknown", producer.RequestedModel);
        Assert.Equal("effective-only", producer.EffectiveModel);
        Assert.Equal(ReviewRouteProvenance.PolicyVersion, producer.RoutePolicyVersion);
    }

    [Fact]
    public async Task TheSidecarIsUnchangedAndTheReviewIsUnobservedWhileTheGateIsClosed()
    {
        using var directory = new TempRepository();
        await WriteSubjectAsync(directory.Path);

        var result = await new ReviewRunner(new RouteAwareAgent("model-a", "high"),
                taxonomyOptions: QualityTaxonomyOptions.Disabled)
            .ReviewAsync(new ReviewRequest("src/Small.cs", RepositoryRoot: directory.Path), Token);

        Assert.Null(result.TaxonomyObservation);
        Assert.True(File.Exists(result.MetaPath));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, ".quality", "observations")));
    }

    [Fact]
    public async Task TheDualWriteLeavesTheSidecarContractUntouched()
    {
        using var directory = new TempRepository();
        await WriteSubjectAsync(directory.Path);

        var result = await Runner("model-a", "high").ReviewAsync(
            new ReviewRequest("src/Small.cs", RepositoryRoot: directory.Path), Token);

        using var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, Token));
        Assert.Equal(ReviewMetaDocument.CurrentSchemaVersion,
            sidecar.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ReviewMetaDocument.SchemaId, sidecar.RootElement.GetProperty("$schema").GetString());
        Assert.False(sidecar.RootElement.TryGetProperty("taxonomy", out _));
        Assert.False(sidecar.RootElement.TryGetProperty("observationId", out _));
    }

    [Fact]
    public async Task NoNewSidecarIsWrittenWhenItsAuthoritativeObservationCannotBeAppended()
    {
        using var directory = new TempRepository();
        await WriteSubjectAsync(directory.Path);
        // A file where the ledger directory belongs makes the append fail without touching the review.
        await File.WriteAllTextAsync(Path.Combine(directory.Path, ".quality", "observations"), "blocked", Token);

        var exception = await Assert.ThrowsAsync<QualityObservationAppendException>(() =>
            Runner("model-a", "high").ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: directory.Path), Token));

        Assert.Contains("previous sidecar stays current", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.review-meta.*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnAppendedObservationIsFullyRecoverableAfterACrashBeforeTheSidecar()
    {
        using var directory = new TempRepository();
        var observation = QualityObservationContractTests.CreateObservation();
        await QualityObservationLedger.AppendAsync(directory.Path, observation, Token);

        var recovered = Assert.Single(await QualityObservationLedger.ReadAsync(
            directory.Path, cancellationToken: Token)).Observation!;

        Assert.Equal(observation.ObservationId, recovered.ObservationId);
        Assert.Equal(observation.Subject.UnitId, recovered.Subject.UnitId);
        Assert.Equal(observation.Grade!.Score, recovered.Grade!.Score);
        Assert.Equal(observation.Findings[0].OccurrenceFingerprint, recovered.Findings[0].OccurrenceFingerprint);
    }

    [Fact]
    public async Task AnObservationWrittenByARealReviewValidatesAgainstThePublishedSchema()
    {
        using var directory = new TempRepository();
        await WriteSubjectAsync(directory.Path);
        var schema = SchemaAssert.Load("quality-observation.v1.schema.json");

        await Runner("model-a", "high").ReviewAsync(
            new ReviewRequest("src/Small.cs", RepositoryRoot: directory.Path, ReviewRunId: "review-1"), Token);

        var line = Assert.Single(await File.ReadAllLinesAsync(QualityObservationLedger.GetLedgerPath(
            directory.Path, DateTimeOffset.UtcNow), Token), text => text.Length > 0);
        using var json = JsonDocument.Parse(line);
        var result = schema.Evaluate(json.RootElement, SchemaAssert.Options);
        Assert.True(result.IsValid, SchemaAssert.Describe(result));
    }

    [Fact]
    public void TheRoutingPolicyNamesTheProviderBehindEachCli()
    {
        Assert.Equal("openai", ReviewRouteProvenance.ProviderForCli("codex"));
        Assert.Equal("unknown", ReviewRouteProvenance.ProviderForCli("not-a-cli"));
        Assert.Equal("unknown", ReviewRouteProvenance.ProviderForCli(null));
        Assert.Equal(ReviewModelCatalog.Default.Snapshot.PolicyVersion, ReviewRouteProvenance.PolicyVersion);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ReviewRunner Runner(string model, string thinkingLevel) =>
        new(new RouteAwareAgent(model, thinkingLevel), taxonomyOptions: WriteEnabled);

    private static async Task WriteSubjectAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Small.cs"),
            "internal static class Small { }\n", Token);
    }

    private sealed class RouteAwareAgent(string model, string thinkingLevel) : IReviewAgent
    {
        public string AgentName => "codex";

        public string? Model => model;

        public string? ThinkingLevel => thinkingLevel;

        public string? Provider => "openai";

        public Task<ReviewAgentResult> RunAsync(
            string prompt, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult(
                "quality-" + model, $"```json\n{ReviewResponseParserTests.ValidResponse}\n```",
                new TokenUsage(120, 34, 56, 7, 890), model));
    }

    private sealed class RouteBlindAgent : IReviewAgent
    {
        public string AgentName => "unknown-cli";

        public string? Model => null;

        public Task<ReviewAgentResult> RunAsync(
            string prompt, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult(
                "quality-blind", $"```json\n{ReviewResponseParserTests.ValidResponse}\n```",
                new TokenUsage(1, 1, 1, 1, 1), "effective-only"));
    }
}

/// <summary>A throwaway repository root that is removed when the test finishes.</summary>
internal sealed class TempRepository : IDisposable
{
    public TempRepository()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "quality-observation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose() => TestDirectory.Delete(Path);
}

public sealed class ReviewObservationProjectionTests
{
    [Fact]
    public void AReviewWithoutFindingsIsAPassBackedByAvailableEvidence()
    {
        var observation = Project(Meta());

        Assert.Equal(CoreTerms.Assessment.Pass, observation.Assessment);
        Assert.Equal(CoreTerms.EvidenceStatus.Available, observation.EvidenceStatus);
        Assert.Equal("code.correctness", Assert.Single(observation.Aspects).AspectId);
        Assert.Equal(CoreTerms.Assessment.Pass, observation.Aspects[0].Assessment);
        Assert.Empty(observation.Findings);
    }

    [Theory]
    [InlineData("critical", "fail")]
    [InlineData("high", "fail")]
    [InlineData("medium", "concern")]
    [InlineData("low", "concern")]
    [InlineData("info", "pass")]
    public void TheAssessmentFollowsTheSeverityOfTheEvidencedFindings(string severity, string expected)
    {
        var meta = Meta();
        meta["findings"] = new JsonArray(Finding(severity));

        var observation = Project(meta);

        Assert.Equal(expected, observation.Assessment);
        Assert.Equal(expected, Assert.Single(observation.Aspects).Assessment);
        Assert.Equal("quality-studio-review-assessment-v1", ReviewObservationProjection.AssessmentRuleId);
    }

    [Fact]
    public void UnavailableSensorEvidenceIsInconclusiveAndNeverASyntheticPass()
    {
        var meta = Meta();
        meta["security"] = new JsonObject
        {
            ["verdict"] = "unavailable",
            ["combinationRule"] = "security-sensor-agent-v1",
        };

        var observation = Project(meta);

        Assert.Equal(CoreTerms.EvidenceStatus.Unavailable, observation.EvidenceStatus);
        Assert.Equal(CoreTerms.Assessment.Inconclusive, observation.Assessment);
        var policy = Assert.Single(observation.PolicyOutcomes);
        Assert.Equal("security-sensor-agent-v1", policy.PolicyRef);
        Assert.Equal(CoreTerms.Decision.Defer, policy.Decision);
        Assert.Equal("unavailable", policy.LegacyValue);
    }

    [Fact]
    public void SensorAvailabilityBecomesEvidenceStatusInsteadOfAnAspect()
    {
        var meta = Meta();
        meta["security"] = new JsonObject { ["verdict"] = "unavailable", ["combinationRule"] = "security-sensor-agent-v1" };
        meta["aspects"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "sensor-availability",
            ["title"] = "Sensor availability",
            ["grade"] = new JsonObject { ["score"] = 59, ["band"] = "F", ["rationale"] = "Scanner unavailable." },
        });

        var observation = Project(meta);

        Assert.DoesNotContain(observation.Aspects, aspect => aspect.AspectId == "sensor-availability");
        Assert.Equal(CoreTerms.EvidenceStatus.Unavailable, observation.EvidenceStatus);
    }

    [Fact]
    public void ALegacySensorFindingIsAttributedToItsSensorNotToTheAgent()
    {
        var meta = Meta();
        var finding = Finding("high");
        finding["evidence"] = """{"source":"machine-sensor","sensorId":"gitleaks","sensorVersion":"8.24.2","fact":{}}""";
        meta["security"] = new JsonObject { ["verdict"] = "block", ["combinationRule"] = "security-sensor-agent-v1" };
        meta["findings"] = new JsonArray(finding);

        var observation = Project(meta);

        var source = Assert.Single(observation.Findings).Source;
        Assert.Equal(CoreTerms.ProducerKind.DeterministicSensor, source.Kind);
        Assert.Equal("gitleaks", source.SensorId);
        Assert.Equal("8.24.2", source.SensorVersion);
    }

    [Fact]
    public void AnAmbiguousFindingInASensorCarryingDocumentStaysUnknown()
    {
        var meta = Meta();
        meta["security"] = new JsonObject { ["verdict"] = "warn", ["combinationRule"] = "security-sensor-agent-v1" };
        meta["findings"] = new JsonArray(Finding("medium"));

        var observation = Project(meta);

        Assert.Equal(CoreTerms.ProducerKind.Unknown, Assert.Single(observation.Findings).Source.Kind);
    }

    [Fact]
    public void AFindingInAnAgentOnlyDocumentIsAttributedToTheAgent()
    {
        var meta = Meta();
        meta["findings"] = new JsonArray(Finding("medium"));

        var observation = Project(meta);

        var source = Assert.Single(observation.Findings).Source;
        Assert.Equal(CoreTerms.ProducerKind.Agent, source.Kind);
        Assert.Equal("codex", source.ProducerRef);
    }

    [Fact]
    public void LocationsAndEvidenceStringsBecomeTypedEvidenceTheFindingRefersTo()
    {
        var meta = Meta();
        var finding = Finding("medium");
        finding["evidence"] = "The value is never checked.";
        meta["findings"] = new JsonArray(finding);

        var observation = Project(meta);

        Assert.Equal(2, observation.Evidence.Count);
        Assert.Equal(CoreTerms.EvidenceKind.SourceCode, observation.Evidence[0].Kind);
        Assert.Equal("src/Small.cs", observation.Evidence[0].Locator!.Path);
        Assert.Equal(CoreTerms.EvidenceKind.Document, observation.Evidence[1].Kind);
        Assert.Equal(["ev-1", "ev-2"], Assert.Single(observation.Findings).EvidenceRefs);
    }

    [Fact]
    public void FindingIdentityKeepsItsVersionedFingerprintAlgorithm()
    {
        var meta = Meta();
        meta["findings"] = new JsonArray(Finding("medium"));

        var finding = Assert.Single(Project(meta).Findings);

        Assert.Equal("fingerprint-1", finding.OccurrenceFingerprint);
        Assert.Equal(FindingIdentity.Canonicalization, finding.FingerprintAlgorithm);
        Assert.Equal("correctness.risk", finding.RuleRef);
    }

    [Fact]
    public void AProjectedObservationValidatesAndIsIdempotentForOneRun()
    {
        var observation = Project(Meta());

        var repeated = Project(Meta());

        Assert.Equal(observation.ObservationId, repeated.ObservationId);
        Assert.Equal(QualityTaxonomyCatalogue.Core.Reference, observation.Taxonomy);
        Assert.Equal("sha256:" + new string('a', 64), observation.Subject.ManifestHash);
        Assert.Equal("file-code-review", observation.Profile.Id);
        Assert.True(QualityObservationJson.Read(
            QualityObservationJson.Serialize(observation, indented: false)).IsSupported);
    }

    [Fact]
    public void AnUnrecognizedAspectStaysVisibleWithoutBecomingACoreTerm()
    {
        var meta = Meta();
        meta["aspects"] = new JsonArray(new JsonObject
        {
            ["id"] = "resilience",
            ["title"] = "Resilience",
            ["grade"] = new JsonObject { ["score"] = 80, ["band"] = "B", ["rationale"] = "Fine." },
        });

        var aspect = Assert.Single(Project(meta).Aspects);

        Assert.Equal("resilience", aspect.AspectId);
        Assert.False(QualityTaxonomyResolver.Default.ResolveAspect(aspect.AspectId).IsKnown);
    }

    private static QualityObservation Project(JsonObject meta) => ReviewObservationProjection.FromReviewMeta(
        meta,
        new ObservationProducer(CoreTerms.ProducerKind.Agent, "codex", "openai", "model-a", "model-a", "high",
            "2026-07-24", RunId: "quality-1", ReviewRunId: "review-1"),
        new DateTimeOffset(2026, 8, 11, 9, 15, 0, TimeSpan.Zero));

    private static JsonObject Finding(string severity) => new()
    {
        ["id"] = "correctness-1",
        ["aspect"] = "correctness",
        ["severity"] = severity,
        ["title"] = "Risk",
        ["description"] = "A risk.",
        ["recommendation"] = "Fix it.",
        ["locations"] = new JsonArray(new JsonObject
        {
            ["path"] = "src/Small.cs",
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = 1, ["column"] = 1 },
                ["end"] = new JsonObject { ["line"] = 1, ["column"] = 8 },
            },
        }),
        ["fingerprint"] = "fingerprint-1",
        ["ruleId"] = "correctness.risk",
    };

    private static JsonObject Meta() => new()
    {
        ["$schema"] = ReviewMetaDocument.SchemaId,
        ["schemaVersion"] = ReviewMetaDocument.CurrentSchemaVersion,
        ["unit"] = new JsonObject
        {
            ["id"] = "qs-v1/dotnet/file/" + new string('b', 64),
            ["adapter"] = "dotnet",
            ["level"] = "file",
            ["path"] = "src/Small.cs",
            ["displayName"] = "Small.cs",
        },
        ["kind"] = "code",
        ["reviewedHash"] = new JsonObject { ["value"] = new string('a', 64) },
        ["reviewInputs"] = new JsonObject
        {
            ["effectiveHash"] = new JsonObject { ["value"] = new string('c', 64) },
            ["complete"] = true,
            ["prompt"] = new JsonObject
            {
                ["id"] = "file-code-review",
                ["version"] = "1.0.0",
                ["contentHash"] = "sha256:" + new string('d', 64),
            },
        },
        ["grade"] = new JsonObject { ["score"] = 95, ["band"] = "A", ["rationale"] = "Correct and clear." },
        ["summary"] = "Looks sound.",
        ["aspects"] = new JsonArray(new JsonObject
        {
            ["id"] = "correctness",
            ["title"] = "Correctness",
            ["grade"] = new JsonObject { ["score"] = 95, ["band"] = "A", ["rationale"] = "No issue found." },
        }),
        ["findings"] = new JsonArray(),
    };
}
