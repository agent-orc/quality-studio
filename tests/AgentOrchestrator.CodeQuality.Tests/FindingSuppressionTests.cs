using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class FindingSuppressionTests
{
    private static readonly string Fingerprint = "sha256:" + new string('a', 64);

    [Fact]
    public void Exact_ignore_is_atomic_revisioned_and_survives_store_restart()
    {
        using var root = new TemporaryRoot();
        var now = new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);
        var store = new FindingSuppressionStore(root.Path, () => now);
        var rule = new FindingSuppressionRule(
            "finding-a",
            true,
            new FindingSuppressionMatch(Fingerprint: Fingerprint),
            "suppress",
            "Known generated noise.",
            "Ada",
            now,
            now.AddDays(1));

        var written = store.Add(rule, expectedRevision: 0);

        Assert.Equal(1, written.Revision);
        var restarted = new FindingSuppressionStore(root.Path, () => now);
        var persisted = Assert.Single(restarted.Read().Rules);
        Assert.Equal(Fingerprint, persisted.Match.Fingerprint);
        Assert.True(restarted.IsSuppressed(Candidate()));
        var path = Path.Combine(root.Path, FindingSuppressionStore.RelativePath);
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(Fingerprint, json.RootElement.GetProperty("rules")[0]
                .GetProperty("match").GetProperty("fingerprint").GetString());
        }
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));

        Assert.Throws<FindingSuppressionConflictException>(() => restarted.Remove("finding-a", expectedRevision: 0));
        Assert.Empty(restarted.Remove("finding-a", expectedRevision: 1).Rules);
        Assert.False(new FindingSuppressionStore(root.Path, () => now).IsSuppressed(Candidate()));
    }

    [Fact]
    public void Expired_ignore_reexposes_the_finding_without_deleting_the_rule()
    {
        using var root = new TemporaryRoot();
        var now = new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);
        var store = new FindingSuppressionStore(root.Path, () => now);
        store.Add(new FindingSuppressionRule(
            "finding-expiring", true, new FindingSuppressionMatch(Fingerprint: Fingerprint), "suppress",
            "Temporary exception.", "Ada", now, now.AddHours(1)), 0);

        now = now.AddHours(2);

        Assert.False(store.IsSuppressed(Candidate()));
        Assert.Single(store.Read().Rules);
    }

    private static FindingSuppressionCandidate Candidate() =>
        new(Fingerprint, "correctness.test", "src/A.cs", "code", "agent");

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quality-finding-suppressions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => TestDirectory.Delete(Path);
    }
}
