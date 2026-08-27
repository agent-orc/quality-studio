using System.Text;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Metrics;
using CodingAgentRunner.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger _logger;
    private readonly TimeSpan _attachTimeout;
    private readonly WatchdogPolicy _watchdogPolicy;

    public CodingAgentReviewAgent(string cliType = "codex", string? model = null, string? thinkingLevel = null,
        CliOptions? options = null,
        Action<string, CliRunEvent>? eventObserver = null,
        ILogger? logger = null,
        TimeSpan? attachTimeout = null,
        WatchdogPolicy? watchdogPolicy = null)
    {
        _cliType = cliType;
        _thinkingLevel = thinkingLevel;
        Model = model;
        _logger = logger ?? NullLogger.Instance;
        _runner = new CliRunner(options ?? new CliOptions(), _logger);
        _eventObserver = eventObserver;
        // Covers the gap RunWatchdog structurally cannot see: it only starts tracking a
        // run once the driver's OnStarted fires, so a hang before that (pre-spawn health
        // check, process spawn itself) has no budget without this. Once attached,
        // per-phase silence budgets in _watchdogPolicy take over.
        _attachTimeout = attachTimeout ?? DefaultAttachTimeout;
        _watchdogPolicy = watchdogPolicy ?? WatchdogPolicy.Default;
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
        using var watchdog = RunWatchdog.Attach(driver, _watchdogPolicy, autoStop: true);
        var attachTimedOut = false;

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

        try
        {
            // MoveNextAsync on an async-iterator-backed IAsyncEnumerable can run
            // synchronously up to its first internal await, which would let a blocking
            // pre-spawn prefix starve Task.WhenAny before the race even begins. Task.Run
            // forces that first call onto a pool thread so the attach-timeout can win.
            // The delay is deliberately NOT linked to cancellationToken: a real caller
            // cancellation is observed by firstMoveNext itself (the enumerator was built
            // with this token), which then wins the race and is reported as a cancel, not
            // a timeout. Linking both here would race two cancellation-driven completions
            // and could misreport a real cancel as "did not attach".
            var firstMoveNext = Task.Run(() => enumerator.MoveNextAsync().AsTask(), cancellationToken);
            var attachWinner = await Task.WhenAny(firstMoveNext, Task.Delay(_attachTimeout))
                .ConfigureAwait(false);

            if (attachWinner != firstMoveNext)
            {
                _logger.LogError(
                    "Review run {RunId} ({CliType}) did not attach within {TimeoutSeconds}s of dispatch; treating as a dead run",
                    runId, _cliType, _attachTimeout.TotalSeconds);
                // The enumerator only allows one MoveNextAsync/DisposeAsync in flight at a
                // time, so disposing here (with firstMoveNext still outstanding) would throw
                // and mask the timeout below. Let it settle first, on its own.
                attachTimedOut = true;
                _ = firstMoveNext.ContinueWith(
                    static (task, state) => ((IAsyncDisposable)state!).DisposeAsync(),
                    enumerator, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw new TimeoutException(
                    $"{_cliType} reviewer did not attach (no run-started signal) within {_attachTimeout.TotalSeconds}s.");
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
                if (runEvent is CliRunEvent.RunEnded { Outcome: RunOutcome.Stopped, Reason: "Watchdog" })
                {
                    throw new TimeoutException(
                        $"{_cliType} reviewer was stopped by the watchdog (no activity within its phase budget).");
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
            if (!attachTimedOut)
                await enumerator.DisposeAsync().ConfigureAwait(false);
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
