using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    [Fact]
    public void Built_in_core_catalogue_validates_against_the_taxonomy_schema()
    {
        var path = Path.Combine(RepositoryTestContext.FindRepositoryRoot(),
            "src", "AgentOrchestrator.CodeQuality", "catalogues", "quality-taxonomy-core.v1.json");
        using var parsed = JsonDocument.Parse(File.ReadAllText(path));

        var evaluation = TaxonomySchema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void Catalogue_loader_accepts_the_built_in_catalogue_and_computes_a_stable_digest()
    {
        var catalogue = QualityTaxonomyCatalogue.LoadBuiltIn();

        Assert.Equal("quality-studio/core", catalogue.TaxonomyId);
        Assert.Equal("1.0.0", catalogue.Version);
        Assert.True(QualityTaxonomyCatalogue.IsKnownAspect(catalogue, "code.correctness"));
        Assert.False(QualityTaxonomyCatalogue.IsKnownAspect(catalogue, "com.acme:resilience.backpressure"));
        Assert.True(QualityTaxonomyCatalogue.TryResolveAspectByMigrationAlias(catalogue, "boundaries", out var resolved));
        Assert.Equal("security.boundary-exposure", resolved);

        var digest = QualityTaxonomyCatalogue.ComputeDigest(catalogue);
        Assert.StartsWith("sha256:", digest, StringComparison.Ordinal);
        Assert.Equal(digest, QualityTaxonomyCatalogue.ComputeDigest(catalogue));
    }

    [Theory]
    [InlineData("taxonomyId", "\"quality-studio/core\"", "\"not-the-core-taxonomy\"")]
    [InlineData("version", "\"1.0.0\"", "\"1.0\"")]
    public void Taxonomy_schema_rejects_negative_fixtures(string _, string validLiteral, string invalidLiteral)
    {
        var path = Path.Combine(RepositoryTestContext.FindRepositoryRoot(),
            "src", "AgentOrchestrator.CodeQuality", "catalogues", "quality-taxonomy-core.v1.json");
        var json = File.ReadAllText(path).Replace(validLiteral, invalidLiteral, StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(json);

        var evaluation = TaxonomySchema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    [Fact]
    public void Observation_round_trips_preserves_extensions_and_validates_against_the_schema()
    {
        var original = CreateDocument() with
        {
            Extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["com.acme:resilience.backpressure"] = JsonDocument.Parse("""{"queueDepth":42}""").RootElement.Clone(),
            },
            LegacyExtensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["x-future-field"] = JsonDocument.Parse("""{"enabled":true}""").RootElement.Clone(),
            },
        };

        var json = QualityObservationJson.Serialize(original);
        AssertValidObservation(json);

        using var result = QualityObservationJson.Parse(json);
        Assert.True(result.IsSupported, result.UnsupportedReason);
        var loaded = result.Document!;

        Assert.Equal(original.ObservationId, loaded.ObservationId);
        Assert.Equal(original.Assessment, loaded.Assessment);
        Assert.Equal(42, loaded.Extensions["com.acme:resilience.backpressure"].GetProperty("queueDepth").GetInt32());
        Assert.True(loaded.LegacyExtensions["x-future-field"].GetProperty("enabled").GetBoolean());

        var roundTripJson = QualityObservationJson.Serialize(loaded);
        Assert.Equal(json, roundTripJson);
        AssertValidObservation(roundTripJson);
    }

    [Fact]
    public void Core_aggregation_excludes_extension_aspects_without_an_installed_catalogue()
    {
        var document = CreateDocument() with
        {
            Aspects =
            [
                new QualityObservationAspectResult("code.correctness", "pass", "Reviewed and clean."),
                new QualityObservationAspectResult("com.acme:resilience.backpressure", "concern", "Not in the core catalogue."),
            ],
        };

        var core = QualityObservationAggregation.CoreAspectResults(document);
        var unrecognized = QualityObservationAggregation.UnrecognizedAspectResults(document);

        Assert.Equal(["code.correctness"], core.Select(a => a.AspectId));
        Assert.Equal(["com.acme:resilience.backpressure"], unrecognized.Select(a => a.AspectId));
    }

    [Fact]
    public void Parse_quarantines_an_unsupported_schema_major_without_discarding_raw_data()
    {
        var json = QualityObservationJson.Serialize(CreateDocument())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        using var result = QualityObservationJson.Parse(json);

        Assert.False(result.IsSupported);
        Assert.NotNull(result.UnsupportedReason);
        Assert.Null(result.Document);
        Assert.Equal("observation-sha256:" + new string('a', 64),
            result.RawDocument.RootElement.GetProperty("observationId").GetString());
    }

    [Fact]
    public void Parse_quarantines_an_unsupported_taxonomy_major_without_discarding_raw_data()
    {
        var json = QualityObservationJson.Serialize(CreateDocument())
            .Replace("\"version\": \"1.0.0\"", "\"version\": \"2.0.0\"", StringComparison.Ordinal);

        using var result = QualityObservationJson.Parse(json);

        Assert.False(result.IsSupported);
        Assert.Contains("taxonomy major", result.UnsupportedReason, StringComparison.Ordinal);
        Assert.Null(result.Document);
        Assert.True(result.RawDocument.RootElement.TryGetProperty("taxonomy", out _));
    }

    [Fact]
    public void Serialize_rejects_a_term_outside_the_installed_taxonomy()
    {
        var document = CreateDocument() with { Assessment = "definitely-not-a-term" };

        Assert.Throws<JsonException>(() => QualityObservationJson.Serialize(document));
    }

    private static void AssertValidObservation(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var evaluation = ObservationSchema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    private static QualityObservationDocument CreateDocument()
    {
        var catalogue = QualityTaxonomyCatalogue.Current.Value;
        var digest = QualityTaxonomyCatalogue.ComputeDigest(catalogue);
        return new QualityObservationDocument
        {
            ObservationId = "observation-sha256:" + new string('a', 64),
            Taxonomy = new QualityObservationTaxonomyReference("quality-studio/core", "1.0.0", digest),
            Subject = new QualityObservationSubject("qs-v1/dotnet/file/" + new string('b', 64), "sha256:" + new string('c', 64)),
            Profile = new QualityObservationProfile("file-code-review", "1.0.0", "sha256:" + new string('d', 64), "sha256:" + new string('e', 64)),
            Producer = new QualityObservationProducer(
                "agent", "gpt-5.4-mini", "gpt-5.4-mini", "high", "2026-07-24", "quality-run-1",
                Agent: "codex", Provider: "openai", ReviewRunId: "review-1"),
            EvidenceStatus = "available",
            Evidence =
            [
                new QualityObservationEvidence(
                    "ev-1", "source-code",
                    new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["path"] = JsonDocument.Parse("\"src/A.cs\"").RootElement.Clone(),
                    },
                    "Unchecked value reaches the dereference.",
                    "sha256:" + new string('f', 64)),
            ],
            Aspects = [new QualityObservationAspectResult("code.correctness", "fail", "One high-severity defect is evidenced.",
                new QualityObservationGrade(68, GradeBand.D))],
            Assessment = "fail",
            Findings =
            [
                new QualityObservationFinding(
                    "of-1", "issue-" + new string('1', 8), "sha256:" + new string('0', 64), "quality-studio-occurrence-v2",
                    "code.correctness", FindingSeverity.High, ["ev-1"],
                    new QualityObservationFindingSource("agent", "self"), "built-in/code.correctness.null-deref@1"),
            ],
        };
    }
}
