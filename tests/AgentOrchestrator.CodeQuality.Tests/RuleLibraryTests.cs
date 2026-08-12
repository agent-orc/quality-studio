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
    public void Embedded_library_has_complete_language_seed_sets_and_stable_ids()
    {
        var rules = new RuleLibrary().Rules;

        Assert.Equal(9, rules.Count);
        Assert.Equal(rules.Count, rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(rules, rule => rule.Id == "QS-NG-002" && rule.DefaultOn && rule.DeterministicCheck is not null);
        Assert.Contains(rules, rule => rule.Id == "QS-NG-003" && rule.DefaultOn);
        Assert.Contains(rules, rule => rule.Id == "QS-CS-003" && rule.DefaultOn && rule.DeterministicCheck is not null);
        Assert.All(rules, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Statement));
            Assert.False(string.IsNullOrWhiteSpace(rule.Rationale));
            Assert.False(string.IsNullOrWhiteSpace(rule.BadExample.Code));
            Assert.False(string.IsNullOrWhiteSpace(rule.GoodExample.Code));
            Assert.NotEmpty(rule.History);
        });
    }

    [Fact]
    public void Rule_and_repository_configuration_files_validate_against_their_schemas()
    {
        var repository = RepositoryTestContext.FindRepositoryRoot();
        var ruleSchema = JsonSchema.FromText(File.ReadAllText(
            Path.Combine(repository, "schemas", "quality-rule.v1.schema.json")));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(repository, "rules"), "*.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var result = ruleSchema.Evaluate(document.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, $"{Path.GetRelativePath(repository, path)}: {result}");
        }

        var configurationSchema = JsonSchema.FromText(File.ReadAllText(
            Path.Combine(repository, "schemas", "rule-configuration.v1.schema.json")));
        using var configuration = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repository, ".quality", "rules.json")));
        var configurationResult = configurationSchema.Evaluate(configuration.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(configurationResult.IsValid, configurationResult.ToString());
    }

    [Fact]
    public void Repository_config_disables_enables_and_adjusts_individual_rules()
    {
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, "Thing.cs"), "public sealed class Thing { }\n");
        File.WriteAllText(Path.Combine(root, ".quality", "rules.json"), """
            {
              "$schema": "https://quality.studio/schemas/rule-configuration.v1.schema.json",
              "schemaVersion": 1,
              "rules": {
                "QS-CS-002": { "severity": "high" },
                "QS-CS-003": { "enabled": false },
                "QS-CS-004": { "enabled": true }
              }
            }
            """);

        var resolved = new RuleLibrary().Resolve(root, "code", ["Thing.cs"]);

        Assert.Equal(["QS-CS-001", "QS-CS-002", "QS-CS-004"],
            resolved.Rules.Select(rule => rule.Definition.Id));
        Assert.Equal(FindingSeverity.High,
            resolved.Rules.Single(rule => rule.Definition.Id == "QS-CS-002").Severity);
        Assert.Equal(RuleConfigurationPath, resolved.ConfigurationPath);
    }

    [Fact]
    public async Task Deterministic_wave_uses_named_ids_and_honors_language_scope()
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "angular.json"), "{}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "bad.css"),
            ".badge { margin: 12px; color: #c00; }\n--space-3: 12px;\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "styles.css"),
            ":root { --space-3: 12px; --danger: #c00; }\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "bad.html"),
            "<div style=\"margin: 12px\">Bad</div>\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Bad.cs"),
            "public Task RunAsync() => LoadAsync().Result;\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Fixture.cs"),
            "const string Example = \"LoadAsync().Result\"; // Wait()\n", TestContext.Current.CancellationToken);

        var result = await new RulePrecheckSensor().RunAsync(
            new SensorScanRequest(root), TestContext.Current.CancellationToken);

        Assert.True(result.Available);
        Assert.Contains(result.Findings, finding => finding.RuleId == "QS-NG-002");
        Assert.Contains(result.Findings, finding => finding.RuleId == "QS-NG-004");
        Assert.Contains(result.Findings, finding => finding.RuleId == "QS-CS-003");
        Assert.All(result.Findings, finding =>
        {
            Assert.Equal(FindingSourceKind.Deterministic, finding.Source!.Kind);
            Assert.Equal(RulePrecheckSensor.SensorId, finding.Source.SensorId);
        });
        Assert.DoesNotContain(result.Findings, finding =>
            finding.Locations[0].Path == "src/bad.css" && finding.Locations[0].Range!.Start.Line == 2);
        Assert.DoesNotContain(result.Findings, finding => finding.Locations[0].Path == "src/styles.css");
        Assert.DoesNotContain(result.Findings, finding => finding.Locations[0].Path == "src/Fixture.cs");

        var securityResult = await new RulePrecheckSensor().RunAsync(
            new SensorScanRequest(root, Configuration: new Dictionary<string, string>
            {
                ["reviewKind"] = "security",
            }), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(securityResult.Findings, finding => finding.RuleId == "QS-CS-003");
    }

    [Fact]
    public void Unknown_override_is_a_visible_configuration_error()
    {
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, "Thing.cs"), "public sealed class Thing { }\n");
        File.WriteAllText(Path.Combine(root, ".quality", "rules.json"), """
            {
              "$schema": "https://quality.studio/schemas/rule-configuration.v1.schema.json",
              "schemaVersion": 1,
              "rules": { "QS-CS-999": { "enabled": false } }
            }
            """);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new RuleLibrary().Resolve(root, "code", ["Thing.cs"]));

        Assert.Contains("unknown rule", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private const string RuleConfigurationPath = ".quality/rules.json";

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
