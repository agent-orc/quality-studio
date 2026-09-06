namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewExecutionPipelineTests
{
    [Fact]
    public async Task Steps_run_in_order_and_the_run_is_charged_once()
    {
        var steps = new List<string>();
        var pipeline = new ReviewExecutionPipeline(new StubAgent(new ReviewAgentResult(
            "run-1", "answer", new TokenUsage(10, 2, 1, 0, 33), "gpt-fixture")));

        var result = await pipeline.ExecuteAsync(new ReviewExecution<string>(
            "prompt",
            ".",
            () => throw new InvalidOperationException("The agent reported its usage."),
            usage =>
            {
                steps.Add($"usage:{usage.RunId}:{usage.Usage.DurationMs}:{usage.EffectiveModel}");
                return Task.CompletedTask;
            },
            outcome => steps.Add($"parse:{outcome.Response}"),
            _ =>
            {
                steps.Add("drift");
                return Task.FromResult<string?>(null);
            },
            (outcome, _) =>
            {
                steps.Add("persist");
                return Task.FromResult(outcome.RunId);
            }), TestContext.Current.CancellationToken);

        Assert.Equal("run-1", result);
        Assert.Equal(["usage:run-1:33:gpt-fixture", "parse:answer", "drift", "persist"], steps);
    }

    [Fact]
    public async Task An_agent_that_reports_no_usage_falls_back_to_the_callers_measurement()
    {
        ReviewAgentUsage? reported = null;
        var pipeline = new ReviewExecutionPipeline(new StubAgent(
            new ReviewAgentResult("run-2", "answer", null, null)));

        await pipeline.RunAsync("prompt", ".", () => new TokenUsage(null, null, null, null, 1200),
            usage =>
            {
                reported = usage;
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);

        Assert.Equal(1200, reported!.Usage.DurationMs);
    }

    [Fact]
    public async Task Drift_refuses_the_result_before_anything_is_persisted()
    {
        var persisted = false;
        var pipeline = new ReviewExecutionPipeline(new StubAgent(new ReviewAgentResult(
            "run-3", "answer", new TokenUsage(1, 1, 0, 0, 1), null)));

        var exception = await Assert.ThrowsAsync<ReviewRunException>(() => pipeline.ExecuteAsync(
            new ReviewExecution<string>(
                "prompt", ".", () => new TokenUsage(null, null, null, null, 0),
                _ => Task.CompletedTask,
                _ => { },
                _ => Task.FromResult<string?>("The subject moved."),
                (_, _) =>
                {
                    persisted = true;
                    return Task.FromResult("persisted");
                }),
            TestContext.Current.CancellationToken));

        Assert.Equal("The subject moved.", exception.Message);
        Assert.False(persisted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_or_cancelled_run_is_still_charged_before_it_propagates(bool cancelled)
    {
        var usage = new TokenUsage(40, 0, 0, 0, 800);
        Exception failure = cancelled
            ? new ReviewAgentRunCanceledException("run-4", usage, "gpt-fixture",
                new OperationCanceledException("stopped"), CancellationToken.None)
            : new ReviewAgentRunException("run-4", usage, "gpt-fixture", new IOException("failed"));
        ReviewAgentUsage? reported = null;
        var pipeline = new ReviewExecutionPipeline(new StubAgent(failure));

        await Assert.ThrowsAsync(failure.GetType(), () => pipeline.RunAsync(
            "prompt", ".", () => new TokenUsage(null, null, null, null, 0),
            charged =>
            {
                reported = charged;
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken));

        Assert.Equal("run-4", reported!.RunId);
        Assert.Equal(40, reported.Usage.InputTokens);
        Assert.Equal("gpt-fixture", reported.EffectiveModel);
    }

    private sealed class StubAgent : IReviewAgent
    {
        private readonly ReviewAgentResult? result;
        private readonly Exception? failure;

        public StubAgent(ReviewAgentResult result) => this.result = result;

        public StubAgent(Exception failure) => this.failure = failure;

        public string AgentName => "stub";

        public string? Model => "gpt-fixture";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default) =>
            failure is null ? Task.FromResult(result!) : Task.FromException<ReviewAgentResult>(failure);
    }
}
