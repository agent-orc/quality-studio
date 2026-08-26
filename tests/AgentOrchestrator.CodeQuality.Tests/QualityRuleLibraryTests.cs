using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityRuleLibraryTests
{
    private static string RepositoryRoot => RepositoryTestContext.FindRepositoryRoot();
    private static string RulesRoot => Path.Combine(RepositoryRoot, "rules");
    private static readonly Lazy<JsonSchema> ConfigurationSchema = new(() => JsonSchema.FromText(
        File.ReadAllText(Path.Combine(RepositoryRoot, "schemas", "quality-rules-config.v1.schema.json"))));

    [Fact]
    public void Seed_library_is_schema_valid_unique_and_covers_required_defect_classes()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            RepositoryRoot, "schemas", "quality-rule.v1.schema.json")));
        var paths = Directory.EnumerateFiles(RulesRoot, "*.rule.json", SearchOption.AllDirectories).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var result = schema.Evaluate(json.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, $"{path}: {result}");
            Assert.True(ids.Add(json.RootElement.GetProperty("id").GetString()!));
        }

        var rules = new QualityRuleLibrary(RulesRoot).Load();
        Assert.Equal(9, rules.Count);
        Assert.Contains(rules, rule => rule.Id == "QS-NG-002" && rule.DefaultOn &&
                                       rule.Category == "design-tokens");
        Assert.Contains(rules, rule => rule.Id == "QS-NG-003" && rule.DefaultOn &&
                                       rule.Category == "component-reuse");
        Assert.Contains(rules, rule => rule.Language == "angular" && rule.Category == "change-detection");
        Assert.Contains(rules, rule => rule.Language == "csharp" && rule.Category == "async-hygiene");

        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, ".quality", "rules.json")));
        Assert.True(ConfigurationSchema.Value.Evaluate(configuration.RootElement).IsValid);
    }

    [Fact]
    public async Task Aggregate_keeps_rule_active_when_only_one_descendant_scope_is_disabled()
    {
        var root = NewRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".quality"));
            await File.WriteAllTextAsync(Path.Combine(root, ".quality", "rules.json"), """
                {
                  "$schema": "https://quality.studio/schemas/quality-rules-config.v1.schema.json",
                  "schemaVersion": 1,
                  "overrides": [
                    { "ruleId": "QS-NG-002", "enabled": false, "scope": { "include": ["legacy/*"] } }
                  ]
                }
                """, TestContext.Current.CancellationToken);

            var result = new QualityRuleResolver(RulesRoot).Resolve(
                root, ["legacy/panel.ts", "src/panel.ts"]);

            Assert.Contains(result.Rules, rule => rule.Definition.Id == "QS-NG-002");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Resolver_applies_defaults_and_ordered_scoped_project_overrides()
    {
        var root = NewRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".quality"));
            await File.WriteAllTextAsync(Path.Combine(root, ".quality", "rules.json"), """
                {
                  "$schema": "https://quality.studio/schemas/quality-rules-config.v1.schema.json",
                  "schemaVersion": 1,
                  "overrides": [
                    { "ruleId": "QS-NG-002", "severity": "high" },
                    { "ruleId": "QS-NG-002", "enabled": false, "scope": { "include": ["legacy/*"] } },
                    { "ruleId": "QS-NG-001", "enabled": true, "scope": { "include": ["src/*"] } }
                  ]
                }
                """, TestContext.Current.CancellationToken);
            var resolver = new QualityRuleResolver(RulesRoot);

            var current = resolver.Resolve(root, ["src/panel.ts"]);
            var tokenRule = Assert.Single(current.Rules, rule => rule.Definition.Id == "QS-NG-002");
            Assert.Equal(FindingSeverity.High, tokenRule.Severity);
            Assert.Contains(current.Rules, rule => rule.Definition.Id == "QS-NG-001");
            Assert.Contains("QS-NG-003", current.PromptContext(), StringComparison.Ordinal);

            var legacy = resolver.Resolve(root, ["legacy/panel.ts"]);
            Assert.DoesNotContain(legacy.Rules, rule => rule.Definition.Id == "QS-NG-002");
            Assert.NotEqual(current.EffectiveHash, legacy.EffectiveHash);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Configuration_schema_and_runtime_reject_unknown_rule_ids()
    {
        var root = NewRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".quality"));
            var path = Path.Combine(root, ".quality", "rules.json");
            await File.WriteAllTextAsync(path, """
                {
                  "$schema": "https://quality.studio/schemas/quality-rules-config.v1.schema.json",
                  "schemaVersion": 1,
                  "overrides": [{ "ruleId": "QS-NG-999", "enabled": false }]
                }
                """, TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.True(ConfigurationSchema.Value.Evaluate(json.RootElement).IsValid);

            var error = Assert.Throws<QualityRuleConfigurationException>(() =>
                new QualityRuleResolver(RulesRoot).Resolve(root, ["src/panel.ts"]));
            Assert.Contains("unknown rule", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Deterministic_precheck_emits_named_findings_and_honors_disable_override()
    {
        var root = NewRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            await File.WriteAllTextAsync(Path.Combine(root, "src", "panel.css"),
                ".card { padding: 13px; color: #123456; }\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "src", "panel.ts"),
                "@Component({ selector: 'app-panel' }) export class Panel {}\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "src", "Worker.cs"),
                "public Result Run() => operation.Result;\n", TestContext.Current.CancellationToken);
            var sensor = new QualityRulePrecheckSensor(new QualityRuleResolver(RulesRoot));

            var result = await sensor.RunAsync(new SensorScanRequest(root), TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            Assert.Contains(result.Findings, finding => finding.RuleId == "QS-NG-002");
            Assert.Contains(result.Findings, finding => finding.RuleId == "QS-NG-005");
            Assert.Contains(result.Findings, finding => finding.RuleId == "QS-CS-003");
            Assert.All(result.Findings, finding => Assert.Equal(FindingSourceKind.Deterministic, finding.Source!.Kind));

            Directory.CreateDirectory(Path.Combine(root, ".quality"));
            await File.WriteAllTextAsync(Path.Combine(root, ".quality", "rules.json"), """
                {
                  "$schema": "https://quality.studio/schemas/quality-rules-config.v1.schema.json",
                  "schemaVersion": 1,
                  "overrides": [{ "ruleId": "QS-NG-002", "enabled": false }]
                }
                """, TestContext.Current.CancellationToken);
            var overridden = await sensor.RunAsync(new SensorScanRequest(root), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(overridden.Findings, finding => finding.RuleId == "QS-NG-002");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-rule-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
