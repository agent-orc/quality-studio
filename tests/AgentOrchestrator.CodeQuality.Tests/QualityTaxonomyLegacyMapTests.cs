using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Conformance vectors for every compatibility mapping the taxonomy dossier approved.
/// The enum-typed rows are total by construction: a new legacy value stops compiling here.
/// </summary>
public sealed class QualityTaxonomyLegacyMapTests
{
    [Theory]
    [InlineData(SecurityVerdict.Pass, "pass", "available", "allow")]
    [InlineData(SecurityVerdict.Warn, "concern", "available", "warn")]
    [InlineData(SecurityVerdict.Block, "fail", "available", "block")]
    [InlineData(SecurityVerdict.Unavailable, "inconclusive", "unavailable", "defer")]
    public void SecurityVerdictsSplitIntoAssessmentEvidenceStatusAndPolicyDecision(
        SecurityVerdict verdict, string assessment, string evidenceStatus, string decision)
    {
        var mapped = QualityTaxonomyLegacyMap.Security(verdict);

        Assert.Equal(assessment, mapped.Assessment);
        Assert.Equal(evidenceStatus, mapped.EvidenceStatus);
        Assert.Equal(decision, mapped.Decision);
        Assert.Equal("security-sensor-agent-v1", mapped.PolicyRef);
    }

    [Fact]
    public void EveryCurrentVerdictSpellingMapsToAnInstalledCoreTerm()
    {
        var resolver = QualityTaxonomyResolver.Default;

        foreach (var verdict in Enum.GetValues<SecurityVerdict>())
        {
            var mapped = QualityTaxonomyLegacyMap.Security(verdict);
            Assert.True(resolver.ResolveTerm("assessment", mapped.Assessment).IsKnown);
            Assert.True(resolver.ResolveTerm("evidenceStatus", mapped.EvidenceStatus).IsKnown);
            Assert.True(resolver.ResolveTerm("decision", mapped.Decision!).IsKnown);
        }

        foreach (var verdict in Enum.GetValues<FlowReviewVerdict>())
            Assert.True(resolver.ResolveTerm("assessment", QualityTaxonomyLegacyMap.Flow(verdict)).IsKnown);
        foreach (var verdict in Enum.GetValues<AttackCoverageVerdict>())
            Assert.True(resolver.ResolveTerm("assessment", QualityTaxonomyLegacyMap.Attack(verdict)).IsKnown);
        foreach (var verdict in Enum.GetValues<ChangeReviewVerdict>())
            Assert.True(resolver.ResolveTerm("change", QualityTaxonomyLegacyMap.ChangeSummary(verdict)).IsKnown);
        foreach (var state in Enum.GetValues<FindingState>())
            Assert.True(resolver.ResolveTerm("lifecycle", QualityTaxonomyLegacyMap.FindingState(state)).IsKnown);
        foreach (var severity in Enum.GetValues<FindingSeverity>())
            Assert.True(resolver.ResolveTerm("severity", QualityTaxonomyLegacyMap.Severity(severity)).IsKnown);
    }

    [Theory]
    [InlineData(FlowReviewVerdict.Pass, "pass")]
    [InlineData(FlowReviewVerdict.Fail, "fail")]
    [InlineData(FlowReviewVerdict.Undetermined, "inconclusive")]
    public void FlowVerdictsProjectOntoTheAssessmentAxis(FlowReviewVerdict verdict, string expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMap.Flow(verdict));

    [Theory]
    [InlineData(AttackCoverageVerdict.Pass, "pass")]
    [InlineData(AttackCoverageVerdict.Finding, "fail")]
    [InlineData(AttackCoverageVerdict.NotApplicable, "not-applicable")]
    [InlineData(AttackCoverageVerdict.NotYetChecked, "not-assessed")]
    public void AttackVerdictsProjectOntoTheAssessmentAxis(AttackCoverageVerdict verdict, string expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMap.Attack(verdict));

    [Theory]
    [InlineData(ChangeReviewVerdict.NoQualityDelta, "no-observed-delta")]
    [InlineData(ChangeReviewVerdict.Improved, "improved")]
    [InlineData(ChangeReviewVerdict.Neutral, "unchanged")]
    [InlineData(ChangeReviewVerdict.Regression, "regressed")]
    public void ChangeSummariesProjectOntoTheChangeAxis(ChangeReviewVerdict verdict, string expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMap.ChangeSummary(verdict));

    [Theory]
    [InlineData("good", "pass")]
    [InlineData("mixed", "concern")]
    [InlineData("concerning", "fail")]
    [InlineData("unknown", "inconclusive")]
    [InlineData("not-reviewed", "not-assessed")]
    public void ChangeAspectVerdictsProjectOntoTheAssessmentAxis(string verdict, string expected)
    {
        Assert.True(QualityTaxonomyLegacyMap.TryChangeAspect(verdict, out var assessment));

        Assert.Equal(expected, assessment);
    }

    [Fact]
    public void TheFourCommittedChangeAspectIdsAllMapToTheChangeFamily()
    {
        string[] committed = ["risk", "test-evidence", "scope-discipline", "architecture-drift"];

        var mapped = committed.Select(id => QualityTaxonomyLegacyMap.Aspect(id)).ToArray();

        Assert.All(mapped, item => Assert.Equal(LegacyAspectDisposition.Mapped, item.Disposition));
        Assert.Equal(
            ["change.risk", "change.test-evidence", "change.scope-discipline", "change.architecture-drift"],
            mapped.Select(item => item.AspectId));
    }

    [Theory]
    [InlineData("accepted", "accepted-risk")]
    [InlineData("acceptedRisk", "accepted-risk")]
    [InlineData("falsePositive", "false-positive")]
    [InlineData("false-positive", "false-positive")]
    [InlineData("open", "open")]
    [InlineData("waived", "waived")]
    [InlineData("resolved", "resolved")]
    public void FindingStateSpellingsCollapseOntoOneLifecycleTerm(string state, string expected)
    {
        Assert.True(QualityTaxonomyLegacyMap.TryFindingState(state, out var lifecycle));

        Assert.Equal(expected, lifecycle);
    }

    [Theory]
    [InlineData("mostly-fine")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnmappedLegacyValueIsReportedRatherThanTurnedIntoAPass(string? value)
    {
        Assert.False(QualityTaxonomyLegacyMap.TryChangeAspect(value, out var assessment));
        Assert.False(QualityTaxonomyLegacyMap.TryFindingState(value, out var lifecycle));

        Assert.Equal(string.Empty, assessment);
        Assert.Equal(string.Empty, lifecycle);
    }

    [Theory]
    [InlineData("correctness", "code.correctness")]
    [InlineData("architecture", "code.architecture")]
    [InlineData("security", "security.general")]
    [InlineData("secrets", "security.secrets")]
    [InlineData("dependencies", "security.dependencies")]
    [InlineData("authentication-authorization", "security.authentication-authorization")]
    [InlineData("input-validation", "security.input-validation")]
    [InlineData("configuration-iac", "security.configuration-iac")]
    [InlineData("boundaries", "security.boundary-exposure")]
    [InlineData("performance", "performance.general")]
    public void LegacyAspectIdsMigrateToTheirNamespacedCoreAspect(string legacy, string expected)
    {
        var mapped = QualityTaxonomyLegacyMap.Aspect(legacy);

        Assert.Equal(LegacyAspectDisposition.Mapped, mapped.Disposition);
        Assert.Equal(expected, mapped.AspectId);
    }

    [Fact]
    public void SensorAvailabilityMigratesToEvidenceStatusInsteadOfBecomingAnAspect()
    {
        var mapped = QualityTaxonomyLegacyMap.Aspect("sensor-availability");

        Assert.Equal(LegacyAspectDisposition.MigratesToEvidenceStatus, mapped.Disposition);
        Assert.Null(mapped.AspectId);
    }

    [Fact]
    public void TheGenericAnalyzerBucketNeverSilentlyBecomesACoreAspect()
    {
        var known = QualityTaxonomyLegacyMap.Aspect("analyzer", knownAspectForRule: "correctness");
        var unknown = QualityTaxonomyLegacyMap.Aspect("analyzer", producerId: "sensor.roslyn");
        var unattributable = QualityTaxonomyLegacyMap.Aspect("analyzer");

        Assert.Equal("code.correctness", known.AspectId);
        Assert.Equal(LegacyAspectDisposition.ProducerScoped, unknown.Disposition);
        Assert.Equal("sensor.roslyn:analyzer.unmapped", unknown.AspectId);
        Assert.False(QualityTaxonomyResolver.Default.ResolveAspect(unknown.AspectId!).IsKnown);
        Assert.Equal(LegacyAspectDisposition.Unrecognized, unattributable.Disposition);
        Assert.Null(unattributable.AspectId);
    }

    [Fact]
    public void AMissingFindingSourceBecomesUnknownAndNeverAgent()
    {
        var absent = QualityTaxonomyLegacyMap.FindingSource(null);
        var proven = QualityTaxonomyLegacyMap.FindingSource(null, agentAuthorshipProven: true, agentName: "codex");
        var sensor = QualityTaxonomyLegacyMap.FindingSource(
            new FindingSource(FindingSourceKind.Deterministic, "gitleaks", "gitleaks", "8.18.4"));

        Assert.Equal("unknown", absent.Kind);
        Assert.Equal("agent", proven.Kind);
        Assert.Equal("codex", proven.ProducerRef);
        Assert.Equal("deterministic-sensor", sensor.Kind);
        Assert.Equal("gitleaks", sensor.SensorId);
        Assert.Equal("8.18.4", sensor.SensorVersion);
    }

    [Fact]
    public void MachineJsonEvidenceBecomesATypedToolResultWithADigest()
    {
        const string payload = """{"rule":"generic-api-key","file":"src/A.cs"}""";

        var evidence = QualityTaxonomyLegacyMap.Evidence("ev-1", payload, "Gitleaks match.");

        Assert.Equal("tool-result", evidence.Kind);
        Assert.Equal("application/json", evidence.MediaType);
        Assert.Equal("generic-api-key", evidence.Content!["rule"]!.GetValue<string>());
        Assert.Matches("^sha256:[0-9a-f]{64}$", evidence.ContentHash!);
    }

    [Fact]
    public void PlainTextEvidenceKeepsItsOriginalTextInsteadOfBeingDiscarded()
    {
        var evidence = QualityTaxonomyLegacyMap.Evidence("ev-2", "Line 42 dereferences a nullable.", "Agent note.");

        Assert.Equal("document", evidence.Kind);
        Assert.Equal("text/plain", evidence.MediaType);
        Assert.Equal("Line 42 dereferences a nullable.", evidence.Content!.GetValue<string>());
        Assert.Matches("^sha256:[0-9a-f]{64}$", evidence.ContentHash!);
    }

    [Fact]
    public void MalformedJsonEvidenceIsPreservedAsTextRatherThanDropped()
    {
        var evidence = QualityTaxonomyLegacyMap.Evidence("ev-3", "{\"rule\":", "Truncated sensor output.");

        Assert.Equal("document", evidence.Kind);
        Assert.Equal("{\"rule\":", evidence.Content!.GetValue<string>());
    }

    [Fact]
    public void ALegacyLocationBecomesTypedSourceCodeEvidence()
    {
        var evidence = QualityTaxonomyLegacyMap.SourceEvidence(
            "ev-4",
            new FindingLocation("src/A.cs", new FindingRange(new(12, 3), new(12, 20)), "M:A.Run"),
            "Unchecked value reaches the dereference.");

        Assert.Equal("source-code", evidence.Kind);
        Assert.Equal("src/A.cs", evidence.Locator!.Path);
        Assert.Equal("M:A.Run", evidence.Locator.SymbolId);
        Assert.Equal(12, evidence.Locator.Range!.Start.Line);
    }

    [Fact]
    public void ImportedObservationsCarryTheirLegacyOriginAndCompleteness()
    {
        var observation = QualityObservationContractTests.CreateObservation() with
        {
            Producer = new(CoreTerms.ProducerKind.Unknown),
            Legacy = new("review-meta.v2", ".quality/reviews/files/file.abc.review-meta.code.json",
                ObservationLegacyOrigin.Partial, Value: "warn", ImportId: "import-1"),
        };

        var loaded = QualityObservationJson.Deserialize(QualityObservationJson.Serialize(observation));

        Assert.Equal("review-meta.v2", loaded.Legacy!.Schema);
        Assert.Equal(ObservationLegacyOrigin.Partial, loaded.Legacy.Completeness);
        Assert.Equal("warn", loaded.Legacy.Value);
        Assert.Equal(CoreTerms.ProducerKind.Unknown, loaded.Producer.Kind);
        Assert.Null(loaded.Producer.ThinkingLevel);
    }

    [Fact]
    public void EveryEvidenceKindProducedByTheAdaptersIsAnInstalledTerm()
    {
        string[] produced =
        [
            QualityTaxonomyLegacyMap.Evidence("ev-1", "{}", "s").Kind,
            QualityTaxonomyLegacyMap.Evidence("ev-2", "text", "s").Kind,
            QualityTaxonomyLegacyMap.SourceEvidence("ev-3", new FindingLocation("a.cs"), "s").Kind,
        ];

        Assert.All(produced, kind =>
            Assert.True(QualityTaxonomyResolver.Default.ResolveTerm("evidenceKind", kind).IsKnown));
    }
}
