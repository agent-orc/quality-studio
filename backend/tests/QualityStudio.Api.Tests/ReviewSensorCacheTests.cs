using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;
using Xunit;
using static QualityStudio.Api.Tests.ReviewRunStoreTests;

namespace QualityStudio.Api.Tests;

/// <summary>
/// Review runs on an unchanged Git working copy reuse the previous run's sensor results instead of
/// executing every enabled sensor again (QS-111, defect D7 of the 2026-09-28 evaluation).
/// </summary>
[Trait("Category", "ToolBound")]
public sealed class ReviewSensorCacheTests
{
    [Fact]
    public async Task Second_run_on_the_same_commit_reuses_sensor_evidence_and_an_edit_or_refresh_runs_it_again()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        var events = new ConcurrentQueue<string>();
        var executor = new OrderingExecutorFactory(events);
        try
        {
            await GitTestRepository.InitializeAsync(fixture.RepositoryRoot, cancellationToken);
            await GitTestRepository.RunAsync(fixture.RepositoryRoot, cancellationToken, "add", ".");
            await GitTestRepository.RunAsync(fixture.RepositoryRoot, cancellationToken, "commit", "--quiet", "-m", "fixture");
            await using var application = fixture.CreateApplication(executor, new OrderingSensor(events));
            using var client = application.CreateClient();

            var first = await ReviewAsync(client, refreshSensors: false, cancellationToken);
            var estimate = await client.PostAsJsonAsync("/api/review/estimate",
                new { path = "Sample.cs", kind = "code", cliType = "test-agent" }, cancellationToken);
            estimate.EnsureSuccessStatusCode();
            var plan = (await estimate.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("sensorPlan");
            var second = await ReviewAsync(client, refreshSensors: false, cancellationToken);

            Assert.Equal(["check", "model", "model"], events.ToArray());
            Assert.Equal("ran", SensorOutcome(first));
            Assert.Equal("cached", SensorOutcome(second));
            Assert.Equal("cached", Assert.Single(plan.GetProperty("sensors").EnumerateArray())
                .GetProperty("decision").GetString());
            Assert.Equal(0, plan.GetProperty("expectedDurationMs").GetInt64());
            var reused = Assert.Single(executor.Requests[1].DeterministicEvidence!);
            Assert.Equal("TEST1001", Assert.Single(reused.Findings).RuleId);
            Assert.Equal(
                Assert.Single(executor.Requests[0].DeterministicEvidence!).Provenance.ScannedAt,
                reused.Provenance.ScannedAt);

            await File.WriteAllTextAsync(Path.Combine(fixture.RepositoryRoot, "Second.cs"),
                "namespace Sample; public static class Second { public const int Changed = 1; }", cancellationToken);
            var edited = await ReviewAsync(client, refreshSensors: false, cancellationToken);
            var editedAgain = await ReviewAsync(client, refreshSensors: false, cancellationToken);
            var refreshed = await ReviewAsync(client, refreshSensors: true, cancellationToken);

            Assert.Equal("ran", SensorOutcome(edited));
            Assert.Equal("cached", SensorOutcome(editedAgain));
            Assert.Equal("ran", SensorOutcome(refreshed));
            Assert.True(refreshed.GetProperty("refreshSensors").GetBoolean());
            Assert.Equal(3, events.Count(value => value == "check"));
        }
        finally
        {
            TemporaryDirectory.Delete(QualityDataRoot.For(fixture.RepositoryRoot));
            fixture.Dispose();
        }
    }

    private static async Task<JsonElement> ReviewAsync(HttpClient client, bool refreshSensors, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind = "code",
            cliType = "test-agent",
            force = true,
            refreshSensors,
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return await WaitForStateAsync(client, accepted.GetProperty("id").GetString()!, "done", cancellationToken);
    }

    private static string? SensorOutcome(JsonElement run) =>
        Assert.Single(run.GetProperty("sensors").EnumerateArray()).GetProperty("outcome").GetString();
}
