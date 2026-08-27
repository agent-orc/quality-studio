using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyTests
{
    private static readonly string RepositoryRoot = RepositoryTestContext.FindRepositoryRoot();
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryRoot, "schemas", "quality-taxonomy.v1.schema.json"))));
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryRoot, "schemas", "quality-observation.v1.schema.json"))));

    [Fact]
    public void CoreCataloguePinsApprovedTermsAndValidatesAgainstSchema()
    {
        var catalogue = QualityTaxonomyCatalogue.LoadCore();
        var cataloguePath = Path.Combine(
            RepositoryRoot,
            "src",
            "AgentOrchestrator.CodeQuality",
            "catalogues",
            "quality-studio-core.v1.json");
        using var json = JsonDocument.Parse(File.ReadAllText(cataloguePath));

        var evaluation = TaxonomySchema.Value.Evaluate(
            json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, evaluation.ToString());
        Assert.Equal(QualityTaxonomyCatalogue.CoreId, catalogue.Document.Id);
        Assert.Equal(QualityTaxonomyCatalogue.CoreVersion, catalogue.Document.Version);
        Assert.StartsWith("sha256:", catalogue.Digest, StringComparison.Ordinal);
        Assert.Equal(16, catalogue.Document.Terms.Count(term => term.Axis == "aspect"));
        Assert.True(catalogue.TryGetTerm("lifecycle", "accepted-risk", out var acceptedRisk));
        Assert.Contains("accepted", acceptedRisk!.Aliases);
        Assert.True(catalogue.TryGetTerm("lifecycle", "false-positive", out var falsePositive));
        Assert.Contains("falsePositive", falsePositive!.Aliases);
    }

    [Fact]
    public void TaxonomySchemaRejectsInvalidCatalogueFixture()
    {
        using var invalid = JsonDocument.Parse("""
            {
              "$schema": "https://quality.studio/schemas/quality-taxonomy.v1.schema.json",
              "schemaVersion": 1,
              "id": "quality-studio/core",
              "version": "not-semver",
              "terms": []
            }
            """);

        var evaluation = TaxonomySchema.Value.Evaluate(
            invalid.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    [Fact]
    public void ObservationRoundTripsExplicitAndLegacyExtensionsAndValidates()
    {
        var document = CreateObservation() with
        {
            Extensions = new Dictionary<string, JsonElement>
            {
                ["com.acme:experiment"] = Element("{\"enabled\":true}"),
            },
            LegacyExtensions = new Dictionary<string, JsonElement>
            {
                ["x-legacy-note"] = Element("{\"value\":42}"),
            },
            Aspects =
            [
                new QualityObservationAspect(
                    "code.correctness",
                    "The evidence supports the grade.",
                    QualityAssessment.Pass,
                    Grade: new QualityObservationGrade(96, GradeBand.A),
                    Extensions: new Dictionary<string, JsonElement>
                    {
                        ["com.acme:confidence"] = Element("0.97"),
                    }),
                new QualityObservationAspect(
                    "com.acme:resilience.backpressure",
                    "Extension evidence is preserved.",
                    QualityAssessment.Concern),
            ],
        };

        var serialized = QualityObservationJson.Serialize(document);
        var result = QualityObservationJson.Read(serialized);
        var roundTrip = QualityObservationJson.Serialize(Assert.IsType<QualityObservationDocument>(result.Observation));
        using var json = JsonDocument.Parse(roundTrip);
        var evaluation = ObservationSchema.Value.Evaluate(
            json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.Equal(QualityObservationReadStatus.Supported, result.Status);
        Assert.True(evaluation.IsValid, evaluation.ToString());
        Assert.True(json.RootElement.GetProperty("extensions").GetProperty("com.acme:experiment").GetProperty("enabled").GetBoolean());
        Assert.Equal(42, json.RootElement.GetProperty("x-legacy-note").GetProperty("value").GetInt32());
        Assert.Equal(0.97, json.RootElement.GetProperty("aspects")[0].GetProperty("extensions")
            .GetProperty("com.acme:confidence").GetDouble());
        Assert.Equal("A", json.RootElement.GetProperty("aspects")[0].GetProperty("grade").GetProperty("band").GetString());
    }

    [Fact]
    public void ObservationSchemaRejectsInvalidFixture()
    {
        var serialized = QualityObservationJson.Serialize(CreateObservation());
        using var invalid = JsonDocument.Parse(serialized.Replace(
            "\"producer\": {",
            "\"producer\": { \"unexpected\": true,",
            StringComparison.Ordinal));

        var evaluation = ObservationSchema.Value.Evaluate(
            invalid.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    [Fact]
    public void UnsupportedMajorsAreQuarantinedWithoutDiscardingRawJson()
    {
        var serialized = QualityObservationJson.Serialize(CreateObservation());
        var futureSchema = serialized.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);
        var futureTaxonomy = serialized.Replace("\"version\": \"1.0.0\"", "\"version\": \"2.0.0\"", StringComparison.Ordinal);

        var schemaResult = QualityObservationJson.Read(futureSchema);
        var taxonomyResult = QualityObservationJson.Read(futureTaxonomy);

        Assert.Equal(QualityObservationReadStatus.UnsupportedSchemaMajor, schemaResult.Status);
        Assert.Null(schemaResult.Observation);
        Assert.Equal(futureSchema, schemaResult.RawJson);
        Assert.Equal(QualityObservationReadStatus.UnsupportedTaxonomyMajor, taxonomyResult.Status);
        Assert.Null(taxonomyResult.Observation);
        Assert.Equal(futureTaxonomy, taxonomyResult.RawJson);
    }

    [Fact]
    public void UnknownExtensionTermsRemainVisibleButAreExcludedFromCoreAggregation()
    {
        var catalogue = QualityTaxonomyCatalogue.LoadCore();
        var document = CreateObservation() with
        {
            Aspects =
            [
                new QualityObservationAspect("code.correctness", "Known.", QualityAssessment.Pass),
                new QualityObservationAspect("com.acme:resilience.backpressure", "Unknown.", QualityAssessment.Pass),
            ],
        };
        var loaded = Assert.IsType<QualityObservationDocument>(
            QualityObservationJson.Read(QualityObservationJson.Serialize(document)).Observation);

        var aggregatable = loaded.Aspects
            .Where(aspect => catalogue.SupportsAggregation(aspect.AspectId, "assessment"))
            .Select(aspect => aspect.AspectId)
            .ToArray();

        Assert.Equal(["code.correctness"], aggregatable);
        Assert.Contains(loaded.Aspects, aspect => aspect.AspectId == "com.acme:resilience.backpressure");
    }

    [Fact]
    public void AgentDeterministicAndHumanFindingSourcesRoundTripWithResolvableEvidence()
    {
        var fingerprint = "sha256:" + new string('c', 64);
        var document = CreateObservation() with
        {
            Findings = Enum.GetValues<QualityProducerKind>()
                .Where(kind => kind is QualityProducerKind.Agent or QualityProducerKind.DeterministicSensor or QualityProducerKind.Human)
                .Select((kind, index) => new QualityObservationFinding(
                    $"finding-{index}",
                    "issue-sha256:" + new string('d', 64),
                    fingerprint,
                    "quality-studio-occurrence-v2",
                    "correctness.test",
                    "code.correctness",
                    FindingSeverity.Info,
                    ["ev-1"],
                    new QualityFindingSource(kind, kind == QualityProducerKind.Agent ? "self" : kind.ToString())))
                .ToArray(),
        };

        var loaded = Assert.IsType<QualityObservationDocument>(
            QualityObservationJson.Read(QualityObservationJson.Serialize(document)).Observation);
        var evidenceIds = loaded.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            [QualityProducerKind.Agent, QualityProducerKind.DeterministicSensor, QualityProducerKind.Human],
            loaded.Findings.Select(finding => finding.Source.Kind));
        Assert.All(loaded.Findings.SelectMany(finding => finding.EvidenceRefs), reference => Assert.Contains(reference, evidenceIds));
    }

    [Theory]
    [InlineData("pass", QualityAssessment.Pass, QualityDecision.Allow, QualityEvidenceStatus.Available)]
    [InlineData("warn", QualityAssessment.Concern, QualityDecision.Warn, QualityEvidenceStatus.Available)]
    [InlineData("block", QualityAssessment.Fail, QualityDecision.Block, QualityEvidenceStatus.Available)]
    [InlineData("unavailable", QualityAssessment.Inconclusive, QualityDecision.Defer, QualityEvidenceStatus.Unavailable)]
    public void MapsEverySecurityVerdict(
        string legacy,
        QualityAssessment assessment,
        QualityDecision decision,
        QualityEvidenceStatus evidenceStatus)
    {
        var mapped = LegacyQualityMapper.MapSecurityVerdict(legacy);

        Assert.Equal(assessment, mapped.Assessment);
        Assert.Equal(decision, mapped.Decision);
        Assert.Equal(evidenceStatus, mapped.EvidenceStatus);
        Assert.Equal(LegacyQualityMapper.SecurityPolicyRef, mapped.PolicyRef);
        Assert.Equal(legacy, mapped.LegacyValue);
    }

    [Theory]
    [InlineData("pass", QualityAssessment.Pass)]
    [InlineData("fail", QualityAssessment.Fail)]
    [InlineData("undetermined", QualityAssessment.Inconclusive)]
    public void MapsEveryFlowVerdict(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, LegacyQualityMapper.MapFlowVerdict(legacy).Assessment);

    [Theory]
    [InlineData("pass", QualityAssessment.Pass)]
    [InlineData("finding", QualityAssessment.Fail)]
    [InlineData("not-applicable", QualityAssessment.NotApplicable)]
    [InlineData("not-yet-checked", QualityAssessment.NotAssessed)]
    public void MapsEveryAttackVerdict(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, LegacyQualityMapper.MapAttackVerdict(legacy).Assessment);

    [Theory]
    [InlineData("no-quality-delta", QualityChange.NoObservedDelta)]
    [InlineData("improved", QualityChange.Improved)]
    [InlineData("neutral", QualityChange.Unchanged)]
    [InlineData("regression", QualityChange.Regressed)]
    public void MapsEveryChangeSummary(string legacy, QualityChange expected) =>
        Assert.Equal(expected, LegacyQualityMapper.MapChangeSummary(legacy).Change);

    [Theory]
    [InlineData("good", QualityAssessment.Pass)]
    [InlineData("mixed", QualityAssessment.Concern)]
    [InlineData("concerning", QualityAssessment.Fail)]
    [InlineData("unknown", QualityAssessment.Inconclusive)]
    public void MapsEveryChangeAspect(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, LegacyQualityMapper.MapChangeAspect(legacy).Assessment);

    [Theory]
    [InlineData("open", QualityLifecycleState.Open)]
    [InlineData("accepted", QualityLifecycleState.AcceptedRisk)]
    [InlineData("waived", QualityLifecycleState.Waived)]
    [InlineData("falsePositive", QualityLifecycleState.FalsePositive)]
    [InlineData("false-positive", QualityLifecycleState.FalsePositive)]
    [InlineData("resolved", QualityLifecycleState.Resolved)]
    public void MapsEveryFindingStateSpelling(string legacy, QualityLifecycleState expected) =>
        Assert.Equal(expected, LegacyQualityMapper.MapFindingState(legacy).Lifecycle);

    [Fact]
    public void MapsJsonAndTextEvidenceWithoutInterpretingOrDiscardingIt()
    {
        const string jsonValue = "{\"verdict\":\"block\",\"producerSpecific\":true}";
        const string textValue = "not json; preserve exactly";

        var json = LegacyQualityMapper.MapEvidenceString(jsonValue, "json");
        var text = LegacyQualityMapper.MapEvidenceString(textValue, "text");

        Assert.Equal(QualityEvidenceKind.ToolResult, json.Kind);
        Assert.Equal("application/json", json.MediaType);
        Assert.Equal(jsonValue, json.RawContent);
        Assert.Equal(QualityEvidenceKind.Document, text.Kind);
        Assert.Equal("text/plain", text.MediaType);
        Assert.Equal(textValue, text.RawContent);
        Assert.NotEqual(json.ContentHash, text.ContentHash);
    }

    [Fact]
    public void MappingRejectsUnknownLegacyValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyQualityMapper.MapSecurityVerdict("green"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyQualityMapper.MapFlowVerdict("maybe"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyQualityMapper.MapAttackVerdict("skipped"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyQualityMapper.MapChangeSummary("worse"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyQualityMapper.MapChangeAspect("fine"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyQualityMapper.MapFindingState("closed"));
    }

    private static QualityObservationDocument CreateObservation()
    {
        var hash = "sha256:" + new string('a', 64);
        return new QualityObservationDocument
        {
            ObservationId = "observation-sha256:" + new string('b', 64),
            ObservedAt = new DateTimeOffset(2026, 8, 11, 10, 30, 0, TimeSpan.Zero),
            Taxonomy = new QualityTaxonomyReference("quality-studio/core", "1.0.0", hash),
            Subject = new QualityObservationSubject("qs-v1/dotnet/file/example", hash),
            Profile = new QualityObservationProfile("file-code-review", "1.0.0", hash, hash),
            Producer = new QualityObservationProducer(
                QualityProducerKind.Agent,
                "codex",
                "openai",
                "gpt-5",
                "gpt-5",
                "high",
                "2026-07-24",
                "quality-example",
                "review-example"),
            EvidenceStatus = QualityEvidenceStatus.Available,
            Evidence =
            [
                new QualityEvidence(
                    "ev-1",
                    QualityEvidenceKind.SourceCode,
                    new QualityEvidenceLocator("src/Example.cs", "M:Example.Run", Line: 10, Column: 5),
                    "The reviewed source range.",
                    hash),
            ],
            Aspects =
            [
                new QualityObservationAspect(
                    "code.correctness",
                    "No correctness defect was observed.",
                    QualityAssessment.Pass,
                    Grade: new QualityObservationGrade(96, GradeBand.A)),
            ],
            Assessment = QualityAssessment.Pass,
            Findings = [],
        };
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

