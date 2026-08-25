using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyTests
{
    private static string Root => RepositoryTestContext.FindRepositoryRoot();

    [Fact]
    public void EmbeddedCoreCatalogueConformsAndPinsEveryApprovedAxis()
    {
        var cataloguePath = Path.Combine(Root, "src", "AgentOrchestrator.CodeQuality", "catalogues",
            "quality-taxonomy.core.v1.json");
        using var catalogue = JsonDocument.Parse(File.ReadAllText(cataloguePath));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            Root, "schemas", "quality-taxonomy.v1.schema.json")));

        var result = schema.Evaluate(catalogue.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
        Assert.Equal(QualityTaxonomyTerms.CatalogueId, QualityTaxonomyCatalogue.Core.Id);
        Assert.Equal(QualityTaxonomyTerms.CatalogueVersion, QualityTaxonomyCatalogue.Core.Version);
        Assert.Matches("^sha256:[0-9a-f]{64}$", QualityTaxonomyCatalogue.Digest);
        Assert.Equal(
            ["producer-kind", "evidence-status", "assessment", "change", "decision", "severity", "lifecycle", "evidence-kind"],
            QualityTaxonomyCatalogue.Core.Axes.Select(axis => axis.Id));
    }

    [Fact]
    public void ObservationSchemaAcceptsContractAndRejectsMissingEvidenceStatus()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            Root, "schemas", "quality-observation.v1.schema.json")));
        var json = QualityObservationJson.Serialize(CreateObservation());
        using var document = JsonDocument.Parse(json);

        var positive = schema.Evaluate(document.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(positive.IsValid, positive.ToString());

        var invalid = JsonNode.Parse(json)!.AsObject();
        invalid.Remove("evidenceStatus");
        using var invalidDocument = JsonDocument.Parse(invalid.ToJsonString());
        var negative = schema.Evaluate(invalidDocument.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(negative.IsValid);
    }

    [Fact]
    public void ExtensionsRoundTripAndUnknownMajorIsQuarantinedWithRawJson()
    {
        var observation = CreateObservation() with
        {
            Extensions = new Dictionary<string, JsonElement>
            {
                ["com.acme:review"] = JsonSerializer.SerializeToElement(new { enabled = true }),
            },
        };
        var json = QualityObservationJson.Serialize(observation).Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"x-legacy-root\": { \"retained\": true },",
            StringComparison.Ordinal);

        var loaded = QualityObservationJson.Deserialize(json);
        Assert.Equal(QualityObservationSupport.Supported, loaded.Support);
        Assert.True(loaded.Observation!.Extensions.ContainsKey("com.acme:review"));
        Assert.True(loaded.Observation.LegacyExtensions!.ContainsKey("x-legacy-root"));
        var roundTripped = QualityObservationJson.Serialize(loaded.Observation);
        Assert.Contains("\"x-legacy-root\"", roundTripped, StringComparison.Ordinal);
        Assert.Contains("\"com.acme:review\"", roundTripped, StringComparison.Ordinal);

        var future = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal)
            .Replace("quality-observation.v1", "quality-observation.v2", StringComparison.Ordinal);
        var unsupported = QualityObservationJson.Deserialize(future);
        Assert.Equal(QualityObservationSupport.UnsupportedMajor, unsupported.Support);
        Assert.Null(unsupported.Observation);
        Assert.Equal(2, unsupported.Raw.GetProperty("schemaVersion").GetInt32());
        Assert.True(unsupported.Raw.TryGetProperty("x-legacy-root", out _));
    }

    [Theory]
    [InlineData("pass", "pass", "allow", "available")]
    [InlineData("warn", "concern", "warn", "available")]
    [InlineData("block", "fail", "block", "available")]
    [InlineData("unavailable", "inconclusive", "defer", "unavailable")]
    public void MapsEverySecurityVerdict(string legacy, string assessment, string decision, string evidenceStatus)
    {
        var mapped = QualityLegacyMapping.SecurityVerdict(legacy);
        Assert.Equal(assessment, mapped.Assessment);
        Assert.Equal(decision, mapped.Decision);
        Assert.Equal(evidenceStatus, mapped.EvidenceStatus);
        Assert.Equal("security-sensor-agent-v1", mapped.PolicyRef);
    }

    [Theory]
    [InlineData("pass", "pass")]
    [InlineData("fail", "fail")]
    [InlineData("undetermined", "inconclusive")]
    public void MapsEveryFlowVerdict(string legacy, string expected) =>
        Assert.Equal(expected, QualityLegacyMapping.FlowVerdict(legacy).Assessment);

    [Theory]
    [InlineData("pass", "pass")]
    [InlineData("finding", "fail")]
    [InlineData("not-applicable", "not-applicable")]
    [InlineData("not-yet-checked", "not-assessed")]
    public void MapsEveryAttackVerdict(string legacy, string expected) =>
        Assert.Equal(expected, QualityLegacyMapping.AttackVerdict(legacy).Assessment);

    [Theory]
    [InlineData("no-quality-delta", "no-observed-delta")]
    [InlineData("improved", "improved")]
    [InlineData("neutral", "unchanged")]
    [InlineData("regression", "regressed")]
    public void MapsEveryChangeSummary(string legacy, string expected) =>
        Assert.Equal(expected, QualityLegacyMapping.ChangeSummary(legacy).Change);

    [Theory]
    [InlineData("good", "pass")]
    [InlineData("mixed", "concern")]
    [InlineData("concerning", "fail")]
    [InlineData("unknown", "inconclusive")]
    public void MapsEveryChangeAspect(string legacy, string expected) =>
        Assert.Equal(expected, QualityLegacyMapping.ChangeAspect(legacy).Assessment);

    [Theory]
    [InlineData("open", "open")]
    [InlineData("accepted", "accepted-risk")]
    [InlineData("accepted-risk", "accepted-risk")]
    [InlineData("waived", "waived")]
    [InlineData("falsePositive", "false-positive")]
    [InlineData("false-positive", "false-positive")]
    [InlineData("resolved", "resolved")]
    public void MapsEveryFindingStateAlias(string legacy, string expected) =>
        Assert.Equal(expected, QualityLegacyMapping.FindingState(legacy).Lifecycle);

    [Fact]
    public void MapsAspectsAndPreservesUnknownNamespaceTerms()
    {
        Assert.Equal("code.correctness", QualityLegacyMapping.Aspect("correctness"));
        Assert.Equal("security.dependencies", QualityLegacyMapping.Aspect("dependencies"));
        Assert.Equal("change.architecture-drift", QualityLegacyMapping.Aspect("architecture-drift"));
        Assert.Equal("com.acme:resilience.backpressure",
            QualityLegacyMapping.Aspect("com.acme:resilience.backpressure"));
    }

    [Fact]
    public void MapsStructuredAndPlainEvidenceWithoutDiscardingOriginalContent()
    {
        var structured = QualityLegacyMapping.Evidence("ev-json", "{\"scanner\":\"gitleaks\"}");
        Assert.Equal("tool-result", structured.Kind);
        Assert.Equal("application/json", structured.MediaType);
        Assert.Equal("gitleaks", structured.Content!.Value.GetProperty("scanner").GetString());

        var text = QualityLegacyMapping.Evidence("ev-text", "line 4 contains a credential");
        Assert.Equal("document", text.Kind);
        Assert.Equal("text/plain", text.MediaType);
        Assert.Equal("line 4 contains a credential", text.Content!.Value.GetString());
    }

    [Fact]
    public async Task ObservationLedgerIsIdempotentAndToleratesMalformedLines()
    {
        var root = Directory.CreateTempSubdirectory("quality-observations-");
        try
        {
            var observation = CreateObservation();
            Assert.True(await QualityObservationLedger.AppendAsync(
                root.FullName, observation, TestContext.Current.CancellationToken));
            Assert.False(await QualityObservationLedger.AppendAsync(
                root.FullName, observation, TestContext.Current.CancellationToken));
            var path = QualityObservationLedger.GetLedgerPath(root.FullName, observation.ObservedAt);
            await File.AppendAllTextAsync(path, "{malformed}\n", TestContext.Current.CancellationToken);

            var read = await QualityObservationLedger.ReadAsync(root.FullName,
                TestContext.Current.CancellationToken);

            Assert.Equal(observation.ObservationId, Assert.Single(read).ObservationId);
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static QualityObservationDocument CreateObservation() => new()
    {
        ObservationId = "observation-sha256:" + new string('a', 64),
        ObservedAt = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero),
        Taxonomy = new QualityTaxonomyReference
        {
            Id = QualityTaxonomyTerms.CatalogueId,
            Version = QualityTaxonomyTerms.CatalogueVersion,
            Digest = "sha256:" + new string('b', 64),
        },
        Subject = new QualityObservationSubject
        {
            UnitId = "qs-v1/dotnet/file/test",
            ManifestHash = "sha256:" + new string('c', 64),
            Path = "src/A.cs",
            Scope = "file",
        },
        Profile = new QualityObservationProfile
        {
            Id = "file-code-review",
            Version = "1.0.0",
            PromptHash = "sha256:" + new string('d', 64),
            ReviewInputsHash = "sha256:" + new string('e', 64),
        },
        Producer = new QualityObservationProducer
        {
            Kind = "agent",
            Agent = "codex",
            Provider = "openai",
            RequestedModel = "model-a",
            EffectiveModel = "model-a",
            ThinkingLevel = "high",
            RoutePolicyVersion = "2026-07-24",
            RunId = "run-a",
        },
        EvidenceStatus = "available",
        Evidence =
        [
            new QualityEvidence
            {
                Id = "ev-1",
                Kind = "source-code",
                Locator = new QualityEvidenceLocator { Path = "src/A.cs", StartLine = 1 },
                Summary = "Relevant source.",
                ContentHash = "sha256:" + new string('f', 64),
            },
        ],
        Aspects =
        [
            new QualityObservationAspect
            {
                AspectId = "code.correctness",
                Assessment = "pass",
                Rationale = "No defect observed.",
                Grade = new QualityObservationGrade { Score = 95, Band = "A" },
            },
        ],
        Assessment = "pass",
        Findings = [],
    };
}
