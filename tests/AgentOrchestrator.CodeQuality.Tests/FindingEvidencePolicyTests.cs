using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class FindingEvidencePolicyTests
{
    [Fact]
    public async Task Assessments_AreAppendOnlySurviveRestartAndRejectStaleRevision()
    {
        var root = Directory.CreateTempSubdirectory("finding-assessment-");
        var now = new DateTimeOffset(2026, 8, 11, 7, 0, 0, TimeSpan.Zero);
        var finding = Identity('a');
        try
        {
            var store = new FindingAssessmentStore(root.FullName, () => now);
            var confirmed = await store.AppendAsync(finding, FindingAssessmentStatus.Confirmed, null,
                "Ada", "Reproduced against the captured revision.", 0, cancellationToken: TestContext.Current.CancellationToken);
            now = now.AddMinutes(1);
            var planned = await new FindingAssessmentStore(root.FullName, () => now).AppendAsync(finding, null,
                FindingResolutionStatus.Planned, "Ada", "Tracked in QS-71.", confirmed.Revision, taskKey: "QS-71",
                cancellationToken: TestContext.Current.CancellationToken);

            var restarted = await new FindingAssessmentStore(root.FullName).ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, restarted.Revision);
            Assert.Equal(FindingAssessmentStatus.Confirmed, restarted.Findings[finding.Fingerprint].Assessment);
            Assert.Equal(FindingResolutionStatus.Planned, restarted.Findings[finding.Fingerprint].Resolution);
            Assert.Equal("QS-71", restarted.Findings[finding.Fingerprint].TaskKey);
            Assert.Equal(2, File.ReadLines(Path.Combine(root.FullName, FindingAssessmentStore.RelativePath,
                "2026-08.jsonl")).Count());

            await Assert.ThrowsAsync<FindingAssessmentConflictException>(() => store.AppendAsync(finding,
                FindingAssessmentStatus.Dismissed, null, "Grace", "Stale judgement.", confirmed.Revision,
                cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData(FindingState.Accepted, FindingAssessmentStatus.Confirmed, FindingResolutionStatus.Open)]
    [InlineData(FindingState.Waived, FindingAssessmentStatus.Confirmed, FindingResolutionStatus.RiskAccepted)]
    [InlineData(FindingState.FalsePositive, FindingAssessmentStatus.Dismissed, FindingResolutionStatus.Obsolete)]
    [InlineData(FindingState.Resolved, FindingAssessmentStatus.Unassessed, FindingResolutionStatus.FixedByAbsence)]
    public async Task LegacyLifecycleProjectsToIndependentAxes(
        FindingState legacy, FindingAssessmentStatus assessment, FindingResolutionStatus resolution)
    {
        var root = Directory.CreateTempSubdirectory("finding-compatibility-");
        var occurredAt = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        var finding = Identity('b');
        try
        {
            var legacyStore = new FindingStateStore(root.FullName, () => occurredAt);
            var initial = await legacyStore.MergeReviewAsync([finding], [], "review", TestContext.Current.CancellationToken);
            if (legacy == FindingState.Resolved)
                await legacyStore.MergeReviewAsync([], [finding], "review", TestContext.Current.CancellationToken);
            else
                await legacyStore.SetAsync(finding.Fingerprint, legacy, "Reviewer", "Legacy decision.",
                    expectedTimestamp: initial[finding.Fingerprint].Timestamp,
                    cancellationToken: TestContext.Current.CancellationToken);

            var projected = (await new FindingAssessmentStore(root.FullName).ReadAsync(TestContext.Current.CancellationToken))
                .Findings[finding.Fingerprint];
            Assert.Equal(assessment, projected.Assessment);
            Assert.Equal(resolution, projected.Resolution);
            Assert.Equal("compatibility", projected.Source);
            Assert.Equal(occurredAt, projected.OccurredAt);
            Assert.Equal(0, projected.Revision);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task SuppressionPreviewAndStoredProjectionUseTheSameScopedMatcherAndExpiry()
    {
        var root = Directory.CreateTempSubdirectory("finding-suppression-");
        var now = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var observations = new[]
        {
            Observation('c', "src/Api/Handler.cs", "code", "agent"),
            Observation('d', "src/Api/Token.cs", "security", "deterministic"),
            Observation('e', "tests/HandlerTests.cs", "code", "agent"),
        };
        var rule = new FindingSuppressionRule("api-code", true,
            new FindingSuppressionMatch(RuleId: "correctness.test", PathPattern: "src/Api/**", ReviewKinds: ["code"], SourceKinds: ["agent"]),
            "suppress", "Generated adapter policy.", "Ada", now, now.AddHours(1));
        try
        {
            var store = new FindingSuppressionStore(root.FullName, () => now);
            var preview = store.Preview(rule, observations);
            Assert.Equal([observations[0]], preview);
            var saved = await store.SetAsync(rule, 0, TestContext.Current.CancellationToken);
            Assert.Equal(rule, FindingSuppressionStore.Match(saved, observations[0], now));
            Assert.Null(FindingSuppressionStore.Match(saved, observations[1], now));
            Assert.Null(FindingSuppressionStore.Match(saved, observations[0], now.AddHours(2)));
            await Assert.ThrowsAsync<FindingSuppressionConflictException>(() =>
                store.SetAsync(rule with { Reason = "Stale update." }, 0, TestContext.Current.CancellationToken));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ProjectionKeepsSuppressedObservationsAndCountsBothAxes()
    {
        var root = Directory.CreateTempSubdirectory("finding-policy-projection-");
        var now = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
        var finding = Identity('f');
        try
        {
            var assessment = await new FindingAssessmentStore(root.FullName, () => now).AppendAsync(finding,
                FindingAssessmentStatus.Confirmed, FindingResolutionStatus.RiskAccepted, "Ada", "Accepted by owner.", 0,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, assessment.Revision);
            var suppression = await new FindingSuppressionStore(root.FullName, () => now).SetAsync(
                new FindingSuppressionRule("exact-f", true, new FindingSuppressionMatch(Fingerprint: finding.Fingerprint),
                    "suppress", "Noise in this context.", "Ada", now), 0, TestContext.Current.CancellationToken);
            var metadata = Metadata(finding);
            var snapshot = new FindingPolicySnapshot(
                await new FindingAssessmentStore(root.FullName).ReadAsync(TestContext.Current.CancellationToken), suppression, now);

            var projected = FindingEvidencePolicyProjection.Apply(metadata, snapshot);
            Assert.Single(projected["findings"]!.AsArray());
            Assert.Equal("confirmed", projected["findings"]![0]!["assessment"]!["status"]!.GetValue<string>());
            Assert.Equal("risk-accepted", projected["findings"]![0]!["resolution"]!["status"]!.GetValue<string>());
            Assert.Equal("exact-f", projected["findings"]![0]!["suppression"]!["ruleId"]!.GetValue<string>());
            Assert.Equal(1, projected["assessmentCounts"]!["confirmed"]!.GetValue<int>());
            Assert.Equal(1, projected["suppressionCounts"]!["suppressed"]!.GetValue<int>());
            Assert.Equal(0, projected["suppressionCounts"]!["visible"]!.GetValue<int>());
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static FindingIdentityRecord Identity(char value)
    {
        var hash = new string(value, 64);
        return new($"sha256:{hash}", $"finding-{hash}", "src/Api/Handler.cs", "correctness.test");
    }

    private static FindingObservation Observation(char value, string path, string kind, string source) =>
        new(Identity(value).Fingerprint, "correctness.test", path, kind, source, "finding-" + value, "Finding " + value);

    private static JsonObject Metadata(FindingIdentityRecord finding) => new()
    {
        ["kind"] = "code",
        ["findings"] = new JsonArray(new JsonObject
        {
            ["id"] = finding.Id,
            ["fingerprint"] = finding.Fingerprint,
            ["ruleId"] = finding.RuleId,
            ["title"] = "Finding",
            ["origin"] = new JsonObject { ["kind"] = "agent" },
            ["locations"] = new JsonArray(new JsonObject { ["path"] = finding.Path }),
        }),
    };
}
