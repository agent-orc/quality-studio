using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>A browser session opened by an Agent Studio hand-off.</summary>
public sealed class FederatedSession
{
    private long lastSeenTicks;

    internal FederatedSession(HandoffClaims claims, string csrfToken, DateTimeOffset createdAt)
    {
        Subject = claims.Subject;
        SessionId = claims.SessionId;
        Role = claims.Role;
        Repositories = claims.Repositories;
        CsrfToken = csrfToken;
        CreatedAt = createdAt;
        lastSeenTicks = createdAt.UtcTicks;
    }

    public string Subject { get; }

    /// <summary>The Agent Studio session (<c>sid</c>) this session was derived from; revocation names it.</summary>
    public string SessionId { get; }
    public FederatedRole Role { get; }
    public IReadOnlySet<string> Repositories { get; }

    /// <summary>The value unsafe requests must repeat in <see cref="FederatedSessionStore.CsrfHeader"/>.</summary>
    public string CsrfToken { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastSeenAt => new(Interlocked.Read(ref lastSeenTicks), TimeSpan.Zero);

    internal void Touch(DateTimeOffset now) => Interlocked.Exchange(ref lastSeenTicks, now.UtcTicks);

    public bool CsrfMatches(string? supplied) =>
        supplied is { Length: > 0 } &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(CsrfToken));
}

/// <summary>
/// Holds Quality Studio's own federated sessions: idle 60 minutes, absolute 12 hours. The cookie
/// carries only a random handle; the store keys sessions by its SHA-256, so neither a log line nor a
/// memory dump of the dictionary yields a usable cookie. Sessions live in memory: a restart ends them
/// and the next page load signs in through Agent Studio again.
/// </summary>
public sealed class FederatedSessionStore(TimeProvider time)
{
    public const string CookieName = "__Host-qs-session";
    public const string CsrfCookieName = "__Host-qs-csrf";
    public const string CsrfHeader = "X-CSRF-Token";
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromHours(12);

    /// <summary>
    /// How long a revocation keeps refusing hand-offs issued before it: longer than any assertion
    /// can live, so an assertion minted just before a logout cannot open a session after it.
    /// </summary>
    private static readonly TimeSpan RevocationMemory =
        AgentStudioFederation.MaximumLifetime + AgentStudioFederation.ClockSkew + AgentStudioFederation.ClockSkew;

    private readonly ConcurrentDictionary<string, FederatedSession> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> revokedSubjects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> revokedSessionIds = new(StringComparer.Ordinal);
    private readonly object revocationGate = new();

    public (string CookieValue, FederatedSession Session)? TryCreate(HandoffClaims claims)
    {
        var now = time.GetUtcNow();
        lock (revocationGate)
        {
            Prune(now);
            // Revocation can arrive while signature verification and replay recording are in flight.
            // Check again at creation under the same gate used to revoke existing sessions.
            if (IsRevoked(claims.Subject, claims.SessionId, claims.IssuedAt)) return null;
            var cookieValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var csrf = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var session = new FederatedSession(claims, csrf, now);
            sessions[Key(cookieValue)] = session;
            return (cookieValue, session);
        }
    }

    /// <summary>The live session behind <paramref name="cookieValue"/>, or null; a hit slides the idle window.</summary>
    public FederatedSession? Find(string? cookieValue)
    {
        if (string.IsNullOrEmpty(cookieValue) || cookieValue.Length > 128) return null;
        var key = Key(cookieValue);
        if (!sessions.TryGetValue(key, out var session)) return null;
        var now = time.GetUtcNow();
        if (now - session.LastSeenAt > IdleTimeout || now - session.CreatedAt > AbsoluteTimeout)
        {
            sessions.TryRemove(key, out _);
            return null;
        }
        session.Touch(now);
        return session;
    }

    /// <summary>Ends every session of the Agent Studio session <paramref name="sessionId"/> and/or the user <paramref name="subject"/>.</summary>
    public int Revoke(string? sessionId, string? subject)
    {
        var now = time.GetUtcNow();
        lock (revocationGate)
        {
            Prune(now);
            if (!string.IsNullOrEmpty(sessionId)) revokedSessionIds[sessionId] = now;
            if (!string.IsNullOrEmpty(subject)) revokedSubjects[subject] = now;
            var ended = 0;
            foreach (var (key, session) in sessions)
            {
                var matches = (!string.IsNullOrEmpty(sessionId) && session.SessionId == sessionId) ||
                              (!string.IsNullOrEmpty(subject) && session.Subject == subject);
                if (matches && sessions.TryRemove(key, out _)) ended++;
            }
            return ended;
        }
    }

    /// <summary>True when a revocation after <paramref name="issuedAt"/> covers this user or Studio session.</summary>
    public bool IsRevoked(string subject, string sessionId, DateTimeOffset issuedAt) =>
        (revokedSubjects.TryGetValue(subject, out var subjectRevoked) && issuedAt <= subjectRevoked) ||
        (revokedSessionIds.TryGetValue(sessionId, out var sessionRevoked) && issuedAt <= sessionRevoked);

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, session) in sessions)
        {
            if (now - session.LastSeenAt > IdleTimeout || now - session.CreatedAt > AbsoluteTimeout)
                sessions.TryRemove(key, out _);
        }
        foreach (var marker in new[] { revokedSubjects, revokedSessionIds })
        {
            foreach (var (key, at) in marker)
            {
                if (now - at > RevocationMemory) marker.TryRemove(key, out _);
            }
        }
    }

    private static string Key(string cookieValue) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cookieValue)));
}

/// <summary>
/// Remembers every accepted hand-off <c>jti</c> until its assertion could no longer be valid, in
/// <c>&lt;data root&gt;/federation/used-assertions.json</c>, so a replayed POST body is refused even
/// after a restart. A lock file serialises writers across hosts sharing the data root; the
/// document itself is replaced atomically.
/// </summary>
internal sealed class FederationReplayStore(TimeProvider time)
{
    private readonly string path = Path.Combine(QualityDataRoot.BaseDirectory, "federation", "used-assertions.json");
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<bool> TryConsumeAsync(string jti, DateTimeOffset keepUntil, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(jti)));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var exclusive = await LockAsync(cancellationToken);
            var now = time.GetUtcNow().ToUnixTimeSeconds();
            var used = Read();
            foreach (var expired in used.Where(entry => entry.Value < now).Select(entry => entry.Key).ToArray())
                used.Remove(expired);
            if (used.ContainsKey(key)) return false;
            used[key] = keepUntil.ToUnixTimeSeconds();
            await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(used), cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private Dictionary<string, long> Read()
    {
        if (!File.Exists(path)) return new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path))
                ?? new Dictionary<string, long>(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            // Refuse rather than forget: an unreadable ledger must not turn every used assertion valid again.
            throw new FederationStoreUnavailableException($"The hand-off replay ledger {path} is unreadable.", exception);
        }
    }

    private async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 100)
            {
                await Task.Delay(20, cancellationToken);
            }
        }
    }
}

public sealed class FederationStoreUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);
