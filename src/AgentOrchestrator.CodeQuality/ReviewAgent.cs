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
    /// <summary>
    /// Wall-clock cap for a single review run. Dossier S1 exit criterion: "Enforce
    /// wall-clock … quotas" (docs/operations/security/index.html §08 S1) — nothing
    /// upstream previously bounded how long a run could stay open.
    /// </summary>
    public static readonly TimeSpan DefaultRunTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Output-byte cap for a single review run (approximated by accumulated
    /// character count). Dossier S1 exit criterion: "Enforce … output-byte …
    /// quotas" — previously <see cref="System.Text.StringBuilder"/> output grew
    /// unbounded for the lifetime of the run.
    /// </summary>
    public const int DefaultMaxOutputChars = 2_000_000;

    private readonly string _cliType;
    private readonly string? _thinkingLevel;
    private readonly CliRunner _runner;
    private readonly Action<string, CliRunEvent>? _eventObserver;
    private readonly TimeSpan _runTimeout;
    private readonly int _maxOutputChars;

    public CodingAgentReviewAgent(string cliType = "codex", string? model = null, string? thinkingLevel = null,
        CliOptions? options = null,
        Action<string, CliRunEvent>? eventObserver = null,
        TimeSpan? runTimeout = null,
        int? maxOutputChars = null)
    {
        _cliType = cliType;
        _thinkingLevel = thinkingLevel;
        Model = model;
        _runner = new CliRunner(options ?? new CliOptions());
        _eventObserver = eventObserver;
        _runTimeout = runTimeout is { } timeout && timeout > TimeSpan.Zero ? timeout : DefaultRunTimeout;
        _maxOutputChars = maxOutputChars is > 0 ? maxOutputChars.Value : DefaultMaxOutputChars;
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
        using var limitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limitCts.CancelAfter(_runTimeout);
        var outputCapExceeded = false;
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
                ContextMode = CliContextModes.Clean,
            }, limitCts.Token))
            {
                metrics.Observe(runEvent);
                _eventObserver?.Invoke(_cliType, runEvent);
                if (runEvent is CliRunEvent.OutputDelta delta)
                {
                    output.Append(delta.Text);
                    if (!outputCapExceeded && output.Length > _maxOutputChars)
                    {
                        outputCapExceeded = true;
                        limitCts.Cancel();
                    }
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            var partial = BuildUsage(metrics, stopwatch);
            if (outputCapExceeded)
            {
                throw new ReviewAgentRunException(runId, partial.Usage, partial.Model,
                    new InvalidOperationException(
                        $"The coding agent run exceeded its {_maxOutputChars}-character output cap and was stopped."));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new ReviewAgentRunCanceledException(runId, partial.Usage, partial.Model, exception, cancellationToken);
            }

            throw new ReviewAgentRunException(runId, partial.Usage, partial.Model,
                new TimeoutException($"The coding agent run exceeded its {_runTimeout} wall-clock limit and was stopped."));
        }
        catch (Exception exception)
        {
            var failed = BuildUsage(metrics, stopwatch);
            throw new ReviewAgentRunException(runId, failed.Usage, failed.Model, exception);
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
