using System.Runtime.CompilerServices;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// The runner's Claude usage summary drops <c>cache_creation_input_tokens</c>; the agent reads it
/// from the raw result line and reports input that counts fresh input, cache reads and cache writes.
/// </summary>
public sealed class ReviewAgentUsageTests
{
    private const string ClaudeResultLine =
        """{"type":"result","subtype":"success","is_error":false,"result":"{}","usage":{"input_tokens":1000,"cache_creation_input_tokens":50000,"cache_read_input_tokens":400000,"output_tokens":200}}""";

    [Fact]
    public async Task ClaudeRunReportsCacheWritesAndCountsAllInput()
    {
        var driver = new RawOutputDriver("claude", runId =>
        [
            new CliOutputLine { Stream = "stdout", Text = """{"type":"assistant","message":{"usage":{"cache_creation_input_tokens":999}}}""" },
            new CliOutputLine { Stream = "stdout", Text = ClaudeResultLine },
        ], runId =>
        [
            new CliRunEvent.TurnCompleted("input=1000 output=200 cache_read=400000") { RunId = runId },
            new CliRunEvent.RunEnded(RunOutcome.Completed, null, 0, 1.5) { RunId = runId },
        ]);
        var agent = new CodingAgentReviewAgent("claude", driver, model: "claude-opus-5",
            attachTimeout: TimeSpan.FromSeconds(30));

        var result = await agent.RunAsync("review", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken);

        var usage = Assert.IsType<TokenUsage>(result.Usage);
        Assert.Equal(451_000, usage.InputTokens);
        Assert.Equal(400_000, usage.CachedInputTokens);
        Assert.Equal(50_000, usage.CacheWriteInputTokens);
        Assert.Equal(200, usage.OutputTokens);
        Assert.Equal(0, driver.OutputSubscribers);
    }

    [Fact]
    public void CodexCachedTokensStayASubsetOfTheInputItAlreadyCounts()
    {
        var codex = CodingAgentReviewAgent.NormalizeReportedUsage("codex", 100_000, 2_000, 60_000, 500, 0, 10);
        Assert.Equal(100_000, codex.InputTokens);
        Assert.Equal(60_000, codex.CachedInputTokens);
        Assert.Null(codex.CacheWriteInputTokens);

        var claude = CodingAgentReviewAgent.NormalizeReportedUsage("claude", 1_000, 200, 400_000, 0, 50_000, 10);
        Assert.Equal(451_000, claude.InputTokens);
        Assert.Equal(50_000, claude.CacheWriteInputTokens);
    }

    [Theory]
    [InlineData(ClaudeResultLine, 50_000L)]
    [InlineData("""{"type":"assistant","message":{"usage":{"cache_creation_input_tokens":7}}}""", null)]
    [InlineData("""{"type":"result","usage":{"cache_creation_input_tokens":-3}}""", null)]
    [InlineData("""{"type":"result","usage":{"cache_creation_input_tokens":null}}""", null)]
    [InlineData("""{"type":"result","usage":{"cache_creation_input_tokens":"50000"}}""", null)]
    [InlineData("""{"type":"result","usage":{"cache_creation_input_tokens":true}}""", null)]
    [InlineData("""{"type":"result","usage":{"cache_creation_input_tokens":{}}}""", null)]
    [InlineData("""{"type":"result","usage":{"cache_creation_input_tokens":[]}}""", null)]
    [InlineData("""{"type":"result","usage":{"cache_creation_input_tokens":1.5}}""", null)]
    [InlineData("""{"type":"result","usage":{"cache_creation_input_tokens":9223372036854775808}}""", null)]
    [InlineData("""{"type":"result","usage":{"cache_creation_input_tokens":""", null)]
    [InlineData("plain text mentioning cache_creation_input_tokens", null)]
    [InlineData("", null)]
    public void OnlyResultLinesContributeCacheWrites(string line, long? expected) =>
        Assert.Equal(expected, ClaudeCacheWriteCounter.TryReadCacheWrite(line));

    [Fact]
    public async Task MalformedCacheWriteValueDoesNotFailReview()
    {
        var driver = new RawOutputDriver("claude", runId =>
        [
            new CliOutputLine { Stream = "stdout", Text = """{"type":"result","usage":{"cache_creation_input_tokens":null}}""" },
            new CliOutputLine { Stream = "stdout", Text = """{"type":"result","usage":{"cache_creation_input_tokens":"50000"}}""" },
        ], runId =>
        [
            new CliRunEvent.TurnCompleted("input=1000 output=200 cache_read=400000") { RunId = runId },
            new CliRunEvent.RunEnded(RunOutcome.Completed, null, 0, 1.5) { RunId = runId },
        ]);
        var agent = new CodingAgentReviewAgent("claude", driver, model: "claude-opus-5",
            attachTimeout: TimeSpan.FromSeconds(30));

        var result = await agent.RunAsync("review", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken);

        var usage = Assert.IsType<TokenUsage>(result.Usage);
        Assert.Equal(401_000, usage.InputTokens);
        Assert.Equal(0, usage.CacheWriteInputTokens);
        Assert.Equal(0, driver.OutputSubscribers);
    }

    [Fact]
    public void TheCounterIgnoresOtherRunsStderrAndNonClaudeClis()
    {
        var counter = new ClaudeCacheWriteCounter("run-1", enabled: true);
        counter.Observe("run-2", new CliOutputLine { Stream = "stdout", Text = ClaudeResultLine });
        counter.Observe("run-1", new CliOutputLine { Stream = "stderr", Text = ClaudeResultLine });
        Assert.Equal(0, counter.Total);
        counter.Observe("run-1", new CliOutputLine { Stream = "stdout", Text = ClaudeResultLine });
        counter.Observe("run-1", new CliOutputLine { Stream = "stdout", Text = ClaudeResultLine });
        Assert.Equal(100_000, counter.Total);

        var codex = new ClaudeCacheWriteCounter("run-1", enabled: false);
        codex.Observe("run-1", new CliOutputLine { Stream = "stdout", Text = ClaudeResultLine });
        Assert.Equal(0, codex.Total);
    }

    /// <summary>Raises raw output lines for a run before streaming its typed events, as the engine does.</summary>
    private sealed class RawOutputDriver(
        string cliType,
        Func<string, CliOutputLine[]> lines,
        Func<string, CliRunEvent[]> events) : ICliDriver
    {
        private Action<string, CliOutputLine>? onOutput;

        public int OutputSubscribers => onOutput?.GetInvocationList().Length ?? 0;
        public string CliType => cliType;
        public bool SupportsCleanContext => false;

        public event Action<string, CliOutputLine>? OnOutput
        {
            add => onOutput += value;
            remove => onOutput -= value;
        }
#pragma warning disable CS0067 // required by ICliDriver; this fake raises raw output only
        public event Action<string, CliRunInfo>? OnStarted;
        public event Action<string, CliRunInfo>? OnFinished;
        public event Action<string, CliRunEvent>? OnRunEvent;
#pragma warning restore CS0067

        public string GetCliPath() => "fake-cli";
        public bool IsAvailable() => true;
        public (bool Available, string? Version, string Path) TestCliPath(string? path = null) => (true, "0.0.0", "fake-cli");

        public Task<(CliRunInfo? Run, string? Error)> StartAsync(CliRunRequest request, CancellationToken ct = default) =>
            Task.FromResult<(CliRunInfo?, string?)>((new CliRunInfo { RunId = request.RunId }, null));

        public async IAsyncEnumerable<CliRunEvent> StreamAsync(
            CliRunRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            onOutput?.Invoke("another-run", new CliOutputLine { Stream = "stdout", Text = ClaudeResultLine });
            foreach (var line in lines(request.RunId)) onOutput?.Invoke(request.RunId, line);
            foreach (var runEvent in events(request.RunId))
            {
                await Task.Yield();
                yield return runEvent;
            }
        }

        public bool Stop(string runId, RunStopReason reason = RunStopReason.UserStop) => false;
        public bool SendInput(string runId, string input) => false;
        public IReadOnlyList<CliOutputLine> GetOutput(string runId) => [];
        public CliRunInfo? GetExecution(string runId) => null;
        public bool IsCompatibleSessionId(string? sessionId) => true;
        public bool Forget(string runId) => false;
        public CliCapabilities Capabilities(string? model) => new();
    }
}
