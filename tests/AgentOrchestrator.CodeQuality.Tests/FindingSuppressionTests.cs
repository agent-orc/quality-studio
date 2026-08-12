using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class FindingSuppressionTests
{
    private static readonly string Fingerprint = "sha256:" + new string('a', 64);

    [Fact]
    public void Exact_ignore_survives_store_recreation_and_projection_retains_observation()
    {
        var root = Directory.CreateTempSubdirectory("finding-suppression-");
        try
        {
        var now = new DateTimeOffset(2026, 8, 12, 20, 0, 0, TimeSpan.Zero);
        var store = new FindingSuppressionStore(root.FullName, () => now);

        var written = store.AddExact(Fingerprint, "Ada", "Generated compatibility finding.", null, 0);
        var reloaded = new FindingSuppressionStore(root.FullName, () => now).Read();
        var metadata = new JsonObject
        {
            ["grade"] = new JsonObject { ["score"] = 60, ["band"] = "D", ["rationale"] = "One finding." },
            ["findings"] = new JsonArray(new JsonObject
            {
                ["id"] = "finding-a",
                ["fingerprint"] = Fingerprint,
                ["title"] = "Retained observation",
                ["severity"] = "high",
                ["state"] = "open",
            }),
        };
        var projected = FindingSuppressionProjection.Apply(metadata, reloaded, now);

        Assert.Equal(1, written.Revision);
        Assert.Single(reloaded.Rules);
        Assert.Single(projected["findings"]!.AsArray());
        Assert.True(projected["findings"]![0]!["suppressed"]!.GetValue<bool>());
        Assert.Equal("Generated compatibility finding.",
            projected["findings"]![0]!["suppression"]!["reason"]!.GetValue<string>());
        Assert.Equal(100, projected["grade"]!["score"]!.GetValue<int>());
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Expiry_reexposes_finding_and_revision_conflicts_are_rejected()
    {
        var root = Directory.CreateTempSubdirectory("finding-suppression-expiry-");
        try
        {
        var now = new DateTimeOffset(2026, 8, 12, 20, 0, 0, TimeSpan.Zero);
        var store = new FindingSuppressionStore(root.FullName, () => now);
        var written = store.AddExact(Fingerprint, "Ada", "Temporary ignore.", now.AddHours(1), 0);

        Assert.Throws<FindingSuppressionConflictException>(() =>
            store.Remove(written.Rules[0].Id, expectedRevision: 0));
        Assert.NotNull(FindingSuppressionStore.Match(written, Fingerprint, now));
        Assert.Null(FindingSuppressionStore.Match(written, Fingerprint, now.AddHours(2)));

        now = now.AddHours(2);
        var renewed = store.AddExact(Fingerprint, "Grace", "Renewed after expiry.", null, written.Revision);
        Assert.Single(renewed.Rules);
        Assert.Equal("Grace", renewed.Rules[0].Author);

        var restored = store.Remove(renewed.Rules[0].Id, renewed.Revision);
        Assert.Equal(3, restored.Revision);
        Assert.Empty(restored.Rules);
        }
        finally { root.Delete(true); }
    }
}
