using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RuleLibraryTests
{
    [Fact]
    public void Embedded_catalogue_has_stable_default_on_seed_sets()
    {
        var rules = new RuleLibrary().List();

        Assert.Equal(9, rules.Count);
        Assert.Equal(5, rules.Count(rule => rule.Language == "angular"));
        Assert.Equal(4, rules.Count(rule => rule.Language == "csharp"));
        Assert.All(rules, rule =>
        {
            Assert.StartsWith("QS-", rule.Id, StringComparison.Ordinal);
            Assert.True(rule.DefaultEnabled);
            Assert.Contains(rule.History, entry => entry.Version == rule.Version);
            Assert.False(string.IsNullOrWhiteSpace(rule.Examples.Good));
            Assert.False(string.IsNullOrWhiteSpace(rule.Examples.Bad));
        });
        Assert.Contains(rules, rule => rule.Id == "QS-NG-002" && rule.Category == "design-tokens");
        Assert.Contains(rules, rule => rule.Id == "QS-NG-003" && rule.Category == "component-reuse");
    }

    [Fact]
    public void Repository_json_overrides_individual_rule_enablement_and_severity()
    {
        using var root = new TemporaryRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, ".quality"));
        File.WriteAllText(Path.Combine(root.Path, ".quality", "rules.json"), """
            {
              "$schema": "https://quality.studio/schemas/quality-rules-config.v1.schema.json",
              "schemaVersion": 1,
              "overrides": {
                "QS-NG-002": { "severity": "medium" },
                "QS-NG-003": { "enabled": false }
              }
            }
            """);

        var resolution = new RuleLibrary().Resolve(root.Path, "code", ["frontend/src/app/card/card.css"]);

        Assert.Equal("medium", resolution.Rules.Single(rule => rule.Definition.Id == "QS-NG-002").Severity);
        Assert.DoesNotContain(resolution.Rules, rule => rule.Definition.Id == "QS-NG-003");
        Assert.Contains("QS-NG-002", resolution.PromptContext, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_override_is_rejected_instead_of_silently_ignored()
    {
        using var root = new TemporaryRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, ".quality"));
        File.WriteAllText(Path.Combine(root.Path, ".quality", "rules.json"), """
            {
              "$schema": "https://quality.studio/schemas/quality-rules-config.v1.schema.json",
              "schemaVersion": 1,
              "overrides": { "QS-NG-999": { "enabled": false } }
            }
            """);

        Assert.Throws<RuleFormatException>(() =>
            new RuleLibrary().Resolve(root.Path, "code", ["frontend/src/app/card/card.ts"]));
    }

    [Fact]
    public void Repository_config_and_rule_files_match_their_json_schemas()
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        var ruleSchema = JsonSchema.FromText(File.ReadAllText(
            Path.Combine(repositoryRoot, "schemas", "quality-rule.v1.schema.json")));
        var configurationSchema = JsonSchema.FromText(File.ReadAllText(
            Path.Combine(repositoryRoot, "schemas", "quality-rules-config.v1.schema.json")));
        using var configuration = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, ".quality", "rules.json")));
        Assert.Equal(1, configuration.RootElement.GetProperty("schemaVersion").GetInt32());
        var configurationValidation = configurationSchema.Evaluate(configuration.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(configurationValidation.IsValid, configurationValidation.ToString());
        foreach (var path in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "rules"), "*.json", SearchOption.AllDirectories))
        {
            using var rule = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(Path.GetFileNameWithoutExtension(path), rule.RootElement.GetProperty("id").GetString());
            var validation = ruleSchema.Evaluate(rule.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(validation.IsValid, $"{path}: {validation}");
        }
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quality-rule-library-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => TestDirectory.Delete(Path);
    }
}

public sealed class RulePrecheckSensorTests
{
    [Fact]
    public async Task Static_wave_emits_named_rule_ids_and_honours_overrides()
    {
        using var root = new TemporaryRoot();
        var app = Path.Combine(root.Path, "frontend", "src", "app", "card");
        Directory.CreateDirectory(app);
        await File.WriteAllTextAsync(Path.Combine(app, "card.css"),
            ".card { padding: 13px; color: var(--studio-fg); }\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(app, "card.html"),
            "<div style=\"padding: 8px\">Card</div>\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(app, "card.ts"),
            "@Component({ selector: 'qs-card', templateUrl: './card.html' })\nexport class Card {}\n",
            TestContext.Current.CancellationToken);
        var serviceDirectory = Path.Combine(root.Path, "src");
        Directory.CreateDirectory(serviceDirectory);
        await File.WriteAllTextAsync(Path.Combine(serviceDirectory, "Blocking.cs"),
            "internal sealed class Blocking { void Run(Task work) => work.Wait(); }\n",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(root.Path, ".quality"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, ".quality", "rules.json"), """
            {
              "$schema": "https://quality.studio/schemas/quality-rules-config.v1.schema.json",
              "schemaVersion": 1,
              "overrides": { "QS-NG-005": { "enabled": false } }
            }
            """, TestContext.Current.CancellationToken);

        var result = await new RulePrecheckSensor().RunAsync(
            new SensorScanRequest(root.Path), TestContext.Current.CancellationToken);

        Assert.True(result.Available);
        Assert.Contains(result.Findings, finding => finding.RuleId == "QS-NG-002");
        Assert.Contains(result.Findings, finding => finding.RuleId == "QS-NG-004");
        Assert.Contains(result.Findings, finding => finding.RuleId == "QS-CS-003");
        Assert.DoesNotContain(result.Findings, finding => finding.RuleId == "QS-NG-005");
        Assert.All(result.Findings, finding =>
        {
            Assert.Equal(FindingSourceKind.Deterministic, finding.Source!.Kind);
            Assert.Equal("quality-rules", finding.Source.SensorId);
        });
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quality-rule-precheck-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => TestDirectory.Delete(Path);
    }
}
