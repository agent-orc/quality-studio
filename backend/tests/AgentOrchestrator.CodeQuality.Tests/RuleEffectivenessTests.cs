namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RuleEffectivenessTests
{
    [Fact]
    public void AggregatesDistinctHitsCurrentDispositionAndAttributedCost()
    {
        var day = DateTimeOffset.Parse("2026-09-28T10:00:00Z");
        FindingStateRecord Finding(char key, string rule, FindingState state, int offset = 0) =>
            new("sha256:" + new string(key, 64), "id-" + key, "src/A.cs", rule, state,
                "Ada", "reviewed", day.AddDays(offset));
        ReviewUsageEntry Usage(string run, decimal? cost, string[]? rules, int offset = 0) =>
            new(run, day.AddDays(offset), "test-model", "codex", new TokenUsage(1, 1, 0, 0, 1),
                "code", "file", "src/A.cs", Cost: new UsageCost(cost, "USD", cost is null ? "unknownModel" : "resolved"),
                RuleIds: rules);

        var report = RuleEffectiveness.Aggregate(
            [Finding('a', "QS-A", FindingState.Accepted), Finding('b', "QS-A", FindingState.FalsePositive, 1),
             Finding('c', "QS-B", FindingState.Waived), Finding('d', "QS-B", FindingState.Resolved),
             Finding('a', "QS-A", FindingState.Accepted)],
            [Usage("one", 6m, ["QS-A", "QS-B"]), Usage("two", 2m, ["QS-A"], 1),
             Usage("old", 4m, null), Usage("unknown", null, ["QS-B"])], day.AddDays(2));

        var a = Assert.Single(report.Rules, row => row.RuleId == "QS-A");
        Assert.Equal(2, a.Hits);
        Assert.Equal(1, a.Accepted);
        Assert.Equal(1, a.FalsePositives);
        Assert.Equal(5m, a.Cost);
        Assert.Equal(2, a.Trend.Count);
        Assert.Equal(3m, a.Trend[0].Cost);
        var b = Assert.Single(report.Rules, row => row.RuleId == "QS-B");
        Assert.Equal(1, b.Dismissed);
        Assert.Equal(1, b.Resolved);
        Assert.Equal(3m, b.Cost);
        Assert.Equal(1, b.UnpricedRuns);
        Assert.Equal(4m, report.UnattributedCost);
        Assert.Equal(1, report.UnpricedRuns);
        Assert.Equal("QS-A", report.WorstOffenders[0].RuleId);
    }

    [Fact]
    public async Task ScopedSuppressionMatchesRuleAndPathBoundaryUntilExpiry()
    {
        var root = Directory.CreateTempSubdirectory("scoped-suppression-");
        var now = DateTimeOffset.Parse("2026-09-28T10:00:00Z");
        try
        {
            var store = new FindingSuppressionStore(root.FullName, () => now);
            var saved = await store.AddScopedAsync("QS-A", "src/app", "Ada", "Generated code.", now.AddDays(1),
                cancellationToken: TestContext.Current.CancellationToken);
            var active = Assert.Single(FindingSuppressionStore.ActiveByFingerprint(saved, now).Values);
            Assert.True(active.Matches("sha256:any", "QS-A", "src/app/file.cs"));
            Assert.True(active.Matches(null, "QS-A", "src/app"));
            Assert.False(active.Matches(null, "QS-B", "src/app/file.cs"));
            Assert.False(active.Matches(null, "QS-A", "src/application/file.cs"));
            var metadata = System.Text.Json.Nodes.JsonNode.Parse(ReviewResponseParserTests.ValidResponse.Replace(
                "\"findings\": []", "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]", StringComparison.Ordinal))!.AsObject();
            FindingIdentity.Assign(metadata, new Dictionary<string, string> { ["src/Small.cs"] = "internal static class Small { }\n" });
            var observedRule = metadata["findings"]![0]!["ruleId"]!.GetValue<string>();
            var observedPath = metadata["findings"]![0]!["locations"]![0]!["path"]!.GetValue<string>();
            var matching = await store.AddScopedAsync(observedRule, observedPath, "Ada", "Generated code.",
                cancellationToken: TestContext.Current.CancellationToken);
            var projected = FindingStateProjection.Apply(metadata, new Dictionary<string, FindingStateRecord>(),
                FindingSuppressionStore.ActiveByFingerprint(matching, now));
            Assert.Equal(1, projected["suppressedFindingCount"]!.GetValue<int>());
            var repositoryWide = await store.AddScopedAsync("QS-C", ".", "Ada", "Repository exception.",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains(FindingSuppressionStore.ActiveByFingerprint(repositoryWide, now).Values,
                rule => rule.Match.RuleId == "QS-C" && rule.Matches(null, "QS-C", "other/place.cs"));
            Assert.Empty(FindingSuppressionStore.ActiveByFingerprint(saved, now.AddDays(2)));
            var reloaded = await new FindingSuppressionStore(root.FullName).ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(repositoryWide.Rules, reloaded.Rules);
        }
        finally { root.Delete(true); }
    }
}
