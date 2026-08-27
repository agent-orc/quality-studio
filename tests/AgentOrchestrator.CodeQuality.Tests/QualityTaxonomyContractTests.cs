using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => LoadSchema("quality-taxonomy.v1.schema.json"));
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => LoadSchema("quality-observation.v1.schema.json"));

    [Fact]
    public void CoreCatalogueIsVersionedOrderedAndValidatesAgainstSchema()
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        var path = Path.Combine(repositoryRoot, "src", "AgentOrchestrator.CodeQuality", "catalogues",
            "quality-studio-core.v1.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));

        var result = TaxonomySchema.Value.Evaluate(json.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.True(result.IsValid, result.ToString());
        Assert.Equal("quality-studio/core", QualityTaxonomyCatalogueLoader.Core.Id);
        Assert.Equal("1.0.0", QualityTaxonomyCatalogueLoader.Core.Version);
        Assert.Matches("^sha256:[a-f0-9]{64}$", QualityTaxonomyCatalogueLoader.CoreDigest);
        Assert.Equal(
            Enumerable.Range(0, QualityTaxonomyCatalogueLoader.Core.Axes.Count),
            QualityTaxonomyCatalogueLoader.Core.Axes.Select(axis => axis.Order));
        Assert.Equal(
            Enumerable.Range(0, QualityTaxonomyCatalogueLoader.Core.Aspects.Count),
            QualityTaxonomyCatalogueLoader.Core.Aspects.Select(aspect => aspect.Order));
    }

    [Fact]
    public void TaxonomySchemaRejectsInvalidDeprecationAndMissingRequiredData()
    {
        var invalid = JsonNode.Parse("""
            {
              "$schema": "https://quality.studio/schemas/quality-taxonomy.v1.schema.json",
              "schemaVersion": 1,
              "id": "quality-studio/core",
              "version": "1.0.0",
              "axes": [{
                "id": "assessment",
                "description": "Assessment axis.",
                "order": 0,
                "terms": [{
                  "id": "pass",
                  "description": "Pass.",
                  "order": 0,
                  "aliases": [],
                  "deprecated": true
                }]
              }],
              "aspects": []
            }
            """)!;

        var result = TaxonomySchema.Value.Evaluate(Element(invalid.ToJsonString()),
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ObservationRoundTripsExplicitAndLegacyExtensionsLosslessly()
    {
        var original = CreateObservation() with
        {
            Extensions = new Dictionary<string, JsonElement>
            {
                ["com.example/review"] = Element("""{"enabled":true,"weight":2}"""),
            },
            LegacyRootExtensions = new Dictionary<string, JsonElement>
            {
                ["x-legacy"] = Element("""{"keep":[1,2,3]}"""),
            },
            Evidence =
            [
                new QualityEvidenceItem
                {
                    Id = "ev-source",
                    Kind = QualityEvidenceKind.SourceCode,
                    Locator = new QualityEvidenceLocator
                    {
                        Path = "src/A.cs",
                        Extensions = new Dictionary<string, JsonElement>
                        {
                            ["com.example/anchor"] = Element("""{"line":7}"""),
                        },
                    },
                    Summary = "Source location.",
                    ContentHash = Hash('7'),
                },
            ],
        };

        var serialized = QualityObservationJson.Serialize(original);
        var loaded = QualityObservationJson.Deserialize(serialized);
        var roundTripped = QualityObservationJson.Serialize(loaded);

        Assert.True(JsonElement.DeepEquals(Element(serialized), Element(roundTripped)));
        Assert.True(loaded.Extensions.ContainsKey("com.example/review"));
        Assert.True(loaded.LegacyRootExtensions.ContainsKey("x-legacy"));
        Assert.Equal(7, loaded.Evidence[0].Locator!.Extensions["com.example/anchor"].GetProperty("line").GetInt32());
    }

    [Fact]
    public void UnknownObservationMajorIsQuarantinedWithoutDiscardingRawData()
    {
        var json = QualityObservationJson.Serialize(CreateObservation())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal)
            .Replace("\"extensions\": {}", "\"extensions\": { \"com.example/future\": { \"raw\": true } }",
                StringComparison.Ordinal);

        var result = QualityObservationJson.Read(json);

        Assert.False(result.IsSupported);
        Assert.Equal(2, result.SchemaVersion);
        Assert.True(result.Raw.GetRawText().Contains("com.example/future", StringComparison.Ordinal));
        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));
        Assert.Contains("Raw data remains available through Read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PositiveObservationValidatesAndUnknownNestedMembersAreRejected()
    {
        var json = QualityObservationJson.Serialize(CreateObservation());
        using var valid = JsonDocument.Parse(json);
        var positive = ObservationSchema.Value.Evaluate(valid.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        var invalid = JsonNode.Parse(json)!.AsObject();
        invalid["producer"]!.AsObject()["future"] = true;
        var negative = ObservationSchema.Value.Evaluate(Element(invalid.ToJsonString()),
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(positive.IsValid, positive.ToString());
        Assert.False(negative.IsValid);
        Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(invalid.ToJsonString()));
    }

    [Fact]
    public void EveryCompatibilityMappingRowIsDeterministic()
    {
        AssertProjection(LegacyQualityTaxonomyMapper.MapSecurityVerdict("pass"),
            QualityAssessment.Pass, QualityEvidenceStatus.Available, QualityDecisionValue.Allow);
        AssertProjection(LegacyQualityTaxonomyMapper.MapSecurityVerdict("warn"),
            QualityAssessment.Concern, QualityEvidenceStatus.Available, QualityDecisionValue.Warn);
        AssertProjection(LegacyQualityTaxonomyMapper.MapSecurityVerdict("block"),
            QualityAssessment.Fail, QualityEvidenceStatus.Available, QualityDecisionValue.Block);
        AssertProjection(LegacyQualityTaxonomyMapper.MapSecurityVerdict("unavailable"),
            QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable, null);

        Assert.Equal(QualityAssessment.Pass, LegacyQualityTaxonomyMapper.MapFlowVerdict("pass").Assessment);
        Assert.Equal(QualityAssessment.Fail, LegacyQualityTaxonomyMapper.MapFlowVerdict("fail").Assessment);
        Assert.Equal(QualityAssessment.Inconclusive,
            LegacyQualityTaxonomyMapper.MapFlowVerdict("undetermined").Assessment);

        Assert.Equal(QualityAssessment.Pass, LegacyQualityTaxonomyMapper.MapAttackVerdict("pass").Assessment);
        Assert.Equal(QualityAssessment.Fail, LegacyQualityTaxonomyMapper.MapAttackVerdict("finding").Assessment);
        Assert.Equal(QualityAssessment.NotApplicable,
            LegacyQualityTaxonomyMapper.MapAttackVerdict("not-applicable").Assessment);
        Assert.Equal(QualityAssessment.NotAssessed,
            LegacyQualityTaxonomyMapper.MapAttackVerdict("not-yet-checked").Assessment);

        Assert.Equal(QualityChange.NoObservedDelta,
            LegacyQualityTaxonomyMapper.MapChangeSummary("no-quality-delta").Change);
        Assert.Equal(QualityChange.Improved, LegacyQualityTaxonomyMapper.MapChangeSummary("improved").Change);
        Assert.Equal(QualityChange.Unchanged, LegacyQualityTaxonomyMapper.MapChangeSummary("neutral").Change);
        Assert.Equal(QualityChange.Regressed, LegacyQualityTaxonomyMapper.MapChangeSummary("regression").Change);

        Assert.Equal(QualityAssessment.Pass, LegacyQualityTaxonomyMapper.MapChangeAspect("good").Assessment);
        Assert.Equal(QualityAssessment.Concern, LegacyQualityTaxonomyMapper.MapChangeAspect("mixed").Assessment);
        Assert.Equal(QualityAssessment.Fail, LegacyQualityTaxonomyMapper.MapChangeAspect("concerning").Assessment);
        Assert.Equal(QualityAssessment.Inconclusive,
            LegacyQualityTaxonomyMapper.MapChangeAspect("unknown").Assessment);

        Assert.Equal("accepted-risk", LegacyQualityTaxonomyMapper.MapFindingState("accepted").CanonicalLifecycle);
        Assert.Equal("false-positive", LegacyQualityTaxonomyMapper.MapFindingState("falsePositive").CanonicalLifecycle);
        Assert.Equal("false-positive", LegacyQualityTaxonomyMapper.MapFindingState("false-positive").CanonicalLifecycle);
    }

    [Fact]
    public void EvidenceStringsArePreservedAndHashedWithoutSemanticInterpretation()
    {
        const string structured = "{\"sensor\":\"gitleaks\",\"count\":2}";
        const string plain = "not json: keep this exactly";

        var jsonEvidence = LegacyQualityTaxonomyMapper.MapEvidenceString("ev-json", structured);
        var textEvidence = LegacyQualityTaxonomyMapper.MapEvidenceString("ev-text", plain);

        Assert.Equal(QualityEvidenceKind.ToolResult, jsonEvidence.Kind);
        Assert.Equal("application/json", jsonEvidence.ContentType);
        Assert.Equal("gitleaks", jsonEvidence.Raw!.Value.GetProperty("sensor").GetString());
        Assert.Equal("sha256:0056e41f3a45a2a9f34c0bce319d5ae46311800bacde33f089b71a09d0b1e958",
            jsonEvidence.ContentHash);
        Assert.Equal(QualityEvidenceKind.Document, textEvidence.Kind);
        Assert.Equal("text/plain", textEvidence.ContentType);
        Assert.Equal(plain, textEvidence.Raw!.Value.GetString());
    }

    [Fact]
    public void UnknownLegacyTermsAreNeverCoercedToPassingCoreTerms()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LegacyQualityTaxonomyMapper.MapSecurityVerdict("ok"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LegacyQualityTaxonomyMapper.MapFindingState("dismissed"));
        Assert.Null(LegacyQualityTaxonomyMapper.MapAspect("sensor-availability"));
        Assert.Equal("com.example.sensor:analyzer",
            LegacyQualityTaxonomyMapper.MapAspect("analyzer", producerNamespace: "com.example.sensor"));
    }

    private static void AssertProjection(
        LegacyQualityProjection projection,
        QualityAssessment assessment,
        QualityEvidenceStatus evidenceStatus,
        QualityDecisionValue? decision)
    {
        Assert.Equal(assessment, projection.Assessment);
        Assert.Equal(evidenceStatus, projection.EvidenceStatus);
        Assert.Equal(decision, projection.Decision);
        Assert.Equal(LegacyQualityTaxonomyMapper.SecurityPolicyRef, projection.PolicyRef);
    }

    private static QualityObservationDocument CreateObservation() => new()
    {
        ObservationId = "observation-" + Hash('1'),
        ObservedAt = new DateTimeOffset(2026, 8, 27, 8, 30, 0, TimeSpan.Zero),
        Taxonomy = new QualityTaxonomyReference
        {
            Id = QualityTaxonomyConstants.CoreCatalogueId,
            Version = QualityTaxonomyConstants.CoreCatalogueVersion,
            Digest = Hash('2'),
        },
        Subject = new QualityObservationSubject
        {
            UnitId = "qs-v1/dotnet/file/example",
            ManifestHash = Hash('3'),
        },
        Profile = new QualityObservationProfile
        {
            Id = "file-code-review",
            Version = "1.0.0",
            PromptHash = Hash('4'),
            ReviewInputsHash = Hash('5'),
        },
        Producer = new QualityObservationProducer
        {
            Kind = QualityProducerKind.Agent,
            Agent = "codex",
            Provider = "openai",
            RequestedModel = "gpt-test-a",
            EffectiveModel = "gpt-test-a",
            ThinkingLevel = "high",
            RoutePolicyVersion = "2026-07-24",
            RunId = "quality-test",
            ReviewRunId = "review-test",
        },
        EvidenceStatus = QualityEvidenceStatus.Available,
        Aspects =
        [
            new QualityObservationAspect
            {
                AspectId = "code.correctness",
                Assessment = QualityAssessment.Pass,
                Rationale = "No correctness defect was observed.",
                Grade = new QualityObservationGrade { Score = 95, Band = "A" },
            },
        ],
        Assessment = QualityAssessment.Pass,
    };

    private static string Hash(char value) => "sha256:" + new string(value, 64);

    private static JsonElement Element(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }

    private static JsonSchema LoadSchema(string fileName) => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", fileName)));
}
