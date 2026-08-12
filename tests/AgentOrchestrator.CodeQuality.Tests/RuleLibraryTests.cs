using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RuleLibraryTests
{
    [Fact]
    public void Embedded_catalogue_contains_valid_Angular_and_DotNet_seed_sets()
    {
        var rules = new RuleLibrary().List();

        Assert.Equal(9, rules.Count);
        Assert.Equal(5, rules.Count(rule => rule.Id.StartsWith("QS-NG-", StringComparison.Ordinal)));
        Assert.Equal(4, rules.Count(rule => rule.Id.StartsWith("QS-CS-", StringComparison.Ordinal)));
        Assert.Equal(rules.Count, rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rules, rule =>
        {
            Assert.Matches("^QS-[A-Z][A-Z0-9]+-[0-9]{3}$", rule.Id);
            Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", rule.Version);
            Assert.True(rule.DefaultEnabled);
            Assert.NotEmpty(rule.Statement);
            Assert.NotEmpty(rule.Rationale);
            Assert.Contains("```", rule.BadExample, StringComparison.Ordinal);
            Assert.Contains("```", rule.GoodExample, StringComparison.Ordinal);
            Assert.NotEmpty(rule.ChangeHistory);
            Assert.NotEmpty(rule.References);
        });

        var tokens = Assert.Single(rules, rule => rule.Id == "QS-NG-002");
        Assert.Equal("quality-rules/design-token-literals", tokens.DeterministicCheck);
        Assert.Contains("central design tokens", tokens.Statement, StringComparison.OrdinalIgnoreCase);
        var reuse = Assert.Single(rules, rule => rule.Id == "QS-NG-003");
        Assert.Contains("standard component", reuse.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Checked_in_rule_tree_matches_the_embedded_catalogue()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var checkedIn = RuleLibrary.LoadDirectory(Path.Combine(root, "rules")).List();
        var embedded = new RuleLibrary().List();

        Assert.Equal(embedded.Select(rule => rule.Id), checkedIn.Select(rule => rule.Id));
        Assert.Equal(embedded.Select(rule => rule.Version), checkedIn.Select(rule => rule.Version));
        Assert.Equal(embedded.Select(rule => rule.Statement), checkedIn.Select(rule => rule.Statement));
    }

    [Fact]
    public void Resolve_selects_rules_by_kind_level_and_subject_extension()
    {
        var library = new RuleLibrary();

        var angularStyles = library.Resolve(["frontend/src/card.scss"], "code", ReviewLevel.File);
        var angularComponent = library.Resolve(["frontend/src/card.ts"], "code", ReviewLevel.File);
        var dotnetPerformance = library.Resolve(["src/Worker.cs"], "performance", ReviewLevel.File);

        Assert.Equal(["QS-NG-002"], angularStyles.Select(rule => rule.Id));
        Assert.Equal(
            ["QS-NG-001", "QS-NG-003", "QS-NG-004", "QS-NG-005"],
            angularComponent.Select(rule => rule.Id));
        Assert.Equal(["QS-CS-003"], dotnetPerformance.Select(rule => rule.Id));
    }

    [Fact]
    public void Parse_rejects_a_rule_missing_a_required_section()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var path = Path.Combine(root, "rules", "angular", "QS-NG-002.md");
        var invalid = File.ReadAllText(path).Replace("## Rationale", "## Why", StringComparison.Ordinal);

        var exception = Assert.Throws<RuleFormatException>(() =>
            RuleLibrary.Parse(invalid, "invalid.md"));

        Assert.Contains("Rationale", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Input_resolver_injects_named_rules_as_versioned_built_in_standards()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-rule-inputs-").FullName;
        try
        {
            var resolved = new InputResolver().Resolve(
                root, "code", ReviewLevel.File, subjectPaths: ["frontend/src/card.css"]);

            var input = Assert.Single(resolved.Inputs);
            Assert.Equal("QS-NG-002", input.Id);
            Assert.Equal("built-in", input.Scope);
            Assert.Equal("1.0.0", input.Version);
            Assert.Contains("[QS-NG-002]", resolved.NamedRules(), StringComparison.Ordinal);
            Assert.Contains("Autofixable: false", resolved.NamedRules(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Repository_config_disables_and_adjusts_exact_rules()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-rule-config-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".quality"));
            File.WriteAllText(Path.Combine(root, ".quality", "rules.json"), """
                {
                  "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
                  "schemaVersion": 1,
                  "rules": {
                    "QS-NG-002": { "enabled": false },
                    "QS-NG-004": { "severity": "high" }
                  }
                }
                """);

            var library = new RuleLibrary();
            var configuration = QualityRuleConfiguration.Load(root, library);
            var resolved = library.Resolve(
                ["frontend/card.css", "frontend/card.ts"], "code", ReviewLevel.File, configuration);

            Assert.DoesNotContain(resolved, rule => rule.Id == "QS-NG-002");
            Assert.Equal(FindingSeverity.High, Assert.Single(resolved, rule => rule.Id == "QS-NG-004").Severity);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Repository_config_rejects_unknown_rule_ids()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-rule-config-invalid-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".quality"));
            File.WriteAllText(Path.Combine(root, ".quality", "rules.json"), """
                {
                  "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
                  "schemaVersion": 1,
                  "rules": { "QS-NG-999": { "enabled": false } }
                }
                """);

            var exception = Assert.Throws<RuleConfigurationException>(() =>
                QualityRuleConfiguration.Load(root, new RuleLibrary()));

            Assert.Contains("unknown rule id 'QS-NG-999'", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Repository_config_sample_conforms_to_schema_and_runtime_contract()
    {
        var repository = RepositoryTestContext.FindRepositoryRoot();
        var schema = JsonSchema.FromText(File.ReadAllText(
            Path.Combine(repository, "schemas", "rule-config.v1.schema.json")));
        var sample = File.ReadAllText(Path.Combine(repository, "samples", "rule-config.v1.sample.json"));
        using var document = JsonDocument.Parse(sample);

        var evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.True(evaluation.IsValid, evaluation.ToString());

        var root = Directory.CreateTempSubdirectory("quality-studio-rule-schema-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".quality"));
            File.WriteAllText(Path.Combine(root, ".quality", "rules.json"), sample);
            _ = QualityRuleConfiguration.Load(root, new RuleLibrary());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public sealed class RulePrecheckSensorTests
{
    [Fact]
    public async Task Scan_reports_named_rule_ids_for_raw_styles_and_inline_templates()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-rule-sensor-").FullName;
        try
        {
            var source = Path.Combine(root, "frontend", "src");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "card.scss"), """
                :root { --local-space: 12px; --local-color: #fff; }
                .card { padding: 12px; color: var(--studio-fg); background-color: #fff; }
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(source, "card.ts"), """
                import { Component } from '@angular/core';
                @Component({
                  selector: 'app-card',
                  template: `<button>Save</button>`,
                  styleUrl: './card.scss',
                })
                export class Card {}
                """, TestContext.Current.CancellationToken);

            var result = await new RulePrecheckSensor().RunAsync(
                new SensorScanRequest(root, PersistMetadata: false),
                TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            Assert.Equal(3, result.Findings.Count);
            Assert.Equal(2, result.Findings.Count(finding => finding.RuleId == "QS-NG-002"));
            Assert.Single(result.Findings, finding => finding.RuleId == "QS-NG-004");
            Assert.All(result.Findings, finding =>
            {
                Assert.Equal(FindingSourceKind.Deterministic, finding.Source?.Kind);
                Assert.Equal(RulePrecheckSensor.SensorId, finding.Source?.SensorId);
                Assert.StartsWith("sha256:", finding.Fingerprint, StringComparison.Ordinal);
            });
            Assert.DoesNotContain(result.Findings,
                finding => finding.Description.Contains("--local", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Path_scan_is_confined_to_the_requested_subject()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-rule-sensor-path-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "selected.css"), ".a { gap: 8px; }",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "other.css"), ".b { gap: 12px; }",
                TestContext.Current.CancellationToken);

            var result = await new RulePrecheckSensor().RunAsync(
                new SensorScanRequest(root, SensorScope.Path, "selected.css", PersistMetadata: false),
                TestContext.Current.CancellationToken);

            var finding = Assert.Single(result.Findings);
            Assert.Equal("selected.css", finding.Locations[0].Path);
            Assert.Equal("QS-NG-002", finding.RuleId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Scan_honors_repository_enablement_and_severity_overrides()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-rule-sensor-config-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".quality"));
            await File.WriteAllTextAsync(Path.Combine(root, ".quality", "rules.json"), """
                {
                  "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
                  "schemaVersion": 1,
                  "rules": {
                    "QS-NG-002": { "enabled": false },
                    "QS-NG-004": { "severity": "high" }
                  }
                }
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "card.css"), ".card { gap: 12px; }",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "card.ts"), """
                @Component({ template: `<button>Save</button>` })
                export class Card {}
                """, TestContext.Current.CancellationToken);

            var result = await new RulePrecheckSensor().RunAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);

            var finding = Assert.Single(result.Findings);
            Assert.Equal("QS-NG-004", finding.RuleId);
            Assert.Equal(FindingSeverity.High, finding.Severity);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
