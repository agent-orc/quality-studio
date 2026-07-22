using System.Text.Json;
using System.Text.Json.Nodes;

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
    public async Task Merge_PreservesWaiver_ReopensExpiredState_AndRecordsResolution()
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

            var resolved = await store.MergeReviewAsync([], [finding], "agent", TestContext.Current.CancellationToken);
            Assert.Equal(FindingState.Resolved, resolved[finding.Fingerprint].State);
            Assert.True(File.Exists(Path.Combine(root.FullName, FindingStateStore.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
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
