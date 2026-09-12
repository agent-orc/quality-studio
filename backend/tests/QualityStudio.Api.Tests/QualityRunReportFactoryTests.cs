using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class QualityRunReportFactoryTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly string Fingerprint = "sha256:" + new string('a', 64);

    [Fact]
    public void Factory_preserves_captured_v3_evidence_and_observed_reviewer_without_changing_requested_route()
    {
        var anchor = new FindingAnchor("primary", FindingAnchorRole.Primary, "src/App.cs",
            new FindingRange(new(2, 1), new(2, 9)),
            new CapturedExcerpt("unsafe()", Fingerprint, "sha256:" + new string('b', 64)), "App.Run");
        var evidence = new FindingEvidenceItem("span", FindingEvidenceClass.SourceSpan,
            FindingEvidenceStatus.Observed, "primary");
        var finding = new ReviewFinding("finding-1", "correctness", FindingSeverity.High,
            "Unsafe call", "Retained problem.", "Retained remedy.",
            [new FindingLocation("src/App.cs", anchor.Range, anchor.SymbolId)],
            Fingerprint, "safe-call", "Legacy source claim.", Anchors: [anchor],
            EvidenceItems: [evidence], Reproduction: new(ReproductionStatus.Unknown));
        var sensorFinding = finding with
        {
            Id = "sensor-finding",
            Fingerprint = "sha256:" + new string('c', 64),
            Source = new(FindingSourceKind.Deterministic, "fixture-sensor", "fixture-producer", "1", 0),
        };
        var reviewer = new ReviewerIdentity("claude", "claude-opus-5", "2.1.270", "actual-provider-run",
            new ReviewerUsage("claude", 100, 20, 10, 5, 1500),
            RequestedModel: "claude-fable-5-1", RequestedThinkingLevel: "high");
        var metadata = new JsonObject
        {
            ["schemaVersion"] = 3,
            // Full-document parsing would reject this runtime precision in older metadata readers.
            // Projection only needs the captured child contracts and must not recalculate provenance.
            ["reviewedAt"] = "2026-09-12T10:00:00.1234567Z",
            ["sourceRevision"] = "git:" + new string('d', 40) + "-dirty",
            ["reviewer"] = JsonSerializer.SerializeToNode(reviewer, ReviewMetaJson.Options),
            ["reviewedHash"] = new JsonObject { ["value"] = Fingerprint },
            ["findings"] = JsonSerializer.SerializeToNode(new[] { finding }, ReviewMetaJson.Options),
            ["deterministicEvidence"] = new JsonArray(new JsonObject
            {
                ["provenance"] = new JsonObject { ["sensorId"] = "fixture-sensor" },
                ["findings"] = JsonSerializer.SerializeToNode(new[] { sensorFinding }, ReviewMetaJson.Options),
            }),
        };

        var report = Build(metadata);
        var observation = Assert.Single(report.Observations);
        Assert.Equal("claude-fable-5-1", report.Run.Model);
        Assert.Equal("high", report.Run.ThinkingLevel);
        Assert.Equal("claude-opus-5", observation.Reviewer!.Model);
        Assert.Equal("claude-fable-5-1", observation.Reviewer.RequestedModel);
        Assert.Equal("high", observation.Reviewer.RequestedThinkingLevel);
        Assert.Equal(reviewer.Usage, observation.Reviewer.Usage);
        Assert.Equal("actual-provider-run", observation.ProviderRunId);
        Assert.Equal("git:" + new string('d', 40) + "-dirty", observation.SourceRevision);
        foreach (var retained in observation.Findings)
        {
            Assert.Equal(anchor, Assert.Single(retained.Anchors!));
            Assert.Equal(evidence, Assert.Single(retained.EvidenceItems!));
            Assert.Equal(ReproductionStatus.Unknown, retained.Reproduction!.Status);
        }
        Assert.Equal("agent", observation.Findings[0].Source);
        Assert.Equal("deterministic", observation.Findings[1].Source);
        Assert.Equal("fixture-sensor", observation.Findings[1].SensorId);
        Assert.Equal("fixture-producer", observation.Findings[1].Producer);
        var roundTrip = QualityRunReportJson.Deserialize(QualityRunReportJson.Serialize(report));
        Assert.Equal("claude-opus-5", roundTrip.Observations[0].Reviewer!.Model);
        Assert.Equal(anchor, Assert.Single(roundTrip.Observations[0].Findings[0].Anchors!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Factory_keeps_missing_legacy_evidence_and_requested_effort_unknown(int version)
    {
        var metadata = new JsonObject
        {
            ["schemaVersion"] = version,
            ["reviewer"] = new JsonObject { ["agent"] = "claude", ["model"] = "claude-opus-5" },
            ["findings"] = new JsonArray(new JsonObject
            {
                ["id"] = "finding-legacy",
                ["fingerprint"] = Fingerprint,
                ["evidence"] = "An unverified old claim.",
            }),
        };

        var observation = Assert.Single(Build(metadata).Observations);
        Assert.Equal("claude-opus-5", observation.Reviewer!.Model);
        Assert.Null(observation.Reviewer.RequestedThinkingLevel);
        Assert.Null(observation.Reviewer.RequestedModel);
        Assert.Null(observation.SourceRevision);
        var finding = Assert.Single(observation.Findings);
        Assert.Equal("An unverified old claim.", finding.Evidence);
        Assert.Null(finding.Anchors);
        Assert.Null(finding.EvidenceItems);
        Assert.Null(finding.Reproduction);
    }

    private static QualityRunReportDocument Build(JsonObject metadata)
    {
        var manifest = new ReviewRunManifest("review-native", "default",
            new("unit-file", "App.cs", "src/App.cs"), "file", "code", "claude-fable-5-1", "claude",
            Timestamp, [new("unit-file", "App.cs", "src/App.cs", Fingerprint)], null,
            ThinkingLevel: "high");
        var status = new ReviewRunStatus(manifest.RunId, "done", 1, 1, 0, 1,
            Timestamp, Timestamp, Timestamp.AddSeconds(2), [], 1, new(100, 20, 10, 5, 1500));
        var progress = new ReviewRunFileTransition("src/App.cs", "done", Timestamp,
            Timestamp.AddSeconds(2), manifest.RunId, null);
        var snapshot = new ReviewObservationSnapshot("reviews/App.review-meta.code.json",
            "sha256:" + new string('e', 64), Timestamp.AddSeconds(1), metadata.ToJsonString(),
            new Dictionary<string, string> { [Fingerprint] = "open" });
        return QualityRunReportFactory.Build(manifest, status, [progress],
            new Dictionary<string, ReviewObservationSnapshot> { ["src/App.cs"] = snapshot },
            Path.GetTempPath(), "Synthetic fixture", 1, []);
    }
}
