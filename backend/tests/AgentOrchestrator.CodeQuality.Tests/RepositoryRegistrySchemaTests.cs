using System.Text.Json;
using Json.Schema;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// The published shape of the repository registry file. A registry written before external references
/// existed must stay valid, and a reference that the host would refuse must not validate either.
/// </summary>
public sealed class RepositoryRegistrySchemaTests
{
    private static JsonSchema Schema => SchemaCatalogue.Get("repository-registry.v1.schema.json");

    private const string LegacyEntry = """
        {
          "id": "default",
          "displayName": "quality-studio",
          "rootPath": "/repositories/quality-studio",
          "globalInputsDirectory": null,
          "inputBudgetCharacters": 12000,
          "enabledReviewKinds": ["code", "security", "performance"],
          "sensors": [{ "id": "eslint", "enabled": true, "configuration": { "profile": "eslint-frontend-sarif" } }],
          "archived": false,
          "defaultReviewTokenCap": null,
          "defaultReviewCostCap": null
        }
        """;

    [Fact]
    public void A_registry_without_external_references_is_valid()
    {
        AssertValid($"[{LegacyEntry}]", expected: true);
    }

    [Fact]
    public void A_registration_with_an_agent_studio_reference_is_valid()
    {
        AssertValid($$"""
            [{{LegacyEntry}},
             { "id": "proj-002", "displayName": "Agent Studio", "rootPath": "/srv/runner/PROJ-002/repo",
               "inputBudgetCharacters": 12000, "enabledReviewKinds": ["code"], "archived": false,
               "externalRefs": [{ "system": "agent-studio", "id": "PROJ-002", "url": "https://robert.omgai.dev/#/projects/PROJ-002" }] }]
            """, expected: true);
    }

    [Theory]
    [InlineData("""{ "system": "Agent Studio", "id": "PROJ-002" }""")]
    [InlineData("""{ "system": "agent-studio", "id": "" }""")]
    [InlineData("""{ "system": "agent-studio" }""")]
    [InlineData("""{ "system": "agent-studio", "id": "PROJ-002", "url": "javascript:alert(1)" }""")]
    [InlineData("""{ "system": "agent-studio", "id": "PROJ-002", "extra": true }""")]
    public void A_reference_the_host_would_refuse_is_invalid(string reference)
    {
        AssertValid($$"""
            [{ "id": "proj-002", "displayName": "Agent Studio", "rootPath": "/srv/runner/PROJ-002/repo",
               "inputBudgetCharacters": 12000, "enabledReviewKinds": ["code"], "externalRefs": [{{reference}}] }]
            """, expected: false);
    }

    [Fact]
    public void An_empty_reference_list_is_written_as_no_list_at_all()
    {
        AssertValid("""
            [{ "id": "proj-002", "displayName": "Agent Studio", "rootPath": "/srv/runner/PROJ-002/repo",
               "inputBudgetCharacters": 12000, "enabledReviewKinds": ["code"], "externalRefs": [] }]
            """, expected: false);
    }

    private static void AssertValid(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        var result = Schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid == expected, result.IsValid ? "Document unexpectedly valid." : json);
    }
}
