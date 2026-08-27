using System.Text;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Metrics;
using CodingAgentRunner.Model;
using Microsoft.Extensions.Logging;

namespace AgentOrchestrator.CodeQuality;

public interface IReviewAgent
{
    string AgentName { get; }

    string? Model { get; }

    Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default);
}

public sealed record ReviewAgentResult(string RunId, string Response, TokenUsage? Usage = null, string? EffectiveModel = null);

public sealed class ReviewAgentRunException(
    string runId, TokenUsage usage, string? effectiveModel, Exception innerException)
    : Exception($"The coding agent run failed: {innerException.Message}", innerException)
{
    public string RunId { get; } = runId;
    public TokenUsage Usage { get; } = usage;
    public string? EffectiveModel { get; } = effectiveModel;
}

public sealed class ReviewAgentRunCanceledException(
    string runId, TokenUsage usage, string? effectiveModel, OperationCanceledException innerException,
    CancellationToken cancellationToken)
    : OperationCanceledException("The coding agent run was cancelled.", innerException, cancellationToken)
{
    public string RunId { get; } = runId;
    public TokenUsage Usage { get; } = usage;
    public string? EffectiveModel { get; } = effectiveModel;
}

public sealed class CodingAgentReviewAgent : IReviewAgent
{
    private static readonly TimeSpan DefaultAttachTimeout = TimeSpan.FromSeconds(90);

    private readonly string _cliType;
    private readonly string? _thinkingLevel;
    private readonly CliRunner _runner;
    private readonly Action<string, CliRunEvent>? _eventObserver;
    private readonly WatchdogPolicy _watchdogPolicy;
    private readonly TimeSpan _attachTimeout;

    public CodingAgentReviewAgent(string cliType = "codex", string? model = null, string? thinkingLevel = null,
        CliOptions? options = null,
        Action<string, CliRunEvent>? eventObserver = null,
        ILogger? logger = null,
        WatchdogPolicy? watchdogPolicy = null,
        TimeSpan? attachTimeout = null)
    {
        _cliType = cliType;
        _thinkingLevel = thinkingLevel;
        Model = model;
        _runner = new CliRunner(options ?? new CliOptions(), logger);
        _eventObserver = eventObserver;
        _watchdogPolicy = watchdogPolicy ?? WatchdogPolicy.Default;
        _attachTimeout = attachTimeout ?? DefaultAttachTimeout;
        _runner.Get(cliType); // Fail at construction for unknown adapters.
    }

    public string AgentName => _cliType;

    public string? Model { get; }

    public async Task<ReviewAgentResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var runId = "quality-" + Guid.NewGuid().ToString("N");
        var output = new StringBuilder();
        var metrics = new RunMetricsRecorder();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var driver = _runner.Get(_cliType);

        // A dead/never-attaching reviewer process must fail loudly within a bounded
        // time instead of silently completing empty (the run has no operations/tokens
        // to surface). RunWatchdog only tracks a run from OnStarted onward, so the
        // attach-timeout below separately bounds the pre-spawn gap it cannot cover.
        using var watchdog = RunWatchdog.Attach(driver, _watchdogPolicy, autoStop: true);
        var stream = driver.StreamAsync(new CliRunRequest
        {
            RunId = runId,
            Prompt = prompt,
            WorkingDirectory = workingDirectory,
            Model = Model,
            ThinkingLevel = _thinkingLevel,
            PermissionMode = "read-only",
            ContextMode = "shared",
        }, cancellationToken);
        var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        var disposeDeferred = false;
        try
        {
            // Task.Run, not a bare .AsTask(): a driver whose pre-spawn path never awaits
            // (e.g. a synchronous, blocking spawn mechanism) would otherwise run
            // MoveNextAsync synchronously on this thread, so the WhenAny race below would
            // never get a chance to start before the hang already happened.
            var firstMoveNext = Task.Run(async () => await enumerator.MoveNextAsync().ConfigureAwait(false));
            // Deliberately not tied to the caller's cancellationToken: a plain user-cancel
            // must surface as a cancellation, not be misreported as an attach timeout.
            var attachTimeout = Task.Delay(_attachTimeout);
            if (await Task.WhenAny(firstMoveNext, attachTimeout).ConfigureAwait(false) == attachTimeout)
            {
                // The real MoveNextAsync call is still outstanding — an async-iterator
                // enumerator only allows one MoveNextAsync/DisposeAsync in flight at a
                // time, so disposing here would throw and mask this exception. Let it
                // settle on its own and clean up once it does.
                disposeDeferred = true;
                _ = DisposeAfterSettledAsync(enumerator, firstMoveNext);
                throw new TimeoutException(
                    $"The coding agent run '{runId}' produced no activity within {_attachTimeout.TotalSeconds:0}s of starting (attach timeout).");
            }

            var hasCurrent = await firstMoveNext.ConfigureAwait(false);
            while (hasCurrent)
            {
                var runEvent = enumerator.Current;
                metrics.Observe(runEvent);
                _eventObserver?.Invoke(_cliType, runEvent);
                if (runEvent is CliRunEvent.OutputDelta delta)
                {
                    output.Append(delta.Text);
                }
                if (runEvent is CliRunEvent.RunEnded { Outcome: RunOutcome.Stopped } ended &&
                    ended.Reason == nameof(RunStopReason.Watchdog))
                {
                    throw new TimeoutException(
                        $"The coding agent run '{runId}' was stopped by the silence watchdog after no activity.");
                }
                hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            var canceled = BuildUsage(metrics, stopwatch);
            throw new ReviewAgentRunCanceledException(runId, canceled.Usage, canceled.Model, exception, cancellationToken);
        }
        catch (Exception exception)
        {
            var failed = BuildUsage(metrics, stopwatch);
            throw new ReviewAgentRunException(runId, failed.Usage, failed.Model, exception);
        }
        finally
        {
            if (!disposeDeferred) await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        var completed = BuildUsage(metrics, stopwatch);
        return new ReviewAgentResult(runId, output.ToString(), completed.Usage, completed.Model);
    }

    private static async Task DisposeAfterSettledAsync(IAsyncEnumerator<CliRunEvent> enumerator, Task pendingMoveNext)
    {
        try { await pendingMoveNext.ConfigureAwait(false); } catch { /* already reported as an attach timeout */ }
        try { await enumerator.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort cleanup */ }
    }

    private (TokenUsage Usage, string? Model) BuildUsage(RunMetricsRecorder metrics,
        System.Diagnostics.Stopwatch stopwatch)
    {
        var snapshot = metrics.Build();
        var hasReportedUsage = snapshot.TurnCount > 0;
        return (new TokenUsage(
            hasReportedUsage ? snapshot.TotalInputTokens : null,
            hasReportedUsage ? snapshot.TotalOutputTokens : null,
            hasReportedUsage ? snapshot.TotalCachedInputTokens : null,
            hasReportedUsage ? snapshot.TotalReasoningOutputTokens : null,
            snapshot.TotalDurationMs is double duration ? (long)Math.Round(duration) : stopwatch.ElapsedMilliseconds),
            snapshot.Model ?? Model);
    }
}
