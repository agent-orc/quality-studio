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
    string runId, TokenUsage usage, string? effectiveModel, string code, string stage,
    string cliType, string configuredExecutable, string logDirectory, Exception innerException)
    : Exception(
        $"The {cliType} reviewer failed during {stage} ({code}): {innerException.Message} " +
        $"Configured executable: '{configuredExecutable}'. Runner logs: '{logDirectory}'.",
        innerException)
{
    public ReviewAgentRunException(
        string runId, TokenUsage usage, string? effectiveModel, Exception innerException)
        : this(runId, usage, effectiveModel, "reviewer-run-failed", "run", "unknown",
            "unknown", "unknown", innerException)
    {
    }

    public string RunId { get; } = runId;
    public TokenUsage Usage { get; } = usage;
    public string? EffectiveModel { get; } = effectiveModel;
    public string Code { get; } = code;
    public string Stage { get; } = stage;
    public string CliType { get; } = cliType;
    public string ConfiguredExecutable { get; } = configuredExecutable;
    public string LogDirectory { get; } = logDirectory;
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
    private readonly ICliDriver _driver;
    private readonly Action<string, CliRunEvent>? _eventObserver;
    private readonly ILogger _logger;

    public CodingAgentReviewAgent(string cliType = "codex", string? model = null, string? thinkingLevel = null,
        CliOptions? options = null,
        Action<string, CliRunEvent>? eventObserver = null,
        ILogger? logger = null)
    {
        _cliType = cliType;
        _thinkingLevel = thinkingLevel;
        Model = model;
        _logger = logger ?? NullLogger.Instance;
        _runner = new CliRunner(options ?? new CliOptions(), _logger);
        _eventObserver = eventObserver;
        _driver = _runner.Get(cliType); // Fail at construction for unknown adapters.
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
        var configuredExecutable = _driver.GetCliPath();
        var logDirectory = Path.Combine(Path.GetTempPath(), "coding-agent-runner", "cli-output", runId);
        string? turnFailure = null;
        CliRunEvent.RunEnded? runEnded = null;
        _logger.LogInformation(
            "Launching {ReviewerCli} reviewer {ReviewerRunId} with configured executable {ReviewerExecutable}; runner logs: {ReviewerLogDirectory}",
            _cliType, runId, configuredExecutable, logDirectory);
        try
        {
            await foreach (var runEvent in _driver.StreamAsync(new CliRunRequest
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
                switch (runEvent)
                {
                    case CliRunEvent.RunStarted started:
                        _logger.LogInformation(
                            "Attached {ReviewerCli} reviewer {ReviewerRunId} to PID {ReviewerProcessId} using {ReviewerExecutable}; runner logs: {ReviewerLogDirectory}",
                            _cliType, runId, started.ProcessId, configuredExecutable, logDirectory);
                        break;
                    case CliRunEvent.OutputDelta delta:
                        output.Append(delta.Text);
                        break;
                    case CliRunEvent.TurnFailed failed:
                        turnFailure = failed.Reason;
                        break;
                    case CliRunEvent.RunEnded ended:
                        runEnded = ended;
                        break;
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
            throw Failure(runId, failed.Usage, failed.Model, "reviewer-launch-or-stream-failed", "launch-or-stream",
                configuredExecutable, logDirectory, exception);
        }

        var completed = BuildUsage(metrics, stopwatch);
        if (!string.IsNullOrWhiteSpace(turnFailure))
        {
            var outputText = output.ToString();
            var failureDetail = string.IsNullOrWhiteSpace(outputText) ? turnFailure : outputText.Trim();
            var code = failureDetail.Contains("logged in", StringComparison.OrdinalIgnoreCase) ||
                       failureDetail.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                ? "reviewer-authentication-failed"
                : "reviewer-turn-failed";
            throw Failure(runId, completed.Usage, completed.Model, code, "review-turn",
                configuredExecutable, logDirectory, new InvalidOperationException(failureDetail));
        }
        if (runEnded is { Outcome: RunOutcome.Failed } failedRun)
        {
            throw Failure(runId, completed.Usage, completed.Model, "reviewer-process-exited", "process-exit",
                configuredExecutable, logDirectory,
                new InvalidOperationException(failedRun.Reason ?? $"Reviewer exited with code {failedRun.ExitCode}."));
        }
        return new ReviewAgentResult(runId, output.ToString(), completed.Usage, completed.Model);
    }

    private ReviewAgentRunException Failure(
        string runId,
        TokenUsage usage,
        string? effectiveModel,
        string code,
        string stage,
        string configuredExecutable,
        string logDirectory,
        Exception exception) =>
        new(runId, usage, effectiveModel, code, stage, _cliType, configuredExecutable, logDirectory, exception);

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
