using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class GuidelineImpactAnalyzerTests
{
    [Fact]
    public async Task Dry_run_reports_a_real_finding_diff_on_a_fixture_repository()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-impact-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".quality", "inputs"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "class Sample { }\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, ".quality", "inputs", "fixture-rule.md"),
            "---\nid: fixture-rule\nenabled: true\nkinds: [code]\nlevels: [file]\npriority: 10\n---\nAllow marker.\n", cancellationToken);
        try
        {
            var analyzer = new GuidelineImpactAnalyzer(new FixtureImpactAgent());
            var result = await analyzer.AnalyzeAsync(root, new GuidelineImpactRequest(
                new GuidelineDraft("fixture-rule", true, 10, ["code"], ["file"], "Flag marker."),
                ["Sample.cs"]), cancellationToken);

            Assert.True(result.Changed);
            Assert.Equal(1, result.AddedCount);
            Assert.Equal("fixture-rule", Assert.Single(Assert.Single(result.Files).Added).RuleId);
            Assert.False(File.Exists(Path.Combine(root, ".quality", "reviews")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FixtureImpactAgent : IReviewAgent
    {
        public string AgentName => "fixture";
        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default)
        {
            var findings = prompt.Contains("Flag marker.", StringComparison.Ordinal)
                ? "[{\"id\":\"fixture-finding\",\"aspect\":\"correctness\",\"severity\":\"medium\",\"ruleId\":\"fixture-rule\",\"title\":\"Marker is flagged\",\"description\":\"The draft policy flags the fixture.\",\"recommendation\":\"Remove the marker.\",\"locations\":[{\"path\":\"Sample.cs\",\"range\":{\"start\":{\"line\":1,\"column\":1},\"end\":{\"line\":1,\"column\":6}}}]}]"
                : "[]";
            var response = "{\"grade\":{\"score\":90,\"band\":\"A\",\"rationale\":\"Fixture.\"},\"summary\":\"Fixture.\",\"aspects\":[{\"id\":\"correctness\",\"title\":\"Correctness\",\"grade\":{\"score\":90,\"band\":\"A\",\"rationale\":\"Fixture.\"}}],\"findings\":" + findings + "}";
            return Task.FromResult(new ReviewAgentResult(Guid.NewGuid().ToString("N"), response));
        }
    }
}
