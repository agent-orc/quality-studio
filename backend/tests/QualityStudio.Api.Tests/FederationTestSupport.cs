using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// An Ed25519 signer for tests, on the verifier's own curve arithmetic. Agent Studio signs; Quality
/// Studio only verifies, so signing exists only here. It reproduces RFC 8032's deterministic
/// signatures, which <see cref="Ed25519Tests"/> checks.
/// </summary>
internal sealed class TestEd25519Key
{
    private readonly BigInteger scalar;
    private readonly byte[] prefix;

    public TestEd25519Key(byte[] seed)
    {
        var hash = SHA512.HashData(seed);
        hash[0] &= 248;
        hash[31] &= 127;
        hash[31] |= 64;
        scalar = new BigInteger(hash.AsSpan(0, 32), isUnsigned: true, isBigEndian: false);
        prefix = hash[32..];
        PublicKey = Ed25519.Encode(Ed25519.Multiply(scalar, Ed25519.BasePoint));
    }

    public static TestEd25519Key Create() => new(RandomNumberGenerator.GetBytes(32));

    public byte[] PublicKey { get; }

    public string PublicKeyBase64Url => Base64Url.EncodeToString(PublicKey);

    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var r = Ed25519.HashToScalar(prefix, message, []);
        var encodedR = Ed25519.Encode(Ed25519.Multiply(r, Ed25519.BasePoint));
        var h = Ed25519.HashToScalar(encodedR, PublicKey, message);
        var s = (r + h * scalar) % Ed25519.L;
        var signature = new byte[64];
        encodedR.CopyTo(signature, 0);
        s.TryWriteBytes(signature.AsSpan(32), out _, isUnsigned: true, isBigEndian: false);
        return signature;
    }
}

/// <summary>Builds hand-off assertions the way Agent Studio's issuer does, with one claim changed at a time.</summary>
internal static class Handoffs
{
    public const string Issuer = "https://studio.example";
    public const string Audience = "https://localhost";
    public const string StudioOrigin = "https://studio.example";
    public const string KeyId = "studio-2026-11";

    public static Dictionary<string, object?> Claims(TimeProvider time, string subject = "robert", string role = "operator",
        string[]? repositories = null, string sessionId = "studio-session-1", string target = "/?repo=default&card=AGT-3352")
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        return new Dictionary<string, object?>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = subject,
            ["sid"] = sessionId,
            ["role"] = role,
            ["repos"] = repositories ?? ["default"],
            ["target"] = target,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["iat"] = now,
            ["exp"] = now + 60,
            ["ver"] = 1,
        };
    }

    public static string Mint(TestEd25519Key key, IReadOnlyDictionary<string, object?> claims, string alg = "EdDSA",
        string? kid = KeyId)
    {
        var header = new Dictionary<string, object?> { ["alg"] = alg, ["typ"] = "JWT" };
        if (kid is not null) header["kid"] = kid;
        var signingInput = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(header)) + "." +
                           Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims));
        return signingInput + "." + Base64Url.EncodeToString(key.Sign(Encoding.ASCII.GetBytes(signingInput)));
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string? assertion, string? origin = StudioOrigin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AgentStudioFederation.SignInPath)
        {
            Content = new FormUrlEncodedContent(assertion is null ? [] : [new KeyValuePair<string, string>("handoff", assertion)]),
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static string? SessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        var cookie = cookies.FirstOrDefault(value => value.StartsWith(FederatedSessionStore.CookieName + "=", StringComparison.Ordinal));
        return cookie?[(FederatedSessionStore.CookieName.Length + 1)..].Split(';')[0];
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
}

/// <summary>Keeps every formatted log line and exception a host writes, to prove what never appears in them.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
    }
}
