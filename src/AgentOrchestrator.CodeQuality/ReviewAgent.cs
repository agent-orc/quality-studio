using System.Text;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Metrics;

namespace AgentOrchestrator.CodeQuality;

public interface IReviewAgent
{
    string AgentName { get; }

    string? Model { get; }

    string? ThinkingLevel => null;

    /// <summary>One of the <see cref="ReviewModelSource"/> values; null when the agent does not know.</summary>
    string? ModelSource => null;

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
        Action<string, CliRunEvent>? eventObserver = null,
        string? modelSource = null)
    {
        _cliType = cliType;
        _thinkingLevel = thinkingLevel;
        Model = model;
        ModelSource = modelSource ?? (string.IsNullOrWhiteSpace(model)
            ? ReviewModelSource.RunnerDefault
            : ReviewModelSource.Explicit);
        _runner = new CliRunner(options ?? new CliOptions());
        _eventObserver = eventObserver;
        _runner.Get(cliType); // Fail at construction for unknown adapters.
    }

    /// <summary>
    /// Builds the agent used when a caller supplies none. The model comes from the synchronized
    /// routing policy's route for a single-file review of <paramref name="kind"/>, so sidecars and
    /// the usage ledger name a real model instead of "runner-default". A CLI the policy does not
    /// route keeps the CLI's own default and is recorded as such.
    /// </summary>
    public static CodingAgentReviewAgent CreateDefault(string cliType = "codex", string kind = "code")
    {
        var catalog = ReviewModelCatalog.Default;
        var route = catalog.ResolveDefault(cliType, catalog.Recommend(kind, ReviewLevel.File, 1));
        return route is null
            ? new CodingAgentReviewAgent(cliType, modelSource: ReviewModelSource.RunnerDefault)
            : new CodingAgentReviewAgent(cliType, route.Model, route.ThinkingLevel,
                modelSource: ReviewModelSource.PolicyDefault);
    }

    public string AgentName => _cliType;

    public string? Model { get; }

    public string? ThinkingLevel => _thinkingLevel;

    public string? ModelSource { get; }

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
