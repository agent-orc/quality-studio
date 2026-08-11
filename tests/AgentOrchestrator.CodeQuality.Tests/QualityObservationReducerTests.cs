using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationReducerTests
{
    [Fact]
    public void CurrentSelectionRequiresExactFreshnessAndProfileIdentity()
    {
        var expected = Observation("expected", "model-a", "high", "sha256:inputs", 80,
            new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero));
        var newerWrongInputs = Observation("wrong", "model-b", "high", "sha256:other", 99,
            new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero));

        var selected = QualityObservationReducer.SelectCurrent([newerWrongInputs, expected],
            new QualityObservationSelectionTarget(
                "unit:file:src/App.cs",
                "subject",
                "file-code-review",
                "1.0.0",
                "sha256:prompt",
                "inputs",
                QualityTaxonomyCatalogue.CoreDocument.Id,
                QualityTaxonomyCatalogue.CoreDocument.Version,
                QualityTaxonomyCatalogue.CoreDigest));

        Assert.Equal("expected", selected?.ObservationId);
    }

    [Fact]
    public void PerModelRecordsRetainBothModelsAndExcludeUnknownAspectsFromGrade()
    {
        var modelA = Observation("a", "model-a", "high", "sha256:inputs", 80,
            new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero));
        var modelB = Observation("b", "model-b", "medium", "sha256:inputs", 60,
            new DateTimeOffset(2026, 8, 11, 8, 1, 0, TimeSpan.Zero)) with
        {
            Aspects =
            [
                new QualityObservationAspect("code.correctness", "pass", "known", Grade: new QualityObservationGrade(60, "C")),
                new QualityObservationAspect("vendor.experimental", "pass", "extension", Grade: new QualityObservationGrade(100, "A")),
            ],
        };

        var reduction = QualityObservationReducer.Reduce([modelA, modelB]);

        Assert.Equal(["model-a", "model-b"], reduction.Models.Select(item => item.EffectiveModel));
        Assert.All(reduction.Models, item => Assert.Equal("controlled", item.Comparability));
        Assert.Equal(60, Assert.Single(reduction.Models, item => item.EffectiveModel == "model-b").AverageScore);
        var unknown = Assert.Single(reduction.UnknownAspects);
        Assert.Equal("vendor.experimental", unknown.AspectId);
        Assert.Equal(1, unknown.Observations);
    }

    [Fact]
    public void ComparabilityDistinguishesObservationalAndIncompleteRecords()
    {
        var first = Observation("a", "model-a", "high", "sha256:inputs-a", 80, DateTimeOffset.UtcNow);
        var second = Observation("b", "model-b", "high", "sha256:inputs-b", 80, DateTimeOffset.UtcNow);
        Assert.All(QualityObservationReducer.Reduce([first, second]).Models,
            item => Assert.Equal("observational", item.Comparability));

        var missingRoute = second with
        {
            Producer = second.Producer with { EffectiveModel = "unknown" },
        };
        Assert.All(QualityObservationReducer.Reduce([first, missingRoute]).Models,
            item => Assert.Equal("incomplete", item.Comparability));
    }

    [Fact]
    public void EffectiveModelIsTheAggregationKeyAndRequestedRouteMismatchesRemainVisible()
    {
        var first = Observation("a", "effective", "high", "sha256:inputs", 80, DateTimeOffset.UtcNow);
        first = first with { Producer = first.Producer with { RequestedModel = "requested-a" } };
        var second = first with
        {
            ObservationId = "b",
            Producer = first.Producer with { RequestedModel = "requested-b", RunId = "b", ReviewRunId = "b" },
        };

        var record = Assert.Single(QualityObservationReducer.Reduce([first, second]).Models);

        Assert.Equal("effective", record.EffectiveModel);
        Assert.Equal("mixed", record.RequestedModel);
        Assert.Equal(2, record.Samples);
    }

    private static QualityObservationDocument Observation(
        string id,
        string model,
        string thinking,
        string inputsHash,
        int score,
        DateTimeOffset observedAt) => new()
    {
        ObservationId = id,
        ObservedAt = observedAt,
        Taxonomy = QualityTaxonomyCatalogue.CoreReference,
        Subject = new QualityObservationSubject("unit:file:src/App.cs", "sha256:subject", "unit"),
        Profile = new QualityObservationProfile(
            "file-code-review", "1.0.0", "sha256:prompt", inputsHash),
        Producer = new QualityObservationProducer(
            "agent", "codex", "openai", model, model, thinking, "route-v1", id, id),
        EvidenceStatus = "available",
        Aspects =
        [
            new QualityObservationAspect("code.correctness", "pass", "known",
                Grade: new QualityObservationGrade(score, score >= 80 ? "B" : "C")),
        ],
        Assessment = "pass",
        Extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
    };
}
