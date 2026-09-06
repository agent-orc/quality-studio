using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewMetaReaderTests
{
    private static JsonSchema V3Schema => SchemaCatalogue.Get("review-meta.v3.schema.json");

    [Fact]
    public async Task The_runner_writes_every_field_a_real_sidecar_carries_and_the_v3_schema_demands()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-meta-writer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "Small.cs"),
                "internal static class Small { }\n", TestContext.Current.CancellationToken);
            // sourceRevision is only written inside a repository, and a real sidecar carries it.
            await GitAsync(root, "init", "--quiet");
            await GitAsync(root, "config", "user.email", "quality@example.test");
            await GitAsync(root, "config", "user.name", "Quality Fixture");
            await GitAsync(root, "add", ".");
            await GitAsync(root, "commit", "--quiet", "-m", "fixture");
            var response = ReviewResponseParserTests.ValidResponse.Replace(
                "\"findings\": []", "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]",
                StringComparison.Ordinal);

            var result = await new ReviewRunner(new WritingAgent(response)).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root), TestContext.Current.CancellationToken);

            var text = await File.ReadAllTextAsync(result.MetaPath, TestContext.Current.CancellationToken);
            using var written = JsonDocument.Parse(text);
            var validation = V3Schema.Evaluate(
                written.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(validation.IsValid, validation.ToString());

            // And it loads back through the one reader.
            var sidecar = ReviewMetaReader.Load(result.MetaPath);
            Assert.Equal(ReviewKind.Code, sidecar.Document.Kind);
            Assert.Equal("src/Small.cs", sidecar.Document.Unit.Path);
            Assert.Equal(result.ReviewedHash, sidecar.Document.ReviewedHash.Value);
            Assert.Equal("deterministic", sidecar.Document.Reviewer.Model);
            Assert.Single(sidecar.Document.Findings);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Theory]
    [InlineData("{ not json", ReviewMetaReadFailure.Malformed)]
    [InlineData("[]", ReviewMetaReadFailure.Malformed)]
    public void Content_that_is_not_the_contract_is_reported_with_its_path_and_cause(
        string content, ReviewMetaReadFailure expected)
    {
        Assert.False(ReviewMetaReader.TryParse(content, "sidecar.json", out _, out var error));
        Assert.Equal(expected, error.Failure);
        Assert.Equal("sidecar.json", error.Source);
        Assert.StartsWith("sidecar.json: ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_schema_version_is_distinguished_from_a_missing_field()
    {
        var document = Sample();
        var future = ReviewMetaJson.Serialize(document)
            .Replace("\"schemaVersion\": 3", "\"schemaVersion\": 9", StringComparison.Ordinal);
        var withoutModel = JsonNode.Parse(ReviewMetaJson.Serialize(document))!.AsObject();
        withoutModel["reviewer"]!.AsObject().Remove("model");

        Assert.False(ReviewMetaReader.TryParse(future, "future.json", out _, out var version));
        Assert.Equal(ReviewMetaReadFailure.UnsupportedVersion, version.Failure);

        Assert.False(ReviewMetaReader.TryParse(withoutModel.ToJsonString(), "partial.json", out _, out var missing));
        Assert.Equal(ReviewMetaReadFailure.IncompleteContract, missing.Failure);
        Assert.Contains("reviewer.model", missing.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_turns_a_read_error_into_an_exception_that_names_the_sidecar()
    {
        var exception = Assert.Throws<ReviewMetaReadException>(
            () => ReviewMetaReader.Load(Path.Combine(Path.GetTempPath(), "quality-absent-sidecar.json")));

        Assert.Equal(ReviewMetaReadFailure.Missing, exception.Error.Failure);
    }

    private static ReviewMetaDocument Sample() => ReviewMetaFixture.Document(
        "qs-v1/generic/file/" + new string('a', 64),
        "src/App.cs",
        new string('d', 64),
        [new SubjectInputHash("src/App.cs", "file", "sha256:" + new string('c', 64))]);

    private static async Task GitAsync(string root, params string[] arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        })!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class WritingAgent(string response) : IReviewAgent
    {
        public string AgentName => "codex";

        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult("run-1", response, new TokenUsage(120, 30, 10, 5, 900), Model));
    }
}
