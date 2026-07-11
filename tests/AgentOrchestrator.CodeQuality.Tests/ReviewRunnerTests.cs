using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewPromptBuilderTests
{
    [Theory]
    [InlineData("code")]
    [InlineData("security")]
    [InlineData("performance")]
    public void Build_UsesVersionedKindTemplateAndInsertionPoints(string kind)
    {
        var prompt = new ReviewPromptBuilder().Build("src/Thing.cs", kind, "No globals.", "Project rule.", "class Thing { }");

        Assert.Contains($"File {kind} review v1", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`src/Thing.cs`", prompt, StringComparison.Ordinal);
        Assert.Contains("No globals.", prompt, StringComparison.Ordinal);
        Assert.Contains("Project rule.", prompt, StringComparison.Ordinal);
        Assert.Contains("class Thing { }", prompt, StringComparison.Ordinal);
        Assert.Contains("Strict output format", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", prompt, StringComparison.Ordinal);
    }
}

public sealed class ReviewResponseParserTests
{
    [Fact]
    public void Parse_AcceptsSingleFencedContract()
    {
        var parsed = new ReviewResponseParser().Parse("before```json\n" + ValidResponse + "\n```after");

        Assert.Equal("Looks sound.", parsed["summary"]!.GetValue<string>());
        Assert.Empty(parsed["findings"]!.AsArray());
    }

    [Fact]
    public void Parse_RejectsFindingWithoutFileLocation()
    {
        var response = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [{\"id\":\"bad-id\",\"aspect\":\"correctness\",\"severity\":\"high\",\"title\":\"Bad\",\"description\":\"Bad.\",\"recommendation\":\"Fix.\",\"locations\":[]}]",
            StringComparison.Ordinal);

        var exception = Assert.Throws<ReviewResponseException>(() => new ReviewResponseParser().Parse(response));
        Assert.Contains("location", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal const string ValidResponse = """
        {
          "grade": { "score": 95, "band": "A", "rationale": "Correct and clear." },
          "summary": "Looks sound.",
          "aspects": [
            { "id": "correctness", "title": "Correctness", "grade": { "score": 95, "band": "A", "rationale": "No issue found." } }
          ],
          "findings": []
        }
        """;
}

public sealed class ReviewRunnerTests
{
    [Fact]
    public async Task ReviewAsync_WritesFreshQs3Metadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "quality-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var file = Path.Combine(root, "src", "Small.cs");
        await File.WriteAllTextAsync(file, "internal static class Small { }\n", cancellationToken);
        try
        {
            var result = await new ReviewRunner(new FakeAgent()).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root), cancellationToken);

            Assert.True(File.Exists(result.MetaPath));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken));
            var json = document.RootElement;
            Assert.Equal("code", json.GetProperty("kind").GetString());
            Assert.Equal("file", json.GetProperty("unit").GetProperty("level").GetString());
            Assert.Equal("src/Small.cs", json.GetProperty("unit").GetProperty("path").GetString());
            Assert.Equal(result.ReviewedHash, json.GetProperty("reviewedHash").GetProperty("value").GetString());
            Assert.StartsWith(Path.Combine(root, "src", ".quality", "reviews", "files"), result.MetaPath, StringComparison.Ordinal);

            // Independently verify the stored manifest against the current file bytes.
            var currentContentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(file, cancellationToken);
            var currentManifest = ReviewSubjectHasher.ComputeManifestHash(
                json.GetProperty("unit").GetProperty("id").GetString()!,
                [new SubjectInputHash("src/Small.cs", "file", currentContentHash)]);
            Assert.Equal(result.ReviewedHash, currentManifest);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeAgent : IReviewAgent
    {
        public string AgentName => "test-agent";

        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult("run-test", $"```json\n{ReviewResponseParserTests.ValidResponse}\n```"));
    }
}

public sealed class LiveReviewIntegrationTests
{
    [Fact]
    public async Task CodexCanReviewSmallFile_WhenExplicitlyEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("QUALITY_RUN_LIVE_REVIEW"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("Set QUALITY_RUN_LIVE_REVIEW=1 to run the installed Codex CLI integration.");
        }

        var root = Directory.GetCurrentDirectory();
        var result = await new ReviewRunner().ReviewAsync(new ReviewRequest(
            "src/AgentOrchestrator.CodeQuality/StalenessState.cs",
            RepositoryRoot: root), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(result.MetaPath));
    }
}
