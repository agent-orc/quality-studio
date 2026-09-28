using System.Runtime.CompilerServices;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// CodingAgentRunner folds Claude's result usage into <c>input= output= cache_read=</c> and drops
/// <c>cache_creation_input_tokens</c>; the review agent reads them from the raw result frame.
/// </summary>
public sealed class ClaudeUsageCaptureTests
{
    private const string ResultFrame =
        """{"type":"result","subtype":"success","is_error":false,"usage":{"input_tokens":2000,"cache_read_input_tokens":600000,"cache_creation_input_tokens":300000,"output_tokens":10000}}""";

    [Fact]
    public async Task ClaudeRunsReportCacheWritesAndCountEveryInputToken()
    {
        var driver = new RawLineDriver("claude", runId =>
        [
            (ResultFrame, new CliRunEvent.TurnCompleted("input=2000 output=10000 cache_read=600000") { RunId = runId }),
            (null, new CliRunEvent.RunEnded(RunOutcome.Completed, null, 0, 1) { RunId = runId }),
        ]);
        var agent = new CodingAgentReviewAgent("claude", driver, model: "claude-opus-5",
            attachTimeout: TimeSpan.FromSeconds(30));

        var result = await agent.RunAsync("review", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken);

        var usage = Assert.IsType<TokenUsage>(result.Usage);
        Assert.Equal(902_000, usage.InputTokens);
        Assert.Equal(600_000, usage.CachedInputTokens);
        Assert.Equal(300_000, usage.CacheWriteInputTokens);
        Assert.Equal(10_000, usage.OutputTokens);
        Assert.Equal(0, driver.Subscribers);
    }

    [Fact]
    public async Task FailedClaudeTurnsStillReportTheirUsage()
    {
        const string failedFrame =
            """{"type":"result","subtype":"error_max_turns","is_error":true,"usage":{"input_tokens":10,"cache_read_input_tokens":0,"cache_creation_input_tokens":5000,"output_tokens":20}}""";
        var driver = new RawLineDriver("claude", runId =>
        [
            (failedFrame, new CliRunEvent.TurnFailed("error_max_turns") { RunId = runId }),
            (null, new CliRunEvent.RunEnded(RunOutcome.Failed, "error", 1, 1) { RunId = runId }),
        ]);
        var agent = new CodingAgentReviewAgent("claude", driver, model: "claude-opus-5",
            attachTimeout: TimeSpan.FromSeconds(30));

        var failure = await Assert.ThrowsAsync<ReviewAgentRunException>(() =>
            agent.RunAsync("review", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken));

        Assert.Equal(5_010, failure.Usage.InputTokens);
        Assert.Equal(5_000, failure.Usage.CacheWriteInputTokens);
    }

    [Fact]
    public void TheTapIgnoresOtherRunsStderrAndNonResultFrames()
    {
        var tap = new ClaudeUsageTap("run-1");
        tap.Observe("run-2", new CliOutputLine { Stream = "stdout", Text = ResultFrame });
        tap.Observe("run-1", new CliOutputLine { Stream = "stderr", Text = ResultFrame });
        tap.Observe("run-1", new CliOutputLine { Stream = "stdout", Text = """{"type":"assistant","message":"result"}""" });
        tap.Observe("run-1", new CliOutputLine { Stream = "stdout", Text = "not json \"result\"" });
        Assert.Null(tap.Build(1));

        tap.Observe("run-1", new CliOutputLine { Stream = "stdout", Text = ResultFrame });
        Assert.Equal(new TokenUsage(902_000, 10_000, 600_000, 0, 1, 300_000), tap.Build(1));
    }

    [Fact]
    public async Task OtherClisKeepTheRunnerSummary()
    {
        var driver = new RawLineDriver("codex", runId =>
        [
            (ResultFrame, new CliRunEvent.TurnCompleted("input=100 cached=40 output=10 reasoning=2") { RunId = runId }),
            (null, new CliRunEvent.RunEnded(RunOutcome.Completed, null, 0, 1) { RunId = runId }),
        ]);
        var agent = new CodingAgentReviewAgent("codex", driver, model: "gpt-5.6-luna",
            attachTimeout: TimeSpan.FromSeconds(30));

        var result = await agent.RunAsync("review", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken);

        Assert.Equal(100, result.Usage!.InputTokens);
        Assert.Equal(40, result.Usage.CachedInputTokens);
        Assert.Null(result.Usage.CacheWriteInputTokens);
    }

    private sealed class RawLineDriver(string cliType, Func<string, (string? Line, CliRunEvent Event)[]> script) : ICliDriver
    {
        private Action<string, CliOutputLine>? output;

        public int Subscribers => output?.GetInvocationList().Length ?? 0;

        public string CliType => cliType;
        public bool SupportsCleanContext => false;

        public event Action<string, CliOutputLine>? OnOutput
        {
            add => output += value;
            remove => output -= value;
        }
        public event Action<string, CliRunInfo>? OnStarted;
        public event Action<string, CliRunInfo>? OnFinished;
#pragma warning disable CS0067 // required by ICliDriver; this fake never raises run events
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
            OnStarted?.Invoke(request.RunId, new CliRunInfo { RunId = request.RunId });
            foreach (var (line, runEvent) in script(request.RunId))
            {
                await Task.Yield();
                if (line is not null)
                    output?.Invoke(request.RunId, new CliOutputLine { Stream = "stdout", Text = line });
                yield return runEvent;
            }
            OnFinished?.Invoke(request.RunId, new CliRunInfo { RunId = request.RunId, Status = "completed" });
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
