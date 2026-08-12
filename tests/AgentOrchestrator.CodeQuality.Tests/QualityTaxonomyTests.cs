using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyTests
{
    private static readonly string RepositoryRoot = RepositoryTestContext.FindRepositoryRoot();
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => LoadSchema("quality-taxonomy.v1.schema.json"));
    internal static readonly Lazy<JsonSchema> ObservationSchema = new(() => LoadSchema("quality-observation.v1.schema.json"));

    [Fact]
    public void EmbeddedCoreCatalogueValidatesAndPinsTheApprovedTerms()
    {
        var path = Path.Combine(RepositoryRoot, "src", "AgentOrchestrator.CodeQuality", "catalogues",
            "quality-taxonomy.core.v1.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));

        AssertValid(TaxonomySchema.Value, json.RootElement);
        Assert.Equal(QualityTaxonomy.CoreId, QualityTaxonomy.CoreCatalogue.Id);
        Assert.Equal(QualityTaxonomy.CoreVersion, QualityTaxonomy.CoreCatalogue.Version);
        Assert.Matches("^sha256:[0-9a-f]{64}$", QualityTaxonomy.CoreReference.Digest);
        Assert.Equal(16, QualityTaxonomy.CoreCatalogue.Aspects.Count);
        Assert.True(QualityTaxonomy.IsCoreTerm("assessment", QualityTerms.Assessment.Inconclusive));
        Assert.Equal("security.dependencies", QualityTaxonomy.ResolveCoreAspect("dependencies"));
    }

    [Fact]
    public void ObservationSchemaAcceptsContractAndRejectsInvalidVectors()
    {
        var observation = CreateObservation();
        using var json = JsonDocument.Parse(QualityObservationJson.Serialize(observation));
        AssertValid(ObservationSchema.Value, json.RootElement);

        var unsupportedVersion = JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
        unsupportedVersion["schemaVersion"] = 2;
        AssertInvalid(ObservationSchema.Value, unsupportedVersion);

        var decisionWithoutPolicy = JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
        decisionWithoutPolicy["decision"] = new JsonObject { ["value"] = "allow" };
        AssertInvalid(ObservationSchema.Value, decisionWithoutPolicy);

        var missingProducerKind = JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
        missingProducerKind["producer"]!.AsObject().Remove("kind");
        AssertInvalid(ObservationSchema.Value, missingProducerKind);
    }

    [Fact]
    public void ExtensionsAndUnknownSameMajorTermsRoundTripWithoutBecomingCoreTerms()
    {
        var original = CreateObservation() with
        {
            Aspects =
            [
                new QualityAspectAssessment(
                    "com.acme:resilience.backpressure",
                    "com.acme:assessment.degraded",
                    Extensions: new Dictionary<string, JsonElement>
                    {
                        ["threshold"] = JsonSerializer.SerializeToElement(12),
                    }),
            ],
            LegacyExtensions = new Dictionary<string, JsonElement>
            {
                ["x-acme-routing"] = JsonSerializer.SerializeToElement(new { lane = "canary" }),
            },
        };

        var serialized = QualityObservationJson.Serialize(original);
        var read = QualityObservationJson.Read(serialized);
        var reserialized = QualityObservationJson.Serialize(read.Observation!);
        using var roundTrip = JsonDocument.Parse(reserialized);

        Assert.True(read.Supported);
        Assert.Null(QualityTaxonomy.ResolveCoreAspect("com.acme:resilience.backpressure"));
        Assert.False(QualityTaxonomy.IsCoreTerm("assessment", "com.acme:assessment.degraded"));
        Assert.Equal(12, roundTrip.RootElement.GetProperty("aspects")[0]
            .GetProperty("extensions").GetProperty("threshold").GetInt32());
        Assert.Equal("canary", roundTrip.RootElement.GetProperty("x-acme-routing").GetProperty("lane").GetString());
    }

    [Fact]
    public void UnknownMajorIsQuarantinedWithoutDiscardingRawJson()
    {
        var json = JsonNode.Parse(QualityObservationJson.Serialize(CreateObservation()))!.AsObject();
        json["schemaVersion"] = 9;
        json["$schema"] = "https://quality.studio/schemas/quality-observation.v9.schema.json";
        json["futureFact"] = new JsonObject { ["answer"] = 42 };

        var read = QualityObservationJson.Read(json.ToJsonString());

        Assert.False(read.Supported);
        Assert.Equal(9, read.SchemaVersion);
        Assert.Null(read.Observation);
        Assert.Equal(42, read.Raw.GetProperty("futureFact").GetProperty("answer").GetInt32());
    }

    [Fact]
    public void UnknownTaxonomyMajorIsQuarantinedWithoutInferringAnAssessment()
    {
        var json = JsonNode.Parse(QualityObservationJson.Serialize(CreateObservation()))!.AsObject();
        json["taxonomy"]!["version"] = "2.0.0";
        json["assessment"] = "pass";

        var read = QualityObservationJson.Read(json.ToJsonString());

        Assert.False(read.Supported);
        Assert.Null(read.Observation);
        Assert.Equal("pass", read.Raw.GetProperty("assessment").GetString());
        Assert.Equal("2.0.0", read.Raw.GetProperty("taxonomy").GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("pass", "pass", "available", "allow")]
    [InlineData("warn", "concern", "available", "warn")]
    [InlineData("block", "fail", "available", "block")]
    [InlineData("unavailable", "inconclusive", "unavailable", "defer")]
    public void SecurityMappingsSeparateAssessmentEvidenceAndDecision(
        string legacy, string assessment, string evidenceStatus, string decision)
    {
        var mapped = LegacyQualityMapping.SecurityVerdict(legacy);

        Assert.Equal(assessment, mapped.Assessment);
        Assert.Equal(evidenceStatus, mapped.EvidenceStatus);
        Assert.Equal(decision, mapped.Decision);
        Assert.Equal("security-sensor-agent-v1", mapped.PolicyRef);
    }

    [Theory]
    [InlineData("pass", "pass")]
    [InlineData("fail", "fail")]
    [InlineData("undetermined", "inconclusive")]
    public void FlowMappingsCoverEveryLegacySpelling(string legacy, string expected) =>
        Assert.Equal(expected, LegacyQualityMapping.FlowVerdict(legacy).Assessment);

    [Theory]
    [InlineData("pass", "pass")]
    [InlineData("finding", "fail")]
    [InlineData("not-applicable", "not-applicable")]
    [InlineData("not-yet-checked", "not-assessed")]
    public void AttackMappingsCoverEveryLegacySpelling(string legacy, string expected) =>
        Assert.Equal(expected, LegacyQualityMapping.AttackVerdict(legacy).Assessment);

    [Theory]
    [InlineData("no-quality-delta", "no-observed-delta")]
    [InlineData("improved", "improved")]
    [InlineData("neutral", "unchanged")]
    [InlineData("regression", "regressed")]
    public void ChangeSummaryMappingsCoverEveryLegacySpelling(string legacy, string expected) =>
        Assert.Equal(expected, LegacyQualityMapping.ChangeSummary(legacy).Change);

    [Theory]
    [InlineData("good", "pass")]
    [InlineData("mixed", "concern")]
    [InlineData("concerning", "fail")]
    [InlineData("unknown", "inconclusive")]
    public void ChangeAspectMappingsCoverEveryLegacySpelling(string legacy, string expected) =>
        Assert.Equal(expected, LegacyQualityMapping.ChangeAspect(legacy).Assessment);

    [Theory]
    [InlineData("accepted", "accepted-risk")]
    [InlineData("accepted-risk", "accepted-risk")]
    [InlineData("falsePositive", "false-positive")]
    [InlineData("false-positive", "false-positive")]
    public void LifecycleAliasesMapDeterministically(string legacy, string expected) =>
        Assert.Equal(expected, LegacyQualityMapping.FindingState(legacy));

    [Fact]
    public void LegacyEvidencePreservesStructuredAndTextPayloads()
    {
        var structured = LegacyQualityMapping.Evidence("ev-json", "{\"scanner\":\"gitleaks\"}");
        var text = LegacyQualityMapping.Evidence("ev-text", "line 12 contains a secret");

        Assert.Equal("application/json", structured.MediaType);
        Assert.Equal("gitleaks", structured.Raw!.Value.GetProperty("scanner").GetString());
        Assert.Equal("text/plain", text.MediaType);
        Assert.Equal("line 12 contains a secret", text.Raw!.Value.GetString());
        Assert.Matches("^sha256:[0-9a-f]{64}$", structured.ContentHash!);
    }

    private static QualityObservation CreateObservation()
    {
        var manifest = "sha256:" + new string('a', 64);
        var prompt = "sha256:" + new string('b', 64);
        var inputs = "sha256:" + new string('c', 64);
        return new QualityObservation
        {
            ObservationId = QualityObservationJson.CreateObservationId(
                "quality-run-1", "unit-1", "code", manifest, inputs, QualityTaxonomy.CoreReference.Digest),
            ObservedAt = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            Taxonomy = QualityTaxonomy.CoreReference,
            Subject = new QualitySubject("unit-1", manifest, "file", "src/A.cs"),
            Profile = new QualityProfile("file-code-review", "1.0.0", prompt, inputs, "code"),
            Producer = new QualityProducer("agent", "codex", "openai", "gpt-5", "gpt-5",
                "high", "2026-07-24", "quality-run-1", ReviewRunId: "review-run-1", UsageRunId: "quality-run-1"),
            EvidenceStatus = "available",
            Evidence =
            [
                new QualityEvidence("ev-1", "source-code", "The relevant source range.",
                    new QualityEvidenceLocator("src/A.cs", Line: 1, Column: 1),
                    "sha256:" + new string('d', 64)),
            ],
            Aspects =
            [
                new QualityAspectAssessment("code.correctness", "pass", Rationale: "No defect found.",
                    Grade: new QualityObservationGrade(95, "A")),
            ],
            Assessment = "pass",
            Findings = [],
        };
    }

    private static JsonSchema LoadSchema(string name) =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(RepositoryRoot, "schemas", name)));

    private static void AssertValid(JsonSchema schema, JsonElement json)
    {
        var result = schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static void AssertInvalid(JsonSchema schema, JsonNode json)
    {
        using var document = JsonDocument.Parse(json.ToJsonString());
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(result.IsValid);
    }
}
