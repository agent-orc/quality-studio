using System.Security.Cryptography;
using System.Text;

namespace QualityStudio.Api;

public sealed record LocalMutationTokenResponse(string Token, DateTimeOffset ExpiresAt);

/// <summary>Issues bounded, process-local synchronizer nonces for browser mutations in Local mode.</summary>
public sealed class LocalMutationProtection
{
    public const string HeaderName = "X-Quality-CSRF-Token";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private const int MaximumTokens = 4096;
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> tokens = new(StringComparer.Ordinal);

    public LocalMutationTokenResponse Issue()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime);
        var hash = Hash(token);
        lock (gate)
        {
            RemoveExpired();
            if (tokens.Count >= MaximumTokens)
            {
                var oldest = tokens.MinBy(pair => pair.Value).Key;
                tokens.Remove(oldest);
            }
            tokens[hash] = expiresAt;
        }
        return new LocalMutationTokenResponse(token, expiresAt);
    }

    public bool Validate(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > 128) return false;
        lock (gate)
        {
            RemoveExpired();
            return tokens.TryGetValue(Hash(supplied), out var expiresAt) && expiresAt > DateTimeOffset.UtcNow;
        }
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var hash in tokens.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            tokens.Remove(hash);
    }

    private static string Hash(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
