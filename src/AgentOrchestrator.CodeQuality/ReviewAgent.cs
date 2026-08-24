using System.Text;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Metrics;
using CodingAgentRunner.Model;

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
    private readonly string _cliType;
    private readonly string? _thinkingLevel;
    private readonly CliRunner _runner;
    private readonly Action<string, CliRunEvent>? _eventObserver;

    public CodingAgentReviewAgent(string cliType = "codex", string? model = null, string? thinkingLevel = null,
        CliOptions? options = null,
        Action<string, CliRunEvent>? eventObserver = null)
    {
        _cliType = cliType;
        _thinkingLevel = thinkingLevel;
        Model = model;
        _runner = new CliRunner(options ?? new CliOptions());
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
        var attached = false;
        CliRunEvent.RunEnded? ended = null;
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
                    case CliRunEvent.RunStarted:
                        attached = true;
                        break;
                    case CliRunEvent.RunEnded end:
                        ended = end;
                        break;
                    case CliRunEvent.OutputDelta delta:
                        output.Append(delta.Text);
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
            throw new ReviewAgentRunException(runId, failed.Usage, failed.Model, exception);
        }

        var completed = BuildUsage(metrics, stopwatch);
        // A non-Completed outcome is a failed reviewer, not a review. Returning its stderr text
        // as if it were a response hides the real cause behind a downstream JSON parse error.
        if (ended is { Outcome: RunOutcome.Stopped } && cancellationToken.IsCancellationRequested)
        {
            throw new ReviewAgentRunCanceledException(runId, completed.Usage, completed.Model,
                new OperationCanceledException($"The {_cliType} CLI was stopped: {ended.Reason ?? "no reason reported"}."),
                cancellationToken);
        }
        if (ended is null || ended.Outcome != RunOutcome.Completed)
        {
            throw new ReviewAgentRunException(runId, completed.Usage, completed.Model,
                new InvalidOperationException(DescribeFailure(_cliType, attached, ended, output)));
        }
        return new ReviewAgentResult(runId, output.ToString(), completed.Usage, completed.Model);
    }

    private static string DescribeFailure(
        string cliType, bool attached, CliRunEvent.RunEnded? ended, StringBuilder output)
    {
        var detail = Tail(output);
        if (ended is null)
        {
            return attached
                ? $"The {cliType} CLI stream ended without a run-terminal event; the reviewer reported no outcome.{detail}"
                : $"The {cliType} CLI never attached: no reviewer process was observed and the stream ended without a run-terminal event.{detail}";
        }
        var exit = ended.ExitCode is { } code ? $" (exit code {code})" : string.Empty;
        var reason = string.IsNullOrWhiteSpace(ended.Reason) ? string.Empty : $": {ended.Reason}";
        return $"The {cliType} CLI ended with outcome {ended.Outcome}{exit}{reason}.{detail}";
    }

    private static string Tail(StringBuilder output)
    {
        var text = output.ToString().Trim();
        if (text.Length == 0) return string.Empty;
        const int limit = 400;
        return " CLI output: " + (text.Length <= limit ? text : "…" + text[^limit..]);
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
