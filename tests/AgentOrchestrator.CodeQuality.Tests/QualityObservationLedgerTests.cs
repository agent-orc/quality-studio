using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationLedgerTests
{
    [Fact]
    public async Task ForcedRunsWithDifferentModelsKeepTwoObservationsAndOneCurrentSidecar()
    {
        var root = Directory.CreateTempSubdirectory("quality-observation-dual-write-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "src"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "src", "Small.cs"),
                "internal static class Small { }\n", TestContext.Current.CancellationToken);
            var first = await new ReviewRunner(new RoutedAgent("run-model-a", "requested-a", "effective-a"))
                .ReviewAsync(Request(root.FullName), TestContext.Current.CancellationToken);
            var second = await new ReviewRunner(new RoutedAgent("run-model-b", "requested-b", "effective-b"))
                .ReviewAsync(Request(root.FullName), TestContext.Current.CancellationToken);

            Assert.Equal(first.MetaPath, second.MetaPath);
            Assert.NotNull(first.QualityObservation);
            Assert.NotNull(second.QualityObservation);
            Assert.NotEqual(first.QualityObservation.ObservationId, second.QualityObservation.ObservationId);
            var observations = await QualityObservationLedger.QueryAsync(
                root.FullName, TestContext.Current.CancellationToken);
            Assert.Equal(2, observations.Count);
            Assert.Equal(["effective-a", "effective-b"],
                observations.Select(observation => observation.Producer.EffectiveModel).Order().ToArray());
            Assert.All(observations, observation =>
            {
                Assert.Equal("test-provider", observation.Producer.Provider);
                Assert.Equal("high", observation.Producer.ThinkingLevel);
                Assert.Equal("route-policy-test@1", observation.Producer.RoutePolicyVersion);
                Assert.Equal(observation.Producer.RunId, observation.Producer.UsageRunId);
            });

            using var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(
                second.MetaPath, TestContext.Current.CancellationToken));
            Assert.Equal("effective-b",
                sidecar.RootElement.GetProperty("reviewer").GetProperty("model").GetString());
            Assert.False(sidecar.RootElement.GetProperty("reviewer").TryGetProperty("provider", out _));

            var usage = await UsageLedger.QueryAsync(root.FullName,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, usage.Runs);
            foreach (var observation in observations)
            {
                var entry = Assert.Single(usage.Recent,
                    candidate => candidate.RunId == observation.Producer.UsageRunId);
                Assert.Equal(observation.Producer.Provider, entry.Provider);
                Assert.Equal(observation.Producer.RequestedModel, entry.RequestedModel);
                Assert.Equal(observation.Producer.EffectiveModel, entry.EffectiveModel);
                Assert.Equal(3, entry.SchemaVersion);
            }
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ReplayIsIdempotentAndMalformedLinesDoNotHideValidObservations()
    {
        var root = Directory.CreateTempSubdirectory("quality-observation-replay-");
        try
        {
            var observation = CreateObservation(new DateTimeOffset(2026, 8, 11, 9, 30, 0, TimeSpan.Zero));

            Assert.True(await QualityObservationLedger.AppendAsync(
                root.FullName, observation, TestContext.Current.CancellationToken));
            Assert.False(await QualityObservationLedger.AppendAsync(
                root.FullName, observation, TestContext.Current.CancellationToken));
            var path = QualityObservationLedger.GetLedgerPath(root.FullName, observation.ObservedAt);
            await File.AppendAllTextAsync(path, "{not-json}\n", TestContext.Current.CancellationToken);
            var second = observation with
            {
                ObservationId = QualityObservationLedger.CreateObservationId(
                    "run-replay-2", observation.Subject.UnitId, "code",
                    observation.Subject.ManifestHash, observation.Subject.InputHash,
                    observation.Taxonomy.Digest),
                Producer = observation.Producer with { RunId = "run-replay-2", UsageRunId = "run-replay-2" },
            };
            Assert.True(await QualityObservationLedger.AppendAsync(
                root.FullName, second, TestContext.Current.CancellationToken));

            var observations = await QualityObservationLedger.QueryAsync(
                root.FullName, TestContext.Current.CancellationToken);
            Assert.Equal(2, observations.Count);
            Assert.Equal(3, (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken)).Length);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ProjectionFailureLeavesRecoverableObservationAndNoCurrentSidecar()
    {
        var root = Directory.CreateTempSubdirectory("quality-observation-recovery-");
        try
        {
            var source = Path.Combine(root.FullName, "src");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "Small.cs"),
                "internal static class Small { }\n", TestContext.Current.CancellationToken);
            var reviews = Path.Combine(source, ".quality", "reviews");
            Directory.CreateDirectory(reviews);
            await File.WriteAllTextAsync(Path.Combine(reviews, "files"),
                "projection path intentionally blocked", TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                new ReviewRunner(new RoutedAgent("run-before-projection", "requested", "effective"))
                    .ReviewAsync(Request(root.FullName), TestContext.Current.CancellationToken));

            var observation = Assert.Single(await QualityObservationLedger.QueryAsync(
                root.FullName, TestContext.Current.CancellationToken));
            Assert.Equal("run-before-projection", observation.Producer.RunId);
            Assert.False(Directory.Exists(Path.Combine(reviews, "files")));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task V3UsageEntryConformsToRouteProvenanceSchema()
    {
        var entry = new ReviewUsageEntry(
            "run-schema",
            new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero),
            "effective-model",
            "codex",
            new TokenUsage(10, 2, 3, 1, 100),
            "code",
            "file",
            "src/Small.cs",
            "review-schema",
            3,
            "openai",
            "requested-model",
            "effective-model",
            "high",
            "route-policy@1",
            "observation-sha256:" + new string('a', 64));
        var root = Directory.CreateTempSubdirectory("quality-usage-v3-");
        try
        {
            await UsageLedger.AppendAsync(root.FullName, entry, TestContext.Current.CancellationToken);
            var line = Assert.Single(await File.ReadAllLinesAsync(
                UsageLedger.GetLedgerPath(root.FullName, entry.Timestamp),
                TestContext.Current.CancellationToken));
            using var json = JsonDocument.Parse(line);
            var schema = JsonSchema.FromText(await File.ReadAllTextAsync(Path.Combine(
                RepositoryTestContext.FindRepositoryRoot(), "schemas", "usage-ledger.v3.schema.json"),
                TestContext.Current.CancellationToken));

            var result = schema.Evaluate(json.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });

            Assert.True(result.IsValid, result.ToString());
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task FindingLocationsBecomeResolvableEvidenceAndCarryV2IssueIdentity()
    {
        var root = Directory.CreateTempSubdirectory("quality-observation-evidence-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "src"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "src", "Small.cs"),
                "internal static class Small { }\n", TestContext.Current.CancellationToken);
            var response = ReviewResponseParserTests.ValidResponse.Replace(
                "\"findings\": []",
                "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]",
                StringComparison.Ordinal);

            var result = await new ReviewRunner(new RoutedAgent(
                    "run-evidence", "requested", "effective", response))
                .ReviewAsync(Request(root.FullName), TestContext.Current.CancellationToken);

            var observation = Assert.IsType<QualityObservationDocument>(result.QualityObservation);
            var finding = Assert.Single(observation.Findings);
            Assert.StartsWith("issue-sha256:", finding.IssueId, StringComparison.Ordinal);
            Assert.Equal(FindingIdentity.OccurrenceCanonicalization, finding.FingerprintAlgorithm);
            Assert.Matches("^sha256:[a-f0-9]{64}$", finding.OccurrenceFingerprint);
            Assert.Single(finding.LegacyFingerprints);
            Assert.Equal("agent", finding.Source.Kind);
            var evidenceRef = Assert.Single(finding.EvidenceRefs);
            var evidence = Assert.Single(observation.Evidence, item => item.Id == evidenceRef);
            Assert.Equal("source-code", evidence.Kind);
            Assert.Equal("src/Small.cs", evidence.Locator!.Path);
            Assert.Matches("^sha256:[a-f0-9]{64}$", evidence.ContentHash!);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("deterministic-sensor")]
    [InlineData("human")]
    public void ProducerKindsRoundTripExplicitly(string producerKind)
    {
        var original = CreateObservation(new DateTimeOffset(2026, 8, 11, 11, 0, 0, TimeSpan.Zero));
        var fingerprint = "sha256:" + new string('a', 64);
        var document = original with
        {
            Findings =
            [
                new QualityObservationFinding(
                    "finding-source-test",
                    fingerprint,
                    FindingIdentity.OccurrenceCanonicalization,
                    [fingerprint],
                    "source.test",
                    "code.correctness",
                    "medium",
                    "Source test",
                    "Producer kind must remain explicit.",
                    "Preserve the producer kind.",
                    [],
                    new QualityFindingSource(producerKind, "producer-test"),
                    "issue-sha256:" + new string('b', 64)),
            ],
        };

        var roundTrip = QualityObservationJson.Deserialize(QualityObservationJson.Serialize(document));

        Assert.Equal(producerKind, Assert.Single(roundTrip.Findings).Source.Kind);
    }

    private static ReviewRequest Request(string root) => new(
        "src/Small.cs",
        RepositoryRoot: root,
        ReviewRunId: "review-dual-write",
        Provider: "test-provider",
        ThinkingLevel: "high",
        RoutePolicyVersion: "route-policy-test@1",
        ObservationWriteEnabled: true);

    private static QualityObservationDocument CreateObservation(DateTimeOffset observedAt)
    {
        var taxonomy = CoreQualityCatalogue.Instance.Reference;
        var subject = new QualityObservationSubject(
            "qs-v1/dotnet/file/test",
            "file",
            "sha256:" + new string('b', 64),
            "sha256:" + new string('c', 64),
            "src/Small.cs");
        return new QualityObservationDocument(
            QualityObservationDocument.SchemaId,
            1,
            QualityObservationLedger.CreateObservationId(
                "run-replay", subject.UnitId, "code", subject.ManifestHash, subject.InputHash,
                taxonomy.Digest),
            observedAt,
            taxonomy,
            [],
            subject,
            new QualityObservationProfile(
                "file-code-review", "1.0.0", "sha256:" + new string('d', 64),
                subject.InputHash),
            new QualityObservationProducer(
                "agent", "test-agent", "test-provider", "requested", "effective", "high",
                "route-policy@1", "run-replay", UsageRunId: "run-replay"),
            "available",
            [],
            [new QualityObservationAspect(
                "code.correctness", "assessment", "pass", "No defect found.",
                new QualityObservationGrade(95, "A"))],
            "pass",
            [],
            "complete");
    }

    private sealed class RoutedAgent(
        string runId,
        string requestedModel,
        string effectiveModel,
        string? response = null) : IReviewAgent
    {
        public string AgentName => "test-agent";
        public string? Model => requestedModel;
        public string? Provider => "test-provider";
        public string? ThinkingLevel => "high";
        public string? RoutePolicyVersion => "route-policy-test@1";

        public Task<ReviewAgentResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken = default) => Task.FromResult(new ReviewAgentResult(
            runId,
            response ?? ReviewResponseParserTests.ValidResponse,
            new TokenUsage(100, 20, 10, 5, 500),
            effectiveModel,
            Provider,
            requestedModel,
            ThinkingLevel,
            RoutePolicyVersion));
    }
}
