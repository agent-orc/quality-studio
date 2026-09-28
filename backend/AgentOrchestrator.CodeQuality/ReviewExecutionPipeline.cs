namespace AgentOrchestrator.CodeQuality;

/// <summary>What one agent run consumed, reported whether or not the run produced a response.</summary>
public sealed record ReviewAgentUsage(string RunId, TokenUsage Usage, string? EffectiveModel);

/// <summary>A completed agent run: its identity, its answer, and what it cost.</summary>
public sealed record ReviewAgentOutcome(string RunId, string Response, TokenUsage Usage, string? EffectiveModel,
    ReviewerContext? Context = null);

/// <summary>
/// One agent answer the response parser refused. The raw answer is kept, capped at
/// <see cref="MaxResponseCharacters"/> as its head and tail, so a rejected review can be diagnosed
/// from the run journal instead of only from the one-line error.
/// </summary>
public sealed record ReviewResponseRejection(
    string AgentRunId,
    int Attempt,
    bool Retried,
    string Error,
    string Response,
    int ResponseCharacters,
    bool Truncated,
    DateTimeOffset RejectedAt,
    string? Path = null,
    string? Kind = null,
    string? Level = null)
{
    public const int MaxResponseCharacters = 32_768;

    public static ReviewResponseRejection Capture(
        string agentRunId, int attempt, bool retried, string error, string response, DateTimeOffset rejectedAt)
    {
        response ??= string.Empty;
        if (response.Length <= MaxResponseCharacters)
            return new(agentRunId, attempt, retried, error, response, response.Length, false, rejectedAt);

        // Both ends matter: a truncated answer fails at its tail, a wrong shape shows at its head.
        const string marker = "\n[... truncated by Quality Studio ...]\n";
        var tail = MaxResponseCharacters / 4;
        var head = MaxResponseCharacters - tail - marker.Length;
        return new(agentRunId, attempt, retried, error,
            response[..head] + marker + response[^tail..], response.Length, true, rejectedAt);
    }
}

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
    Func<ReviewAgentOutcome, CancellationToken, Task<TResult>> PersistAsync,
    Func<ReviewResponseRejection, Task>? RecordRejectionAsync = null);

/// <summary>
/// The order every agent-backed review follows: run the agent, record what it consumed, parse its
/// answer — asking once more with the rejection reason when the answer breaks the output contract —
/// refuse the result if the subject changed underneath it, and only then persist.
/// </summary>
public sealed class ReviewExecutionPipeline(IReviewAgent agent)
{
    /// <summary>
    /// How many times a rejected answer is followed by a repair prompt. One: a model that breaks the
    /// contract twice in a row for the same prompt is not converging, and each attempt is a full run.
    /// </summary>
    public const int RepairAttempts = 1;

    private const int MaxRepairReasonCharacters = 600;

    public async Task<TResult> ExecuteAsync<TResult>(
        ReviewExecution<TResult> execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var outcome = await RunAsync(
            execution.Prompt, execution.WorkingDirectory, execution.UsageWhenUnreported,
            execution.RecordUsageAsync, cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                execution.ParseResponse(outcome);
                break;
            }
            catch (ReviewResponseException exception)
            {
                var retry = attempt <= RepairAttempts;
                if (execution.RecordRejectionAsync is { } recordRejection)
                {
                    await recordRejection(ReviewResponseRejection.Capture(
                        outcome.RunId, attempt, retry, exception.Message, outcome.Response,
                        DateTimeOffset.UtcNow)).ConfigureAwait(false);
                }
                if (!retry)
                {
                    throw attempt == 1
                        ? exception
                        : new ReviewResponseException(
                            $"{exception.Message} (The answer was rejected again after {attempt - 1} repair attempt.)",
                            exception);
                }

                outcome = await RunAsync(
                    RepairPrompt(execution.Prompt, exception.Message), execution.WorkingDirectory,
                    execution.UsageWhenUnreported, execution.RecordUsageAsync, cancellationToken).ConfigureAwait(false);
            }
        }

        if (await execution.DriftReasonAsync(cancellationToken).ConfigureAwait(false) is { } reason)
        {
            throw new ReviewRunException(reason);
        }

        return await execution.PersistAsync(outcome, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The original prompt followed by why its answer was refused. The whole prompt is sent again
    /// rather than only the rejected answer: the agent must be able to redo the review, not merely
    /// patch syntax, and a glitched answer usually carries nothing worth patching.
    /// </summary>
    internal static string RepairPrompt(string prompt, string reason)
    {
        var shortReason = reason.Length <= MaxRepairReasonCharacters ? reason : reason[..MaxRepairReasonCharacters] + " ...";
        return prompt + """


## Your previous answer was rejected

Quality Studio could not accept your previous answer for this exact request. The validator reported:

""" + shortReason + """


Answer the request above again from the beginning. Return exactly one JSON object that satisfies the output
format required above, and nothing else: no prose before or after it and no second JSON block.
""";
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
            result.RunId, result.Response, result.Usage ?? usageWhenUnreported(), result.EffectiveModel,
            result.Context);
        await recordUsageAsync(new ReviewAgentUsage(
            outcome.RunId, outcome.Usage, outcome.EffectiveModel)).ConfigureAwait(false);
        return outcome;
    }
}
