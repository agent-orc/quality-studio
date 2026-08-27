using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoExtensions =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    [Fact]
    public void CoreCatalogueValidatesAndPinsCanonicalTermsAliasesAndOrder()
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            repositoryRoot, "schemas", "quality-taxonomy.v1.schema.json")));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "AgentOrchestrator.CodeQuality", "catalogues", "quality-studio-core.v1.json")));

        var validation = schema.Evaluate(json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(validation.IsValid, validation.ToString());
        var catalogue = QualityTaxonomyCatalogue.CoreDocument;
        Assert.Equal("quality-studio/core", catalogue.Id);
        Assert.Equal("1.0.0", catalogue.Version);
        Assert.Matches("^sha256:[0-9a-f]{64}$", QualityTaxonomyCatalogue.CoreDigest);
        Assert.Equal(
            ["agent", "deterministic-sensor", "human", "imported", "unknown"],
            catalogue.Axes.ProducerKind.OrderBy(term => term.Order).Select(term => term.Id));
        Assert.Equal(
            ["pass", "concern", "fail", "inconclusive", "not-applicable", "not-assessed"],
            catalogue.Axes.Assessment.OrderBy(term => term.Order).Select(term => term.Id));
        Assert.Contains("accepted", Assert.Single(catalogue.Axes.Lifecycle,
            term => term.Id == "accepted-risk").Aliases!);
        Assert.Contains("falsePositive", Assert.Single(catalogue.Axes.Lifecycle,
            term => term.Id == "false-positive").Aliases!);
        Assert.Equal(16, catalogue.Aspects.Count);
        Assert.Equal("correctness", Assert.Single(catalogue.Aspects,
            aspect => aspect.Id == "code.correctness").Aliases!.Single());
        Assert.All(catalogue.Aspects.Take(12), aspect => Assert.Equal(["assessment"], aspect.AllowedAxes));
        Assert.All(catalogue.Aspects.Skip(12), aspect => Assert.Equal(["change"], aspect.AllowedAxes));
        AssertCatalogueOrderAndIdentity(catalogue);

        var invalidCatalogue = JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
        invalidCatalogue["unexpected"] = true;
        using var invalidJson = JsonDocument.Parse(invalidCatalogue.ToJsonString());
        AssertValid(schema, invalidJson.RootElement, expected: false);
    }

    [Fact]
    public void ObservationSchemaAcceptsContractAndRejectsInvalidMajorsAndPolicyFreeDecision()
    {
        var schema = LoadSchema("quality-observation.v1.schema.json");
        var observation = CreateObservation();
        using var json = JsonDocument.Parse(QualityObservationJson.Serialize(observation));
        AssertValid(schema, json.RootElement, expected: true);

        using var unknownSchema = JsonDocument.Parse(json.RootElement.GetRawText()
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
        AssertValid(schema, unknownSchema.RootElement, expected: false);

        using var unknownTaxonomy = JsonDocument.Parse(json.RootElement.GetRawText()
            .Replace("\"version\": \"1.0.0\"", "\"version\": \"2.0.0\"", StringComparison.Ordinal));
        AssertValid(schema, unknownTaxonomy.RootElement, expected: true);
        var quarantined = QualityObservationJson.ReadPreservingUnsupported(unknownTaxonomy.RootElement.GetRawText());
        Assert.Equal(QualityObservationSupport.UnsupportedTaxonomyMajor, quarantined.Support);
        Assert.Null(quarantined.Observation);
        Assert.Equal("preserved", quarantined.Raw.GetProperty("extensions").GetProperty("com.acme:future").GetString());

        var unsupportedSchema = QualityObservationJson.ReadPreservingUnsupported(unknownSchema.RootElement.GetRawText());
        Assert.Equal(QualityObservationSupport.UnsupportedSchemaMajor, unsupportedSchema.Support);
        Assert.Null(unsupportedSchema.Observation);
        Assert.Equal("preserved", unsupportedSchema.Raw.GetProperty("extensions").GetProperty("com.acme:future").GetString());

        using var invalidTaxonomyVersion = JsonDocument.Parse(json.RootElement.GetRawText()
            .Replace("\"version\": \"1.0.0\"", "\"version\": \"1.invalid\"", StringComparison.Ordinal));
        Assert.Throws<JsonException>(() =>
            QualityObservationJson.ReadPreservingUnsupported(invalidTaxonomyVersion.RootElement.GetRawText()));

        var decisionWithoutPolicy = json.RootElement.GetRawText()
            .Replace("\"findings\": []", "\"decision\": { \"value\": \"block\" },\n  \"findings\": []", StringComparison.Ordinal);
        using var invalidDecision = JsonDocument.Parse(decisionWithoutPolicy);
        AssertValid(schema, invalidDecision.RootElement, expected: false);

        using var wrongCore = JsonDocument.Parse(json.RootElement.GetRawText()
            .Replace("\"id\": \"quality-studio/core\"", "\"id\": \"com.acme/core\"", StringComparison.Ordinal));
        AssertValid(schema, wrongCore.RootElement, expected: false);
        Assert.Throws<JsonException>(() => QualityObservationJson.Serialize(observation with
        {
            Taxonomy = observation.Taxonomy with { Id = "com.acme/core" },
        }));
    }

    [Fact]
    public void ExtensionsAndUnknownSameMajorTermsRoundTripWithoutEnteringCoreAggregation()
    {
        var aspectExtension = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["com.acme:detail"] = JsonSerializer.SerializeToElement(42),
        };
        var observation = CreateObservation() with
        {
            Aspects =
            [
                new QualityObservationAspect(
                    "code.correctness", "pass", "Core result.",
                    Grade: new QualityObservationGrade(92, "A"), Extensions: NoExtensions),
                new QualityObservationAspect(
                    "com.acme:resilience.backpressure", "concern", "Extension result.", Extensions: NoExtensions),
                new QualityObservationAspect(
                    "code.forward-compatibility", "pass", "Future same-major core result.", Extensions: aspectExtension),
            ],
        };

        var serialized = QualityObservationJson.Serialize(observation);
        var loaded = QualityObservationJson.ReadPreservingUnsupported(serialized);
        var roundTripped = QualityObservationJson.Serialize(Assert.IsType<QualityObservationDocument>(loaded.Observation));
        using var json = JsonDocument.Parse(roundTripped);

        Assert.Equal("preserved", json.RootElement.GetProperty("extensions").GetProperty("com.acme:future").GetString());
        Assert.Contains(loaded.Observation.Aspects, aspect => aspect.AspectId == "com.acme:resilience.backpressure");
        Assert.Equal(42, loaded.Observation.Aspects.Single(aspect => aspect.AspectId == "code.forward-compatibility")
            .Extensions!["com.acme:detail"].GetInt32());
        var installed = QualityTaxonomyCatalogue.SelectInstalledAspects(
            loaded.Observation, [QualityTaxonomyCatalogue.CoreDocument]);
        Assert.Equal("code.correctness", Assert.Single(installed).AspectId);

        var futureCore = QualityTaxonomyCatalogue.CoreDocument with
        {
            Version = "1.1.0",
            Aspects =
            [
                .. QualityTaxonomyCatalogue.CoreDocument.Aspects,
                new QualityAspectTerm(
                    "code.forward-compatibility", "Forward compatibility", "Future same-major term.", 16,
                    ["assessment"], Extensions: NoExtensions),
            ],
        };
        var withFutureCatalogue = QualityTaxonomyCatalogue.SelectInstalledAspects(
            loaded.Observation, [futureCore]);
        Assert.Equal(["code.correctness", "code.forward-compatibility"],
            withFutureCatalogue.Select(aspect => aspect.AspectId));
        Assert.Equal(observation.Taxonomy,
            QualityTaxonomyCatalogue.SourceCatalogue(observation, "code.forward-compatibility"));
    }

    [Fact]
    public void LegacyRootExtensionsMoveIntoExplicitExtensionBagWithoutDataLoss()
    {
        var legacy = JsonNode.Parse(QualityObservationJson.Serialize(CreateObservation()))!.AsObject();
        legacy["x-legacy-producer"] = new JsonObject { ["revision"] = 7 };

        var loaded = QualityObservationJson.ReadPreservingUnsupported(legacy.ToJsonString());
        var observation = Assert.IsType<QualityObservationDocument>(loaded.Observation);
        Assert.Equal(7, observation.Extensions["x-legacy-producer"].GetProperty("revision").GetInt32());
        Assert.True(loaded.Raw.TryGetProperty("x-legacy-producer", out _));

        using var roundTripped = JsonDocument.Parse(QualityObservationJson.Serialize(observation));
        Assert.False(roundTripped.RootElement.TryGetProperty("x-legacy-producer", out _));
        Assert.Equal(7, roundTripped.RootElement.GetProperty("extensions")
            .GetProperty("x-legacy-producer").GetProperty("revision").GetInt32());
    }

    [Fact]
    public void ConflictingLegacyAndExplicitExtensionsAreRejectedRatherThanSilentlyDiscarded()
    {
        var conflict = JsonNode.Parse(QualityObservationJson.Serialize(CreateObservation()))!.AsObject();
        conflict["x-conflict"] = 1;
        conflict["extensions"]!["x-conflict"] = 2;

        Assert.Throws<JsonException>(() => QualityObservationJson.ReadPreservingUnsupported(conflict.ToJsonString()));
    }

    [Fact]
    public void ExtensionCatalogueTermsUseTheirDeclaredPrefixAndRetainSourceIdentity()
    {
        var schema = LoadSchema("quality-taxonomy.v1.schema.json");
        var extension = QualityTaxonomyCatalogue.CoreDocument with
        {
            Id = "com.acme/quality",
            Prefix = "com.acme",
            Aspects =
            [
                new QualityAspectTerm(
                    "com.acme:resilience.backpressure",
                    "Backpressure",
                    "The subject applies bounded backpressure.",
                    0,
                    ["assessment"],
                    Extensions: NoExtensions),
            ],
        };
        using var extensionJson = JsonDocument.Parse(JsonSerializer.Serialize(
            extension, QualityObservationJson.Options));
        AssertValid(schema, extensionJson.RootElement, expected: true);

        var extensionReference = new QualityCatalogueReference(
            extension.Id, extension.Version, "sha256:" + new string('f', 64));
        var observation = CreateObservation() with
        {
            ExtensionCatalogues = [extensionReference],
            Aspects =
            [
                new QualityObservationAspect(
                    "com.acme:resilience.backpressure", "pass", "Bounded.", Extensions: NoExtensions),
            ],
        };

        Assert.Empty(QualityTaxonomyCatalogue.SelectInstalledAspects(
            observation, [QualityTaxonomyCatalogue.CoreDocument]));
        Assert.Single(QualityTaxonomyCatalogue.SelectInstalledAspects(
            observation, [QualityTaxonomyCatalogue.CoreDocument, extension]));
        Assert.Equal(extensionReference,
            QualityTaxonomyCatalogue.SourceCatalogue(observation, observation.Aspects[0].AspectId));

        var wrongPrefix = extension with
        {
            Aspects = [extension.Aspects[0] with { Id = "vendor.experimental" }],
        };
        Assert.False(QualityTaxonomyCatalogue.IsInstalledAspect("vendor.experimental", [wrongPrefix]));
    }

    [Fact]
    public void FindingsMustReferenceExistingTypedEvidence()
    {
        var observation = CreateObservation() with
        {
            Findings =
            [
                new QualityObservationFinding(
                    "of-1",
                    "issue-sha256:" + new string('a', 64),
                    "sha256:" + new string('b', 64),
                    "quality-studio-occurrence-v2",
                    "rule@1",
                    "code.correctness",
                    "high",
                    ["missing-evidence"],
                    new QualityFindingSource("agent", "self", NoExtensions),
                    Extensions: NoExtensions),
            ],
        };

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Serialize(observation));
        Assert.Contains("unresolved evidence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeContractRejectsInvalidHashesAndSemanticAxisValues()
    {
        var observation = CreateObservation();

        Assert.Throws<JsonException>(() => QualityObservationJson.Serialize(observation with
        {
            Subject = observation.Subject with { ManifestHash = "sha256:not-a-digest" },
        }));
        Assert.Throws<JsonException>(() => QualityObservationJson.Serialize(observation with
        {
            Producer = observation.Producer with { Kind = "model" },
        }));
        Assert.Throws<JsonException>(() => QualityObservationJson.Serialize(observation with
        {
            EvidenceStatus = "assumed",
        }));
        Assert.Throws<JsonException>(() => QualityObservationJson.Serialize(observation with
        {
            Aspects =
            [
                observation.Aspects[0] with
                {
                    Grade = new QualityObservationGrade(101, "A"),
                },
            ],
        }));
    }

    public static TheoryData<LegacyQualityVocabulary, string, string?, string?, string?, string?, string?, string?> MappingVectors => new()
    {
        { LegacyQualityVocabulary.SecurityVerdict, "pass", "pass", "available", null, "allow", QualityLegacyMapper.SecurityCombinationPolicy, null },
        { LegacyQualityVocabulary.SecurityVerdict, "warn", "concern", "available", null, "warn", QualityLegacyMapper.SecurityCombinationPolicy, null },
        { LegacyQualityVocabulary.SecurityVerdict, "block", "fail", "available", null, "block", QualityLegacyMapper.SecurityCombinationPolicy, null },
        { LegacyQualityVocabulary.SecurityVerdict, "unavailable", "inconclusive", "unavailable", null, null, QualityLegacyMapper.SecurityCombinationPolicy, null },
        { LegacyQualityVocabulary.FlowVerdict, "pass", "pass", null, null, null, null, null },
        { LegacyQualityVocabulary.FlowVerdict, "fail", "fail", null, null, null, null, null },
        { LegacyQualityVocabulary.FlowVerdict, "undetermined", "inconclusive", null, null, null, null, null },
        { LegacyQualityVocabulary.AttackVerdict, "pass", "pass", null, null, null, null, null },
        { LegacyQualityVocabulary.AttackVerdict, "finding", "fail", null, null, null, null, null },
        { LegacyQualityVocabulary.AttackVerdict, "not-applicable", "not-applicable", null, null, null, null, null },
        { LegacyQualityVocabulary.AttackVerdict, "not-yet-checked", "not-assessed", null, null, null, null, null },
        { LegacyQualityVocabulary.ChangeSummary, "no-quality-delta", null, null, "no-observed-delta", null, null, null },
        { LegacyQualityVocabulary.ChangeSummary, "improved", null, null, "improved", null, null, null },
        { LegacyQualityVocabulary.ChangeSummary, "neutral", null, null, "unchanged", null, null, null },
        { LegacyQualityVocabulary.ChangeSummary, "regression", null, null, "regressed", null, null, null },
        { LegacyQualityVocabulary.ChangeAspect, "good", "pass", null, null, null, null, null },
        { LegacyQualityVocabulary.ChangeAspect, "mixed", "concern", null, null, null, null, null },
        { LegacyQualityVocabulary.ChangeAspect, "concerning", "fail", null, null, null, null, null },
        { LegacyQualityVocabulary.ChangeAspect, "unknown", "inconclusive", null, null, null, null, null },
        { LegacyQualityVocabulary.FindingState, "open", null, null, null, null, null, "open" },
        { LegacyQualityVocabulary.FindingState, "accepted", null, null, null, null, null, "accepted-risk" },
        { LegacyQualityVocabulary.FindingState, "accepted-risk", null, null, null, null, null, "accepted-risk" },
        { LegacyQualityVocabulary.FindingState, "waived", null, null, null, null, null, "waived" },
        { LegacyQualityVocabulary.FindingState, "falsePositive", null, null, null, null, null, "false-positive" },
        { LegacyQualityVocabulary.FindingState, "false-positive", null, null, null, null, null, "false-positive" },
        { LegacyQualityVocabulary.FindingState, "resolved", null, null, null, null, null, "resolved" },
    };

    [Theory]
    [MemberData(nameof(MappingVectors))]
    public void LegacyValuesMapDeterministically(
        LegacyQualityVocabulary vocabulary,
        string legacyValue,
        string? assessment,
        string? evidenceStatus,
        string? change,
        string? decision,
        string? policyRef,
        string? lifecycle)
    {
        var mapping = QualityLegacyMapper.Map(vocabulary, legacyValue);

        Assert.Equal(assessment, mapping.Assessment);
        Assert.Equal(evidenceStatus, mapping.EvidenceStatus);
        Assert.Equal(change, mapping.Change);
        Assert.Equal(decision, mapping.Decision);
        Assert.Equal(policyRef, mapping.PolicyRef);
        Assert.Equal(lifecycle, mapping.Lifecycle);
        Assert.Equal(legacyValue, mapping.LegacyValue);
    }

    [Fact]
    public void LegacyEvidencePreservesStructuredAndPlainPayloads()
    {
        var locator = new QualityEvidenceLocator(Path: "src/a.cs");
        var structured = QualityLegacyMapper.MapEvidence("ev-json", "{\"rule\":\"CA1\"}", locator);
        var text = QualityLegacyMapper.MapEvidence("ev-text", "Exact legacy evidence", locator);
        var empty = QualityLegacyMapper.MapEvidence("ev-empty", string.Empty, locator);

        Assert.Equal("tool-result", structured.Kind);
        Assert.Equal("application/json", structured.MediaType);
        Assert.Equal("CA1", structured.Raw!.Value.GetProperty("rule").GetString());
        Assert.Equal("document", text.Kind);
        Assert.Equal("text/plain", text.MediaType);
        Assert.Equal("Exact legacy evidence", text.Raw!.Value.GetString());
        Assert.Equal(string.Empty, empty.Raw!.Value.GetString());
        Assert.Equal("Legacy evidence text.", empty.Summary);
        Assert.StartsWith("sha256:", structured.ContentHash, StringComparison.Ordinal);
        Assert.NotEqual(structured.ContentHash, text.ContentHash);
    }

    private static QualityObservationDocument CreateObservation()
    {
        var extension = JsonSerializer.SerializeToElement("preserved");
        return new QualityObservationDocument
        {
            ObservationId = "observation-sha256:" + new string('a', 64),
            ObservedAt = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
            Taxonomy = QualityTaxonomyCatalogue.CoreReference,
            Subject = new QualityObservationSubject(
                "qs-v1/dotnet/file/a", "sha256:" + new string('b', 64), "unit", NoExtensions),
            Profile = new QualityObservationProfile(
                "file-code-review", "1.0.0", "sha256:" + new string('c', 64),
                "sha256:" + new string('d', 64), NoExtensions),
            Producer = new QualityObservationProducer(
                "agent", "codex", "openai", "gpt-5", "gpt-5", "high", "2026-07-24",
                "run-1", "review-1", NoExtensions),
            EvidenceStatus = "available",
            Evidence =
            [
                new QualityEvidence(
                    "ev-1", "source-code", new QualityEvidenceLocator(Path: "src/a.cs"),
                    "The reviewed source.", "sha256:" + new string('e', 64), Extensions: NoExtensions),
            ],
            Aspects =
            [
                new QualityObservationAspect(
                    "code.correctness", "pass", "No defect found.",
                    Grade: new QualityObservationGrade(92, "A"), Extensions: NoExtensions),
            ],
            Assessment = "pass",
            Findings = [],
            Extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["com.acme:future"] = extension,
            },
        };
    }

    private static JsonSchema LoadSchema(string fileName) => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", fileName)),
        new BuildOptions { SchemaRegistry = new SchemaRegistry() });

    private static void AssertValid(JsonSchema schema, JsonElement element, bool expected)
    {
        var result = schema.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.Equal(expected, result.IsValid);
    }

    private static void AssertCatalogueOrderAndIdentity(QualityTaxonomyDocument catalogue)
    {
        var axes = new IReadOnlyList<QualityTaxonomyTerm>[]
        {
            catalogue.Axes.ProducerKind,
            catalogue.Axes.EvidenceStatus,
            catalogue.Axes.Assessment,
            catalogue.Axes.Change,
            catalogue.Axes.Decision,
            catalogue.Axes.Severity,
            catalogue.Axes.Lifecycle,
            catalogue.Axes.EvidenceKind,
        };

        foreach (var terms in axes)
        {
            Assert.Equal(terms.Count, terms.Select(term => term.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(Enumerable.Range(0, terms.Count), terms.OrderBy(term => term.Order).Select(term => term.Order));
            Assert.All(terms, term => Assert.False(term.Deprecated));
        }

        Assert.Equal(catalogue.Aspects.Count,
            catalogue.Aspects.Select(aspect => aspect.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(0, catalogue.Aspects.Count),
            catalogue.Aspects.OrderBy(aspect => aspect.Order).Select(aspect => aspect.Order));
        Assert.All(catalogue.Aspects, aspect => Assert.False(aspect.Deprecated));
    }
}
