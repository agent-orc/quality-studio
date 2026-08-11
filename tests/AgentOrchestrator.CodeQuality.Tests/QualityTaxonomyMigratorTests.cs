using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyMigratorTests
{
    [Fact]
    public async Task FindingStateImportsOneSnapshotEventAndRetainsLegacyStateBytes()
    {
        var root = Directory.CreateTempSubdirectory("quality-lifecycle-migration-").FullName;
        try
        {
            var path = Path.Combine(root, ".quality", "findings", "state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var state = """
                        {
                          "schemaVersion": 1,
                          "revision": 4,
                          "findings": [{
                            "fingerprint": "sha256:legacy",
                            "findingId": "finding-legacy",
                            "path": "src/App.cs",
                            "ruleId": "legacy-rule",
                            "state": "accepted",
                            "author": "Ada",
                            "reason": "Accepted for the migration window.",
                            "timestamp": "2026-08-10T08:00:00Z"
                          }]
                        }
                        """;
            await File.WriteAllTextAsync(path, state, TestContext.Current.CancellationToken);
            var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

            var dryRun = await QualityTaxonomyMigrator.MigrateAsync(
                root, apply: false, TestContext.Current.CancellationToken);
            Assert.Equal(1, dryRun.Imported);
            Assert.False(File.Exists(IssueLifecycleStore.GetPath(root)));

            var applied = await QualityTaxonomyMigrator.MigrateAsync(
                root, apply: true, TestContext.Current.CancellationToken);
            Assert.Equal(1, applied.Imported);
            var lifecycleEvent = Assert.Single(await IssueLifecycleStore.ReadAsync(
                root, TestContext.Current.CancellationToken));
            Assert.Equal("accepted-risk", lifecycleEvent.State);
            Assert.Equal("imported", lifecycleEvent.ProducerKind);
            Assert.Equal("Ada", lifecycleEvent.Author);

            var repeated = await QualityTaxonomyMigrator.MigrateAsync(
                root, apply: true, TestContext.Current.CancellationToken);
            Assert.Equal(0, repeated.Imported);
            Assert.Equal(1, repeated.Skipped);
            Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ResolvedFindingStateImportsAsHonestLegacySnapshotWithoutInventedObservationBasis()
    {
        var root = Directory.CreateTempSubdirectory("quality-resolved-lifecycle-migration-").FullName;
        try
        {
            var path = Path.Combine(root, ".quality", "findings", "state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fingerprint = "sha256:" + new string('a', 64);
            await File.WriteAllTextAsync(path, $$"""
                {
                  "schemaVersion": 1,
                  "revision": 1,
                  "findings": [{
                    "fingerprint": "{{fingerprint}}",
                    "findingId": "finding-resolved",
                    "path": "src/App.cs",
                    "ruleId": "legacy-rule",
                    "state": "resolved",
                    "author": "legacy-review",
                    "reason": "Absent in the historical current projection.",
                    "timestamp": "2026-08-10T08:00:00Z"
                  }]
                }
                """, TestContext.Current.CancellationToken);

            var report = await QualityTaxonomyMigrator.MigrateAsync(
                root, apply: true, TestContext.Current.CancellationToken);

            Assert.Equal(1, report.Imported);
            var lifecycleEvent = Assert.Single(await IssueLifecycleStore.ReadAsync(
                root, TestContext.Current.CancellationToken));
            Assert.Equal("resolved", lifecycleEvent.State);
            Assert.Equal("imported", lifecycleEvent.ProducerKind);
            Assert.Equal(FindingStateStore.RelativePath, lifecycleEvent.LegacySource);
            Assert.Null(lifecycleEvent.BasisObservationIds);
            Assert.Null(lifecycleEvent.PolicyRef);

            var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
                RepositoryTestContext.FindRepositoryRoot(), "schemas", "issue-lifecycle-event.v1.schema.json")),
                new BuildOptions { SchemaRegistry = new SchemaRegistry() });
            using var json = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(
                IssueLifecycleStore.GetPath(root), TestContext.Current.CancellationToken)));
            var validation = schema.Evaluate(
                json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(validation.IsValid, validation.ToString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TaxonomyCliDryRunWritesOnlyTheRequestedMachineReadableReport()
    {
        var root = Directory.CreateTempSubdirectory("quality-taxonomy-cli-").FullName;
        var reportPath = Path.Combine(Path.GetTempPath(), $"quality-taxonomy-report-{Guid.NewGuid():N}.json");
        try
        {
            var exitCode = await global::QualityCli.RunAsync(
                ["taxonomy", "migrate", root, "--dry-run", "--report", reportPath]);

            Assert.Equal(0, exitCode);
            Assert.False(Directory.Exists(Path.Combine(root, ".quality")));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
                reportPath, TestContext.Current.CancellationToken));
            Assert.Equal("dry-run", report.RootElement.GetProperty("mode").GetString());
            Assert.Equal(0, report.RootElement.GetProperty("errors").GetInt32());
        }
        finally
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReviewMetaAdapterUsesProvenSourceAndNeverDefaultsMissingSourceToAgent()
    {
        var metadata = JsonNode.Parse("""
            {
              "unit": { "id": "unit", "level": "file", "path": "src/App.cs" },
              "kind": "security",
              "grade": { "score": 50, "band": "F" },
              "findings": [
                {
                  "id": "finding-one", "fingerprint": "sha256:one", "ruleId": "secret", "severity": "high",
                  "locations": [{ "path": "src/App.cs" }],
                  "source": { "kind": "deterministic", "sensorId": "gitleaks" }
                },
                {
                  "id": "finding-two", "fingerprint": "sha256:two", "ruleId": "auth", "severity": "medium",
                  "locations": [{ "path": "src/App.cs" }]
                }
              ]
            }
            """)!.AsObject();

        var observation = QualityDomainObservationAdapters.FromReviewMeta(
            metadata, ".quality/reviews/app.review-meta.security.json", usage: null);

        Assert.Equal("deterministic-sensor", observation.Findings[0].Source.Kind);
        Assert.Equal("gitleaks", observation.Findings[0].Source.ProducerRef);
        Assert.Equal("unknown", observation.Findings[1].Source.Kind);
        Assert.NotEqual("agent", observation.Findings[1].Source.Kind);
        Assert.All(observation.Findings, finding => Assert.NotEmpty(finding.EvidenceRefs));
    }

    [Fact]
    public void ChangeAdapterPreservesFourJudgementsOnTheChangeAxis()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var path = Directory.EnumerateFiles(Path.Combine(root, ".quality", "changes"), "*.json").First();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        var document = JsonSerializer.Deserialize<ChangeReviewDocument>(File.ReadAllText(path), options)!;

        var observation = QualityDomainObservationAdapters.FromChange(document,
            Path.GetRelativePath(root, path).Replace('\\', '/'));

        Assert.Equal(
            ["change.risk", "change.test-evidence", "change.scope-discipline", "change.architecture-drift"],
            observation.Aspects.Select(item => item.AspectId));
        Assert.All(observation.Aspects, item => Assert.NotNull(item.Change));
        Assert.Equal("change-review.v1", observation.Legacy?.Schema);
        Assert.Equal("task", observation.Subject.Scope);
        AssertObservationValid(observation);
    }

    [Fact]
    public async Task DryRunDoesNotWriteAndApplyIsIdempotentWithoutChangingLegacyBytes()
    {
        var root = Directory.CreateTempSubdirectory("quality-taxonomy-migration-").FullName;
        try
        {
            var sidecar = Path.Combine(root, ".quality", "reviews", "files", "app.review-meta.code.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            var legacy = """
                         {
                           "schemaVersion": 2,
                           "unit": { "id": "unit:file:src/App.cs", "level": "file", "path": "src/App.cs" },
                           "reviewedAt": "2026-08-11T08:00:00Z",
                           "kind": "code",
                           "reviewer": { "agent": "codex", "model": "legacy-model", "runId": "legacy-run" },
                           "reviewedHash": { "value": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                           "reviewInputs": {
                             "effectiveHash": { "value": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
                             "prompt": { "id": "file-code-review", "version": "1.0.0", "contentHash": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" }
                           },
                           "grade": { "score": 84, "band": "B", "rationale": "Legacy grade." },
                           "aspects": [{
                             "id": "correctness",
                             "title": "Correctness",
                             "grade": { "score": 84, "band": "B", "rationale": "Legacy aspect grade." }
                           }],
                           "findings": []
                         }
                         """;
            await File.WriteAllTextAsync(sidecar, legacy, TestContext.Current.CancellationToken);
            var original = await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken);

            var dryRun = await QualityTaxonomyMigrator.MigrateAsync(
                root, apply: false, TestContext.Current.CancellationToken);

            Assert.Equal(1, dryRun.Imported);
            Assert.Equal("would-import", Assert.Single(dryRun.Items).Status);
            Assert.Equal(0, dryRun.UnknownModel);
            Assert.False(Directory.Exists(Path.Combine(root, ".quality", "observations")));
            Assert.Equal(original, await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken));

            var applied = await QualityTaxonomyMigrator.MigrateAsync(
                root, apply: true, TestContext.Current.CancellationToken);
            Assert.Equal(1, applied.Imported);
            var observation = Assert.Single(await QualityObservationLedger.ReadAsync(
                root, TestContext.Current.CancellationToken));
            Assert.Equal("review-meta.v2", observation.Legacy?.Schema);
            Assert.Equal("partial", observation.Legacy?.Completeness);
            Assert.Equal("legacy-model", observation.Producer.EffectiveModel);
            Assert.Equal("unknown", observation.Producer.ThinkingLevel);
            var aspect = Assert.Single(observation.Aspects);
            Assert.Equal("code.correctness", aspect.AspectId);
            Assert.Equal("pass", aspect.Assessment);
            Assert.Equal(84, aspect.Grade?.Score);
            Assert.Equal("Legacy aspect grade.", aspect.Rationale);
            var observationSchema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
                RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json")),
                new BuildOptions { SchemaRegistry = new SchemaRegistry() });
            using var observationJson = JsonDocument.Parse(QualityObservationJson.Serialize(observation));
            var validation = observationSchema.Evaluate(
                observationJson.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(validation.IsValid, validation.ToString());

            var repeated = await QualityTaxonomyMigrator.MigrateAsync(
                root, apply: true, TestContext.Current.CancellationToken);
            Assert.Equal(0, repeated.Imported);
            Assert.Equal(1, repeated.Skipped);
            Assert.Equal("skipped", Assert.Single(repeated.Items).Status);
            Assert.Single(await QualityObservationLedger.ReadAsync(
                root, TestContext.Current.CancellationToken));
            Assert.Equal(original, await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken));

            var reportJson = JsonSerializer.Serialize(repeated);
            Assert.Contains("UnknownModel", reportJson, StringComparison.Ordinal);
            Assert.Contains("Errors", reportJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AssertObservationValid(QualityObservationDocument observation)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json")),
            new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        using var json = JsonDocument.Parse(QualityObservationJson.Serialize(observation));
        var validation = schema.Evaluate(
            json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
    }
}
