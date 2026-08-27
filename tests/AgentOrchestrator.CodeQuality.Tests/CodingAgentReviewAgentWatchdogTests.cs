using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// QS-93 M-1: a reviewer process that dies or never attaches must fail loudly within a
/// bounded time instead of hanging the caller forever (dossier finding N-01 — the
/// third-party CLI's pre-spawn health probe can block a whole OS thread on a
/// synchronous pipe read that ignores any CancellationToken).
/// </summary>
public sealed class CodingAgentReviewAgentWatchdogTests
{
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task RunAsync_FailsFastWithTypedException_WhenDriverNeverAttaches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var driver = new FakeCliDriver(NeverAttachAsync);
        var agent = new CodingAgentReviewAgent("codex", driver, attachTimeout: AttachTimeout);
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<ReviewAgentRunException>(
            () => agent.RunAsync("review this", Directory.GetCurrentDirectory(), cancellationToken));

        stopwatch.Stop();
        var timeout = Assert.IsType<ReviewAgentAttachTimeoutException>(exception.InnerException);
        Assert.Equal(AttachTimeout, timeout.Timeout);
        // Bounded, not hung: well under the driver's infinite delay.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected the attach timeout to fire quickly; took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_ThrowsTypedException_WhenRunEndsWithoutCompleting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var driver = new FakeCliDriver(runId => StreamAsync(runId,
            new CliRunEvent.OutputDelta("partial output") { RunId = runId },
            new CliRunEvent.RunEnded(RunOutcome.Failed, "self-crash", 1, 0.5) { RunId = runId }));
        var agent = new CodingAgentReviewAgent("codex", driver, attachTimeout: AttachTimeout);

        var exception = await Assert.ThrowsAsync<ReviewAgentRunException>(
            () => agent.RunAsync("review this", Directory.GetCurrentDirectory(), cancellationToken));

        var aborted = Assert.IsType<ReviewAgentRunAbortedException>(exception.InnerException);
        Assert.Equal(RunOutcome.Failed, aborted.Outcome);
        Assert.Equal("self-crash", aborted.Reason);
    }

    [Fact]
    public async Task RunAsync_ReturnsAccumulatedOutput_WhenRunCompletesCleanly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var driver = new FakeCliDriver(runId => StreamAsync(runId,
            new CliRunEvent.OutputDelta("hello ") { RunId = runId },
            new CliRunEvent.OutputDelta("world") { RunId = runId },
            new CliRunEvent.RunEnded(RunOutcome.Completed, null, 0, 0.2) { RunId = runId }));
        var agent = new CodingAgentReviewAgent("codex", driver, attachTimeout: AttachTimeout);

        var result = await agent.RunAsync("review this", Directory.GetCurrentDirectory(), cancellationToken);

        Assert.Equal("hello world", result.Response);
    }

    private static async IAsyncEnumerable<CliRunEvent> NeverAttachAsync(string runId)
    {
        // Mirrors the real defect: a blocking call that never observes any
        // CancellationToken, so the first MoveNextAsync never completes on its own.
        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        yield return new CliRunEvent.RunEnded(RunOutcome.Completed, null, 0, 0) { RunId = runId };
    }

    private static async IAsyncEnumerable<CliRunEvent> StreamAsync(
        string runId, params CliRunEvent[] events)
    {
        foreach (var runEvent in events)
        {
            await Task.Yield();
            yield return runEvent;
        }
    }

    private sealed class FakeCliDriver(Func<string, IAsyncEnumerable<CliRunEvent>> stream) : ICliDriver
    {
        public string CliType => "codex";
        public bool SupportsCleanContext => false;

#pragma warning disable CS0067 // required by ICliDriver; this fake never raises raw output
        public event Action<string, CliOutputLine>? OnOutput;
#pragma warning restore CS0067
        public event Action<string, CliRunInfo>? OnStarted;
        public event Action<string, CliRunInfo>? OnFinished;
        public event Action<string, CliRunEvent>? OnRunEvent;

        public string GetCliPath() => "fake-cli";
        public bool IsAvailable() => true;
        public (bool Available, string? Version, string Path) TestCliPath(string? path = null) => (true, "0.0.0", "fake-cli");

        public Task<(CliRunInfo? Run, string? Error)> StartAsync(CliRunRequest request, CancellationToken ct = default) =>
            Task.FromResult<(CliRunInfo?, string?)>((new CliRunInfo { RunId = request.RunId }, null));

        public async IAsyncEnumerable<CliRunEvent> StreamAsync(
            CliRunRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            OnStarted?.Invoke(request.RunId, new CliRunInfo { RunId = request.RunId });
            await foreach (var runEvent in stream(request.RunId).WithCancellation(ct))
            {
                OnRunEvent?.Invoke(request.RunId, runEvent);
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
