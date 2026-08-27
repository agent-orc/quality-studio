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
    // How long a run may go without its reviewer process attaching (CliRunEvent.RunStarted)
    // before it is failed loudly. RunWatchdog cannot cover this gap itself: it only starts
    // tracking silence once a run has attached, so a stall inside CLI health-check/spawn
    // (before a process exists) needs its own bound.
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
        var driver = _runner.Get(_cliType);

        var attached = false;
        void MarkAttached(string id, CliRunInfo info) { if (id == runId) attached = true; }
        driver.OnStarted += MarkAttached;
        // The attach-timeout race is independent of the caller's cancellationToken on
        // purpose: a plain user cancellation must still surface as
        // ReviewAgentRunCanceledException from ExecuteAsync's own cancellation handling,
        // not get reinterpreted as an attach timeout just because both fire around the
        // same time.
        using var attachTimeoutCts = new CancellationTokenSource();
        try
        {
            var runTask = ExecuteAsync(driver, runId, prompt, workingDirectory, cancellationToken);
            var attachWatch = Task.Delay(_attachTimeout, attachTimeoutCts.Token);
            var winner = await Task.WhenAny(runTask, attachWatch).ConfigureAwait(false);
            if (winner == runTask || attached)
                return await runTask.ConfigureAwait(false);

            // The reviewer process never attached at all (RunWatchdog has no phase to
            // track yet) — stop waiting on a possibly non-cooperative spawn/health-check
            // and fail loudly instead of wedging the caller's queue indefinitely.
            driver.Stop(runId, RunStopReason.Watchdog);
            throw new ReviewAgentRunException(runId, EmptyUsage(),
                Model, new TimeoutException(
                    $"The {_cliType} reviewer did not attach within {_attachTimeout.TotalSeconds:F0}s."));
        }
        finally
        {
            driver.OnStarted -= MarkAttached;
            attachTimeoutCts.Cancel();
        }
    }

    private async Task<ReviewAgentResult> ExecuteAsync(
        ICliDriver driver,
        string runId,
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var metrics = new RunMetricsRecorder();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        string? hungReason = null;
        void OnHung(string id, RunPhase phase, double silenceSeconds)
        {
            if (id != runId) return;
            hungReason =
                $"The {_cliType} reviewer produced no activity for {silenceSeconds:F0}s while in phase {phase}; the watchdog stopped it.";
        }
        using var watchdog = RunWatchdog.Attach(driver, _watchdogPolicy, autoStop: true);
        watchdog.OnHung += OnHung;
        try
        {
            await foreach (var runEvent in driver.StreamAsync(new CliRunRequest
            {
                RunId = runId,
                Prompt = prompt,
                WorkingDirectory = workingDirectory,
                Model = Model,
                ThinkingLevel = _thinkingLevel,
                PermissionMode = "read-only",
                ContextMode = "shared",
            }, cancellationToken))
            {
                metrics.Observe(runEvent);
                _eventObserver?.Invoke(_cliType, runEvent);
                if (runEvent is CliRunEvent.OutputDelta delta)
                {
                    output.Append(delta.Text);
                }
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
            watchdog.OnHung -= OnHung;
        }

        if (hungReason is not null)
        {
            var failed = BuildUsage(metrics, stopwatch);
            throw new ReviewAgentRunException(runId, failed.Usage, failed.Model, new TimeoutException(hungReason));
        }

        var completed = BuildUsage(metrics, stopwatch);
        return new ReviewAgentResult(runId, output.ToString(), completed.Usage, completed.Model);
    }

    private static TokenUsage EmptyUsage() => new(null, null, null, null, 0);

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
