using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", "ToolBound")]
public sealed class ReviewRunnerToolBoundTests
{
    [Fact]
    public async Task ReviewAsync_CapturesSourceSpanEvidenceAndExecutionProvenanceInV3Metadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "quality-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var file = Path.Combine(root, "src", "Small.cs");
        await File.WriteAllTextAsync(file, "internal static class Small { }\n", cancellationToken);
        try
        {
            await GitTestRepository.InitializeAsync(root, cancellationToken);
            await GitTestRepository.RunAsync(root, cancellationToken, "add", ".");
            await GitTestRepository.RunAsync(root, cancellationToken, "commit", "--quiet", "-m", "seed");

            var agent = new ReviewRunnerTests.FakeAgent(
                response: ReviewResponseParserTests.ValidResponse.Replace(
                    "\"findings\": []", "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]", StringComparison.Ordinal),
                thinkingLevel: "medium");

            var result = await new ReviewRunner(agent).ReviewAsync(
                new ReviewRequest("src/Small.cs", "code", RepositoryRoot: root), cancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken));
            var json = document.RootElement;
            Assert.Equal(3, json.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("deterministic", json.GetProperty("reviewer").GetProperty("requestedModel").GetString());
            Assert.Equal("medium", json.GetProperty("reviewer").GetProperty("requestedThinkingLevel").GetString());
            Assert.Matches("^git:[a-f0-9]{40}(-dirty)?$", json.GetProperty("sourceRevision").GetString());

            var finding = json.GetProperty("findings")[0];
            var anchor = Assert.Single(finding.GetProperty("anchors").EnumerateArray());
            Assert.Equal("primary", anchor.GetProperty("role").GetString());
            Assert.Equal("src/Small.cs", anchor.GetProperty("path").GetString());
            var excerpt = anchor.GetProperty("capturedExcerpt");
            Assert.Equal("internal", excerpt.GetProperty("text").GetString());
            Assert.Matches("^sha256:[a-f0-9]{64}$", excerpt.GetProperty("contentHash").GetString());
            Assert.Matches("^sha256:[a-f0-9]{64}$", excerpt.GetProperty("excerptHash").GetString());

            var evidenceItem = Assert.Single(finding.GetProperty("evidenceItems").EnumerateArray());
            Assert.Equal("sourceSpan", evidenceItem.GetProperty("class").GetString());
            Assert.Equal("observed", evidenceItem.GetProperty("status").GetString());
            Assert.Equal("primary", evidenceItem.GetProperty("anchorId").GetString());
            Assert.Equal("unknown", finding.GetProperty("reproduction").GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
