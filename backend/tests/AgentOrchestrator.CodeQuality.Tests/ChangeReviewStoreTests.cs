using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ChangeReviewStoreTests : IDisposable
{
    private static readonly string Base = new('a', 40);
    private static readonly string Head = new('b', 40);
    private readonly string root = Path.Combine(Path.GetTempPath(), "qs-change-store", Guid.NewGuid().ToString("N"));
    private DateTimeOffset now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private ChangeReviewStore Store() => new(() => now);

    private static ChangeReviewKey Key(string ruleSet = "1", string repository = "proj-002") =>
        new(repository, Base, Head, "sha256:" + new string('0', 64), "sha256:" + new string(ruleSet[0], 64), "1.6.0");

    private static JsonElement Document(ChangeReviewKey key, string verdict) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["schema"] = ChangeReviewVerdictService.SchemaId,
        ["schemaVersion"] = 1,
        ["repository"] = key.Repository,
        ["baseSha"] = key.BaseSha,
        ["headSha"] = key.HeadSha,
        ["verdict"] = verdict,
        ["policyHash"] = key.PolicyHash,
        ["ruleSetHash"] = key.RuleSetHash,
        ["catalogueVersion"] = key.CatalogueVersion,
        ["findings"] = new JsonArray(),
    });

    private static ChangeReviewProvenance Plane => new(ChangeReviewProvenance.AgentStudioReviewPlane, "AGT-3352", 1, "attempt-1");

    [Fact]
    public async Task Same_document_is_stored_once_and_a_different_decisive_document_is_refused()
    {
        var store = Store();
        var key = Key();

        var first = await store.PutAsync(root, key, Plane, Document(key, "pass"), TestContext.Current.CancellationToken);
        var repeat = await store.PutAsync(root, key, Plane, Document(key, "pass"), TestContext.Current.CancellationToken);
        var different = await store.PutAsync(root, key, Plane, Document(key, "block"), TestContext.Current.CancellationToken);

        Assert.Equal(ChangeReviewStoreOutcome.Stored, first.Outcome);
        Assert.Equal(ChangeReviewStoreOutcome.Unchanged, repeat.Outcome);
        Assert.Equal(ChangeReviewStoreOutcome.Conflict, different.Outcome);
        Assert.Equal("pass", Assert.Single(store.List(root, "proj-002", Base, Head)).Verdict);
    }

    [Fact]
    public async Task Infrastructure_verdict_is_replaced_and_never_answers_a_lookup()
    {
        var store = Store();
        var key = Key();
        await store.PutAsync(root, key, Plane, Document(key, "timeout"), TestContext.Current.CancellationToken);
        var computed = 0;

        var (record, wasComputed) = await store.GetOrComputeAsync(root, key, new(ChangeReviewProvenance.QualityStudio),
            _ => { computed++; return Task.FromResult(Document(key, "concerns")); }, TestContext.Current.CancellationToken);

        Assert.True(wasComputed);
        Assert.Equal(1, computed);
        Assert.Equal("concerns", record.Verdict);
        Assert.Equal(ChangeReviewProvenance.QualityStudio, Assert.Single(store.List(root, "proj-002", Base, Head)).Provenance.Producer);
    }

    [Fact]
    public async Task Concurrent_lookups_of_one_key_compute_once()
    {
        var store = Store();
        var key = Key();
        var computed = 0;
        var release = new TaskCompletionSource();
        async Task<JsonElement> Compute(CancellationToken _)
        {
            Interlocked.Increment(ref computed);
            await release.Task;
            return Document(key, "pass");
        }

        var callers = Enumerable.Range(0, 4).Select(_ => store.GetOrComputeAsync(root, key,
            new(ChangeReviewProvenance.QualityStudio), Compute, TestContext.Current.CancellationToken)).ToArray();
        release.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, computed);
        Assert.Single(results, result => result.Computed);
        Assert.All(results, result => Assert.Equal(results[0].Record.DocumentDigest, result.Record.DocumentDigest));
    }

    [Fact]
    public async Task Range_lists_one_repository_newest_rule_set_first()
    {
        var store = Store();
        foreach (var (ruleSet, repository) in new[] { ("1", "proj-002"), ("2", "proj-002"), ("3", "other") })
        {
            var key = Key(ruleSet, repository);
            await store.PutAsync(root, key, Plane, Document(key, "pass"), TestContext.Current.CancellationToken);
            now = now.AddMinutes(1);
        }

        var records = store.List(root, "proj-002", Base, Head);

        Assert.Equal([Key("2").RuleSetHash, Key("1").RuleSetHash], records.Select(record => record.Key.RuleSetHash));
        Assert.Empty(store.List(root, "proj-002", Head, Base));
        Assert.Throws<ArgumentException>(() => store.List(root, "proj-002", "../escape", Head));
    }

    [Fact]
    public void Validation_reports_unknown_properties_and_bad_findings()
    {
        var key = Key();
        var node = JsonNode.Parse(Document(key, "pass").GetRawText())!.AsObject();
        node["extra"] = true;
        node["findings"] = new JsonArray(new JsonObject { ["id"] = "x", ["line"] = 0 });

        var errors = ChangeReviewDocuments.Validate(JsonSerializer.SerializeToElement(node));

        Assert.Contains(errors, error => error.Contains("'extra'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("findings[0]: 'line'", StringComparison.Ordinal));
        Assert.Empty(ChangeReviewDocuments.Validate(Document(key, "provider-unavailable")));
        Assert.True(ChangeReviewDocuments.IsInfrastructure("auth-failure"));
        Assert.False(ChangeReviewDocuments.IsInfrastructure("pass"));
    }

    [Fact]
    public void Excerpts_anchor_findings_and_round_filter_keeps_lines_the_round_changed()
    {
        const string diff = """
            diff --git a/src/Work.cs b/src/Work.cs
            --- a/src/Work.cs
            +++ b/src/Work.cs
            @@ -1,3 +1,4 @@
             class Work {}
            -old();
            +first();
            +second();
             end();
            """;
        const string round = """
            diff --git a/src/Work.cs b/src/Work.cs
            --- a/src/Work.cs
            +++ b/src/Work.cs
            @@ -2,2 +2,2 @@
             first();
            -secnd();
            +second();
            """;
        ChangeReviewAnchor[] anchors =
        [
            new("first", "src/Work.cs", "head", 2),
            new("second", "src/Work.cs", "head", 3),
            new("removed", "src/Work.cs", "base", 2),
            new("elsewhere", "src/Other.cs", "head", 1),
        ];

        var excerpts = ChangeReviewRange.Excerpts(diff.Replace("\r\n", "\n"), anchors, context: 1);
        var changed = ChangeReviewRange.ChangedInRound(round.Replace("\r\n", "\n"), anchors);

        var second = excerpts["second"];
        Assert.Equal(["added", "added", "context"], second.Select(line => line.Kind));
        Assert.Equal("second();", Assert.Single(second, line => line.Anchor).Text);
        Assert.Equal("old();", Assert.Single(excerpts["removed"], line => line.Anchor).Text);
        Assert.False(excerpts.ContainsKey("elsewhere"));
        Assert.Equal(["removed", "second"], changed.Order(StringComparer.Ordinal));
    }

    public void Dispose()
    {
        TemporaryDirectory.Delete(QualityDataRoot.For(root));
    }
}
