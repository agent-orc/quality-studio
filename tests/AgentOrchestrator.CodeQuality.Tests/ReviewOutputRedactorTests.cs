using System.Text.Json.Nodes;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewOutputRedactorTests
{
    [Fact]
    public void Redact_removes_seeded_credentials_from_all_response_text()
    {
        var response = new JsonObject
        {
            ["summary"] = "Found AKIAIOSFODNN7EXAMPLE and password='correct-horse-battery-staple'.",
            ["rationale"] = "Observed eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJmaXh0dXJlIn0.signaturefixture.",
            ["findings"] = new JsonArray
            {
                new JsonObject
                {
                    ["description"] = "Authorization: Bearer abcdefghijklmnopqrstuvwxyz",
                    ["recommendation"] = "Remove ghp_abcdefghijklmnopqrstuvwxyz123456.",
                    ["nested"] = new JsonArray("-----BEGIN PRIVATE KEY-----\nfixture\n-----END PRIVATE KEY-----"),
                },
            },
        };

        var changes = ReviewOutputRedactor.Redact(response);
        var serialized = response.ToJsonString();

        Assert.Equal(5, changes);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("correct-horse-battery-staple", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", serialized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED CREDENTIAL]", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_preserves_content_hashes_and_non_secret_review_text()
    {
        var response = new JsonObject
        {
            ["summary"] = "The unit-key sha256:95befdd6e691d4d89031a2a2901cc74fc6242109980b060e08ddf87829924483 is content-derived.", // gitleaks:allow
        };

        Assert.Equal(0, ReviewOutputRedactor.Redact(response));
        Assert.Contains("sha256:95befdd6", response["summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
