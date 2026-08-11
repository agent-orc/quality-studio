using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class FindingDecisionTests
{
    private static readonly string Fingerprint = "sha256:" + new string('a', 64);

    [Fact]
    public async Task AssessmentAndResolutionAreIndependentAppendOnlyEventsWithOptimisticConcurrency()
    {
        using var root = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
        var store = new FindingDecisionStore(root.Path, () => now);

        var assessment = await store.AppendAssessmentAsync(
            Fingerprint, "confirmed", "Ada", "Confirmed from source evidence.",
            reviewRunId: "review-1", operationRunId: "operation-1",
            cancellationToken: TestContext.Current.CancellationToken);
        now = now.AddMinutes(1);
        var resolution = await store.AppendResolutionAsync(
            Fingerprint, "planned", "Ada", "Queued for implementation.", taskKey: "QS-70",
            cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = await store.ReadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("confirmed", snapshot.Assessments[Fingerprint].Status);
        Assert.Equal("review-1", snapshot.Assessments[Fingerprint].ReviewRunId);
        Assert.Equal("planned", snapshot.Resolutions[Fingerprint].Status);
        Assert.Equal("QS-70", snapshot.Resolutions[Fingerprint].TaskKey);
        Assert.Single(File.ReadAllLines(Path.Combine(root.Path, FindingDecisionStore.AssessmentRelativePath, "2026-08.jsonl")));
        Assert.Single(File.ReadAllLines(Path.Combine(root.Path, FindingDecisionStore.ResolutionRelativePath, "2026-08.jsonl")));

        await Assert.ThrowsAsync<FindingDecisionConflictException>(() => store.AppendAssessmentAsync(
            Fingerprint, "dismissed", "Grace", "Stale edit.", assessment.AssessedAt.AddSeconds(-1),
            cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FindingDecisionConflictException>(() => store.AppendAssessmentAsync(
            Fingerprint, "dismissed", "Grace", "Client loaded before the first decision.", expectedAssessedAt: null,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("planned", resolution.Status);

        AssertSchemaValid(
            File.ReadAllLines(Path.Combine(root.Path, FindingDecisionStore.AssessmentRelativePath, "2026-08.jsonl"))[0],
            "finding-assessment.v1.schema.json");
        AssertSchemaValid(
            File.ReadAllLines(Path.Combine(root.Path, FindingDecisionStore.ResolutionRelativePath, "2026-08.jsonl"))[0],
            "finding-resolution.v1.schema.json");
    }

    [Theory]
    [InlineData(FindingState.Accepted, "confirmed", "open")]
    [InlineData(FindingState.Waived, "confirmed", "risk-accepted")]
    [InlineData(FindingState.FalsePositive, "dismissed", "obsolete")]
    [InlineData(FindingState.Resolved, "unassessed", "fixed")]
    public async Task LegacyLifecycleProjectsWithoutInventingNewHumanEvidence(
        FindingState state, string assessment, string resolution)
    {
        using var root = new TemporaryDirectory();
        var legacy = new FindingStateRecord(Fingerprint, "finding-a", "src/A.cs", "rule.a", state,
            "Legacy reviewer", "Preserved reason.", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

        var snapshot = await new FindingDecisionStore(root.Path).ReadAsync(
            new Dictionary<string, FindingStateRecord> { [Fingerprint] = legacy },
            TestContext.Current.CancellationToken);

        Assert.Equal(assessment, snapshot.Assessments[Fingerprint].Status);
        Assert.Equal(resolution, snapshot.Resolutions[Fingerprint].Status);
        Assert.True(snapshot.Assessments[Fingerprint].CompatibilityProjection);
        Assert.Empty(Directory.Exists(Path.Combine(root.Path, FindingDecisionStore.AssessmentRelativePath))
            ? Directory.EnumerateFiles(Path.Combine(root.Path, FindingDecisionStore.AssessmentRelativePath))
            : []);
    }

    [Fact]
    public async Task FirstExplicitDecisionReplacesLegacyProjectionUsingItsOriginalTimestamp()
    {
        using var root = new TemporaryDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var identity = new FindingIdentityRecord(Fingerprint, "finding-a", "src/A.cs", "rule.a");
        var legacyStore = new FindingStateStore(root.Path);
        var opened = await legacyStore.MergeReviewAsync([identity], [], "review-agent", cancellationToken);
        var legacy = await legacyStore.SetAsync(Fingerprint, FindingState.Accepted, "Legacy reviewer",
            "Preserved reason.", expectedTimestamp: opened[Fingerprint].Timestamp,
            cancellationToken: cancellationToken);
        var store = new FindingDecisionStore(root.Path, () => legacy.Timestamp.AddMinutes(1));

        var explicitAssessment = await store.AppendAssessmentAsync(
            Fingerprint, "disputed", "Ada", "The current evidence is inconclusive.",
            expectedAssessedAt: legacy.Timestamp, cancellationToken: cancellationToken);
        var explicitResolution = await store.AppendResolutionAsync(
            Fingerprint, "planned", "Ada", "Collect a deterministic reproduction.",
            expectedResolvedAt: legacy.Timestamp, cancellationToken: cancellationToken);

        Assert.False(explicitAssessment.CompatibilityProjection);
        Assert.False(explicitResolution.CompatibilityProjection);
        var snapshot = await store.ReadAsync(await legacyStore.ReadAsync(cancellationToken), cancellationToken);
        Assert.Equal("disputed", snapshot.Assessments[Fingerprint].Status);
        Assert.Equal("planned", snapshot.Resolutions[Fingerprint].Status);
    }

    [Fact]
    public void ExactAndScopedSuppressionsPreviewPersistExpireAndNeverRemoveObservations()
    {
        using var root = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
        var store = new FindingSuppressionStore(root.Path, () => now);
        var candidates = new[]
        {
            new FindingSuppressionCandidate(Fingerprint, "dotnet-nullability", "src/Generated/A.cs", "code", "agent", "A"),
            new FindingSuppressionCandidate("sha256:" + new string('b', 64), "dotnet-nullability", "src/App.cs", "code", "agent", "B"),
        };
        var exact = new FindingSuppressionRule("exact-a", true, new FindingSuppressionMatch(Fingerprint), "suppress",
            "Accepted exact noise.", "Ada", now, now.AddDays(1));
        var exactDocument = store.Add(exact, 0, candidates, confirmBroad: false);
        Assert.Equal(1, exactDocument.Revision);
        Assert.Single(store.Preview(exact, candidates).Matches);

        var scoped = new FindingSuppressionRule("generated-nullability", true,
            new FindingSuppressionMatch(RuleId: "dotnet-nullability", PathPattern: "src/Generated/**", ReviewKinds: ["code"]),
            "suppress", "Generated source is replaced upstream.", "Ada", now);
        var preview = store.Preview(scoped, candidates);
        Assert.True(preview.Broad);
        Assert.Single(preview.Matches);
        Assert.Throws<ArgumentException>(() => store.Add(scoped, 1, candidates, confirmBroad: false));
        store.Add(scoped, 1, candidates, confirmBroad: true);
        Assert.Equal(2, new FindingSuppressionStore(root.Path, () => now).Read().Revision);
        AssertSchemaValid(File.ReadAllText(Path.Combine(root.Path, FindingSuppressionStore.RelativePath)),
            "finding-suppressions.v1.schema.json");

        now = now.AddDays(2);
        Assert.False(FindingSuppressionStore.Matches(exact, candidates[0], now));

        var metadata = Metadata();
        var decisions = new FindingDecisionSnapshot(
            new Dictionary<string, FindingAssessmentEvent>
            {
                [Fingerprint] = new(1, "a", Fingerprint, "confirmed", "Ada", "Valid.", now),
            },
            new Dictionary<string, FindingResolutionEvent>());
        var projected = FindingDecisionProjection.Apply(metadata, decisions, store.Read(), now);
        var findings = projected["findings"]!.AsArray();
        Assert.Equal(2, findings.Count);
        Assert.True(findings[0]!["suppressed"]!.GetValue<bool>());
        Assert.Equal("confirmed", findings[0]!["assessment"]!["status"]!.GetValue<string>());
        Assert.Equal(1, projected["decisionCounts"]!["suppressed"]!.GetValue<int>());
    }

    private static JsonObject Metadata() => new()
    {
        ["kind"] = "code",
        ["grade"] = new JsonObject { ["score"] = 80, ["band"] = "B", ["rationale"] = "Raw." },
        ["findings"] = new JsonArray(
            Finding(Fingerprint, "src/Generated/A.cs"),
            Finding("sha256:" + new string('b', 64), "src/App.cs")),
    };

    private static JsonObject Finding(string fingerprint, string path) => new()
    {
        ["fingerprint"] = fingerprint,
        ["ruleId"] = "dotnet-nullability",
        ["severity"] = "high",
        ["title"] = "Finding",
        ["state"] = "open",
        ["locations"] = new JsonArray(new JsonObject { ["path"] = path }),
    };

    private static void AssertSchemaValid(string json, string schemaName)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "schemas", schemaName)));
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quality-finding-decisions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => TestDirectory.Delete(Path);
    }
}
