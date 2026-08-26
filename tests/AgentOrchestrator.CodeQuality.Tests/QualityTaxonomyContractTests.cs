using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly string RepositoryRoot = RepositoryTestContext.FindRepositoryRoot();
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryRoot, "schemas", "quality-taxonomy.v1.schema.json"))));
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryRoot, "schemas", "quality-observation.v1.schema.json"))));

    [Fact]
    public void Built_in_catalogue_conforms_and_pins_the_approved_core_vocabulary()
    {
        using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", "AgentOrchestrator.CodeQuality", "catalogues",
            "quality-studio-core.v1.json")));

        var evaluation = TaxonomySchema.Value.Evaluate(catalogue.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        var resolved = QualityTaxonomyCatalogue.Core;

        Assert.True(evaluation.IsValid, evaluation.ToString());
        Assert.Equal(QualityTaxonomyDocument.CoreId, resolved.Document.Id);
        Assert.Equal(QualityTaxonomyDocument.CoreVersion, resolved.Document.Version);
        Assert.StartsWith("sha256:", resolved.Digest, StringComparison.Ordinal);
        Assert.Equal(
            ["pass", "concern", "fail", "inconclusive", "not-applicable", "not-assessed"],
            resolved.Document.Terms.Assessments.OrderBy(term => term.Order).Select(term => term.Id));
        Assert.Equal(
            ["open", "accepted-risk", "waived", "false-positive", "resolved"],
            resolved.Document.Terms.Lifecycles.OrderBy(term => term.Order).Select(term => term.Id));
        Assert.Equal(16, resolved.Document.Aspects.Count);
        Assert.Equal("code.correctness", resolved.ResolveCoreAspectAlias("correctness"));
        Assert.Equal("security.boundary-exposure", resolved.ResolveCoreAspectAlias("boundaries"));
    }

    [Fact]
    public void Positive_and_negative_observation_fixtures_have_expected_schema_results()
    {
        using var positive = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, "samples", "quality-observation.v1.sample.json")));
        using var negative = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, "tests", "AgentOrchestrator.CodeQuality.Tests", "Fixtures", "taxonomy",
            "quality-observation.invalid.json")));

        var positiveResult = ObservationSchema.Value.Evaluate(positive.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        var negativeResult = ObservationSchema.Value.Evaluate(negative.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(positiveResult.IsValid, positiveResult.ToString());
        Assert.False(negativeResult.IsValid);
        Assert.True(QualityObservationJson.Read(positive.RootElement.GetRawText()).IsSupported);
    }

    [Fact]
    public void Observation_round_trips_explicit_extensions_and_excludes_unknown_aspects_from_core_lookup()
    {
        var extensionValue = JsonSerializer.SerializeToElement(new { enabled = true, weight = 2 });
        var observation = CreateObservation() with
        {
            Aspects =
            [
                new QualityAspectAssessment(
                    "experimental.new-aspect",
                    QualityAssessment.Concern,
                    "The same-major term is retained but is not installed in the core catalogue.",
                    Extensions: new Dictionary<string, JsonElement> { ["com.acme:signal"] = extensionValue }),
            ],
            Extensions = new Dictionary<string, JsonElement> { ["com.acme:payload"] = extensionValue },
        };

        var serialized = QualityObservationJson.Serialize(observation);
        var loaded = QualityObservationJson.Read(serialized);
        var roundTrip = QualityObservationJson.Serialize(Assert.IsType<QualityObservationDocument>(loaded.Observation));

        Assert.Equal(serialized, roundTrip);
        Assert.Contains("\"com.acme:payload\"", roundTrip, StringComparison.Ordinal);
        Assert.Contains("\"experimental.new-aspect\"", roundTrip, StringComparison.Ordinal);
        Assert.False(QualityTaxonomyCatalogue.Core.IsCoreAspect("experimental.new-aspect"));
    }

    [Fact]
    public void Unknown_schema_or_taxonomy_major_is_quarantined_with_raw_json_intact()
    {
        var supported = QualityObservationJson.Serialize(CreateObservation());
        var schemaV2 = supported.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2",
            StringComparison.Ordinal);
        var taxonomyV2 = supported.Replace("\"version\": \"1.0.0\"", "\"version\": \"2.0.0\"",
            StringComparison.Ordinal);

        var schemaResult = QualityObservationJson.Read(schemaV2);
        var taxonomyResult = QualityObservationJson.Read(taxonomyV2);

        Assert.Equal(QualityObservationReadStatus.UnsupportedSchemaMajor, schemaResult.Status);
        Assert.Null(schemaResult.Observation);
        Assert.Equal(2, schemaResult.Raw.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(QualityObservationReadStatus.UnsupportedTaxonomyMajor, taxonomyResult.Status);
        Assert.Null(taxonomyResult.Observation);
        Assert.Equal("2.0.0", taxonomyResult.Raw.GetProperty("taxonomy").GetProperty("version").GetString());
    }

    [Fact]
    public void Observation_validation_rejects_unresolved_evidence_references()
    {
        var observation = CreateObservation() with
        {
            Findings =
            [
                CreateObservation().Findings[0] with { EvidenceRefs = ["missing-evidence"] },
            ],
        };

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Serialize(observation));

        Assert.Contains("evidenceRefs must resolve", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LegacyTaxonomyContract.SecurityVerdict, "pass", "pass", "available", "allow", null, "security-sensor-agent-v1")]
    [InlineData(LegacyTaxonomyContract.SecurityVerdict, "warn", "concern", "available", "warn", null, "security-sensor-agent-v1")]
    [InlineData(LegacyTaxonomyContract.SecurityVerdict, "block", "fail", "available", "block", null, "security-sensor-agent-v1")]
    [InlineData(LegacyTaxonomyContract.SecurityVerdict, "unavailable", "inconclusive", "unavailable", null, null, "security-sensor-agent-v1")]
    [InlineData(LegacyTaxonomyContract.FlowVerdict, "pass", "pass", null, null, null, null)]
    [InlineData(LegacyTaxonomyContract.FlowVerdict, "fail", "fail", null, null, null, null)]
    [InlineData(LegacyTaxonomyContract.FlowVerdict, "undetermined", "inconclusive", null, null, null, null)]
    [InlineData(LegacyTaxonomyContract.AttackVerdict, "pass", "pass", null, null, null, null)]
    [InlineData(LegacyTaxonomyContract.AttackVerdict, "finding", "fail", null, null, null, null)]
    [InlineData(LegacyTaxonomyContract.AttackVerdict, "not-applicable", "not-applicable", null, null, null, null)]
    [InlineData(LegacyTaxonomyContract.AttackVerdict, "not-yet-checked", "not-assessed", null, null, null, null)]
    [InlineData(LegacyTaxonomyContract.ChangeSummary, "no-quality-delta", null, null, null, "no-observed-delta", null)]
    [InlineData(LegacyTaxonomyContract.ChangeSummary, "improved", null, null, null, "improved", null)]
    [InlineData(LegacyTaxonomyContract.ChangeSummary, "neutral", null, null, null, "unchanged", null)]
    [InlineData(LegacyTaxonomyContract.ChangeSummary, "regression", null, null, null, "regressed", null)]
    [InlineData(LegacyTaxonomyContract.ChangeAspect, "good", "pass", null, null, null, null)]
    [InlineData(LegacyTaxonomyContract.ChangeAspect, "mixed", "concern", null, null, null, null)]
    [InlineData(LegacyTaxonomyContract.ChangeAspect, "concerning", "fail", null, null, null, null)]
    [InlineData(LegacyTaxonomyContract.ChangeAspect, "unknown", "inconclusive", null, null, null, null)]
    [InlineData(LegacyTaxonomyContract.FindingState, "open", null, null, null, null, "open")]
    [InlineData(LegacyTaxonomyContract.FindingState, "accepted", null, null, null, null, "accepted-risk")]
    [InlineData(LegacyTaxonomyContract.FindingState, "accepted-risk", null, null, null, null, "accepted-risk")]
    [InlineData(LegacyTaxonomyContract.FindingState, "waived", null, null, null, null, "waived")]
    [InlineData(LegacyTaxonomyContract.FindingState, "falsePositive", null, null, null, null, "false-positive")]
    [InlineData(LegacyTaxonomyContract.FindingState, "false-positive", null, null, null, null, "false-positive")]
    [InlineData(LegacyTaxonomyContract.FindingState, "resolved", null, null, null, null, "resolved")]
    public void Approved_legacy_mapping_vectors_are_deterministic(
        LegacyTaxonomyContract contract,
        string legacyValue,
        string? assessment,
        string? evidenceStatus,
        string? decision,
        string? change,
        string? lifecycleOrPolicy)
    {
        var mapping = LegacyTaxonomyMapper.Map(contract, legacyValue);

        Assert.Equal(assessment, ToTerm(mapping.Assessment));
        Assert.Equal(evidenceStatus, ToTerm(mapping.EvidenceStatus));
        Assert.Equal(decision, ToTerm(mapping.Decision));
        Assert.Equal(change, mapping.Change);
        if (contract == LegacyTaxonomyContract.SecurityVerdict)
            Assert.Equal(lifecycleOrPolicy, mapping.PolicyRef);
        else
            Assert.Equal(lifecycleOrPolicy, mapping.Lifecycle);
        Assert.Equal(legacyValue, mapping.LegacyValue);
    }

    [Fact]
    public void Legacy_evidence_preserves_structured_json_and_plain_text_with_digests()
    {
        var structured = LegacyTaxonomyMapper.MapEvidence("json", "{\"scanner\":\"gitleaks\"}");
        var text = LegacyTaxonomyMapper.MapEvidence("text", "line 1\nline 2");

        Assert.Equal(QualityEvidenceKind.ToolResult, structured.Kind);
        Assert.Equal("application/json", structured.MediaType);
        Assert.Equal("gitleaks", structured.Content?.GetProperty("scanner").GetString());
        Assert.Equal(QualityEvidenceKind.Document, text.Kind);
        Assert.Equal("text/plain", text.MediaType);
        Assert.Equal("line 1\nline 2", text.Content?.GetString());
        Assert.StartsWith("sha256:", structured.ContentHash, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", text.ContentHash, StringComparison.Ordinal);
    }

    private static QualityObservationDocument CreateObservation()
    {
        var hash = "sha256:" + new string('a', 64);
        var evidence = new QualityEvidence(
            "ev-1",
            QualityEvidenceKind.SourceCode,
            "Exact source evidence.",
            Locator: new QualityEvidenceLocator(Path: "src/Example.cs"),
            ContentHash: hash);
        return new QualityObservationDocument
        {
            ObservationId = "observation-sha256:" + new string('b', 64),
            ObservedAt = new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero),
            Taxonomy = new QualityTaxonomyReference(
                QualityTaxonomyDocument.CoreId,
                QualityTaxonomyDocument.CoreVersion,
                QualityTaxonomyCatalogue.Core.Digest,
                []),
            Subject = new QualityObservationSubject("qs-v1/dotnet/file/example", hash),
            Profile = new QualityReviewProfile("file-code-review", "1.0.0", hash, hash),
            Producer = new QualityObservationProducer(
                QualityProducerKind.Agent, "codex", "openai", "gpt-5.6-sol", "gpt-5.6-sol",
                "xhigh", "2026-07-24", "run-1", "review-1"),
            EvidenceStatus = QualityEvidenceStatus.Available,
            Evidence = [evidence],
            Aspects =
            [
                new QualityAspectAssessment("code.correctness", QualityAssessment.Fail,
                    "A correctness defect is evidenced.", new QualityObservationGrade(68, GradeBand.D)),
            ],
            Assessment = QualityAssessment.Fail,
            Findings =
            [
                new QualityObservationFinding(
                    "of-1", "issue-1", hash, "quality-studio-occurrence-v2",
                    "built-in/code.correctness.example@1", "code.correctness", FindingSeverity.High,
                    [evidence.Id], new QualityFindingSource(QualityProducerKind.Agent, "self")),
            ],
        };
    }

    private static string? ToTerm<T>(T? value) where T : struct, Enum =>
        value?.ToString().Replace("NotApplicable", "not-applicable", StringComparison.Ordinal)
            .Replace("NotAssessed", "not-assessed", StringComparison.Ordinal)
            .ToLowerInvariant();
}
