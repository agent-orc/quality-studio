using System.Text.Json.Nodes;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Finding identity across reruns (QS-109, evaluation defect D3): rule plus anchor span on content
/// with the same hash, never the agent's wording; and a miss on unchanged code is not a fix.
/// </summary>
public sealed class FindingReobservationTests
{
    private const string Path = "src/Sample.cs";

    private const string Source = """
        internal static class Sample
        {
            public static int Divide(int a, int b)
            {
                return a / b;
            }
        }

        """;

    [Fact]
    public void Rerun_with_a_wider_span_on_unchanged_code_keeps_the_identity()
    {
        var first = Assign(Response(("correctness.risk", 5, 5, "Division by zero")));
        var earlier = first.Identities;

        var wider = Assign(Response(("correctness.risk", 4, 6, "Unchecked divisor")), earlier);
        var otherRule = Assign(Response(("correctness.naming", 4, 6, "Unchecked divisor")), earlier);
        var changed = Assign(Response(("correctness.risk", 4, 6, "Unchecked divisor")), earlier,
            "// header\n" + Source);

        var original = Assert.Single(earlier);
        Assert.NotNull(original.Range);
        Assert.Equal(FindingIdentity.ContentHash(Source), original.ContentHash);
        Assert.Equal(original.Fingerprint, Assert.Single(wider.Identities).Fingerprint);
        Assert.Equal(original.Id, wider.Response["findings"]![0]!["id"]!.GetValue<string>());
        // The new span is what the next rerun compares against.
        Assert.Equal(4, wider.Response["findings"]![0]!["anchors"]![0]!["range"]!["start"]!["line"]!.GetValue<int>());
        Assert.NotEqual(original.Fingerprint, Assert.Single(otherRule.Identities).Fingerprint);
        Assert.NotEqual(original.Fingerprint, Assert.Single(changed.Identities).Fingerprint);
    }

    [Fact]
    public void An_exact_reproduction_is_not_taken_by_an_overlapping_neighbour()
    {
        var earlier = Assign(Response(("correctness.risk", 5, 5, "Division by zero"))).Identities;

        var rerun = Assign(Response(
            ("correctness.risk", 4, 6, "Neighbour listed first"),
            ("correctness.risk", 5, 5, "Exact reproduction")), earlier);

        Assert.Equal(2, rerun.Identities.Count);
        Assert.NotEqual(earlier[0].Fingerprint, rerun.Identities[0].Fingerprint);
        Assert.Equal(earlier[0].Fingerprint, rerun.Identities[1].Fingerprint);
    }

    [Fact]
    public void The_same_text_on_changed_content_does_not_keep_the_identity()
    {
        var earlier = Assign(Response(("correctness.risk", 5, 5, "Division by zero"))).Identities;
        var original = Assert.Single(earlier);

        // A line above the defect changed; the enclosed code, rule and path are the same, so the
        // computed text fingerprint is too — but the anchor content hash is not.
        var shifted = Assign(Response(("correctness.risk", 6, 6, "Division by zero")), earlier,
            "// header\n" + Source);

        var rerun = Assert.Single(shifted.Identities);
        Assert.Equal(original.Fingerprint, FindingIdentity.Compute(Path,
            FindingIdentity.NormalizeSnippet(shifted.Response["findings"]![0]!["anchors"]![0]!["capturedExcerpt"]!["text"]!
                .GetValue<string>()), "correctness.risk"));
        Assert.NotEqual(original.Fingerprint, rerun.Fingerprint);
        Assert.NotEqual(original.Id, rerun.Id);
        Assert.Equal(rerun.Fingerprint, shifted.Response["findings"]![0]!["fingerprint"]!.GetValue<string>());
        Assert.NotEqual(original.ContentHash, rerun.ContentHash);
    }

    [Fact]
    public void The_same_text_without_an_overlapping_anchor_or_a_measured_anchor_does_not_keep_the_identity()
    {
        var measured = Assert.Single(Assign(Response(("correctness.risk", 5, 5, "Division by zero"))).Identities);
        var elsewhere = measured with { Range = new FindingRange(new FindingPosition(1, 1), new FindingPosition(2, 1)) };
        var unanchored = measured with { ContentHash = null, Range = null };

        var notOverlapping = Assert.Single(Assign(Response(("correctness.risk", 5, 5, "Division by zero")), [elsewhere]).Identities);
        var withoutAnchor = Assert.Single(Assign(Response(("correctness.risk", 5, 5, "Division by zero")), [unanchored]).Identities);
        var matched = Assert.Single(Assign(Response(("correctness.risk", 5, 5, "Division by zero")), [measured]).Identities);

        Assert.NotEqual(measured.Fingerprint, notOverlapping.Fingerprint);
        Assert.NotEqual(measured.Fingerprint, withoutAnchor.Fingerprint);
        Assert.Equal(measured.Fingerprint, matched.Fingerprint);
        Assert.Equal(measured.Id, matched.Id);
    }

    [Fact]
    public void A_known_lifecycle_fingerprint_is_never_taken_by_text()
    {
        var first = Assert.Single(Assign(Response(("correctness.risk", 5, 5, "Division by zero"))).Identities);

        var parsed = new ReviewResponseParser().Parse(Response(("correctness.risk", 5, 5, "Division by zero")));
        var rerun = Assert.Single(FindingIdentity.Assign(parsed, new Dictionary<string, string> { [Path] = Source },
            earlier: [], knownFingerprints: [first.Fingerprint]));
        var again = Assert.Single(FindingIdentity.Assign(
            new ReviewResponseParser().Parse(Response(("correctness.risk", 5, 5, "Division by zero"))),
            new Dictionary<string, string> { [Path] = Source }, earlier: [], knownFingerprints: [first.Fingerprint]));

        Assert.NotEqual(first.Fingerprint, rerun.Fingerprint);
        Assert.Equal("finding-" + rerun.Fingerprint[7..], rerun.Id);
        // The derived fingerprint is deterministic.
        Assert.Equal(rerun.Fingerprint, again.Fingerprint);
    }

    [Fact]
    public void Duplicates_still_collapse_into_the_finding_that_keeps_the_identity()
    {
        var earlier = Assign(Response(("correctness.risk", 5, 5, "Division by zero"))).Identities;

        var rerun = Assign(Response(
            ("correctness.risk", 5, 5, "First copy"),
            ("correctness.risk", 5, 5, "Second copy")), earlier);

        Assert.Equal(earlier[0].Fingerprint, Assert.Single(rerun.Identities).Fingerprint);
        Assert.Single(rerun.Response["findings"]!.AsArray());
    }

    [Fact]
    public async Task A_disposition_is_kept_by_anchor_on_unchanged_code_and_not_inherited_by_text_after_a_change()
    {
        var root = Directory.CreateTempSubdirectory("finding-disposition-");
        Directory.CreateDirectory(System.IO.Path.Combine(root.FullName, "src"));
        var file = System.IO.Path.Combine(root.FullName, "src", "Sample.cs");
        await File.WriteAllTextAsync(file, Source, TestContext.Current.CancellationToken);
        var runner = new ReviewRunner(new QueueAgent(
            Response(("correctness.risk", 5, 5, "Division by zero")),
            Response(),
            Response(("correctness.risk", 4, 6, "Unchecked divisor")),
            Response(("correctness.risk", 6, 6, "Division by zero"))));
        var request = new ReviewRequest(Path, RepositoryRoot: root.FullName);
        var store = new FindingStateStore(root.FullName);
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var first = await runner.ReviewAsync(request, cancellationToken);
            var fingerprint = SidecarFingerprints(first.MetaPath).Single();
            await store.SetAsync(fingerprint, FindingState.Waived, "Ada", "Accepted for the migration.",
                cancellationToken: cancellationToken);

            // Missed on unchanged code: the waiver stays and keeps its anchor.
            await runner.ReviewAsync(request, cancellationToken);
            var missed = (await store.ReadAsync(cancellationToken))[fingerprint];
            Assert.Equal(FindingState.Waived, missed.State);
            Assert.Equal(5, missed.LastObservedRange!.Start.Line);
            Assert.Equal(FindingIdentity.ContentHash(Source), missed.LastObservedContentHash);

            // Re-reported one line wider on the same code: the anchor carries the identity and the waiver.
            var again = await runner.ReviewAsync(request, cancellationToken);
            Assert.Equal(new[] { fingerprint }, SidecarFingerprints(again.MetaPath));
            var reobserved = (await store.ReadAsync(cancellationToken))[fingerprint];
            Assert.Equal(FindingState.Waived, reobserved.State);
            Assert.Null(reobserved.LastObservedRange);

            // The code changed above the finding; the same text alone does not inherit the waiver.
            await File.WriteAllTextAsync(file, "// edited elsewhere\n" + Source, cancellationToken);
            var changed = await runner.ReviewAsync(request, cancellationToken);
            var reported = SidecarFingerprints(changed.MetaPath).Single();
            Assert.NotEqual(fingerprint, reported);
            var states = await store.ReadAsync(cancellationToken);
            Assert.Equal(FindingState.Open, states[reported].State);
            Assert.Equal(FindingState.Resolved, states[fingerprint].State);
        }
        finally
        {
            TemporaryDirectory.Delete(root.FullName);
        }
    }

    [Fact]
    public async Task Missing_on_unchanged_code_is_not_reobserved_and_keeps_a_human_disposition()
    {
        var root = Directory.CreateTempSubdirectory("finding-reobservation-");
        var now = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);
        var store = new FindingStateStore(root.FullName, () => now);
        var hash = FindingIdentity.ContentHash(Source);
        var range = new FindingRange(new FindingPosition(5, 1), new FindingPosition(5, 20));
        var open = new FindingIdentityRecord("sha256:" + new string('a', 64), "finding-a", Path, "rule.a", hash, range);
        var waived = new FindingIdentityRecord("sha256:" + new string('b', 64), "finding-b", Path, "rule.b", hash, range);
        var unanchored = new FindingIdentityRecord("sha256:" + new string('c', 64), "finding-c", Path, "gitleaks:key");
        var hashes = new Dictionary<string, string> { [Path] = hash };
        try
        {
            var initial = await store.MergeReviewAsync([open, waived, unanchored], [], "agent", hashes,
                TestContext.Current.CancellationToken);
            await store.SetAsync(waived.Fingerprint, FindingState.Waived, "Ada", "Accepted for the migration.",
                expectedTimestamp: initial[waived.Fingerprint].Timestamp,
                cancellationToken: TestContext.Current.CancellationToken);

            var missed = await store.MergeReviewAsync([], [open, waived, unanchored], "agent", hashes,
                TestContext.Current.CancellationToken);

            Assert.Equal(FindingState.NotReobserved, missed[open.Fingerprint].State);
            Assert.Equal(range, missed[open.Fingerprint].LastObservedRange);
            Assert.Equal(hash, missed[open.Fingerprint].LastObservedContentHash);
            Assert.Equal(FindingState.Waived, missed[waived.Fingerprint].State);
            Assert.Equal("Ada", missed[waived.Fingerprint].Author);
            Assert.Equal(range, missed[waived.Fingerprint].LastObservedRange);
            // Without an anchor there is nothing to compare, so the old rule still applies.
            Assert.Equal(FindingState.Resolved, missed[unanchored.Fingerprint].State);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(open.Fingerprint,
                FindingState.NotReobserved, "Ada", "Manual.", cancellationToken: TestContext.Current.CancellationToken));

            var changed = await store.MergeReviewAsync([], [open], "agent",
                new Dictionary<string, string> { [Path] = FindingIdentity.ContentHash("// edited\n" + Source) },
                TestContext.Current.CancellationToken);
            Assert.Equal(FindingState.Resolved, changed[open.Fingerprint].State);
            Assert.Null(changed[open.Fingerprint].LastObservedRange);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task Forced_reruns_keep_identity_and_never_resolve_a_finding_on_unchanged_code()
    {
        var root = Directory.CreateTempSubdirectory("finding-rerun-");
        Directory.CreateDirectory(System.IO.Path.Combine(root.FullName, "src"));
        var file = System.IO.Path.Combine(root.FullName, "src", "Sample.cs");
        await File.WriteAllTextAsync(file, Source, TestContext.Current.CancellationToken);
        var agent = new QueueAgent(
            Response(("correctness.risk", 5, 5, "Division by zero")),
            Response(("correctness.risk", 4, 6, "Unchecked divisor in Divide")),
            Response(),
            Response(("correctness.risk", 5, 6, "Divisor may be zero")),
            Response());
        var runner = new ReviewRunner(agent);
        var request = new ReviewRequest(Path, RepositoryRoot: root.FullName);
        var store = new FindingStateStore(root.FullName);
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var first = await runner.ReviewAsync(request, cancellationToken);
            var fingerprint = SidecarFingerprints(first.MetaPath).Single();

            var second = await runner.ReviewAsync(request, cancellationToken);
            Assert.Equal(new[] { fingerprint }, SidecarFingerprints(second.MetaPath));
            Assert.Equal(FindingState.Open, (await store.ReadAsync(cancellationToken))[fingerprint].State);

            var missed = await runner.ReviewAsync(request, cancellationToken);
            Assert.Empty(SidecarFingerprints(missed.MetaPath));
            var notReobserved = (await store.ReadAsync(cancellationToken))[fingerprint];
            Assert.Equal(FindingState.NotReobserved, notReobserved.State);
            Assert.Equal("not-reobserved", missed.Observation!.FindingStates[fingerprint]);
            Assert.Equal(4, notReobserved.LastObservedRange!.Start.Line);

            // The sidecar no longer lists it; the lifecycle's last-observed anchor still matches it.
            var again = await runner.ReviewAsync(request, cancellationToken);
            Assert.Equal(new[] { fingerprint }, SidecarFingerprints(again.MetaPath));
            var reopened = (await store.ReadAsync(cancellationToken))[fingerprint];
            Assert.Equal(FindingState.Open, reopened.State);
            Assert.Contains("observed again", reopened.Reason, StringComparison.Ordinal);
            Assert.Null(reopened.LastObservedRange);

            await File.WriteAllTextAsync(file, "// guarded in a later change\n" + Source, cancellationToken);
            await runner.ReviewAsync(request, cancellationToken);
            Assert.Equal(FindingState.Resolved, (await store.ReadAsync(cancellationToken))[fingerprint].State);
            Assert.Single((await store.ReadAsync(cancellationToken)).Values);
        }
        finally
        {
            TemporaryDirectory.Delete(root.FullName);
        }
    }

    [Fact]
    public async Task Another_review_kind_neither_matches_nor_resolves_a_not_reobserved_finding()
    {
        var root = Directory.CreateTempSubdirectory("finding-kinds-");
        Directory.CreateDirectory(System.IO.Path.Combine(root.FullName, "src"));
        var file = System.IO.Path.Combine(root.FullName, "src", "Sample.cs");
        await File.WriteAllTextAsync(file, Source, TestContext.Current.CancellationToken);
        var code = new ReviewRunner(new QueueAgent(
            Response(("correctness.risk", 5, 5, "Division by zero")), Response(), Response()));
        var security = new ReviewRunner(new QueueAgent(Response()));
        var store = new FindingStateStore(root.FullName);
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var first = await code.ReviewAsync(new ReviewRequest(Path, RepositoryRoot: root.FullName), cancellationToken);
            var fingerprint = SidecarFingerprints(first.MetaPath).Single();
            await code.ReviewAsync(new ReviewRequest(Path, RepositoryRoot: root.FullName), cancellationToken);
            await File.WriteAllTextAsync(file, "// edited\n" + Source, cancellationToken);

            await security.ReviewAsync(new ReviewRequest(Path, "security", RepositoryRoot: root.FullName), cancellationToken);
            Assert.Equal(FindingState.NotReobserved, (await store.ReadAsync(cancellationToken))[fingerprint].State);

            await code.ReviewAsync(new ReviewRequest(Path, RepositoryRoot: root.FullName), cancellationToken);
            Assert.Equal(FindingState.Resolved, (await store.ReadAsync(cancellationToken))[fingerprint].State);
        }
        finally
        {
            TemporaryDirectory.Delete(root.FullName);
        }
    }

    [Fact]
    public void A_listed_finding_projects_as_open_even_when_another_review_did_not_reobserve_it()
    {
        var assigned = Assign(Response(("correctness.risk", 5, 5, "Division by zero")));
        var identity = assigned.Identities[0];
        var state = new FindingStateRecord(identity.Fingerprint, identity.Id, identity.Path, identity.RuleId,
            FindingState.NotReobserved, "agent", "Not re-observed.", DateTimeOffset.UtcNow);

        var projected = FindingStateProjection.Apply(assigned.Response,
            new Dictionary<string, FindingStateRecord> { [identity.Fingerprint] = state });

        Assert.Equal("open", projected["findings"]![0]!["state"]!.GetValue<string>());
        Assert.Equal(1, projected["findingCounts"]!["open"]!.GetValue<int>());
    }

    private static (JsonObject Response, IReadOnlyList<FindingIdentityRecord> Identities) Assign(
        string response, IReadOnlyList<FindingIdentityRecord>? earlier = null, string content = Source)
    {
        var parsed = new ReviewResponseParser().Parse(response);
        var identities = FindingIdentity.Assign(parsed, new Dictionary<string, string> { [Path] = content }, earlier);
        return (parsed, identities);
    }

    private static string Response(params (string RuleId, int Start, int End, string Title)[] findings) =>
        ReviewResponseParserTests.ValidResponse.Replace("\"findings\": []", "\"findings\": [" + string.Join(",",
            findings.Select((finding, index) => new JsonObject
            {
                ["id"] = $"agent-{index}",
                ["ruleId"] = finding.RuleId,
                ["aspect"] = "correctness",
                ["severity"] = "medium",
                ["title"] = finding.Title,
                ["description"] = finding.Title + " when b is zero.",
                ["recommendation"] = "Guard the divisor.",
                ["locations"] = new JsonArray(new JsonObject
                {
                    ["path"] = Path,
                    ["range"] = new JsonObject
                    {
                        ["start"] = new JsonObject { ["line"] = finding.Start, ["column"] = 1 },
                        ["end"] = new JsonObject { ["line"] = finding.End, ["column"] = 80 },
                    },
                }),
            }.ToJsonString())) + "]", StringComparison.Ordinal);

    private static string[] SidecarFingerprints(string metaPath) =>
        JsonNode.Parse(File.ReadAllText(metaPath))!["findings"]!.AsArray()
            .Select(finding => finding!["fingerprint"]!.GetValue<string>()).ToArray();

    private sealed class QueueAgent(params string[] responses) : IReviewAgent
    {
        private readonly Queue<string> responses = new(responses);
        public string AgentName => "queue-agent";
        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult("run-" + Guid.NewGuid().ToString("N"),
                "```json\n" + responses.Dequeue() + "\n```", new TokenUsage(1, 1, 0, 0, 1), Model));
    }
}
