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
    string runId, TokenUsage usage, string? effectiveModel, Exception innerException,
    string code = "reviewer_run_failed", string? cliType = null, string? executable = null,
    string? logDirectory = null)
    : Exception($"The coding agent run failed: {innerException.Message}", innerException)
{
    public string RunId { get; } = runId;
    public TokenUsage Usage { get; } = usage;
    public string? EffectiveModel { get; } = effectiveModel;
    public string Code { get; } = code;
    public string? CliType { get; } = cliType;
    public string? Executable { get; } = executable;
    public string? LogDirectory { get; } = logDirectory;
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
    private readonly ReviewRunLogPathProvider _logPaths;

    public CodingAgentReviewAgent(string cliType = "codex", string? model = null, string? thinkingLevel = null,
        CliOptions? options = null,
        Action<string, CliRunEvent>? eventObserver = null,
        string? runLogRoot = null,
        ILogger? logger = null)
    {
        _cliType = cliType;
        _thinkingLevel = thinkingLevel;
        Model = model;
        _logger = logger ?? NullLogger.Instance;
        _logPaths = new ReviewRunLogPathProvider(runLogRoot ?? Path.Combine(
            Path.GetTempPath(), "quality-studio", "reviewer-logs"));
        // Claude's argv transport cannot carry normal Quality Studio review prompts on Windows:
        // CreateProcess is capped at 32,767 characters. Stdin is also absent from process listings.
        var resolvedOptions = (options ?? new CliOptions()) with
        {
            ClaudePromptTransport = ClaudePromptTransport.Stdin,
        };
        _runner = new CliRunner(resolvedOptions, _logger, _logPaths, new ReviewUserHomeProvider());
        _eventObserver = eventObserver;
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
        var configuredExecutable = driver.GetCliPath();
        var probe = driver.TestCliPath(configuredExecutable);
        var resolvedExecutable = string.IsNullOrWhiteSpace(probe.Item3) ? configuredExecutable : probe.Item3;
        var logDirectory = _logPaths.GetRunLogDirectory(runId);
        _logger.LogInformation(new EventId(2200, "ReviewerCliProbed"),
            "Probing reviewer CLI {ReviewerCli} at {ReviewerExecutable}; prompt transport {PromptTransport}, prompt characters {PromptCharacters}, logs {ReviewerLogDirectory}",
            _cliType, resolvedExecutable, _cliType == "claude" ? "stdin" : "native", prompt.Length, logDirectory);
        if (!probe.Item1)
        {
            var unavailable = new ReviewerLifecycleException(
                "reviewer_cli_unavailable",
                $"Reviewer CLI '{_cliType}' is unavailable at '{resolvedExecutable}': {probe.Item2}",
                resolvedExecutable,
                logDirectory);
            throw new ReviewAgentRunException(runId, BuildUsage(metrics, stopwatch).Usage, Model, unavailable,
                unavailable.Code, _cliType, unavailable.Executable, unavailable.LogDirectory);
        }

        CliRunEvent.RunEnded? terminalEvent = null;
        string? turnFailure = null;
        using var watchdog = RunWatchdog.Attach(driver, WatchdogPolicy.Default, autoStop: true);
        watchdog.OnHung += (hungRunId, phase, silenceSeconds) =>
            _logger.LogError(new EventId(2203, "ReviewerWatchdogHung"),
                "Reviewer run {ReviewerRunId} was reclaimed in phase {ReviewerPhase} after {SilenceSeconds:F1} seconds without activity",
                hungRunId, phase, silenceSeconds);
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
                switch (runEvent)
                {
                    case CliRunEvent.RunStarted started:
                        _logger.LogInformation(new EventId(2201, "ReviewerProcessAttached"),
                            "Reviewer run {ReviewerRunId} attached process {ReviewerProcessId} via {ReviewerCli}; logs {ReviewerLogDirectory}",
                            runId, started.ProcessId, _cliType, logDirectory);
                        break;
                    case CliRunEvent.OutputDelta delta:
                        output.Append(delta.Text);
                        break;
                    case CliRunEvent.RunEnded ended:
                        terminalEvent = ended;
                        _logger.LogInformation(new EventId(2202, "ReviewerProcessEnded"),
                            "Reviewer run {ReviewerRunId} ended with {ReviewerOutcome}, exit {ReviewerExitCode}, reason {ReviewerReason}",
                            runId, ended.Outcome, ended.ExitCode, ended.Reason);
                        break;
                    case CliRunEvent.TurnFailed failed:
                        turnFailure = failed.Reason;
                        break;
                }
            }

            if (terminalEvent is null)
                throw new ReviewerLifecycleException("reviewer_terminal_event_missing",
                    "Reviewer event stream ended without a terminal process event.", resolvedExecutable, logDirectory);
            if (terminalEvent.Outcome == RunOutcome.Failed)
            {
                var authenticationFailed = ContainsAuthenticationFailure(turnFailure) ||
                                           ContainsAuthenticationFailure(output.ToString());
                throw new ReviewerLifecycleException(
                    authenticationFailed ? "reviewer_authentication_failed" : "reviewer_process_failed",
                    authenticationFailed
                        ? $"Reviewer CLI '{_cliType}' is not authenticated. Run `claude auth login` for the API host identity."
                        : $"Reviewer process failed (exit {terminalEvent.ExitCode?.ToString() ?? "unknown"}): {turnFailure ?? terminalEvent.Reason ?? "no diagnostic"}",
                    resolvedExecutable, logDirectory);
            }
            if (terminalEvent.Outcome == RunOutcome.Stopped && !cancellationToken.IsCancellationRequested)
                throw new ReviewerLifecycleException("reviewer_watchdog_reclaimed",
                    $"Reviewer process was stopped by its watchdog: {terminalEvent.Reason ?? "no diagnostic"}",
                    resolvedExecutable, logDirectory);
            if (output.Length == 0)
                throw new ReviewerLifecycleException("reviewer_empty_response",
                    "Reviewer process completed without producing a response.", resolvedExecutable, logDirectory);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            var canceled = BuildUsage(metrics, stopwatch);
            throw new ReviewAgentRunCanceledException(runId, canceled.Usage, canceled.Model, exception, cancellationToken);
        }
        catch (Exception exception)
        {
            var failed = BuildUsage(metrics, stopwatch);
            var lifecycle = exception as ReviewerLifecycleException;
            throw new ReviewAgentRunException(runId, failed.Usage, failed.Model, exception,
                lifecycle?.Code ?? "reviewer_run_failed", _cliType,
                lifecycle?.Executable ?? resolvedExecutable, lifecycle?.LogDirectory ?? logDirectory);
        }

        var completed = BuildUsage(metrics, stopwatch);
        return new ReviewAgentResult(runId, output.ToString(), completed.Usage, completed.Model);
    }

    private static bool ContainsAuthenticationFailure(string? value) =>
        value?.Contains("not logged in", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("authentication_failed", StringComparison.OrdinalIgnoreCase) == true;

    private sealed class ReviewerLifecycleException(
        string code,
        string message,
        string executable,
        string logDirectory) : Exception(message)
    {
        public string Code { get; } = code;
        public string Executable { get; } = executable;
        public string LogDirectory { get; } = logDirectory;
    }

    private sealed class ReviewRunLogPathProvider(string root) : IRunLogPathProvider
    {
        private readonly string root = Path.GetFullPath(root);

        public string GetRunLogDirectory(string runId)
        {
            ValidateRunId(runId);
            return Path.Combine(root, runId);
        }

        public string GetActiveJobsFile() => Path.Combine(root, "active-jobs.json");

        private static void ValidateRunId(string runId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
                runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
                throw new ArgumentException("A reviewer run id cannot contain path separators.", nameof(runId));
        }
    }

    private sealed class ReviewUserHomeProvider : IUserHomeProvider
    {
        public string GetUserHome() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public string GetTempRoot() => Path.GetTempPath();
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
