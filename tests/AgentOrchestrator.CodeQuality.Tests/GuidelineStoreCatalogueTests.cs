using AgentOrchestrator.CodeQuality;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class GuidelineStoreCatalogueTests
{
    [Fact]
    public void Catalogue_has_one_entry_per_rule_in_the_library()
    {
        Assert.Equal(RuleLibrary.Default.Rules.Count, GuidelineStore.Catalogue.Count);
        Assert.All(GuidelineStore.Catalogue, entry => Assert.NotNull(RuleLibrary.Default.Find(entry.Id.ToUpperInvariant())));
    }

    [Fact]
    public void Catalogue_entry_ids_are_lowercase_and_match_the_guideline_id_pattern()
    {
        Assert.All(GuidelineStore.Catalogue, entry =>
        {
            Assert.Equal(entry.Id, entry.Id.ToLowerInvariant());
            Assert.Matches("^[a-z0-9][a-z0-9._-]{1,127}$", entry.Id);
        });
    }

    [Fact]
    public void Installing_a_catalogue_rule_writes_a_resolver_compatible_guideline()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-catalogue-install-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new GuidelineStore();
            var installed = store.Install(root, "qs-cs-001");

            Assert.Equal("qs-cs-001", installed.Id);
            var resolved = new InputResolver().Resolve(root, "code", ReviewLevel.File);
            Assert.Contains(resolved.Inputs, input => input.Id == "qs-cs-001" && input.Scope == "project");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
