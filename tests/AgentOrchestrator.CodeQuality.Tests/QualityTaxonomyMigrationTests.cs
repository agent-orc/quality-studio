using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyMigrationTests
{
    [Fact]
    public async Task Dry_run_is_read_only_apply_is_idempotent_and_domain_values_are_preserved()
    {
        var root = Directory.CreateTempSubdirectory("quality-taxonomy-migration-");
        try
        {
            var paths = await CreateLegacyFilesAsync(root.FullName);
            var before = paths.ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
            var migrator = new QualityTaxonomyMigrator();

            var dryRun = await migrator.MigrateAsync(root.FullName, apply: false,
                TestContext.Current.CancellationToken);

            Assert.Equal("dry-run", dryRun.Mode);
            Assert.Equal(5, dryRun.Imported);
            Assert.Equal(1, dryRun.AmbiguousSource);
            Assert.Equal(0, dryRun.Errors);
            Assert.False(Directory.Exists(Path.Combine(root.FullName, QualityObservationStore.RelativeDirectory)));
            Assert.All(before, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));

            var applied = await migrator.MigrateAsync(root.FullName, apply: true,
                TestContext.Current.CancellationToken);
            var replay = await migrator.MigrateAsync(root.FullName, apply: true,
                TestContext.Current.CancellationToken);
            var stored = await new QualityObservationStore(root.FullName)
                .ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.Equal(5, applied.Imported);
            Assert.Equal(0, applied.Errors);
            Assert.Equal(0, replay.Imported);
            Assert.Equal(5, replay.Skipped);
            Assert.Equal(4, stored.Observations.Count);
            Assert.All(before, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));

            var sidecar = Assert.Single(stored.Observations,
                item => item.Legacy?.Schema?.Contains("review-meta", StringComparison.Ordinal) == true);
            Assert.Equal(QualityProducerKind.Unknown, Assert.Single(sidecar.Findings).Source.Kind);
            Assert.Equal("file-code-review", sidecar.Profile.Id);
            Assert.Equal("sha256:" + new string('b', 64), sidecar.Profile.PromptHash);

            var flow = Assert.Single(stored.Observations, item => item.Legacy?.Schema == FlowReviewRunner.ReportSchema);
            Assert.Equal(QualityAssessment.Pass, flow.Assessment);
            Assert.Equal("flow-business-logic-review", flow.Profile.Id);

            var change = Assert.Single(stored.Observations, item => item.Profile.Kind == "change");
            Assert.Equal(QualityChange.Regressed, change.Change);
            Assert.Equal(4, change.Aspects.Count);
            Assert.All(change.Aspects, aspect => Assert.Equal(QualityAssessment.NotAssessed, aspect.Assessment));

            var attack = Assert.Single(stored.Observations,
                item => item.Extensions.ContainsKey("quality-studio/attack-assessment-id"));
            Assert.Equal("assessment-1",
                attack.Extensions["quality-studio/attack-assessment-id"].GetString());
            Assert.Equal("high", attack.Producer.ThinkingLevel);
            Assert.Equal(QualityAssessment.Pass, attack.Assessment);

            var lifecycle = await new FindingLifecycleStore(root.FullName)
                .ReadAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(QualityLifecycleState.AcceptedRisk, Assert.Single(lifecycle.ByIssueId).Value.State);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Legacy_sensor_document_is_deterministic_only_when_provenance_is_explicit()
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        var payload = File.ReadAllText(Path.Combine(
            repositoryRoot, "samples", "dependency-vulnerability.real-run.review-meta.security.json"));
        var metadata = JsonNode.Parse(payload)!.AsObject();

        var observation = QualityDomainObservationAdapters.FromReviewMeta(
            metadata,
            "samples/dependency-vulnerability.real-run.review-meta.security.json",
            QualityDomainObservationAdapters.ImportId("review-meta", "sensor-sample", payload));

        Assert.Equal(QualityProducerKind.DeterministicSensor, observation.Producer.Kind);
        Assert.Equal("dependencies", observation.Producer.Agent);
        var finding = Assert.Single(observation.Findings);
        Assert.Equal(QualityProducerKind.DeterministicSensor, finding.Source.Kind);
        Assert.Equal("dependencies", finding.Source.ProducerRef);
        Assert.Equal("dependency-vulnerability-sensor", observation.Profile.Id);
    }

    [Fact]
    public async Task Cli_writes_machine_readable_dry_run_report()
    {
        var root = Directory.CreateTempSubdirectory("quality-taxonomy-cli-");
        try
        {
            await CreateLegacyFilesAsync(root.FullName);
            var reportPath = Path.Combine(root.FullName, "migration-report.json");

            var exitCode = await global::QualityCli.RunAsync(
                ["taxonomy", "migrate", root.FullName, "--dry-run", "--report", reportPath]);

            Assert.Equal(0, exitCode);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                reportPath, TestContext.Current.CancellationToken));
            Assert.Equal("dry-run", report.RootElement.GetProperty("mode").GetString());
            Assert.Equal(5, report.RootElement.GetProperty("imported").GetInt32());
            Assert.Equal(0, report.RootElement.GetProperty("errors").GetInt32());
            Assert.False(Directory.Exists(Path.Combine(root.FullName, QualityObservationStore.RelativeDirectory)));
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static async Task<IReadOnlyList<string>> CreateLegacyFilesAsync(string root)
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        var sidecar = Path.Combine(root, ".quality", "reviews", "files", "sample.review-meta.code.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
        File.Copy(Path.Combine(repositoryRoot, "samples", "review-meta.v1.sample.json"), sidecar);

        var change = Path.Combine(root, ".quality", "changes", "change.json");
        Directory.CreateDirectory(Path.GetDirectoryName(change)!);
        File.Copy(Path.Combine(repositoryRoot, ".quality", "changes",
            "e4cc1d14752690df02cc8e16dcdefdcdc90d882b.json"), change);

        var flow = Path.Combine(root, ".quality", "flows", "fixture.flow-review.json");
        Directory.CreateDirectory(Path.GetDirectoryName(flow)!);
        var flowReport = new FlowReviewReport(
            FlowReviewRunner.ReportSchema,
            1,
            new FlowDefinition("checkout", "Checkout", "Checkout flow.", ["boundary-1"]),
            FlowReviewVerdict.Pass,
            "The flow passed.",
            null,
            [],
            new FlowFindingCounts(0, 0, 0, 0, 0, 0),
            new FlowReviewProvenance("codex", "model-flow", "flow-run", FlowReviewRunner.PromptId,
                FlowReviewRunner.PromptVersion, "sha256:" + new string('a', 64),
                "sha256:" + new string('b', 64), "sha256:" + new string('c', 64),
                new DateTimeOffset(2026, 8, 11, 7, 0, 0, TimeSpan.Zero),
                new TokenUsage(10, 5, 0, 1, 100), new FlowReviewCost("resolved", 0.01m, "USD")));
        var camel = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        camel.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        await File.WriteAllTextAsync(flow, JsonSerializer.Serialize(flowReport, camel),
            TestContext.Current.CancellationToken);

        var attack = Path.Combine(root, AttackCoverageLedger.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(attack)!);
        var attackObservation = new AttackCoverageObservation(
            1, "assessment-1", "boundary-1", "attack-1", AttackCoverageVerdict.Pass,
            "The evidence establishes a pass.",
            [new AttackEvidence("covered-code", "src/App.cs#line:1", "Covered source.")],
            [], null, null, AttackCoverageSource.Agent,
            new AttackReviewerIdentity("codex", "model-attack", "high"),
            "1.0.0", "sha256:" + new string('d', 64), "1.0.0",
            "sha256:" + new string('e', 64), "sha256:" + new string('f', 64),
            "sha256:" + new string('1', 64), new AttackTokenCost(20, 10, 5, 2),
            new DateTimeOffset(2026, 8, 11, 7, 30, 0, TimeSpan.Zero), "commit", null);
        await File.WriteAllTextAsync(attack,
            JsonSerializer.Serialize(attackObservation, AttackCoverageJson.Options).Replace("\r", string.Empty)
                .Replace("\n", string.Empty) + "\n", TestContext.Current.CancellationToken);

        var state = Path.Combine(root, FindingStateStore.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        var stateJson = $$"""
            {
              "schemaVersion": 1,
              "revision": 1,
              "findings": [{
                "fingerprint": "sha256:{{new string('2', 64)}}",
                "findingId": "finding-{{new string('2', 64)}}",
                "path": "src/App.cs",
                "ruleId": "quality.test",
                "state": "accepted",
                "author": "Ada",
                "reason": "Accepted for migration coverage.",
                "timestamp": "2026-08-11T07:45:00+00:00"
              }]
            }
            """;
        await File.WriteAllTextAsync(state, stateJson, TestContext.Current.CancellationToken);

        return [sidecar, change, flow, attack, state];
    }
}
