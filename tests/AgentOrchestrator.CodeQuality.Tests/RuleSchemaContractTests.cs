using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>Keeps rules/**/*.md, RuleLibrary's parser, and schemas/rule.v1.schema.json from drifting apart.</summary>
public sealed class RuleSchemaContractTests
{
    private static readonly Lazy<JsonSchema> RuleSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "rule.v1.schema.json"))));

    private static readonly Lazy<JsonSchema> RulesConfigSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "rules-config.v1.schema.json"))));

    [Fact]
    public void Every_seed_rule_validates_against_the_rule_schema()
    {
        Assert.All(RuleLibrary.Default.Rules, rule =>
        {
            var node = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["id"] = rule.Id,
                ["title"] = rule.Title,
                ["summary"] = rule.Summary,
                ["technology"] = rule.Technology,
                ["category"] = rule.Category,
                ["severity"] = rule.Severity,
                ["autofixable"] = rule.Autofixable,
                ["defaultOn"] = rule.DefaultOn,
                ["kinds"] = new JsonArray(rule.Kinds.Select(value => (JsonNode)value).ToArray()),
                ["levels"] = new JsonArray(rule.Levels.Select(value => (JsonNode)value).ToArray()),
                ["version"] = rule.Version,
                ["status"] = rule.Status,
                ["content"] = rule.Content,
            };
            if (rule.DeterministicCheck is { } check)
                node["deterministicCheck"] = new JsonObject { ["tool"] = check.Tool, ["ruleId"] = check.RuleId };

            using var parsed = JsonDocument.Parse(node.ToJsonString());
            var evaluation = RuleSchema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(evaluation.IsValid, $"{rule.Id}: {evaluation}");
        });
    }

    [Fact]
    public void A_rules_json_override_document_validates_against_its_schema()
    {
        var document = """
        {
          "$schema": "https://agent-orchestrator.dev/quality/schemas/rules-config.v1.schema.json",
          "overrides": [
            { "ruleId": "QS-NG-002", "enabled": false, "reason": "Feature-folder duplication is deliberate here." },
            { "ruleId": "QS-CS-001", "enabled": true }
          ]
        }
        """;
        using var parsed = JsonDocument.Parse(document);
        var evaluation = RulesConfigSchema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void Disabling_a_rule_without_a_reason_fails_schema_validation()
    {
        var document = """{"overrides":[{"ruleId":"QS-NG-001","enabled":false}]}""";
        using var parsed = JsonDocument.Parse(document);
        var evaluation = RulesConfigSchema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(evaluation.IsValid);
    }
}
