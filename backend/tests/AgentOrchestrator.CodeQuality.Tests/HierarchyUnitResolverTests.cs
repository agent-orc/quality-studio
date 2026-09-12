using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class HierarchyUnitResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"quality-studio-{Guid.NewGuid():N}");

    [Fact]
    public void Resolving_a_unit_populates_the_shared_hierarchy_cache()
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "Demo"));
        File.WriteAllText(Path.Combine(root, "Demo.slnx"),
            "<Solution><Project Path=\"src/Demo/Demo.csproj\" /></Solution>");
        File.WriteAllText(Path.Combine(root, "src", "Demo", "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root, "src", "Demo", "Greeter.cs"),
            "namespace Demo.Greetings; public sealed class Greeter\n{\n    public string SayHello() => \"hello\";\n}");
        var cache = new RepositoryHierarchyCache();
        var resolver = new HierarchyUnitResolver(cache);

        var unitId = resolver.ResolveUnitId(root, "src/Demo/Greeter.cs", ReviewLevel.File);

        Assert.StartsWith("qs-v1/dotnet/file/", unitId!, StringComparison.Ordinal);
        // A second question about the same unchanged repository must not rebuild the hierarchy.
        Assert.True(cache.GetMeasured(root).CacheHit);
        Assert.Equal(unitId, resolver.ResolveUnitId(root, "src/Demo/Greeter.cs", ReviewLevel.File));
        Assert.Equal(unitId, resolver.FileUnitsByPath(root)["src/Demo/Greeter.cs"].Id);
    }

    [Fact]
    public void A_path_the_hierarchy_does_not_derive_has_no_unit_id()
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "present.cs"), "class Present {}");

        var resolver = new HierarchyUnitResolver(new RepositoryHierarchyCache());

        Assert.Null(resolver.ResolveUnitId(root, "src/absent.cs", ReviewLevel.File));
        Assert.DoesNotContain("src/absent.cs", resolver.FileUnitsByPath(root));
    }

    public void Dispose() => TemporaryDirectory.Delete(root);
}
