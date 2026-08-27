using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class FindingSuppressionStoreTests
{
    [Fact]
    public void Add_persists_by_fingerprint_and_survives_a_reload()
    {
        using var root = new TemporaryRoot();
        var store = new FindingSuppressionStore(root.Path);
        var suppression = NewSuppression('a');

        var saved = store.Add(suppression);

        Assert.Equal(suppression.Fingerprint, saved.Fingerprint);
        Assert.Equal("Ada", saved.Author);

        var reloaded = new FindingSuppressionStore(root.Path).Read();
        Assert.True(reloaded.ContainsKey(suppression.Fingerprint));
        Assert.Equal("Noisy for generated code.", reloaded[suppression.Fingerprint].Reason);

        var statePath = Path.Combine(root.Path, FindingSuppressionStore.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        using var json = JsonDocument.Parse(File.ReadAllText(statePath));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(statePath)!, "*.tmp"));
    }

    [Fact]
    public void Add_is_an_upsert_by_fingerprint()
    {
        using var root = new TemporaryRoot();
        var store = new FindingSuppressionStore(root.Path);
        var suppression = NewSuppression('b');
        store.Add(suppression);

        store.Add(suppression with { Reason = "Superseded reason." });

        var current = store.Read();
        Assert.Single(current);
        Assert.Equal("Superseded reason.", current[suppression.Fingerprint].Reason);
    }

    [Fact]
    public void Remove_returns_false_when_the_fingerprint_is_not_ignored()
    {
        using var root = new TemporaryRoot();
        var store = new FindingSuppressionStore(root.Path);

        Assert.False(store.Remove("sha256:" + new string('z', 64)));

        var suppression = NewSuppression('c');
        store.Add(suppression);
        Assert.True(store.Remove(suppression.Fingerprint));
        Assert.Empty(store.Read());
    }

    [Fact]
    public void Read_drops_expired_suppressions_so_they_reappear_in_the_default_queue()
    {
        var now = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        using var root = new TemporaryRoot();
        var store = new FindingSuppressionStore(root.Path, () => now);
        var suppression = NewSuppression('d') with { ExpiresAt = now.AddDays(1) };
        store.Add(suppression);
        Assert.Single(store.Read());

        now = now.AddDays(2);
        Assert.Empty(store.Read());
        Assert.Empty(new FindingSuppressionStore(root.Path, () => now).Read());
    }

    [Fact]
    public void Add_requires_author_reason_and_a_future_expiry()
    {
        using var root = new TemporaryRoot();
        var store = new FindingSuppressionStore(root.Path);
        var suppression = NewSuppression('e');

        Assert.Throws<ArgumentException>(() => store.Add(suppression with { Author = "" }));
        Assert.Throws<ArgumentException>(() => store.Add(suppression with { Reason = "" }));
        Assert.Throws<ArgumentException>(() => store.Add(suppression with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) }));
    }

    [Fact]
    public void Projection_marks_suppressed_findings_as_ignored_without_changing_the_grade()
    {
        var finding = Identity('f');
        var metadata = JsonNode.Parse(ReviewResponseParserTests.ValidResponse.Replace(
            "\"findings\": []", "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]", StringComparison.Ordinal))!.AsObject();
        FindingIdentity.Assign(metadata, new Dictionary<string, string> { ["src/Small.cs"] = "internal static class Small { }\n" });
        var actualFingerprint = metadata["findings"]![0]!["fingerprint"]!.GetValue<string>();
        var suppression = new FindingSuppression(actualFingerprint, "finding-id", "src/Small.cs", "correctness.test",
            "Title", "medium", "Noisy.", "Ada", DateTimeOffset.UtcNow);

        var withSuppressions = FindingStateProjection.Apply(metadata,
            new Dictionary<string, FindingStateRecord>(),
            new Dictionary<string, FindingSuppression> { [actualFingerprint] = suppression });
        var withoutSuppressions = FindingStateProjection.Apply(metadata, new Dictionary<string, FindingStateRecord>());

        Assert.True(withSuppressions["findings"]![0]!["ignored"]!.GetValue<bool>());
        Assert.Null(withoutSuppressions["findings"]![0]!["ignored"]);
        Assert.Equal(withoutSuppressions["grade"]!["score"]!.GetValue<int>(), withSuppressions["grade"]!["score"]!.GetValue<int>());
    }

    private static FindingSuppression NewSuppression(char value) => new(
        $"sha256:{new string(value, 64)}", $"finding-{value}", "src/A.cs", "correctness.test",
        "A finding title", "medium", "Noisy for generated code.", "Ada", DateTimeOffset.UtcNow, null);

    private static FindingIdentityRecord Identity(char value)
    {
        var hash = new string(value, 64);
        return new($"sha256:{hash}", $"finding-{hash}", "src/A.cs", "correctness.test");
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot() => Path = Directory.CreateTempSubdirectory("finding-suppressions-").FullName;
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
