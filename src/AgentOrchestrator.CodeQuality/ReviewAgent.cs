using System.Text;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Metrics;
using Microsoft.Extensions.Logging;
using RunOutcome = CodingAgentRunner.Model.RunOutcome;

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

/// <summary>
/// A reviewer run that reached the CLI but ended without completing — killed by the watchdog,
/// stopped by the environment, or failed outright. Surfacing this as a typed error is what keeps
/// a dead reviewer from being reported as a clean, zero-finding review.
/// </summary>
public sealed class ReviewAgentRunAbortedException(string cliType, RunOutcome outcome, string? reason)
    : Exception($"The {cliType} reviewer run ended as {outcome}"
        + (string.IsNullOrWhiteSpace(reason) ? "." : $" ({reason})."))
{
    public string CliType { get; } = cliType;
    public RunOutcome Outcome { get; } = outcome;
    public string? Reason { get; } = reason;
}

/// <summary>
/// A reviewer run whose CLI process never produced its first event within the attach budget —
/// the reviewer never actually started.
/// </summary>
public sealed class ReviewAgentAttachTimeoutException(string cliType, TimeSpan attachTimeout)
    : TimeoutException($"The {cliType} reviewer did not attach within {attachTimeout.TotalSeconds:0.#}s.")
{
    public string CliType { get; } = cliType;
    public TimeSpan AttachTimeout { get; } = attachTimeout;
}

public sealed class CodingAgentReviewAgent : IReviewAgent
{
    /// <summary>
    /// Bounds the gap between requesting a run and the reviewer's first event. Generous enough for
    /// a cold CLI start, short enough that a reviewer which never attaches fails in minutes.
    /// </summary>
    public static readonly TimeSpan DefaultAttachTimeout = TimeSpan.FromSeconds(90);

    private readonly string _cliType;
    private readonly string? _thinkingLevel;
    private readonly CliRunner _runner;
    private readonly Action<string, CliRunEvent>? _eventObserver;
    private readonly ILogger? _logger;
    private readonly WatchdogPolicy? _watchdogPolicy;
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
        _logger = logger;
        _watchdogPolicy = watchdogPolicy;
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
        var request = new CliRunRequest
        {
            RunId = runId,
            Prompt = prompt,
            WorkingDirectory = workingDirectory,
            Model = Model,
            ThinkingLevel = _thinkingLevel,
            PermissionMode = "read-only",
            ContextMode = "shared",
        };
        try
        {
            // autoStop: a reviewer that spawns and then goes silent past its phase budget gets
            // killed, which surfaces below as a Stopped/Watchdog RunEnded event rather than as an
            // empty but "successful" result.
            using var watchdog = RunWatchdog.Attach(driver, _watchdogPolicy, autoStop: true);
            await StreamAsync(driver, request, metrics, output, cancellationToken).ConfigureAwait(false);
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

        var completed = BuildUsage(metrics, stopwatch);
        return new ReviewAgentResult(runId, output.ToString(), completed.Usage, completed.Model);
    }

    private async Task StreamAsync(ICliDriver driver, CliRunRequest request, RunMetricsRecorder metrics,
        StringBuilder output, CancellationToken cancellationToken)
    {
        var enumerator = driver.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var enumeratorAdopted = false;
        try
        {
            // The watchdog only starts tracking a run once the driver reports it started, so it
            // structurally cannot see a reviewer that never attaches at all — the exact failure
            // this race bounds. The timeout is deliberately NOT linked to cancellationToken so a
            // genuine caller cancel is never misreported as an attach timeout.
            //
            // Task.Run matters: MoveNextAsync on an async iterator runs synchronously on the
            // calling thread up to the iterator's first internal await, so a driver that blocks
            // before spawning would keep the race from ever starting.
            var firstMove = Task.Run(async () => await enumerator.MoveNextAsync().ConfigureAwait(false),
                CancellationToken.None);
            if (await Task.WhenAny(firstMove, Task.Delay(_attachTimeout)).ConfigureAwait(false) != firstMove)
            {
                // An async iterator allows only one MoveNextAsync/DisposeAsync in flight, so
                // disposing here would throw and mask the timeout we are trying to report. Hand
                // the enumerator off to be disposed once the pending move finally returns.
                enumeratorAdopted = true;
                DisposeWhenIdle(enumerator, firstMove);
                _logger?.LogError(new EventId(1520, "ReviewerAttachTimeout"),
                    "Reviewer {ReviewCli} did not attach for run {ReviewRunId} within {AttachTimeoutSeconds}s",
                    _cliType, request.RunId, _attachTimeout.TotalSeconds);
                throw new ReviewAgentAttachTimeoutException(_cliType, _attachTimeout);
            }

            var hasEvent = await firstMove.ConfigureAwait(false);
            while (hasEvent)
            {
                Observe(enumerator.Current, metrics, output, cancellationToken);
                hasEvent = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (!enumeratorAdopted)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void Observe(CliRunEvent runEvent, RunMetricsRecorder metrics, StringBuilder output,
        CancellationToken cancellationToken)
    {
        metrics.Observe(runEvent);
        _eventObserver?.Invoke(_cliType, runEvent);
        switch (runEvent)
        {
            case CliRunEvent.OutputDelta delta:
                output.Append(delta.Text);
                break;
            // A run that ends any way other than Completed produced a partial or empty review;
            // reporting that as success is what let dead reviewer runs look like clean
            // zero-finding reviews. A caller cancel stays on the cancellation path.
            case CliRunEvent.RunEnded ended when ended.Outcome is not RunOutcome.Completed:
                cancellationToken.ThrowIfCancellationRequested();
                _logger?.LogError(new EventId(1521, "ReviewerRunAborted"),
                    "Reviewer {ReviewCli} run {ReviewRunId} ended as {RunOutcome} ({RunEndReason})",
                    _cliType, runEvent.RunId, ended.Outcome, ended.Reason ?? "no reason reported");
                throw new ReviewAgentRunAbortedException(_cliType, ended.Outcome, ended.Reason);
        }
    }

    private static void DisposeWhenIdle(IAsyncEnumerator<CliRunEvent> enumerator, Task pending) =>
        _ = pending.ContinueWith(static async (_, state) =>
        {
            try
            {
                await ((IAsyncEnumerator<CliRunEvent>)state!).DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // The run is already being reported as an attach timeout; a failure to clean up
                // the abandoned enumerator must not replace that error.
            }
        }, enumerator, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

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
