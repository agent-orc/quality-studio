using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed partial class SarifSensorTests
{
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"results":null}""")]
    [InlineData("""{"results":{}}""")]
    [InlineData("""{"results":"not-an-array"}""")]
    [InlineData("""{"results":[],"invocations":{}}""")]
    [InlineData("""{"results":[],"invocations":[null]}""")]
    [InlineData("""{"results":[],"invocations":[{}]}""")]
    [InlineData("""{"results":[],"invocations":[{"executionSuccessful":"true"}]}""")]
    [InlineData("""{"results":[],"invocations":[{"executionSuccessful":false,"exitCode":2}]}""")]
    [InlineData("""{"results":[],"invocations":[{"executionSuccessful":true,"exitCode":"2"}]}""")]
    [InlineData("""{"results":[],"externalPropertyFileReferences":{"results":[{"location":{"uri":"results.json"},"itemCount":1}]}}""")]
    [InlineData("""{"results":[],"externalPropertyFileReferences":{"results":{}}}""")]
    [InlineData("""{"results":[],"externalPropertyFileReferences":"invalid"}""")]
    [InlineData("""{"results":[],"invocations":[{"executionSuccessful":true,"toolExecutionNotifications":[{"level":"error","message":{"text":"Analysis incomplete"}}]}]}""")]
    [InlineData("""{"results":[],"invocations":[{"executionSuccessful":true,"toolConfigurationNotifications":[{"level":"error","message":{"text":"Invalid configuration"}}]}]}""")]
    [InlineData("""{"results":[],"invocations":[{"executionSuccessful":true,"toolExecutionNotifications":{}}]}""")]
    [InlineData("""{"results":[],"invocations":[{"executionSuccessful":true,"toolExecutionNotifications":[null]}]}""")]
    public async Task Incomplete_or_failed_sarif_is_unavailable_instead_of_clean(string properties)
    {
        var result = await RunDocumentAsync(SarifDocument(properties));
        Assert.False(result.Available);
        Assert.NotEmpty(result.UnavailableReason!);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData("""{"version":"2.1.0","runs":[]}""")]
    [InlineData("""{"version":"2.1.0","runs":null}""")]
    public async Task Sarif_without_an_analysis_run_is_unavailable(string document)
    {
        var result = await RunDocumentAsync(document);
        Assert.False(result.Available);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData("""{"results":[]}""")]
    [InlineData("""{"results":[],"invocations":[]}""")]
    [InlineData("""{"results":[],"invocations":[{"executionSuccessful":true,"exitCode":0}]}""")]
    [InlineData("""{"results":[],"invocations":[{"executionSuccessful":true,"exitCode":2}]}""")]
    [InlineData("""{"results":[],"externalPropertyFileReferences":{"results":[]}}""")]
    [InlineData("""{"results":[],"invocations":[{"executionSuccessful":true,"toolExecutionNotifications":[{"level":"warning","message":{"text":"Advisory"}}]}]}""")]
    public async Task Completed_sarif_with_an_explicit_empty_results_array_is_clean(string properties)
    {
        var result = await RunDocumentAsync(SarifDocument(properties));
        Assert.True(result.Available, result.UnavailableReason);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData("roslyn")]
    [InlineData("eslint")]
    public async Task Delegating_analyzers_reject_failed_sarif_execution(string sensorId)
    {
        IReviewSensor sensor = sensorId == "roslyn" ? new RoslynAnalyzerSensor() : new EslintAnalyzerSensor();
        var result = await RunDocumentAsync(SarifDocument(
            """{"results":[],"invocations":[{"executionSuccessful":false,"exitCode":2}]}"""), sensor);
        Assert.False(result.Available);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task One_failed_run_makes_a_multi_run_report_unavailable_without_partial_findings()
    {
        var document = JsonNode.Parse(SarifDocument(
            """{"results":[{"ruleId":"fixture-rule","message":{"text":"A finding"}}]}"""))!.AsObject();
        var failure = JsonNode.Parse(SarifDocument(
            """{"results":[],"invocations":[{"executionSuccessful":false}]}"""))!["runs"]![0]!.DeepClone();
        document["runs"]!.AsArray().Add(failure);
        var result = await RunDocumentAsync(document.ToJsonString());
        Assert.False(result.Available);
        Assert.Empty(result.Findings);
    }

    private static string SarifDocument(string properties)
    {
        var run = JsonNode.Parse(properties)!.AsObject();
        run["tool"] = JsonNode.Parse("""{"driver":{"name":"fixture"}}""");
        return new JsonObject { ["version"] = "2.1.0", ["runs"] = new JsonArray(run) }.ToJsonString();
    }

    private static async Task<SensorScanResult> RunDocumentAsync(string document, IReviewSensor? sensor = null)
    {
        var root = CreateRepository("src/Sample.cs");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "fixture.sarif"), document,
                TestContext.Current.CancellationToken);
            return await (sensor ?? new SarifSensor()).RunAsync(new SensorScanRequest(root,
                Configuration: new Dictionary<string, string> { ["reportPath"] = "fixture.sarif" }),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            QualityStudio.Testing.TemporaryDirectory.Delete(root);
        }
    }
}
