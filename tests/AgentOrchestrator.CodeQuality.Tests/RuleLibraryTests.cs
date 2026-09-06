using System.Text.Json;
using Json.Schema;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RuleLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-rule-tests", Guid.NewGuid().ToString("N"));

    public RuleLibraryTests() => Directory.CreateDirectory(Path.GetDirectoryName(OverridePath())!);

    [Fact]
    public async Task Built_in_catalogue_conforms_to_the_repository_schema()
    {
        var repository = RepositoryTestContext.FindRepositoryRoot();
        using var catalogue = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(repository, "src", "AgentOrchestrator.CodeQuality", "catalogues", "rule-catalogue.v1.json"),
            TestContext.Current.CancellationToken));
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(
            Path.Combine(repository, "schemas", "rule-catalogue.v1.schema.json"),
            TestContext.Current.CancellationToken));

        var result = schema.Evaluate(catalogue.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public async Task The_documented_override_example_conforms_to_the_rule_config_schema()
    {
        var repository = RepositoryTestContext.FindRepositoryRoot();
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(
            Path.Combine(repository, "schemas", "rule-config.v1.schema.json"), TestContext.Current.CancellationToken));
        using var document = JsonDocument.Parse(WriteOverrides());

        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void Resolve_reads_the_embedded_catalogue_without_any_repository_configuration()
    {
        var catalogue = new RuleCatalogueResolver().Resolve(root);

        Assert.Matches(@"^\d+\.\d+\.\d+$", catalogue.CatalogueVersion);
        Assert.NotEmpty(catalogue.Rules);
        Assert.All(catalogue.Rules, rule => Assert.Equal("built-in", rule.Scope));
        Assert.All(catalogue.Rules, rule => Assert.NotEmpty(rule.Rule.Kinds));
        Assert.All(catalogue.Rules, rule => Assert.NotEmpty(rule.Rule.Detection));
        Assert.Contains(catalogue.Rules, rule => rule.Rule.Technology == "generic");
        Assert.Equal("embedded:catalogues.rule-catalogue.v1.json", Assert.Single(catalogue.Sources));
    }

    [Fact]
    public void Resolve_applies_a_project_override_and_records_where_it_came_from()
    {
        File.WriteAllText(OverridePath(), WriteOverrides());

        var catalogue = new RuleCatalogueResolver().Resolve(root);

        var disabled = Assert.Single(catalogue.Rules, rule => rule.Rule.Id == "QS-CS-004");
        Assert.False(disabled.EffectiveEnabled);
        Assert.Equal(OverridePath(), disabled.Scope);
        var raised = Assert.Single(catalogue.Rules, rule => rule.Rule.Id == "QS-CS-003");
        Assert.True(raised.SeverityOverridden);
        Assert.Equal(FindingSeverity.Critical, raised.EffectiveSeverity);
        Assert.Equal(FindingSeverity.High, raised.Rule.Severity);
        Assert.Contains(OverridePath(), catalogue.Sources);
    }

    [Fact]
    public void Resolve_rejects_an_override_for_a_rule_that_does_not_exist()
    {
        File.WriteAllText(OverridePath(), """
            { "schemaVersion": 1, "overrides": [ { "id": "QS-CS-999", "enabled": false, "reason": "Typo." } ] }
            """);

        var exception = Assert.Throws<JsonException>(() => new RuleCatalogueResolver().Resolve(root));

        Assert.Contains("QS-CS-999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_rejects_an_override_without_a_reason()
    {
        File.WriteAllText(OverridePath(), """
            { "schemaVersion": 1, "overrides": [ { "id": "QS-CS-003", "enabled": false, "reason": "  " } ] }
            """);

        var exception = Assert.Throws<JsonException>(() => new RuleCatalogueResolver().Resolve(root));

        Assert.Contains("requires a reason", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderAsReviewInputs_selects_by_kind_and_technology()
    {
        var catalogue = new RuleCatalogueResolver().Resolve(root);

        var security = RuleCatalogueResolver.RenderAsReviewInputs(catalogue, "security", "dotnet");
        var performance = RuleCatalogueResolver.RenderAsReviewInputs(catalogue, "performance", "angular");

        Assert.NotEmpty(security);
        Assert.NotEmpty(performance);
        Assert.All(security, input => Assert.Contains("security", input.Kinds));
        Assert.All(security, input => Assert.DoesNotContain("QS-NG-", input.Id, StringComparison.Ordinal));
        Assert.All(performance, input => Assert.DoesNotContain("QS-CS-", input.Id, StringComparison.Ordinal));
        Assert.All(security.Concat(performance), input => Assert.Equal("built-in", input.Scope));
        Assert.Equal(security.OrderByDescending(input => input.Priority).Select(input => input.Id),
            security.Select(input => input.Id));
    }

    [Fact]
    public void Rendered_rule_content_carries_the_statement_and_the_detection_guidance()
    {
        var catalogue = new RuleCatalogueResolver().Resolve(root);

        var input = Assert.Single(RuleCatalogueResolver.RenderAsReviewInputs(catalogue, "code", "dotnet"),
            candidate => candidate.Id == "QS-CS-003");

        var rule = Assert.Single(catalogue.Rules, candidate => candidate.Rule.Id == "QS-CS-003");
        Assert.Contains(rule.Rule.Statement, input.Content, StringComparison.Ordinal);
        Assert.Contains("Detection: " + rule.Rule.Detection, input.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(rule.Rule.GoodExample, input.Content, StringComparison.Ordinal);
        Assert.Equal(rule.Rule.Version, input.Version);
    }

    [Fact]
    public void AppliesTo_matches_the_rules_own_technology_and_the_language_independent_ones()
    {
        Assert.True(RuleCatalogueResolver.AppliesTo("dotnet", "dotnet"));
        Assert.True(RuleCatalogueResolver.AppliesTo("generic", "dotnet"));
        Assert.True(RuleCatalogueResolver.AppliesTo("angular", null));
        Assert.False(RuleCatalogueResolver.AppliesTo("angular", "dotnet"));
        Assert.False(RuleCatalogueResolver.AppliesTo("dotnet", "generic"));
    }

    [Theory]
    [InlineData("qs-v1/dotnet/file/abc", "dotnet")]
    [InlineData("qs-v1/angular/module/abc", "angular")]
    [InlineData("qs-v1/rust/file/abc", null)]
    [InlineData("not-a-unit-id", null)]
    [InlineData(null, null)]
    public void AdapterFromUnitId_reads_only_a_well_formed_unit_id(string? unitId, string? expected) =>
        Assert.Equal(expected, RuleCatalogueResolver.AdapterFromUnitId(unitId));

    private string OverridePath() => Path.Combine(root,
        RuleCatalogueResolver.ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string WriteOverrides() => """
            {
              "$schema": "https://agent-orchestrator.dev/quality/schemas/rule-config.v1.schema.json",
              "schemaVersion": 1,
              "overrides": [
                { "id": "QS-CS-004", "enabled": false, "reason": "This repository has no test project." },
                { "id": "QS-CS-003", "severity": "critical", "reason": "Cancellation defects caused two outages." }
              ]
            }
            """;

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
