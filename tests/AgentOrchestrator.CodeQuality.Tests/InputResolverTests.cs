using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class InputResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-input-tests", Guid.NewGuid().ToString("N"));
    private readonly string global;

    public InputResolverTests()
    {
        global = Path.Combine(root, "global");
        Directory.CreateDirectory(global);
        Directory.CreateDirectory(Path.Combine(root, ".quality", "inputs"));
    }

    [Fact]
    public void Resolves_global_before_project_with_priority_and_applicability()
    {
        Write(global, "low.md", "global-low", "code", "file", 1, "low");
        Write(global, "high.md", "global-high", "all", "all", 9, "high");
        Write(Project, "project.md", "project", "code", "file", 100, "project");
        Write(Project, "security.md", "security", "security", "file", 200, "ignored");

        var result = new InputResolver().Resolve(root, "code", ReviewLevel.File, global);

        Assert.Equal(["global-high", "global-low", "project"], Authored(result).Select(input => input.Id));
        Assert.All(Authored(result).Take(2), input => Assert.Equal("global", input.Scope));
    }

    [Fact]
    public void Built_in_rules_precede_authored_guidelines_and_carry_their_own_version()
    {
        Write(global, "high.md", "global-high", "all", "all", 9, "high");

        var result = new InputResolver().Resolve(root, "code", ReviewLevel.File, global, adapter: "dotnet");

        var rules = result.Inputs.Where(input => input.Scope == "built-in").ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.StartsWith("QS-", rule.Id, StringComparison.Ordinal));
        Assert.All(rules, rule => Assert.NotEqual(ReviewInput.Unversioned, rule.Version));
        Assert.All(rules, rule => Assert.StartsWith("rule-library:", rule.Source, StringComparison.Ordinal));
        Assert.DoesNotContain(rules, rule => rule.Id.StartsWith("QS-NG-", StringComparison.Ordinal));
        Assert.Equal("global-high", result.Inputs[^1].Id);
        Assert.Equal(ReviewInput.Unversioned, result.Inputs[^1].Version);
    }

    [Fact]
    public void An_adapter_selects_its_own_technology_and_the_language_independent_rules()
    {
        var dotnet = new InputResolver().Resolve(root, "code", ReviewLevel.File, adapter: "dotnet");
        var angular = new InputResolver().Resolve(root, "code", ReviewLevel.File, adapter: "angular");
        var everything = new InputResolver().Resolve(root, "code", ReviewLevel.File);

        Assert.All(dotnet.Inputs, input => Assert.DoesNotContain("QS-NG-", input.Id, StringComparison.Ordinal));
        Assert.All(angular.Inputs, input => Assert.DoesNotContain("QS-CS-", input.Id, StringComparison.Ordinal));
        Assert.Contains(everything.Inputs, input => input.Id.StartsWith("QS-CS-", StringComparison.Ordinal));
        Assert.Contains(everything.Inputs, input => input.Id.StartsWith("QS-NG-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_project_override_disables_a_rule_and_restates_its_severity()
    {
        Directory.CreateDirectory(Path.Combine(root, ".quality", "rules"));
        File.WriteAllText(Path.Combine(root, ".quality", "rules", "overrides.json"),
            """
            {
              "schemaVersion": 1,
              "overrides": [
                { "id": "QS-CS-004", "enabled": false, "reason": "This repository has no test project." },
                { "id": "QS-CS-003", "severity": "critical", "reason": "Cancellation defects caused two outages." }
              ]
            }
            """);

        var result = new InputResolver().Resolve(root, "code", ReviewLevel.File, adapter: "dotnet");

        Assert.DoesNotContain(result.Inputs, input => input.Id == "QS-CS-004");
        var raised = Assert.Single(result.Inputs, input => input.Id == "QS-CS-003");
        Assert.Contains("critical severity", raised.Content, StringComparison.Ordinal);
        Assert.Contains("Cancellation defects caused two outages.", raised.Content, StringComparison.Ordinal);
        Assert.Equal(result.Inputs.Max(input => input.Priority), raised.Priority);
    }

    [Fact]
    public void Rules_that_do_not_fit_the_budget_are_reported_as_omitted()
    {
        var full = new InputResolver().Resolve(root, "code", ReviewLevel.File, adapter: "dotnet");
        var firstRule = full.Inputs[0];

        var result = new InputResolver().Resolve(root, "code", ReviewLevel.File,
            budgetCharacters: firstRule.Content.Length + 10, adapter: "dotnet");

        Assert.Equal(firstRule.Content, result.Inputs[0].IncludedContent);
        Assert.False(result.Complete);
        Assert.Contains(result.Omissions, omission => omission.Id == result.Inputs[1].Id &&
            omission.Reason == "truncated-to-budget" && omission.Source == "rule-library:" + result.Inputs[1].Id);
        Assert.Contains(result.Omissions, omission => omission.Reason == "budget-exhausted");
    }

    [Fact]
    public void Project_input_overrides_global_by_id()
    {
        Write(global, "rules.md", "rules", "all", "all", 10, "global body");
        Write(Project, "rules.md", "rules", "code", "file", 1, "project body");

        var result = new InputResolver().Resolve(root, "code", ReviewLevel.File, global);

        var input = Assert.Single(Authored(result));
        Assert.Equal("project", input.Scope);
        Assert.Equal("project body", input.Content);
        Assert.Contains(result.Omissions, omission => omission.Id == "rules" && omission.Reason == "overridden-by-project");
        Assert.True(result.Complete);
    }

    [Fact]
    public void Reports_partial_and_fully_omitted_content_when_budget_is_exhausted()
    {
        Write(global, "first.md", "first", "all", "all", 2, "123456");
        Write(global, "second.md", "second", "all", "all", 1, "abcdef");
        // Named rules are resolved first and spend the budget before authored guidelines do, so the
        // budget under test here is what remains after them.
        var ruleCharacters = new InputResolver().Resolve(root, "performance", ReviewLevel.File, global)
            .Inputs.Where(input => input.Scope == "built-in").Sum(input => input.Content.Length);

        var result = new InputResolver().Resolve(root, "performance", ReviewLevel.File, global, ruleCharacters + 8);
        var authored = Authored(result);

        Assert.Equal("123456", authored[0].IncludedContent);
        Assert.Equal("ab", authored[1].IncludedContent);
        Assert.True(authored[1].Truncated);
        Assert.Contains(result.Omissions, omission => omission.Id == "second" && omission.Reason == "truncated-to-budget" && omission.OmittedCharacters == 4);
        Assert.False(result.Complete);
    }

    [Fact]
    public void Ui_store_writes_a_valid_editable_file_that_the_resolver_uses()
    {
        var store = new GuidelineStore();
        var created = store.Create(root, new GuidelineDraft("api-boundaries", true, 42, ["code"], ["file"], "Validate boundary input."));

        var resolved = new InputResolver().Resolve(root, "code", ReviewLevel.File);

        Assert.Equal("api-boundaries.md", created.FileName);
        Assert.Equal("Validate boundary input.", Assert.Single(Authored(resolved)).IncludedContent);
        Assert.Contains("enabled: true", File.ReadAllText(Path.Combine(Project, created.FileName)), StringComparison.Ordinal);

        store.Update(root, created.Id, new GuidelineDraft(created.Id, false, 42, ["code"], ["file"], created.Content));
        Assert.Empty(Authored(new InputResolver().Resolve(root, "code", ReviewLevel.File)));
    }

    private string Project => Path.Combine(root, ".quality", "inputs");

    /// <summary>The file-authored guidelines, without the built-in named rules resolved alongside them.</summary>
    private static ReviewInput[] Authored(ResolvedInputs resolved) =>
        resolved.Inputs.Where(input => input.Scope != "built-in").ToArray();

    private static void Write(string directory, string file, string id, string kinds, string levels, int priority, string body) =>
        File.WriteAllText(Path.Combine(directory, file), $"---\nid: {id}\nkinds: [{kinds}]\nlevels: [{levels}]\npriority: {priority}\n---\n{body}\n");

    public void Dispose()
    {
        TemporaryDirectory.Delete(root);
    }
}
