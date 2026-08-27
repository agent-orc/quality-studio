using System.Reflection;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RuleLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-rule-tests", Guid.NewGuid().ToString("N"));

    public RuleLibraryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Built_in_library_has_unique_complete_language_seed_sets()
    {
        var rules = RuleLibrary.BuiltIn.List();

        Assert.Equal(9, rules.Count);
        Assert.Equal(rules.Count, rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, rules.Count(rule => rule.Language == "angular"));
        Assert.Equal(4, rules.Count(rule => rule.Language == "csharp"));
        Assert.All(rules, rule =>
        {
            Assert.NotEmpty(rule.Statement);
            Assert.NotEmpty(rule.Rationale);
            Assert.Contains("```", rule.BadExample, StringComparison.Ordinal);
            Assert.Contains("```", rule.GoodExample, StringComparison.Ordinal);
            Assert.Contains("2026-08-12", rule.ChangeHistory, StringComparison.Ordinal);
        });
        Assert.True(rules.Single(rule => rule.Id == "QS-NG-002").DefaultOn);
        Assert.True(rules.Single(rule => rule.Id == "QS-NG-003").DefaultOn);
    }

    [Fact]
    public void Resolve_applies_defaults_repository_override_and_ordered_scope_override()
    {
        WriteConfiguration("""
            {
              "$schema": "https://agent-orchestrator.dev/quality/schemas/rule-config.v1.schema.json",
              "version": 1,
              "rules": {
                "QS-NG-002": { "severity": "low" },
                "QS-NG-005": { "enabled": false }
              },
              "scopes": [
                { "paths": ["legacy/**"], "rules": { "QS-NG-002": { "enabled": false } } },
                { "paths": ["legacy/modern/**"], "rules": { "QS-NG-002": { "enabled": true, "severity": "high" } } }
              ]
            }
            """);

        var regular = RuleLibrary.BuiltIn.Resolve(root, ["frontend/card.css"], "code");
        var legacy = RuleLibrary.BuiltIn.Resolve(root, ["legacy/card.css"], "code");
        var modern = RuleLibrary.BuiltIn.Resolve(root, ["legacy/modern/card.css"], "code");

        Assert.Equal(FindingSeverity.Low, regular.Single(rule => rule.Rule.Id == "QS-NG-002").Severity);
        Assert.DoesNotContain(regular, rule => rule.Rule.Id == "QS-NG-005");
        Assert.DoesNotContain(legacy, rule => rule.Rule.Id == "QS-NG-002");
        Assert.Equal(FindingSeverity.High, modern.Single(rule => rule.Rule.Id == "QS-NG-002").Severity);
    }

    [Fact]
    public void Configuration_rejects_unknown_rule_instead_of_silently_weakening_policy()
    {
        WriteConfiguration("""
            {
              "$schema": "https://agent-orchestrator.dev/quality/schemas/rule-config.v1.schema.json",
              "version": 1,
              "rules": { "QS-NG-999": { "enabled": false } }
            }
            """);

        var exception = Assert.Throws<RuleConfigurationException>(() =>
            RuleLibrary.BuiltIn.Resolve(root, ["feature.css"], "code"));

        Assert.Contains("unknown rule", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repository_configuration_matches_published_json_schema()
    {
        var repositoryRoot = RepositoryRoot();
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(repositoryRoot, "schemas", "rule-config.v1.schema.json")));
        using var instance = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, ".quality", "rules.json")));

        var result = schema.Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void Input_resolver_injects_named_rules_and_hashes_effective_override()
    {
        var resolver = new InputResolver();
        var baseline = resolver.Resolve(root, "code", ReviewLevel.File, subjectPaths: ["feature.css"]);
        WriteConfiguration("""
            {
              "$schema": "https://agent-orchestrator.dev/quality/schemas/rule-config.v1.schema.json",
              "version": 1,
              "rules": { "QS-NG-002": { "severity": "low" } }
            }
            """);
        var adjusted = resolver.Resolve(root, "code", ReviewLevel.File, subjectPaths: ["feature.css"]);

        var input = Assert.Single(adjusted.Inputs, value => value.Id == "QS-NG-002");
        Assert.Equal("rule-library", input.Scope);
        Assert.Equal("1.0.0", input.Version);
        Assert.Contains("Severity: low", input.IncludedContent, StringComparison.Ordinal);
        Assert.NotEqual(baseline.EffectiveHash("prompt"), adjusted.EffectiveHash("prompt"));
    }

    private void WriteConfiguration(string json)
    {
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, ".quality", "rules.json"), json);
    }

    private static string RepositoryRoot() =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "QualityStudioRepositoryRoot").Value!;

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}

public sealed class RulePrecheckSensorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-rule-sensor-tests", Guid.NewGuid().ToString("N"));

    public RulePrecheckSensorTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Emits_named_deterministic_findings_for_angular_and_dotnet_subset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(root, "frontend"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "frontend", "card.css"),
            ".card { padding: 13px; color: #123456; }\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "frontend", "card.html"),
            "<div [style.width.px]=\"width()\">Card</div>\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "frontend", "card.ts"),
            "@Component({ template: `<div>Card</div>` })\nexport class Card {}\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Service.cs"),
            "internal sealed class Service { object Read(Task<object> task) => task.Result; string Hash(Item item) => item.ResultHash; }\n", cancellationToken);

        var result = await new RulePrecheckSensor().RunAsync(new SensorScanRequest(root), cancellationToken);

        Assert.True(result.Available);
        Assert.Contains(result.Findings, finding => finding.RuleId == "QS-NG-002" && finding.Severity == FindingSeverity.High);
        Assert.Contains(result.Findings, finding => finding.RuleId == "QS-NG-004" && finding.Locations[0].Path == "frontend/card.html");
        Assert.Contains(result.Findings, finding => finding.RuleId == "QS-NG-004" && finding.Locations[0].Path == "frontend/card.ts");
        Assert.Contains(result.Findings, finding => finding.RuleId == "QS-DN-003");
        Assert.Single(result.Findings, finding => finding.RuleId == "QS-DN-003");
        Assert.All(result.Findings, finding => Assert.Equal("qs-rules", finding.Source?.SensorId));
    }

    [Fact]
    public async Task Disabled_rule_is_not_enforced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        await File.WriteAllTextAsync(Path.Combine(root, ".quality", "rules.json"), """
            {
              "$schema": "https://agent-orchestrator.dev/quality/schemas/rule-config.v1.schema.json",
              "version": 1,
              "rules": { "QS-NG-002": { "enabled": false } }
            }
            """, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "card.css"), ".card { padding: 13px; }\n", cancellationToken);

        var result = await new RulePrecheckSensor().RunAsync(new SensorScanRequest(root), cancellationToken);

        Assert.DoesNotContain(result.Findings, finding => finding.RuleId == "QS-NG-002");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
