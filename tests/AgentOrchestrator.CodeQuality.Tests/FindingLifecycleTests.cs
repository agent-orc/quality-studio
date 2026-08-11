using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class FindingLifecycleTests
{
    [Fact]
    public void Fingerprint_UsesNormalizedPathSnippetAndRule()
    {
        var first = FindingIdentity.Compute("src\\A.cs", FindingIdentity.NormalizeSnippet("  return   value;\r\n"), "correctness.return");
        var second = FindingIdentity.Compute("./src/A.cs", FindingIdentity.NormalizeSnippet("\treturn value;\n"), "correctness.return");

        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_PreservesWaiver_ReopensExpiredState_AndRequiresExplicitResolution()
    {
        var root = Directory.CreateTempSubdirectory("finding-state-");
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var store = new FindingStateStore(root.FullName, () => now);
        var finding = Identity('a');
        try
        {
            var initial = await store.MergeReviewAsync([finding], [], "agent", TestContext.Current.CancellationToken);
            Assert.Equal(FindingState.Open, initial[finding.Fingerprint].State);

            var waived = await store.SetAsync(finding.Fingerprint, FindingState.Waived, "Ada", "Covered by compatibility policy.",
                now.AddDays(1), initial[finding.Fingerprint].Timestamp, TestContext.Current.CancellationToken);
            var unchanged = await store.MergeReviewAsync([finding], [finding], "agent", TestContext.Current.CancellationToken);
            Assert.Equal(waived, unchanged[finding.Fingerprint]);

            now = now.AddDays(2);
            var expired = await store.MergeReviewAsync([finding], [finding], "agent", TestContext.Current.CancellationToken);
            Assert.Equal(FindingState.Open, expired[finding.Fingerprint].State);
            Assert.Null(expired[finding.Fingerprint].ExpiresAt);
            Assert.Contains("expired", expired[finding.Fingerprint].Reason, StringComparison.OrdinalIgnoreCase);

            var omitted = await store.MergeReviewAsync([], [finding], "different-model", TestContext.Current.CancellationToken);
            Assert.Equal(FindingState.Open, omitted[finding.Fingerprint].State);
            var basis = "observation-sha256:" + new string('d', 64);
            var resolved = await store.ResolveAsync(
                finding.Fingerprint,
                "policy",
                "reconciliation-service",
                "The versioned reconciliation policy confirmed removal.",
                "quality-studio-reconciliation@1",
                [basis],
                omitted[finding.Fingerprint].Timestamp,
                TestContext.Current.CancellationToken);
            Assert.Equal(FindingState.Resolved, resolved.State);
            Assert.True(File.Exists(Path.Combine(root.FullName, FindingStateStore.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(File.Exists(Path.Combine(root.FullName, IssueLifecycleStore.RelativePath.Replace('/', Path.DirectorySeparatorChar))));

            var reopened = await store.MergeReviewAsync([finding], [], "model-a",
                TestContext.Current.CancellationToken);
            Assert.Equal(FindingState.Open, reopened[finding.Fingerprint].State);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task LifecycleEventsAreSchemaValidAndReplayIsIdempotent()
    {
        var root = Directory.CreateTempSubdirectory("finding-lifecycle-ledger-");
        try
        {
            var identity = Identity('d').WithV2Identity();
            var lifecycleEvent = IssueLifecycleEvent.Create(
                identity,
                "open",
                "agent",
                "model-a",
                "First observed by review.",
                new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero));
            var store = new IssueLifecycleStore(root.FullName);

            Assert.True(await store.AppendAsync(lifecycleEvent, TestContext.Current.CancellationToken));
            Assert.False(await store.AppendAsync(lifecycleEvent, TestContext.Current.CancellationToken));
            var stored = Assert.Single(await store.ReadEventsAsync(TestContext.Current.CancellationToken));
            var schema = JsonSchema.FromText(await File.ReadAllTextAsync(Path.Combine(
                RepositoryTestContext.FindRepositoryRoot(), "schemas",
                "quality-issue-lifecycle-event.v1.schema.json"), TestContext.Current.CancellationToken));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(
                store.Path, TestContext.Current.CancellationToken));
            var validation = schema.Evaluate(json.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });

            Assert.True(validation.IsValid, validation.ToString());
            Assert.Equal(lifecycleEvent.EventId, stored.EventId);
            Assert.Equal(identity.Fingerprint, Assert.Single(stored.LegacyFingerprints));
            Assert.Single(await File.ReadAllLinesAsync(store.Path, TestContext.Current.CancellationToken));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task LegacyV1SnapshotRemainsReadableAndImportsAcceptedAlias()
    {
        var root = Directory.CreateTempSubdirectory("finding-lifecycle-import-");
        try
        {
            var finding = Identity('e');
            var statePath = Path.Combine(root.FullName,
                FindingStateStore.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            await File.WriteAllTextAsync(statePath, $$"""
                {
                  "schemaVersion": 1,
                  "revision": 7,
                  "findings": [{
                    "fingerprint": "{{finding.Fingerprint}}",
                    "findingId": "{{finding.Id}}",
                    "path": "{{finding.Path}}",
                    "ruleId": "{{finding.RuleId}}",
                    "state": "accepted",
                    "author": "Ada",
                    "reason": "Legacy accepted risk.",
                    "timestamp": "2026-08-10T10:00:00+00:00"
                  }]
                }
                """, TestContext.Current.CancellationToken);

            var store = new FindingStateStore(root.FullName);
            var projected = await store.ReadAsync(TestContext.Current.CancellationToken);
            var lifecycle = Assert.Single(await new IssueLifecycleStore(root.FullName)
                .ReadEventsAsync(TestContext.Current.CancellationToken));

            Assert.Equal(FindingState.Accepted, projected[finding.Fingerprint].State);
            Assert.Equal("accepted-risk", lifecycle.State);
            Assert.Equal("imported", lifecycle.ActorKind);
            Assert.Equal(finding.Fingerprint, Assert.Single(lifecycle.LegacyFingerprints));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ConcurrentStateWrites_RejectTheStaleWriterWithoutLosingTheWinner()
    {
        var root = Directory.CreateTempSubdirectory("finding-state-conflict-");
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var finding = Identity('b');
        try
        {
            var store = new FindingStateStore(root.FullName, () => now);
            var initial = await store.MergeReviewAsync([finding], [], "agent", TestContext.Current.CancellationToken);
            var expected = initial[finding.Fingerprint].Timestamp;
            now = now.AddMinutes(1);

            async Task<bool> TryWriteAsync(FindingState state, string reason)
            {
                try
                {
                    await new FindingStateStore(root.FullName, () => now).SetAsync(
                        finding.Fingerprint, state, "Reviewer", reason, expectedTimestamp: expected,
                        cancellationToken: TestContext.Current.CancellationToken);
                    return true;
                }
                catch (FindingStateConflictException)
                {
                    return false;
                }
            }

            var results = await Task.WhenAll(
                TryWriteAsync(FindingState.Accepted, "Accepted risk."),
                TryWriteAsync(FindingState.FalsePositive, "Analyzer is not applicable."));

            Assert.Single(results, result => result);
            Assert.Single(results, result => !result);
            var final = (await store.ReadAsync(TestContext.Current.CancellationToken))[finding.Fingerprint];
            Assert.Contains(final.State, new[] { FindingState.Accepted, FindingState.FalsePositive });
            Assert.Equal(2, JsonNode.Parse(await File.ReadAllTextAsync(store.StatePath, TestContext.Current.CancellationToken))!["revision"]!.GetValue<int>());
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ReReview_KeepsComputedIdAndHumanState()
    {
        var root = Directory.CreateTempSubdirectory("finding-rereview-");
        Directory.CreateDirectory(Path.Combine(root.FullName, "src"));
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "src", "Small.cs"),
            "internal static class Small { }\n", TestContext.Current.CancellationToken);
        try
        {
            var runner = new ReviewRunner(new LifecycleAgent());
            var first = await runner.ReviewAsync(new ReviewRequest("src/Small.cs", RepositoryRoot: root.FullName),
                TestContext.Current.CancellationToken);
            var firstFinding = ReadFinding(first.MetaPath);
            var store = new FindingStateStore(root.FullName);
            var states = await store.ReadAsync(TestContext.Current.CancellationToken);
            await store.SetAsync(firstFinding.Fingerprint, FindingState.Waived, "Ada", "Temporary migration waiver.",
                DateTimeOffset.UtcNow.AddDays(7), states[firstFinding.Fingerprint].Timestamp, TestContext.Current.CancellationToken);

            var second = await runner.ReviewAsync(new ReviewRequest("src/Small.cs", RepositoryRoot: root.FullName),
                TestContext.Current.CancellationToken);
            var secondFinding = ReadFinding(second.MetaPath);
            var finalState = (await store.ReadAsync(TestContext.Current.CancellationToken))[secondFinding.Fingerprint];

            Assert.Equal(firstFinding, secondFinding);
            Assert.Equal(FindingState.Waived, finalState.State);
            Assert.Equal("Ada", finalState.Author);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Projection_LeavesExcludedFindingsVisibleButRemovesThemFromGrade()
    {
        var finding = Identity('c');
        var metadata = JsonNode.Parse(ReviewResponseParserTests.ValidResponse.Replace(
            "\"findings\": []", "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]", StringComparison.Ordinal))!.AsObject();
        FindingIdentity.Assign(metadata, new Dictionary<string, string> { ["src/Small.cs"] = "internal static class Small { }\n" });
        var state = new FindingStateRecord(finding.Fingerprint, finding.Id, finding.Path, finding.RuleId,
            FindingState.FalsePositive, "Ada", "Not applicable.", DateTimeOffset.UtcNow);
        var actualFingerprint = metadata["findings"]![0]!["fingerprint"]!.GetValue<string>();
        state = state with { Fingerprint = actualFingerprint, FindingId = metadata["findings"]![0]!["id"]!.GetValue<string>() };

        var projected = FindingStateProjection.Apply(metadata, new Dictionary<string, FindingStateRecord> { [actualFingerprint] = state });

        Assert.Single(projected["findings"]!.AsArray());
        Assert.Equal("false-positive", projected["findings"]![0]!["state"]!.GetValue<string>());
        Assert.Equal(1, projected["findingCounts"]!["falsePositive"]!.GetValue<int>());
        Assert.Equal(100, projected["grade"]!["score"]!.GetValue<int>());
    }

    private static FindingIdentityRecord Identity(char value)
    {
        var hash = new string(value, 64);
        return new($"sha256:{hash}", $"finding-{hash}", "src/A.cs", "correctness.test");
    }

    private static (string Id, string Fingerprint) ReadFinding(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var finding = json.RootElement.GetProperty("findings")[0];
        return (finding.GetProperty("id").GetString()!, finding.GetProperty("fingerprint").GetString()!);
    }

    private sealed class LifecycleAgent : IReviewAgent
    {
        public string AgentName => "lifecycle-agent";
        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult(
                "run-" + Guid.NewGuid().ToString("N"),
                ReviewResponseParserTests.ValidResponse.Replace(
                    "\"findings\": []", "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]", StringComparison.Ordinal),
                new TokenUsage(1, 1, 0, 0, 1), Model));
    }
}
