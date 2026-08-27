using System.Runtime.CompilerServices;
using System.Text;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Metrics;
using CodingAgentRunner.Model;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("AgentOrchestrator.CodeQuality.Tests")]

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
/// Thrown when a run's reviewer process never attaches (no first event) within the
/// agent's attach timeout. The reviewer CLI's pre-spawn health probe can block a whole
/// OS thread forever on a synchronous pipe read, entirely outside any
/// <see cref="CancellationToken"/>'s reach (a known third-party defect — see the
/// N-01 dossier finding); this is the only bound available for that case.
/// </summary>
public sealed class ReviewAgentAttachTimeoutException(string runId, TimeSpan timeout)
    : Exception($"The coding agent run '{runId}' did not attach to a reviewer process within {timeout.TotalSeconds:0}s.")
{
    public string RunId { get; } = runId;
    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>Thrown when a run's terminal event reports it did not complete cleanly (stopped/failed).</summary>
public sealed class ReviewAgentRunAbortedException(string runId, RunOutcome outcome, string? reason)
    : Exception($"The coding agent run '{runId}' ended without completing (outcome: {outcome}" +
                $"{(reason is null ? "" : $", reason: {reason}")}).")
{
    public string RunId { get; } = runId;
    public RunOutcome Outcome { get; } = outcome;
    public string? Reason { get; } = reason;
}

public sealed class CodingAgentReviewAgent : IReviewAgent
{
    /// <summary>
    /// How long a run may go without producing its first event before it's treated as
    /// a dead/never-attached reviewer. Deliberately not tied to the caller's
    /// <see cref="CancellationToken"/> — see <see cref="ReviewAgentAttachTimeoutException"/>.
    /// </summary>
    public static readonly TimeSpan DefaultAttachTimeout = TimeSpan.FromSeconds(90);

    private readonly string _cliType;
    private readonly string? _thinkingLevel;
    private readonly CliRunner? _runner;
    private readonly ICliDriver? _driverOverride;
    private readonly Action<string, CliRunEvent>? _eventObserver;
    private readonly ILogger? _logger;
    private readonly TimeSpan _attachTimeout;

    public CodingAgentReviewAgent(string cliType = "codex", string? model = null, string? thinkingLevel = null,
        CliOptions? options = null,
        Action<string, CliRunEvent>? eventObserver = null,
        ILogger? logger = null,
        TimeSpan? attachTimeout = null)
    {
        _cliType = cliType;
        _thinkingLevel = thinkingLevel;
        Model = model;
        _logger = logger;
        _attachTimeout = attachTimeout ?? DefaultAttachTimeout;
        _runner = new CliRunner(options ?? new CliOptions(), logger);
        _eventObserver = eventObserver;
        _runner.Get(cliType); // Fail at construction for unknown adapters.
    }

    /// <summary>Test seam: drive a caller-supplied driver directly, bypassing CliRunner/process spawn.</summary>
    internal CodingAgentReviewAgent(string cliType, ICliDriver driver, string? model = null,
        string? thinkingLevel = null, Action<string, CliRunEvent>? eventObserver = null,
        ILogger? logger = null, TimeSpan? attachTimeout = null)
    {
        _cliType = cliType;
        _thinkingLevel = thinkingLevel;
        Model = model;
        _logger = logger;
        _attachTimeout = attachTimeout ?? DefaultAttachTimeout;
        _driverOverride = driver;
        _eventObserver = eventObserver;
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
        var driver = _driverOverride ?? _runner!.Get(_cliType);
        using var watchdog = RunWatchdog.Attach(driver, WatchdogPolicy.Default, autoStop: true);
        watchdog.OnHung += (id, phase, silenceSeconds) =>
        {
            if (id == runId)
                _logger?.LogWarning("Review run {RunId} looks hung in phase {Phase} after {SilenceSeconds:0}s of silence; stopping it",
                    runId, phase, silenceSeconds);
        };
        var enumerator = driver.StreamAsync(new CliRunRequest
        {
            RunId = runId,
            Prompt = prompt,
            WorkingDirectory = workingDirectory,
            Model = Model,
            ThinkingLevel = _thinkingLevel,
            PermissionMode = "read-only",
            ContextMode = "shared",
        }, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var attachTimedOut = false;
        try
        {
            // An async iterator runs synchronously up to its first internal await, and the
            // reviewer CLI's pre-spawn health probe can block synchronously before any await
            // is ever reached — so racing that first MoveNextAsync on a background thread
            // against a fixed timeout is the only way to bound it. Later MoveNextAsync calls
            // (post-attach) hit real I/O awaits and already respect cancellationToken normally.
            var attach = Task.Run(() => enumerator.MoveNextAsync().AsTask());
            if (await Task.WhenAny(attach, Task.Delay(_attachTimeout, CancellationToken.None)).ConfigureAwait(false) != attach)
            {
                attachTimedOut = true;
                // The pending MoveNextAsync is still in flight; an async iterator allows only
                // one MoveNextAsync/DisposeAsync at a time, so disposing now would throw and
                // mask this timeout. Defer disposal to a continuation on the abandoned task.
                _ = attach.ContinueWith(async _ =>
                {
                    try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                    catch { /* best-effort cleanup of an abandoned, never-attached run */ }
                }, TaskScheduler.Default);
                throw new ReviewAgentAttachTimeoutException(runId, _attachTimeout);
            }
            var hasCurrent = await attach.ConfigureAwait(false);
            while (hasCurrent)
            {
                var runEvent = enumerator.Current;
                metrics.Observe(runEvent);
                _eventObserver?.Invoke(_cliType, runEvent);
                if (runEvent is CliRunEvent.OutputDelta delta)
                {
                    output.Append(delta.Text);
                }
                if (runEvent is CliRunEvent.RunEnded ended && ended.Outcome != RunOutcome.Completed)
                {
                    throw new ReviewAgentRunAbortedException(runId, ended.Outcome, ended.Reason);
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
            if (!attachTimedOut) await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        var completed = BuildUsage(metrics, stopwatch);
        return new ReviewAgentResult(runId, output.ToString(), completed.Usage, completed.Model);
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
