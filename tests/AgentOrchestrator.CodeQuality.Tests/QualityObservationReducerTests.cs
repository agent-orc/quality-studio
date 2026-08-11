using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationReducerTests
{
    [Fact]
    public void Current_selection_uses_exact_coordinate_then_stable_id_tie_break()
    {
        var old = QualityTaxonomyContractTests.CreateObservation(new string('1', 64));
        var newer = QualityTaxonomyContractTests.CreateObservation(new string('3', 64)) with
        {
            ObservedAt = old.ObservedAt.AddMinutes(1),
            Subject = old.Subject with { ManifestHash = "sha256:" + new string('e', 64) },
        };
        var stableWinner = newer with { ObservationId = "observation-sha256:" + new string('2', 64) };

        var selected = Assert.Single(QualityObservationReducer.SelectCurrent([old, newer, stableWinner]));
        var pinnedOld = Assert.Single(QualityObservationReducer.SelectCurrent(
            [old, newer, stableWinner],
            new QualityObservationSelectionContext(
                old.Subject.UnitId,
                old.Profile.Kind,
                old.Subject.ManifestHash,
                old.Profile.Id,
                old.Profile.Version,
                old.Profile.PromptHash,
                old.Taxonomy.Digest)));

        Assert.Equal(stableWinner.ObservationId, selected.ObservationId);
        Assert.Equal(old.ObservationId, pinnedOld.ObservationId);
    }

    [Fact]
    public void Per_model_records_label_controlled_observational_and_incomplete_comparisons()
    {
        var first = QualityTaxonomyContractTests.CreateObservation(new string('4', 64));
        var second = first with
        {
            ObservationId = "observation-sha256:" + new string('5', 64),
            Producer = first.Producer with { EffectiveModel = "model-b", RunId = "run-b" },
        };
        var controlled = QualityObservationReducer.AggregateByModel([first, second]);
        var observational = QualityObservationReducer.AggregateByModel(
            [first, second with { Subject = second.Subject with { ManifestHash = "sha256:" + new string('f', 64) } }]);
        var incomplete = QualityObservationReducer.AggregateByModel(
            [first, second with { Producer = second.Producer with { ThinkingLevel = "unknown" } }]);

        Assert.Equal(2, controlled.Count);
        Assert.All(controlled, item => Assert.Equal(QualityModelComparability.Controlled, item.Comparability));
        Assert.All(observational, item => Assert.Equal(QualityModelComparability.Observational, item.Comparability));
        Assert.All(incomplete, item => Assert.Equal(QualityModelComparability.Incomplete, item.Comparability));
        Assert.Equal(94, controlled[0].AverageScore);
        Assert.Equal("high", controlled[0].ThinkingLevel);
        var mismatched = Assert.Single(controlled, item => item.EffectiveModel == "model-b");
        Assert.True(mismatched.RequestedEffectiveMismatch);
        Assert.Equal(["gpt-5"], mismatched.RequestedModels);
    }

    [Fact]
    public void Unknown_aspects_remain_visible_but_do_not_enter_core_score()
    {
        var observation = QualityTaxonomyContractTests.CreateObservation(new string('6', 64));
        observation = observation with
        {
            Aspects =
            [
                .. observation.Aspects,
                new QualityAspectObservation("com.acme:resilience.backpressure", "Backpressure",
                    QualityAssessment.Fail, null, "Extension result.", new QualityGrade(10, "F"),
                    QualityObservationJson.NoExtensions),
            ],
        };

        var aggregate = Assert.Single(QualityObservationReducer.AggregateByModel([observation]));
        var projection = QualityObservationReducer.CreateReviewMetaProjection(observation);

        Assert.Equal(94, aggregate.AverageScore);
        Assert.Equal(94, projection["grade"]!["score"]!.GetValue<int>());
        Assert.Contains(projection["aspects"]!.AsArray().OfType<JsonObject>(),
            aspect => aspect["id"]!.GetValue<string>() == "com.acme:resilience.backpressure");
    }

    [Fact]
    public void Explicit_overall_grade_preserves_default_score_policy()
    {
        var observation = QualityTaxonomyContractTests.CreateObservation(new string('9', 64)) with
        {
            Extensions = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["quality-studio/overall-grade"] = System.Text.Json.JsonSerializer.SerializeToElement(
                    new QualityGrade(71, "C"), QualityObservationJson.Options),
            },
        };

        var aggregate = Assert.Single(QualityObservationReducer.AggregateByModel([observation]));
        var projection = QualityObservationReducer.CreateReviewMetaProjection(observation);

        Assert.Equal(71, aggregate.AverageScore);
        Assert.Equal(71, projection["grade"]!["score"]!.GetValue<int>());
    }
}
