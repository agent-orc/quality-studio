namespace AgentOrchestrator.CodeQuality;

/// <summary>What one agent run consumed, reported whether or not the run produced a response.</summary>
public sealed record ReviewAgentUsage(string RunId, TokenUsage Usage, string? EffectiveModel);

/// <summary>A completed agent run: its identity, its answer, and what it cost.</summary>
public sealed record ReviewAgentOutcome(string RunId, string Response, TokenUsage Usage, string? EffectiveModel);

/// <summary>
/// The caller-specific halves of one agent-backed review: how to parse the answer, how to tell
/// whether the subject moved while the agent was reading it, and how to persist the result.
/// Preparation happens before this record is built, because a caller decides for itself what it
/// prepares and how much of it the drift check has to redo.
/// </summary>
public sealed record ReviewExecution<TResult>(
    string Prompt,
    string WorkingDirectory,
    Func<TokenUsage> UsageWhenUnreported,
    Func<ReviewAgentUsage, Task> RecordUsageAsync,
    Action<ReviewAgentOutcome> ParseResponse,
    Func<CancellationToken, Task<string?>> DriftReasonAsync,
    Func<ReviewAgentOutcome, CancellationToken, Task<TResult>> PersistAsync);

/// <summary>
/// The order every agent-backed review follows: run the agent, record what it consumed, parse its
/// answer, refuse the result if the subject changed underneath it, and only then persist.
/// </summary>
public sealed class ReviewExecutionPipeline(IReviewAgent agent)
{
    public async Task<TResult> ExecuteAsync<TResult>(
        ReviewExecution<TResult> execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var outcome = await RunAsync(
            execution.Prompt, execution.WorkingDirectory, execution.UsageWhenUnreported,
            execution.RecordUsageAsync, cancellationToken).ConfigureAwait(false);
        execution.ParseResponse(outcome);
        if (await execution.DriftReasonAsync(cancellationToken).ConfigureAwait(false) is { } reason)
        {
            throw new ReviewRunException(reason);
        }

        return await execution.PersistAsync(outcome, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the agent and reports what it consumed on every exit — an answer, a failure, or a
    /// cancellation — before the failure propagates. The tokens are spent either way, so a review
    /// that is abandoned halfway still shows up in the usage ledger it charged.
    /// </summary>
    public async Task<ReviewAgentOutcome> RunAsync(
        string prompt,
        string workingDirectory,
        Func<TokenUsage> usageWhenUnreported,
        Func<ReviewAgentUsage, Task> recordUsageAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageWhenUnreported);
        ArgumentNullException.ThrowIfNull(recordUsageAsync);
        ReviewAgentResult result;
        try
        {
            result = await agent.RunAsync(prompt, workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (ReviewAgentRunCanceledException exception)
        {
            await recordUsageAsync(new ReviewAgentUsage(
                exception.RunId, exception.Usage, exception.EffectiveModel)).ConfigureAwait(false);
            throw;
        }
        catch (ReviewAgentRunException exception)
        {
            await recordUsageAsync(new ReviewAgentUsage(
                exception.RunId, exception.Usage, exception.EffectiveModel)).ConfigureAwait(false);
            throw;
        }

        var outcome = new ReviewAgentOutcome(
            result.RunId, result.Response, result.Usage ?? usageWhenUnreported(), result.EffectiveModel);
        await recordUsageAsync(new ReviewAgentUsage(
            outcome.RunId, outcome.Usage, outcome.EffectiveModel)).ConfigureAwait(false);
        return outcome;
    }
}
