using System.Security.Cryptography;

namespace QualityStudio.Api;

/// <summary>
/// Per-session anti-CSRF nonce store for Local mode. Local mode has no bearer credential
/// (<see cref="ApiSecurity.Authenticate"/> always succeeds), so cross-origin mutation defense
/// relies on the Origin allow-list plus this nonce: a page open in the operator's browser under a
/// different origin can still trigger a request, but the CORS policy only lets an allow-listed
/// origin read the response body of the issuing endpoint, so an attacker page cannot learn a valid
/// nonce value even if it can send or spoof an Origin header.
/// </summary>
public sealed class LocalCsrfNonceStore
{
    private const int MaxOutstandingNonces = 64;
    private readonly object gate = new();
    private readonly HashSet<string> issued = new(StringComparer.Ordinal);
    private readonly Queue<string> issueOrder = new();

    public string Issue()
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            issued.Add(nonce);
            issueOrder.Enqueue(nonce);
            while (issueOrder.Count > MaxOutstandingNonces)
            {
                issued.Remove(issueOrder.Dequeue());
            }
        }
        return nonce;
    }

    public bool IsValid(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce)) return false;
        lock (gate)
        {
            return issued.Contains(nonce);
        }
    }
}
