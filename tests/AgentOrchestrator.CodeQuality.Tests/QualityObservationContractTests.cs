using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationContractTests
{
    private static readonly Lazy<JsonSchema> ObservationSchema =
        new(() => SchemaAssert.Load("quality-observation.v1.schema.json"));

    [Fact]
    public void AWrittenObservationValidatesAgainstItsPublishedSchema()
    {
        using var json = JsonDocument.Parse(QualityObservationJson.Serialize(CreateObservation()));

        var result = ObservationSchema.Value.Evaluate(json.RootElement, SchemaAssert.Options);

        Assert.True(result.IsValid, SchemaAssert.Describe(result));
    }

    [Fact]
    public void ExtensionsAndLegacyRootKeysSurviveDeserializeAndSerialize()
    {
        var observation = CreateObservation() with
        {
            Extensions = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
            {
                ["com.acme:backpressureShed"] = JsonValue.Create(42),
            },
        };
        var json = QualityObservationJson.Serialize(observation).Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"x-future-field\": { \"enabled\": true },",
            StringComparison.Ordinal);

        var loaded = QualityObservationJson.Deserialize(json);
        var rewritten = QualityObservationJson.Serialize(loaded);

        Assert.Equal(42, loaded.Extensions!["com.acme:backpressureShed"]!.GetValue<int>());
        Assert.True(loaded.LegacyExtensions!["x-future-field"].GetProperty("enabled").GetBoolean());
        Assert.Contains("\"com.acme:backpressureShed\": 42", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"x-future-field\"", rewritten, StringComparison.Ordinal);
        using var reparsed = JsonDocument.Parse(rewritten);
        Assert.True(ObservationSchema.Value.Evaluate(reparsed.RootElement, SchemaAssert.Options).IsValid);
    }

    [Fact]
    public void AnUnknownRootMemberThatIsNotALegacyExtensionIsRejected()
    {
        var json = QualityObservationJson.Serialize(CreateObservation()).Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"verdict\": \"good\",",
            StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));

        Assert.Contains("'verdict' is not a known observation member", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsupportedTaxonomyMajorIsQuarantinedWithItsRawDataIntact()
    {
        var json = QualityObservationJson.Serialize(CreateObservation() with
        {
            Taxonomy = new("quality-studio/core", "2.0.0", QualityTaxonomyCatalogue.Core.Digest),
        });

        var record = QualityObservationJson.Read(json);

        Assert.Equal(ObservationSupport.UnsupportedTaxonomyMajor, record.Support);
        Assert.False(record.IsSupported);
        Assert.Null(record.Observation);
        Assert.Equal(json, record.RawJson);
        Assert.Contains("quality-studio/core@2.0.0", record.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsupportedSchemaVersionIsQuarantinedRatherThanReinterpreted()
    {
        var json = QualityObservationJson.Serialize(CreateObservation())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 9", StringComparison.Ordinal);

        var record = QualityObservationJson.Read(json);

        Assert.Equal(ObservationSupport.UnsupportedSchemaVersion, record.Support);
        Assert.Null(record.Observation);
        Assert.Equal(json, record.RawJson);
    }

    [Fact]
    public void AMalformedLineIsReportedWithoutLosingItsText()
    {
        var record = QualityObservationJson.Read("{\"schemaVersion\":");

        Assert.Equal(ObservationSupport.Malformed, record.Support);
        Assert.Equal("{\"schemaVersion\":", record.RawJson);
    }

    [Fact]
    public void ASupportedObservationReadsBackWithItsProvenance()
    {
        var record = QualityObservationJson.Read(QualityObservationJson.Serialize(CreateObservation()));

        Assert.True(record.IsSupported);
        Assert.Equal("gpt-5.4-mini", record.Observation!.Producer.RequestedModel);
        Assert.Equal("gpt-5.4-mini", record.Observation.Producer.EffectiveModel);
        Assert.Equal("high", record.Observation.Producer.ThinkingLevel);
        Assert.Equal("2026-07-24", record.Observation.Producer.RoutePolicyVersion);
    }

    [Fact]
    public void RecordedAtIsWrittenAsAUtcInstantWithMillisecondPrecision()
    {
        var json = QualityObservationJson.Serialize(CreateObservation());

        Assert.Contains("\"recordedAt\": \"2026-08-11T09:15:00.000Z\"", json, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => QualityObservationJson.Serialize(CreateObservation() with
        {
            RecordedAt = new DateTimeOffset(2026, 8, 11, 9, 15, 0, TimeSpan.FromHours(2)),
        }));
    }

    [Theory]
    [InlineData("\"observationId\": \"observation-sha256:", "\"observationId\": \"sha256:")]
    [InlineData("\"assessment\": \"fail\"", "\"assessment\": \"Fail\"")]
    [InlineData("\"kind\": \"agent\"", "\"kind\": \"AGENT\"")]
    [InlineData("\"evidenceStatus\": \"available\"", "\"evidenceStatus\": \"ok\"")]
    public void TheSchemaRejectsValuesThatAreNeitherCoreTermsNorPrefixedExtensions(string original, string replacement)
    {
        using var json = JsonDocument.Parse(QualityObservationJson.Serialize(CreateObservation())
            .Replace(original, replacement, StringComparison.Ordinal));

        Assert.False(ObservationSchema.Value.Evaluate(json.RootElement, SchemaAssert.Options).IsValid);
    }

    [Fact]
    public void TheSchemaAcceptsAPrefixedExtensionTermSoUnknownTermsStayVisible()
    {
        using var json = JsonDocument.Parse(QualityObservationJson.Serialize(CreateObservation())
            .Replace("\"assessment\": \"fail\"", "\"assessment\": \"com.acme:degraded\"", StringComparison.Ordinal));

        var result = ObservationSchema.Value.Evaluate(json.RootElement, SchemaAssert.Options);

        Assert.True(result.IsValid, SchemaAssert.Describe(result));
    }

    [Fact]
    public void AFingerprintRequiresItsAlgorithmSoIdentityStaysVersioned()
    {
        using var json = JsonDocument.Parse(QualityObservationJson.Serialize(CreateObservation())
            .Replace("\"fingerprintAlgorithm\": \"quality-studio-occurrence-v2\",", string.Empty, StringComparison.Ordinal));

        Assert.False(ObservationSchema.Value.Evaluate(json.RootElement, SchemaAssert.Options).IsValid);
    }

    [Fact]
    public void APolicyDispositionIsCarriedSeparatelyFromTheEvidenceAssessment()
    {
        var observation = CreateObservation() with
        {
            Assessment = CoreTerms.Assessment.Inconclusive,
            EvidenceStatus = CoreTerms.EvidenceStatus.Unavailable,
            PolicyOutcomes = [new(QualityTaxonomyLegacyMap.SecurityPolicyRef, CoreTerms.Decision.Defer,
                LegacyValue: "unavailable")],
        };

        using var json = JsonDocument.Parse(QualityObservationJson.Serialize(observation));

        Assert.True(ObservationSchema.Value.Evaluate(json.RootElement, SchemaAssert.Options).IsValid);
        Assert.DoesNotContain("\"assessment\": \"pass\"",
            QualityObservationJson.Serialize(observation), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLineFormIsASingleJsonLineForTheAppendOnlyLedger()
    {
        var line = QualityObservationJson.Serialize(CreateObservation(), indented: false);

        Assert.DoesNotContain('\n', line);
        Assert.Equal(line, QualityObservationJson.Serialize(
            QualityObservationJson.Deserialize(line), indented: false));
    }

    internal static QualityObservation CreateObservation() => new()
    {
        ObservationId = "observation-sha256:" + new string('1', 64),
        RecordedAt = new DateTimeOffset(2026, 8, 11, 9, 15, 0, TimeSpan.Zero),
        Taxonomy = QualityTaxonomyCatalogue.Core.Reference,
        Subject = new("qs-v1/dotnet/file/" + new string('2', 64), "sha256:" + new string('3', 64),
            "src/A.cs", "file", "project", "code"),
        Profile = new("file-code-review", "1.0.0", "sha256:" + new string('4', 64), "sha256:" + new string('5', 64)),
        Producer = new(CoreTerms.ProducerKind.Agent, "codex", "openai", "gpt-5.4-mini", "gpt-5.4-mini", "high",
            "2026-07-24", RunId: "quality-abc", ReviewRunId: "review-abc"),
        EvidenceStatus = CoreTerms.EvidenceStatus.Available,
        Assessment = CoreTerms.Assessment.Fail,
        Grade = new(68, "D", "One high-severity defect is evidenced."),
        Summary = "Unchecked value reaches the dereference.",
        Evidence =
        [
            new("ev-1", CoreTerms.EvidenceKind.SourceCode, "Unchecked value reaches the dereference.",
                new ObservationLocator("src/A.cs", "M:A.Run")),
        ],
        Aspects =
        [
            new("code.correctness", CoreTerms.Assessment.Fail, "Correctness",
                "One high-severity defect is evidenced.", new ObservationGrade(68, "D"), ["ev-1"]),
        ],
        Findings =
        [
            new("of-1", "code.correctness", CoreTerms.Severity.High,
                new ObservationFindingSource(CoreTerms.ProducerKind.Agent, ObservationFindingSource.Self),
                IssueId: "issue-1",
                OccurrenceFingerprint: "sha256:" + new string('6', 64),
                FingerprintAlgorithm: "quality-studio-occurrence-v2",
                RuleRef: "built-in/code.correctness.null-deref@1",
                Title: "Null dereference",
                EvidenceRefs: ["ev-1"]),
        ],
    };
}
