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

    [Fact]
    public void Build_UsesExplicitDefaultsForGuidelineInsertionPoints()
    {
        var prompt = new ReviewPromptBuilder().Build("Thing.cs", "code");

        Assert.Equal(2, prompt.Split("(none supplied)", StringSplitOptions.None).Length - 1);
        Assert.Contains("(content not supplied)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsUnsupportedKind()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ReviewPromptBuilder().Build("Thing.cs", "accessibility"));

        Assert.Contains("Unsupported review kind", exception.Message, StringComparison.Ordinal);
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

    [Fact]
    public void Parse_AcceptsFindingWithStrictLocationContract()
    {
        var response = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [" + ValidFinding + "]",
            StringComparison.Ordinal);

        var finding = new ReviewResponseParser().Parse(response)["findings"]!.AsArray()[0]!.AsObject();

        Assert.Equal("medium", finding["severity"]!.GetValue<string>());
        Assert.Equal(3, finding["locations"]!.AsArray()[0]!["range"]!["start"]!["line"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("```json\n{}\n```\n```json\n{}\n```")]
    public void Parse_RejectsMissingOrAmbiguousJson(string response)
    {
        Assert.Throws<ReviewResponseException>(() => new ReviewResponseParser().Parse(response));
    }

    [Theory]
    [InlineData("101", "A")]
    [InlineData("95", "B")]
    public void Parse_RejectsInvalidGrade(string score, string band)
    {
        var response = ValidResponse
            .Replace("95, \"band\": \"A\"", $"{score}, \"band\": \"{band}\"", StringComparison.Ordinal);

        Assert.Throws<ReviewResponseException>(() => new ReviewResponseParser().Parse(response));
    }

    [Fact]
    public void Parse_RejectsUnknownAspectAndSeverity()
    {
        var unknownAspect = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [" + ValidFinding.Replace("\"correctness\"", "\"security\"", StringComparison.Ordinal) + "]",
            StringComparison.Ordinal);
        var invalidSeverity = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [" + ValidFinding.Replace("\"medium\"", "\"urgent\"", StringComparison.Ordinal) + "]",
            StringComparison.Ordinal);

        Assert.Contains("unknown aspect", Assert.Throws<ReviewResponseException>(
            () => new ReviewResponseParser().Parse(unknownAspect)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("severity", Assert.Throws<ReviewResponseException>(
            () => new ReviewResponseParser().Parse(invalidSeverity)).Message, StringComparison.OrdinalIgnoreCase);
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

    internal const string ValidFinding = """
        {"id":"correctness-1","aspect":"correctness","severity":"medium","title":"Risk","description":"A risk.","recommendation":"Fix it.","locations":[{"path":"src/Small.cs","range":{"start":{"line":3,"column":1},"end":{"line":3,"column":4}}}]}
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

    [Fact]
    public async Task ReviewAsync_PropagatesAgentAndReviewInputsIntoMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithReviewFileAsync(async (root, file) =>
        {
            var inputDirectory = Path.Combine(root, ".quality", "inputs");
            Directory.CreateDirectory(inputDirectory);
            await File.WriteAllTextAsync(Path.Combine(inputDirectory, "security.md"),
                "---\nid: secure-boundaries\nkinds: [security]\nlevels: [file]\npriority: 50\n---\nTreat external data as untrusted.\n", cancellationToken);
            var agent = new FakeAgent(response: ReviewResponseParserTests.ValidResponse.Replace(
                "\"findings\": []", "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]", StringComparison.Ordinal));

            var result = await new ReviewRunner(agent).ReviewAsync(new ReviewRequest(
                "src/Small.cs", "security", GlobalGuidelines: "Global rule.", ProjectGuidelines: "Project rule.", RepositoryRoot: root),
                cancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken));
            var json = document.RootElement;
            Assert.Equal("security", json.GetProperty("kind").GetString());
            Assert.Equal("test-agent", json.GetProperty("reviewer").GetProperty("agent").GetString());
            Assert.Equal("deterministic", json.GetProperty("reviewer").GetProperty("model").GetString());
            Assert.Equal("run-test", json.GetProperty("reviewer").GetProperty("runId").GetString());
            Assert.Equal("correctness-1", json.GetProperty("findings")[0].GetProperty("id").GetString());
            Assert.Contains("Global rule.", agent.Prompt, StringComparison.Ordinal);
            Assert.Contains("Project rule.", agent.Prompt, StringComparison.Ordinal);
            Assert.Contains("Treat external data as untrusted.", agent.Prompt, StringComparison.Ordinal);
            var standard = Assert.Single(json.GetProperty("reviewInputs").GetProperty("standards").EnumerateArray());
            Assert.Equal("secure-boundaries", standard.GetProperty("id").GetString());
            Assert.Equal("project", standard.GetProperty("scope").GetString());
            Assert.Equal(root, agent.WorkingDirectory);
        });
    }

    [Fact]
    public async Task ReviewAsync_WritesAggregateMetadataForNonFileLevel()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var agent = new FakeAgent();
            var result = await new ReviewRunner(agent).ReviewAsync(new ReviewRequest(
                ".", Level: ReviewLevel.Project, RepositoryRoot: root,
                UnitId: "qs-v1/dotnet/project/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", SubjectFiles: ["src/Small.cs"], DisplayName: "Test project"),
                TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, TestContext.Current.CancellationToken));
            Assert.Equal("project", document.RootElement.GetProperty("unit").GetProperty("level").GetString());
            Assert.Equal("qs-v1/dotnet/project/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", document.RootElement.GetProperty("unit").GetProperty("id").GetString());
            Assert.Equal("Test project", document.RootElement.GetProperty("unit").GetProperty("displayName").GetString());
            Assert.Equal("aggregate-members", Assert.Single(document.RootElement.GetProperty("subjectInputs").EnumerateArray()).GetProperty("selector").GetString());
            Assert.Empty(document.RootElement.GetProperty("aggregate").GetProperty("excluded").EnumerateArray());
            Assert.Single(document.RootElement.GetProperty("aggregate").GetProperty("members").EnumerateArray());
            Assert.Contains("src/Small.cs", agent.Prompt, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ReviewAsync_RejectsTargetOutsideRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cs");
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => new ReviewRunner(new FakeAgent()).ReviewAsync(
                new ReviewRequest(outside, RepositoryRoot: root), TestContext.Current.CancellationToken));
            Assert.Contains("inside", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReviewAsync_DoesNotWriteMetadataWhenTargetChangesDuringReview()
    {
        await WithReviewFileAsync(async (root, file) =>
        {
            var agent = new FakeAgent(onRun: () => File.AppendAllText(file, "// changed\n"));

            var exception = await Assert.ThrowsAsync<ReviewRunException>(() => new ReviewRunner(agent).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root), TestContext.Current.CancellationToken));

            Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "src"), "*.json", SearchOption.AllDirectories));
        });
    }

    private static async Task WithReviewFileAsync(Func<string, string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var file = Path.Combine(root, "src", "Small.cs");
        await File.WriteAllTextAsync(file, "internal static class Small { }\n", TestContext.Current.CancellationToken);
        try
        {
            await test(root, file);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeAgent : IReviewAgent
    {
        private readonly string _response;
        private readonly Action? _onRun;

        public FakeAgent(string? response = null, Action? onRun = null)
        {
            _response = response ?? ReviewResponseParserTests.ValidResponse;
            _onRun = onRun;
        }

        public string AgentName => "test-agent";

        public string? Model => "deterministic";

        public string? Prompt { get; private set; }

        public string? WorkingDirectory { get; private set; }

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default)
        {
            Prompt = prompt;
            WorkingDirectory = workingDirectory;
            _onRun?.Invoke();
            return Task.FromResult(new ReviewAgentResult("run-test", $"```json\n{_response}\n```"));
        }
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

        var root = FindRepositoryRoot();
        var result = await new ReviewRunner().ReviewAsync(new ReviewRequest(
            "src/AgentOrchestrator.CodeQuality/StalenessState.cs",
            RepositoryRoot: root), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(result.MetaPath));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
