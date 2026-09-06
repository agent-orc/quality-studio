using QualityStudio.Api;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ProjectDataStorageTests
{
    [Fact]
    public void Resolve_DefaultsToOperatingSystemLocalApplicationData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var baseRoot = ProjectDataRootResolver.ResolveBaseRoot(null, "/unused/content-root");

        Assert.Equal(Path.Combine(local, "QualityStudio", "projects"), baseRoot);
    }

    [Fact]
    public void Resolve_UsesConfiguredBaseAndProjectIdentity()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "quality-data-root-tests", Guid.NewGuid().ToString("N"));

        var baseRoot = ProjectDataRootResolver.ResolveBaseRoot("../runtime-data", contentRoot);
        var projectRoot = ProjectDataRootResolver.ResolveProjectRoot(baseRoot, "payments-api");

        Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "../runtime-data", "payments-api")), projectRoot);
    }

    [Fact]
    public void Migrate_MovesRootAndNestedQualityTreesOnce()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "quality-data-migration-tests", Guid.NewGuid().ToString("N"));
        var checkout = Path.Combine(fixtureRoot, "checkout");
        var dataRoot = Path.Combine(fixtureRoot, "data");
        Directory.CreateDirectory(Path.Combine(checkout, ".quality", "findings"));
        Directory.CreateDirectory(Path.Combine(checkout, "src", ".quality", "reviews", "files"));
        File.WriteAllText(Path.Combine(checkout, ".quality", "findings", "state.json"), "{}\n");
        File.WriteAllText(Path.Combine(checkout, "src", ".quality", "reviews", "files",
            "file.example.review-meta.code.json"), "{}\n");
        try
        {
            var first = ProjectDataMigrator.Migrate(checkout, dataRoot);
            var second = ProjectDataMigrator.Migrate(checkout, dataRoot);

            Assert.True(first.Migrated);
            Assert.Equal(2, first.FilesMoved);
            Assert.False(second.Migrated);
            Assert.True(File.Exists(Path.Combine(dataRoot, "findings", "state.json")));
            Assert.True(File.Exists(Path.Combine(dataRoot, "reviews", "files",
                "file.example.review-meta.code.json")));
            Assert.Empty(Directory.EnumerateDirectories(checkout, ".quality", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(fixtureRoot, true);
        }
    }
}
