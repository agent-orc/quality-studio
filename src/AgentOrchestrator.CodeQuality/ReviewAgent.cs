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
    public string ErrorCode { get; } = innerException is ReviewAgentCliException cliFailure
        ? cliFailure.Code
        : "reviewer_run_failed";
}

public sealed class ReviewAgentCliException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
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
    private readonly string _cliType;
    private readonly string? _thinkingLevel;
    private readonly CliRunner _runner;
    private readonly Action<string, CliRunEvent>? _eventObserver;
    private readonly ILogger? _logger;
    private readonly TimeSpan _reviewerAttachTimeout;

    public CodingAgentReviewAgent(string cliType = "codex", string? model = null, string? thinkingLevel = null,
        CliOptions? options = null,
        Action<string, CliRunEvent>? eventObserver = null,
        ILogger? logger = null,
        TimeSpan? reviewerAttachTimeout = null)
    {
        _cliType = cliType;
        _thinkingLevel = thinkingLevel;
        Model = model;
        _runner = new CliRunner(options ?? new CliOptions(), logger);
        _eventObserver = eventObserver;
        _logger = logger;
        _reviewerAttachTimeout = reviewerAttachTimeout ?? TimeSpan.FromSeconds(30);
        if (_reviewerAttachTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reviewerAttachTimeout), "The reviewer attach timeout must be positive.");
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
        string? terminalFailure = null;
        CliRunEvent.RunEnded? ended = null;
        var attached = false;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var driver = _runner.Get(_cliType);
        _logger?.LogInformation(
            "Launching reviewer {ReviewerRunId} via {ReviewerCliPath} in {WorkingDirectory}",
            runId, driver.GetCliPath(), workingDirectory);
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stream = driver.StreamAsync(new CliRunRequest
        {
            RunId = runId,
            Prompt = prompt,
            WorkingDirectory = workingDirectory,
            Model = Model,
            ThinkingLevel = _thinkingLevel,
            PermissionMode = "read-only",
            ContextMode = "shared",
        }, streamCancellation.Token);
        var enumerator = stream.GetAsyncEnumerator(streamCancellation.Token);
        var disposeEnumerator = true;
        Task<bool>? pendingMoveNext = null;
        try
        {
            while (true)
            {
                pendingMoveNext = enumerator.MoveNextAsync().AsTask();
                bool hasEvent;
                try
                {
                    hasEvent = attached
                        ? await pendingMoveNext.ConfigureAwait(false)
                        : await pendingMoveNext.WaitAsync(_reviewerAttachTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException exception)
                {
                    disposeEnumerator = false;
                    driver.Stop(runId, RunStopReason.Watchdog);
                    streamCancellation.Cancel();
                    ObserveAbandonedMoveNext(pendingMoveNext);
                    pendingMoveNext = null;
                    throw new ReviewAgentCliException("reviewer_attach_timeout",
                        $"Reviewer process did not attach within the {_reviewerAttachTimeout} startup wall-clock limit.",
                        exception);
                }
                pendingMoveNext = null;
                if (!hasEvent) break;

                var runEvent = enumerator.Current;
                metrics.Observe(runEvent);
                _eventObserver?.Invoke(_cliType, runEvent);
                if (runEvent is CliRunEvent.RunStarted) attached = true;
                if (runEvent is CliRunEvent.TurnFailed failed) terminalFailure = failed.Reason;
                if (runEvent is CliRunEvent.RunEnded terminal) ended = terminal;
                if (runEvent is CliRunEvent.OutputDelta delta)
                {
                    output.Append(delta.Text);
                }
            }
            if (ended is null || ended.Outcome != RunOutcome.Completed || terminalFailure is not null)
            {
                var response = output.ToString();
                var detail = string.Join(" ", new[] { terminalFailure, ended?.Reason, response.Trim() }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                var authenticationFailed = detail.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
                                           detail.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                                           detail.Contains("login", StringComparison.OrdinalIgnoreCase);
                var code = authenticationFailed ? "reviewer_authentication_failed" : "reviewer_cli_failed";
                var outcome = ended is null
                    ? "ended without a terminal event"
                    : $"ended with outcome {ended.Outcome}" +
                      (ended.ExitCode is { } exitCode ? $" (exit code {exitCode})" : string.Empty);
                throw new ReviewAgentCliException(code,
                    $"Reviewer CLI {outcome}: {Tail(detail)}");
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            disposeEnumerator = false;
            driver.Stop(runId, RunStopReason.Cancelled);
            streamCancellation.Cancel();
            if (pendingMoveNext is not null) ObserveAbandonedMoveNext(pendingMoveNext);
            var canceled = BuildUsage(metrics, stopwatch);
            throw new ReviewAgentRunCanceledException(runId, canceled.Usage, canceled.Model, exception, cancellationToken);
        }
        catch (ReviewAgentRunException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failed = BuildUsage(metrics, stopwatch);
            throw new ReviewAgentRunException(runId, failed.Usage, failed.Model, exception);
        }
        finally
        {
            if (disposeEnumerator) await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        var completed = BuildUsage(metrics, stopwatch);
        return new ReviewAgentResult(runId, output.ToString(), completed.Usage, completed.Model);
    }

    private static string Tail(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "no diagnostic output was reported";
        const int limit = 800;
        return value.Length <= limit ? value : "…" + value[^limit..];
    }

    private static void ObserveAbandonedMoveNext(Task task) =>
        _ = task.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

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
