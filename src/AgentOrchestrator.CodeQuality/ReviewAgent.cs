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

public sealed record ReviewAgentDiagnostics(
    string? RunId,
    string CliType,
    string? CliPath,
    string? LogDirectory,
    bool ProcessAttached);

public sealed class ReviewAgentRunException(
    string runId, TokenUsage usage, string? effectiveModel, Exception innerException,
    string code = "reviewer_run_failed", bool retryable = true)
    : Exception($"The coding agent run failed: {innerException.Message}", innerException)
{
    public string RunId { get; } = runId;
    public TokenUsage Usage { get; } = usage;
    public string? EffectiveModel { get; } = effectiveModel;
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
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
    private readonly ILogger _logger;
    private readonly IRunLogPathProvider? _logPaths;
    private readonly TaskCompletionSource processAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? activeRunId;
    private string? cliPath;
    private string? logDirectory;
    private int attached;

    public CodingAgentReviewAgent(string cliType = "codex", string? model = null, string? thinkingLevel = null,
        CliOptions? options = null,
        Action<string, CliRunEvent>? eventObserver = null,
        CliRunner? runner = null,
        ILogger? logger = null,
        IRunLogPathProvider? logPaths = null)
    {
        _cliType = cliType;
        _thinkingLevel = thinkingLevel;
        Model = model;
        _logger = logger ?? NullLogger.Instance;
        _logPaths = logPaths;
        _runner = runner ?? new CliRunner(options ?? new CliOptions(), _logger, logPaths);
        _eventObserver = eventObserver;
        _runner.Get(cliType); // Fail at construction for unknown adapters.
    }

    public string AgentName => _cliType;

    public string? Model { get; }

    public Task ProcessAttached => processAttached.Task;

    public ReviewAgentDiagnostics Diagnostics => new(
        Volatile.Read(ref activeRunId),
        _cliType,
        Volatile.Read(ref cliPath),
        Volatile.Read(ref logDirectory),
        Volatile.Read(ref attached) == 1);

    public bool StopActiveRun(RunStopReason reason)
    {
        var runId = Volatile.Read(ref activeRunId);
        return runId is not null && _runner.Get(_cliType).Stop(runId, reason);
    }

    public async Task<ReviewAgentResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var runId = "quality-" + Guid.NewGuid().ToString("N");
        Volatile.Write(ref activeRunId, runId);
        var output = new StringBuilder();
        var metrics = new RunMetricsRecorder();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var driver = _runner.Get(_cliType);
        Volatile.Write(ref cliPath, driver.GetCliPath());
        Volatile.Write(ref logDirectory, _logPaths?.GetRunLogDirectory(runId));
        _logger.LogInformation(new EventId(1600, "ReviewerCliLaunchRequested"),
            "Launching reviewer operation {ReviewerOperationId} via {ReviewerCliType} at {ReviewerCliPath}; logs: {ReviewerLogDirectory}; prompt transport is configured by the host",
            runId, _cliType, cliPath, logDirectory ?? "runner-default");
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
                if (runEvent is CliRunEvent.RunStarted)
                {
                    Volatile.Write(ref attached, 1);
                    processAttached.TrySetResult();
                    _logger.LogInformation(new EventId(1601, "ReviewerCliAttached"),
                        "Reviewer operation {ReviewerOperationId} attached to {ReviewerCliType}", runId, _cliType);
                }
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
            _logger.LogError(new EventId(1602, "ReviewerCliFailed"), exception,
                "Reviewer operation {ReviewerOperationId} failed before completion; process attached: {ReviewerProcessAttached}; CLI path: {ReviewerCliPath}; logs: {ReviewerLogDirectory}",
                runId, Diagnostics.ProcessAttached, cliPath, logDirectory ?? "runner-default");
            throw new ReviewAgentRunException(runId, failed.Usage, failed.Model, exception);
        }

        var completed = BuildUsage(metrics, stopwatch);
        var response = output.ToString();
        if (string.Equals(_cliType, "claude", StringComparison.OrdinalIgnoreCase) &&
            response.TrimStart().StartsWith("Not logged in", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReviewAgentRunException(
                runId,
                completed.Usage,
                completed.Model,
                new InvalidOperationException($"Claude CLI is not authenticated. Run '{_cliType} /login' as the API service account."),
                "reviewer_authentication_required",
                retryable: false);
        }
        return new ReviewAgentResult(runId, response, completed.Usage, completed.Model);
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
