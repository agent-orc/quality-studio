using System.Text.Json;
using Json.Schema;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityDomainCatalogueTests
{
    [Fact]
    public async Task Authored_quality_domains_conform_to_the_published_schema()
    {
        var repository = RepositoryTestContext.FindRepositoryRoot();
        using var catalogue = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(repository, "rules", "quality-domains.json"), TestContext.Current.CancellationToken));
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(
            Path.Combine(repository, "schemas", "quality-domains.v1.schema.json"), TestContext.Current.CancellationToken));

        var result = schema.Evaluate(catalogue.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
    }
}
