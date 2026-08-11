using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly string RepositoryRoot = RepositoryTestContext.FindRepositoryRoot();
    private static readonly Lazy<JsonSchema> CatalogueSchema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryRoot, "schemas", "quality-taxonomy.v1.schema.json"))));
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryRoot, "schemas", "quality-observation.v1.schema.json"))));

    [Fact]
    public void Embedded_core_catalogue_is_complete_and_schema_conformant()
    {
        var path = Path.Combine(RepositoryRoot, "src", "AgentOrchestrator.CodeQuality", "catalogues",
            "quality-studio-core.v1.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));

        var result = CatalogueSchema.Value.Evaluate(json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
        Assert.Equal("quality-studio/core", QualityTaxonomyCatalogue.CoreDocument.Id);
        Assert.Equal("1.0.0", QualityTaxonomyCatalogue.CoreDocument.Version);
        Assert.Matches("^sha256:[0-9a-f]{64}$", QualityTaxonomyCatalogue.CoreDigest);
        Assert.Equal(16, QualityTaxonomyCatalogue.CoreDocument.Aspects.Count);
        Assert.Equal("code.correctness", QualityLegacyMappings.MapAspect("correctness"));
        Assert.Equal("security.boundary-exposure", QualityLegacyMappings.MapAspect("boundaries"));
        Assert.Equal("com.acme:producer.resilience", QualityLegacyMappings.MapAspect("resilience", "com.acme"));
        Assert.Throws<ArgumentException>(() => QualityLegacyMappings.MapAspect("sensor-availability"));
    }

    [Fact]
    public void Observation_schema_accepts_contract_and_rejects_missing_explicit_producer_provenance()
    {
        var observation = CreateObservation();
        using var json = JsonDocument.Parse(QualityObservationJson.Serialize(observation));
        var positive = ObservationSchema.Value.Evaluate(json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(positive.IsValid, positive.ToString());

        var invalid = JsonSerializer.SerializeToNode(observation, QualityObservationJson.Options)!.AsObject();
        invalid["producer"]!.AsObject().Remove("provider");
        using var invalidJson = JsonDocument.Parse(invalid.ToJsonString());
        var negative = ObservationSchema.Value.Evaluate(invalidJson.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(negative.IsValid);
    }

    [Fact]
    public void Explicit_extensions_and_unknown_aspects_round_trip_without_joining_the_core_catalogue()
    {
        var extensions = new Dictionary<string, JsonElement>
        {
            ["com.acme/trace"] = JsonSerializer.SerializeToElement(new { value = 42 }),
        };
        var original = CreateObservation() with
        {
            Aspects =
            [
                .. CreateObservation().Aspects,
                new QualityAspectObservation("com.acme:resilience.backpressure", "Backpressure",
                    QualityAssessment.Concern, null, "The extension assessment is retained.", null, extensions),
            ],
            Extensions = extensions,
        };

        var serialized = QualityObservationJson.Serialize(original);
        var loaded = QualityObservationJson.Deserialize(serialized);
        var roundTripped = QualityObservationJson.Serialize(loaded);

        Assert.Equal(42, loaded.Extensions["com.acme/trace"].GetProperty("value").GetInt32());
        Assert.Contains(loaded.Aspects, aspect => aspect.AspectId == "com.acme:resilience.backpressure");
        Assert.False(QualityTaxonomyCatalogue.IsCoreAspect("com.acme:resilience.backpressure"));
        Assert.Contains("com.acme:resilience.backpressure", roundTripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_taxonomy_major_is_quarantined_without_discarding_raw_json()
    {
        var json = QualityObservationJson.Serialize(CreateObservation())
            .Replace("\"version\": \"1.0.0\"", "\"version\": \"2.0.0\"", StringComparison.Ordinal);

        var result = QualityObservationJson.Read(json);

        Assert.Equal(QualityObservationSupport.UnsupportedTaxonomy, result.Support);
        Assert.Null(result.Observation);
        Assert.Equal("2.0.0", result.Raw.GetProperty("taxonomy").GetProperty("version").GetString());
        Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));
    }

    [Fact]
    public void Core_aspects_reject_an_axis_not_allowed_by_the_catalogue()
    {
        var observation = CreateObservation() with
        {
            Aspects = [new QualityAspectObservation(
                "code.correctness", "Correctness", null, QualityChange.Improved,
                "A code assessment cannot silently become a change direction.", null,
                QualityObservationJson.NoExtensions)],
        };

        Assert.Throws<JsonException>(() => QualityObservationJson.Serialize(observation));
    }

    [Theory]
    [InlineData("pass", QualityAssessment.Pass, QualityEvidenceStatus.Available, QualityDecisionValue.Allow)]
    [InlineData("warn", QualityAssessment.Concern, QualityEvidenceStatus.Available, QualityDecisionValue.Warn)]
    [InlineData("block", QualityAssessment.Fail, QualityEvidenceStatus.Available, QualityDecisionValue.Block)]
    [InlineData("unavailable", QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable, QualityDecisionValue.Defer)]
    public void Security_verdict_conformance_vectors_are_axis_safe(
        string legacy, QualityAssessment assessment, QualityEvidenceStatus status, QualityDecisionValue decision)
    {
        var mapped = QualityLegacyMappings.MapSecurityVerdict(legacy);
        Assert.Equal(assessment, mapped.Assessment);
        Assert.Equal(status, mapped.EvidenceStatus);
        Assert.Equal(decision, mapped.Decision?.Value);
        Assert.Equal("security-sensor-agent-v1", mapped.Decision?.PolicyRef);
    }

    [Theory]
    [InlineData("pass", QualityAssessment.Pass)]
    [InlineData("fail", QualityAssessment.Fail)]
    [InlineData("undetermined", QualityAssessment.Inconclusive)]
    public void Flow_verdict_conformance_vectors_map(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, QualityLegacyMappings.MapFlowVerdict(legacy));

    [Theory]
    [InlineData("pass", QualityAssessment.Pass)]
    [InlineData("finding", QualityAssessment.Fail)]
    [InlineData("not-applicable", QualityAssessment.NotApplicable)]
    [InlineData("not-yet-checked", QualityAssessment.NotAssessed)]
    public void Attack_verdict_conformance_vectors_map(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, QualityLegacyMappings.MapAttackVerdict(legacy));

    [Theory]
    [InlineData("no-quality-delta", QualityChange.NoObservedDelta)]
    [InlineData("improved", QualityChange.Improved)]
    [InlineData("neutral", QualityChange.Unchanged)]
    [InlineData("regression", QualityChange.Regressed)]
    public void Change_summary_conformance_vectors_map(string legacy, QualityChange expected) =>
        Assert.Equal(expected, QualityLegacyMappings.MapChangeSummary(legacy));

    [Theory]
    [InlineData("good", QualityAssessment.Pass)]
    [InlineData("mixed", QualityAssessment.Concern)]
    [InlineData("concerning", QualityAssessment.Fail)]
    [InlineData("unknown", QualityAssessment.Inconclusive)]
    public void Change_aspect_conformance_vectors_map(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, QualityLegacyMappings.MapChangeAspect(legacy));

    [Theory]
    [InlineData("accepted", QualityLifecycleState.AcceptedRisk)]
    [InlineData("accepted-risk", QualityLifecycleState.AcceptedRisk)]
    [InlineData("falsePositive", QualityLifecycleState.FalsePositive)]
    [InlineData("false-positive", QualityLifecycleState.FalsePositive)]
    public void Lifecycle_alias_conformance_vectors_map(string legacy, QualityLifecycleState expected) =>
        Assert.Equal(expected, QualityLegacyMappings.MapLifecycle(legacy));

    [Fact]
    public void Evidence_string_mapping_preserves_structured_and_plain_payloads()
    {
        var structured = QualityLegacyMappings.MapEvidenceString("ev-json", "{\"scanner\":\"gitleaks\"}");
        var plain = QualityLegacyMappings.MapEvidenceString("ev-text", "line 4 contains the failing value");

        Assert.Equal(QualityEvidenceKind.ToolResult, structured.Kind);
        Assert.Equal("application/json", structured.MediaType);
        Assert.Equal("gitleaks", structured.Raw?.GetProperty("scanner").GetString());
        Assert.Equal(QualityEvidenceKind.Document, plain.Kind);
        Assert.Equal("text/plain", plain.MediaType);
        Assert.Equal("line 4 contains the failing value", plain.Raw?.GetString());
    }

    internal static QualityObservation CreateObservation(string? observationIdSuffix = null)
    {
        var suffix = observationIdSuffix ?? new string('a', 64);
        return new QualityObservation(
            QualityObservation.SchemaId,
            1,
            "observation-sha256:" + suffix,
            new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
            new QualityCatalogueReference(QualityTaxonomyCatalogue.CoreId,
                QualityTaxonomyCatalogue.CoreVersion, QualityTaxonomyCatalogue.CoreDigest),
            null,
            new QualitySubject("qs-v1/dotnet/file/example", "sha256:" + new string('b', 64), "file",
                QualityObservationJson.NoExtensions),
            new QualityProfile("file-code-review", "1.0.0", "sha256:" + new string('c', 64),
                "sha256:" + new string('d', 64), "code", QualityObservationJson.NoExtensions),
            new QualityProducer(QualityProducerKind.Agent, "codex", "openai", "gpt-5", "gpt-5",
                "high", "2026-07-24", "quality-run", "review-run", QualityObservationJson.NoExtensions),
            QualityEvidenceStatus.Available,
            [],
            [new QualityAspectObservation("code.correctness", "Correctness", QualityAssessment.Pass, null,
                "No correctness defect was found.", new QualityGrade(94, "A"), QualityObservationJson.NoExtensions)],
            QualityAssessment.Pass,
            null,
            null,
            [],
            null,
            QualityObservationJson.NoExtensions);
    }
}
