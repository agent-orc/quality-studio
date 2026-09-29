using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public static class ProviderFailureKind
{
    public const string Auth = "auth";
    public const string Provider = "provider";
}

/// <summary>
/// A review operation that failed because the reviewer CLI or its provider did, as opposed to the
/// review itself. <see cref="Signature"/> is the message with run-specific tokens removed, so the
/// same provider fault on two files compares equal.
/// </summary>
public sealed record ProviderFailure(string Kind, string Signature, string Message)
{
    public bool IsAuth => Kind == ProviderFailureKind.Auth;
}

public static partial class ProviderFailureClassifier
{
    /// <summary>
    /// Classifies a failed review operation. Only a failure of the agent run itself is a provider
    /// failure: a refused answer, an edited file or an unreadable path says nothing about the
    /// provider, and counting them would stop a sweep that is working.
    /// </summary>
    public static ProviderFailure? Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!IsAgentRunFailure(exception)) return null;
        var message = Describe(exception);
        var kind = IsAuthMessage(message) ? ProviderFailureKind.Auth : ProviderFailureKind.Provider;
        return new ProviderFailure(kind, kind + ":" + Normalize(message), message);
    }

    /// <summary>True when a provider message says the login or credential was refused.</summary>
    public static bool IsAuthMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message) && AuthFailure().IsMatch(message);

    private static bool IsAgentRunFailure(Exception exception) =>
        exception is ReviewAgentRunException or ReviewAgentRunAbortedException or ReviewAgentAttachTimeoutException;

    /// <summary>The innermost explanation, which is the provider's own words when it gave any.</summary>
    private static string Describe(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null &&
               current is ReviewAgentRunException) current = current.InnerException;
        return current.Message.Trim();
    }

    private static string Normalize(string message) =>
        Whitespace().Replace(Volatile().Replace(message, "*"), " ").Trim();

    [GeneratedRegex(
        @"\b(401|403)\b|unauthori[sz]ed|unauthenticated|authentication|not (logged|signed) in|log ?in (is )?required|please (re-?)?(log|sign) ?in|(codex|claude) (/)?login|invalid[_ ]api[_ ]key|api key (is )?(missing|invalid|not set)|oauth|refresh[_ ]token|(access )?token (has )?expired|expired token|invalid[_ ]grant|credentials?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthFailure();

    // Run ids, GUIDs, timestamps and long hex tokens differ per operation, never per fault.
    [GeneratedRegex(
        @"quality-[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|\b[0-9a-f]{16,}\b|\d{4}-\d{2}-\d{2}T[0-9:.]+Z?|\breq_[A-Za-z0-9]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Volatile();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

/// <summary>What Quality Studio last learned about one provider's login.</summary>
public sealed record ProviderAuthState(
    string Provider,
    string State,
    DateTimeOffset? CheckedAt,
    string? Detail,
    string Source)
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
    public const string ReviewRunSource = "review-run";
    public const string QuotaProbeSource = "quota-probe";
    public const string NoneSource = "none";
}

/// <summary>
/// The provider login state shown next to the quota. A review operation is the evidence: one that
/// reached the model proves the login worked, and one refused with an authentication error proves
/// it did not. A quota probe that failed with an authentication error counts too, unless a review
/// has succeeded since. In memory only: after a restart the state is unknown until the next review.
/// </summary>
public sealed class ProviderAuthStateTracker(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ProviderAuthState> states = new(StringComparer.OrdinalIgnoreCase);

    public void RecordSuccess(string provider) =>
        states[provider] = new ProviderAuthState(provider, ProviderAuthState.Ok, clock.GetUtcNow(), null,
            ProviderAuthState.ReviewRunSource);

    public void RecordFailure(string provider, string detail) =>
        states[provider] = new ProviderAuthState(provider, ProviderAuthState.Failed, clock.GetUtcNow(), detail,
            ProviderAuthState.ReviewRunSource);

    public IReadOnlyCollection<string> Providers => states.Keys.ToArray();

    /// <summary>
    /// The state for <paramref name="provider"/>, combining what reviews observed with the quota
    /// probe's own error for that provider.
    /// </summary>
    public ProviderAuthState Describe(string provider, string? quotaError = null, DateTimeOffset? quotaFetchedAt = null)
    {
        states.TryGetValue(provider, out var observed);
        if (!ProviderFailureClassifier.IsAuthMessage(quotaError))
            return observed ?? new ProviderAuthState(provider, ProviderAuthState.Unknown, null, null,
                ProviderAuthState.NoneSource);
        if (observed is not null && (quotaFetchedAt is null || observed.CheckedAt >= quotaFetchedAt))
            return observed;
        return new ProviderAuthState(provider, ProviderAuthState.Failed, quotaFetchedAt, quotaError,
            ProviderAuthState.QuotaProbeSource);
    }
}
