using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// The review output contract: how an answer is read, what it is asked to look like, and what
/// happens when it is refused (QS-109, evaluation defect D2).
/// </summary>
public sealed class ReviewOutputContractTests
{
    // A recommendation quoting a fenced snippet: valid JSON that the lazy fence regex cut short.
    private static readonly string FenceInsideString = ReviewResponseParserTests.ValidResponse.Replace(
        "\"findings\": []",
        "\"findings\": [" + ReviewResponseParserTests.ValidFinding.Replace(
            "\"recommendation\":\"Fix it.\"",
            "\"recommendation\":\"Replace it with:\\n```json\\n{\\\"a\\\": 1}\\n```\\nand keep braces } { in prose.\"",
            StringComparison.Ordinal) + "]",
        StringComparison.Ordinal);

    [Fact]
    public void Reader_keeps_a_fence_and_braces_inside_a_json_string()
    {
        var parsed = new ReviewResponseParser().Parse("Here is the review.\n```json\n" + FenceInsideString + "\n```\nDone.");

        var recommendation = parsed["findings"]![0]!["recommendation"]!.GetValue<string>();
        Assert.Contains("```json", recommendation, StringComparison.Ordinal);
        Assert.EndsWith("and keep braces } { in prose.", recommendation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("Prose {with braces} first, then the answer: {0} and a trailing note.")]
    [InlineData("```json\n{0}\n```\n```json\n{\"second\": true}\n```")]
    public void Reader_takes_the_first_complete_object_with_or_without_a_fence(string template)
    {
        var parsed = AgentJsonReader.FirstObject(
            template.Replace("{0}", ReviewResponseParserTests.ValidResponse, StringComparison.Ordinal));

        Assert.Equal("Looks sound.", parsed["summary"]!.GetValue<string>());
    }

    [Fact]
    public void Reader_reports_a_truncated_answer_instead_of_a_nested_object()
    {
        var truncated = "```json\n" + ReviewResponseParserTests.ValidResponse[..^20];

        var exception = Assert.Throws<ReviewResponseException>(() => new ReviewResponseParser().Parse(truncated));

        Assert.Contains("incomplete JSON object", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_schema_accepts_the_contract_and_is_requested_in_every_review_prompt()
    {
        var schema = SchemaCatalogue.Get("review-response.v1.schema.json");
        using var valid = JsonDocument.Parse(FenceInsideString);
        var result = schema.Evaluate(valid.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());

        using var wrongBand = JsonDocument.Parse(ReviewResponseParserTests.ValidResponse.Replace(
            "95, \"band\": \"A\"", "95, \"band\": \"B\"", StringComparison.Ordinal));
        Assert.False(schema.Evaluate(wrongBand.RootElement).IsValid);

        // The embedded copy the prompt carries is the repository schema, not a stale duplicate.
        var embedded = JsonNode.Parse(ReviewPromptBuilder.ResponseSchema.Value)!;
        var repository = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "schemas", "review-response.v1.schema.json")))!;
        Assert.True(JsonNode.DeepEquals(repository, embedded));

        foreach (var level in new[] { ReviewLevel.File, ReviewLevel.Module, ReviewLevel.Project })
        {
            var prompt = new ReviewPromptBuilder().Build("src/Small.cs", "code", level: level);
            Assert.Contains("## Response schema", prompt, StringComparison.Ordinal);
            Assert.Contains(ReviewPromptBuilder.ResponseSchema.Value, prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Rejected_answer_is_retried_once_with_the_reason_and_both_runs_are_recorded()
    {
        var agent = new ScriptedAgent("```json\n{\"summary\": \"no grade\"}\n```", ReviewResponseParserTests.ValidResponse);
        var usage = new List<ReviewAgentUsage>();
        var rejections = new List<ReviewResponseRejection>();
        string? parsedSummary = null;

        var result = await new ReviewExecutionPipeline(agent).ExecuteAsync(new ReviewExecution<string>(
            "Review the file.",
            ".",
            () => new TokenUsage(null, null, null, null, 1),
            reported => { usage.Add(reported); return Task.CompletedTask; },
            outcome => parsedSummary = new ReviewResponseParser().Parse(outcome.Response)["summary"]!.GetValue<string>(),
            _ => Task.FromResult<string?>(null),
            (outcome, _) => Task.FromResult(outcome.RunId),
            rejection => { rejections.Add(rejection); return Task.CompletedTask; }),
            TestContext.Current.CancellationToken);

        Assert.Equal("run-2", result);
        Assert.Equal("Looks sound.", parsedSummary);
        Assert.Equal(new[] { "run-1", "run-2" }, usage.Select(item => item.RunId));
        var rejection = Assert.Single(rejections);
        Assert.Equal(("run-1", 1, true), (rejection.AgentRunId, rejection.Attempt, rejection.Retried));
        Assert.Contains("grade", rejection.Error, StringComparison.Ordinal);
        Assert.Contains("\"no grade\"", rejection.Response, StringComparison.Ordinal);
        Assert.False(rejection.Truncated);
        Assert.StartsWith("Review the file.", agent.Prompts[1], StringComparison.Ordinal);
        Assert.Contains("## Your previous answer was rejected", agent.Prompts[1], StringComparison.Ordinal);
        Assert.Contains(rejection.Error, agent.Prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_rejection_fails_the_review_and_keeps_both_raw_answers_capped()
    {
        var huge = "{\"summary\": \"" + new string('x', ReviewResponseRejection.MaxResponseCharacters * 2) + "\"}";
        var agent = new ScriptedAgent("I cannot comply.", huge, ReviewResponseParserTests.ValidResponse);
        var rejections = new List<ReviewResponseRejection>();

        var exception = await Assert.ThrowsAsync<ReviewResponseException>(() => new ReviewExecutionPipeline(agent)
            .ExecuteAsync(new ReviewExecution<string>(
                "Review the file.", ".",
                () => new TokenUsage(null, null, null, null, 1),
                _ => Task.CompletedTask,
                outcome => new ReviewResponseParser().Parse(outcome.Response),
                _ => Task.FromResult<string?>(null),
                (outcome, _) => Task.FromResult(outcome.RunId),
                rejection => { rejections.Add(rejection); return Task.CompletedTask; }),
                TestContext.Current.CancellationToken));

        Assert.Equal(2, agent.Prompts.Count);
        Assert.Contains("rejected again after 1 repair attempt", exception.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { (1, true), (2, false) }, rejections.Select(item => (item.Attempt, item.Retried)));
        Assert.Equal("I cannot comply.", rejections[0].Response);
        var capped = rejections[1];
        Assert.True(capped.Truncated);
        Assert.Equal(huge.Length, capped.ResponseCharacters);
        Assert.Equal(ReviewResponseRejection.MaxResponseCharacters, capped.Response.Length);
        Assert.StartsWith("{\"summary\"", capped.Response, StringComparison.Ordinal);
        Assert.EndsWith("xx\"}", capped.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_runner_reports_the_rejection_with_its_unit_and_writes_the_repaired_review()
    {
        await ReviewRunnerTests.WithReviewFileAsync(async (root, _) =>
        {
            var agent = new ScriptedAgent("Sorry, here is prose instead of JSON.", ReviewResponseParserTests.ValidResponse);
            var rejections = new List<ReviewResponseRejection>();
            var usage = new List<ReviewUsageEntry>();

            var result = await new ReviewRunner(agent, usageRecorded: usage.Add, responseRejected: rejections.Add)
                .ReviewAsync(new ReviewRequest("src/Small.cs", RepositoryRoot: root), TestContext.Current.CancellationToken);

            Assert.Equal("run-2", result.RunId);
            Assert.Equal(2, usage.Count);
            var rejection = Assert.Single(rejections);
            Assert.Equal(("src/Small.cs", "code", "file"), (rejection.Path, rejection.Kind, rejection.Level));
            Assert.Equal("Sorry, here is prose instead of JSON.", rejection.Response);
        });
    }

    private sealed class ScriptedAgent(params string[] responses) : IReviewAgent
    {
        public List<string> Prompts { get; } = [];
        public string AgentName => "scripted-agent";
        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            var index = Prompts.Count;
            return Task.FromResult(new ReviewAgentResult(
                $"run-{index}", responses[Math.Min(index, responses.Length) - 1], new TokenUsage(10, 5, 0, 0, 1), Model));
        }
    }
}
