using AgentOrchestrator.CodeQuality;
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

        Assert.Equal(["global-high", "global-low", "project"], result.Inputs.Select(input => input.Id));
        Assert.All(result.Inputs.Take(2), input => Assert.Equal("global", input.Scope));
    }

    [Fact]
    public void Project_input_overrides_global_by_id()
    {
        Write(global, "rules.md", "rules", "all", "all", 10, "global body");
        Write(Project, "rules.md", "rules", "code", "file", 1, "project body");

        var result = new InputResolver().Resolve(root, "code", ReviewLevel.File, global);

        var input = Assert.Single(result.Inputs);
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

        var result = new InputResolver().Resolve(root, "performance", ReviewLevel.File, global, 8);

        Assert.Equal("123456", result.Inputs[0].IncludedContent);
        Assert.Equal("ab", result.Inputs[1].IncludedContent);
        Assert.True(result.Inputs[1].Truncated);
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
        Assert.Equal("Validate boundary input.", Assert.Single(resolved.Inputs).IncludedContent);
        Assert.Contains("enabled: true", File.ReadAllText(Path.Combine(Project, created.FileName)), StringComparison.Ordinal);

        store.Update(root, created.Id, new GuidelineDraft(created.Id, false, 42, ["code"], ["file"], created.Content));
        Assert.Empty(new InputResolver().Resolve(root, "code", ReviewLevel.File).Inputs);
    }

    private string Project => Path.Combine(root, ".quality", "inputs");

    private static void Write(string directory, string file, string id, string kinds, string levels, int priority, string body) =>
        File.WriteAllText(Path.Combine(directory, file), $"---\nid: {id}\nkinds: [{kinds}]\nlevels: [{levels}]\npriority: {priority}\n---\n{body}\n");

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
