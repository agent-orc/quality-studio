using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Covers the .NET derivation rules described in <c>docs/hierarchy-derivation.md</c>: structural
/// solution and project parsing, MSBuild-faithful compile-item membership, and Roslyn syntax-based
/// namespace and function units.
/// </summary>
public sealed class RepositoryHierarchyDerivationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"quality-studio-derivation-{Guid.NewGuid():N}");

    [Fact]
    public void FileScopedNamespaceProducesOneNamespaceUnit()
    {
        CreateSolution();
        WriteSource("src/Demo/Greeter.cs", "namespace Demo.Greetings;\npublic sealed class Greeter { }\n");

        var module = SingleModule();

        Assert.Equal(["Demo.Greetings"], Namespaces(module));
    }

    [Fact]
    public void SeveralNamespacesInOneFileAliasTheSameCanonicalFileUnit()
    {
        CreateSolution();
        WriteSource("src/Demo/Two.cs", """
            namespace Demo.First { public sealed class A { } }
            namespace Demo.Second { public sealed class B { } }
            """);

        var module = SingleModule();
        var aliases = module.Children.SelectMany(ns => ns.Children).ToArray();

        Assert.Equal(["Demo.First", "Demo.Second"], Namespaces(module));
        Assert.Equal(2, aliases.Length);
        Assert.Single(aliases.Select(file => file.Id).Distinct(StringComparer.Ordinal));
        Assert.Single(Files(module));
    }

    [Fact]
    public void NestedAndDottedNamespaceDeclarationsUseTheirFullName()
    {
        CreateSolution();
        WriteSource("src/Demo/Nested.cs", """
            namespace Outer { namespace Inner { public sealed class A { } } }
            """);
        WriteSource("src/Demo/Dotted.cs", "namespace Outer.Inner { public sealed class B { } }\n");
        WriteSource("src/Demo/Empty.cs", "namespace Outer.Empty { }\n");

        var module = SingleModule();

        Assert.Equal(["Outer.Empty", "Outer.Inner"], Namespaces(module));
        Assert.Equal(2, Files(module).Count(file => file.Name is "Nested.cs" or "Dotted.cs"));
    }

    [Fact]
    public void TypesWithoutANamespaceUseTheGlobalUnit()
    {
        CreateSolution();
        WriteSource("src/Demo/Loose.cs", "public sealed class Loose { }\n");

        var module = SingleModule();

        Assert.Equal(["<global>"], Namespaces(module));
    }

    [Fact]
    public void PartialTypeAcrossFilesKeepsOneNamespaceUnitAndTwoFileUnits()
    {
        CreateSolution();
        WriteSource("src/Demo/Part1.cs", "namespace Demo;\npublic partial class Split { public void One() { } }\n");
        WriteSource("src/Demo/Part2.cs", "namespace Demo;\npublic partial class Split { public void Two() { } }\n");

        var module = SingleModule();
        var ns = Assert.Single(module.Children);

        Assert.Equal("Demo", ns.Name);
        Assert.Equal(["src/Demo/Part1.cs", "src/Demo/Part2.cs"], ns.Children.Select(file => file.Path).ToArray());
    }

    [Fact]
    public void OverloadsBecomeSeparateFunctionUnits()
    {
        CreateSolution();
        WriteSource("src/Demo/Overloads.cs", """
            namespace Demo;
            public sealed class Calculator
            {
                public int Add(int left, int right) => left + right;
                public double Add(double left, double right) => left + right;
                public int Add() => 0;
            }
            """);

        var functions = Functions(SingleModule()).ToArray();

        Assert.Equal(["Add()", "Add(double, double)", "Add(int, int)"],
            functions.Select(function => function.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, functions.Select(function => function.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ConstructorsOperatorsAndAccessorsAreFunctionUnitsButLocalFunctionsAreNot()
    {
        CreateSolution();
        WriteSource("src/Demo/Money.cs", """
            namespace Demo;
            public sealed class Money
            {
                public Money(int amount) { Amount = amount; }
                public int Amount { get; set; }
                public int Doubled => Amount * 2;
                public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount);
                public static explicit operator int(Money value) => value.Amount;
                public void Report()
                {
                    int Helper() => 1;
                    _ = Helper();
                }
            }
            """);

        var names = Functions(SingleModule()).Select(function => function.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [
                "#ctor(int)", "Report()", "get_Amount()", "get_Doubled()",
                "op_Addition(Money, Money)", "op_Explicit(Money)", "set_Amount(int)",
            ],
            names);
        Assert.DoesNotContain("Helper()", names);
    }

    [Fact]
    public void NestedProjectOwnsItsOwnSources()
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "Outer", "Inner"));
        File.WriteAllText(Path.Combine(root, "Demo.slnx"),
            """
            <Solution>
              <Project Path="src/Outer/Outer.csproj" />
              <Project Path="src/Outer/Inner/Inner.csproj" />
            </Solution>
            """);
        WriteProject("src/Outer/Outer.csproj");
        WriteProject("src/Outer/Inner/Inner.csproj");
        WriteSource("src/Outer/Outer.cs", "namespace Outer;\npublic sealed class A { }\n");
        WriteSource("src/Outer/Inner/Inner.cs", "namespace Inner;\npublic sealed class B { }\n");

        var project = Assert.Single(RepositoryHierarchyBuilder.BuildDotNet(root));
        var modules = project.Children.ToDictionary(module => module.Name, StringComparer.Ordinal);

        Assert.Equal(["src/Outer/Inner/Inner.cs"], Files(modules["Inner"]).Select(file => file.Path).ToArray());
        Assert.Equal(["src/Outer/Outer.cs"], Files(modules["Outer"]).Select(file => file.Path).ToArray());
    }

    [Fact]
    public void BuildOutputIsNeverACompileItem()
    {
        CreateSolution();
        WriteSource("src/Demo/Kept.cs", "namespace Demo;\npublic sealed class Kept { }\n");
        WriteSource("src/Demo/bin/Debug/Output.cs", "namespace Demo;\npublic sealed class Output { }\n");
        WriteSource("src/Demo/obj/Debug/Generated.cs", "namespace Demo;\npublic sealed class Generated { }\n");

        var module = SingleModule();

        Assert.Equal(["src/Demo/Kept.cs"], Files(module).Select(file => file.Path).ToArray());
        Assert.Empty(module.Exclusions);
    }

    [Fact]
    public void CompileRemoveDropsMatchingSources()
    {
        CreateSolution();
        WriteProject("src/Demo/Demo.csproj", """
              <ItemGroup>
                <Compile Remove="Legacy/**/*.cs;Obsolete.cs" />
              </ItemGroup>
            """);
        WriteSource("src/Demo/Kept.cs", "namespace Demo;\npublic sealed class Kept { }\n");
        WriteSource("src/Demo/Obsolete.cs", "namespace Demo;\npublic sealed class Obsolete { }\n");
        WriteSource("src/Demo/Legacy/deep/Old.cs", "namespace Demo;\npublic sealed class Old { }\n");

        var module = SingleModule();

        Assert.Equal(["src/Demo/Kept.cs"], Files(module).Select(file => file.Path).ToArray());
    }

    [Fact]
    public void DisabledDefaultItemsUseOnlyExplicitCompileIncludes()
    {
        CreateSolution();
        WriteProject("src/Demo/Demo.csproj", """
              <PropertyGroup>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Chosen/*.cs" />
              </ItemGroup>
            """);
        WriteSource("src/Demo/Chosen/Picked.cs", "namespace Demo;\npublic sealed class Picked { }\n");
        WriteSource("src/Demo/Chosen/deep/TooDeep.cs", "namespace Demo;\npublic sealed class TooDeep { }\n");
        WriteSource("src/Demo/Ignored.cs", "namespace Demo;\npublic sealed class Ignored { }\n");

        var module = SingleModule();

        Assert.Equal(["src/Demo/Chosen/Picked.cs"], Files(module).Select(file => file.Path).ToArray());
    }

    [Fact]
    public void LinkedCompileItemBelongsToTheIncludingModule()
    {
        CreateSolution();
        WriteProject("src/Demo/Demo.csproj", """
              <ItemGroup>
                <Compile Include="..\..\Shared\Linked.cs" />
              </ItemGroup>
            """);
        WriteSource("src/Demo/Own.cs", "namespace Demo;\npublic sealed class Own { }\n");
        WriteSource("Shared/Linked.cs", "namespace Shared;\npublic sealed class Linked { }\n");

        var module = SingleModule();

        Assert.Equal(["Shared/Linked.cs", "src/Demo/Own.cs"], Files(module).Select(file => file.Path).ToArray());
    }

    [Fact]
    public void ClassicSolutionProjectLinesAreParsedStructurally()
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "Demo"));
        File.WriteAllText(Path.Combine(root, "Demo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-0301-11D3-BF4B-00C04F79EFBC}") = "Demo", "src\Demo\Demo.csproj", "{0A1B2C3D-0000-0000-0000-000000000001}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Solution Items", "Solution Items", "{0A1B2C3D-0000-0000-0000-000000000002}"
            EndProject
            Project("{6EC3EE1D-3C4E-46DD-8F32-0CC8E7565705}") = "Legacy", "src\Legacy\Legacy.fsproj", "{0A1B2C3D-0000-0000-0000-000000000003}"
            EndProject
            Global
            EndGlobal
            """);
        WriteProject("src/Demo/Demo.csproj");
        WriteSource("src/Demo/Program.cs", "namespace Demo;\npublic sealed class Program { }\n");

        var project = Assert.Single(RepositoryHierarchyBuilder.BuildDotNet(root));
        var module = Assert.Single(project.Children);

        Assert.Equal("Demo", project.Name);
        Assert.Equal("src/Demo/Demo.csproj", module.Path);
        Assert.Equal(["src/Demo/Program.cs"], Files(module).Select(file => file.Path).ToArray());
    }

    /// <summary>
    /// Units stored by the very first review runs of 2026-07-11 whose IDs no reachable
    /// solution/project/file tuple reproduces — not even in the repository trees of that week. They
    /// were orphaned long before the derivation was sharpened and are listed so that a genuinely
    /// new orphan cannot hide behind them.
    /// </summary>
    private static readonly string[] KnownOrphanedUnits =
    [
        // backend/AgentOrchestrator.CodeQuality/StalenessState.cs, code, reviewed 2026-07-11T19:18:11Z.
        "qs-v1/dotnet/file/81d3729e439083d881cfae72aeb40974a8982a9f2bac908f5a03e66657fb7f6d",
    ];

    /// <summary>
    /// Unmoved published units must retain their IDs. The explicit backend/frontend layout
    /// refactoring moves source paths, which deliberately changes path-derived IDs. For those
    /// units, reconstruct both published backend layouts from current source in isolated fixtures and
    /// prove the original ID still derives there, while the new target resolves in this checkout.
    /// Historical review documents themselves are never rewritten to claim a fresh review.
    /// </summary>
    [Fact]
    public void PublishedReviewSidecarUnitIdsStillResolve()
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        var nodes = Flatten(RepositoryHierarchyBuilder.Build(repositoryRoot)).ToArray();
        var identifiers = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var sidecars = EnumerateReviewSidecars(repositoryRoot)
            .Order(StringComparer.Ordinal)
            .Select(path => (Sidecar: path, Unit: ReadPublishedUnit(path)))
            .Where(entry => entry.Unit is not null)
            .Select(entry => (entry.Sidecar, Unit: entry.Unit!))
            .ToArray();
        var angularMoves = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "RepositoryLayout", "angular-path-moves.json")))!;

        Assert.NotEmpty(sidecars);
        using var publishedLayout = TemporaryDirectory.Create("quality-studio-published-layout");
        WritePublishedLayout(repositoryRoot, publishedLayout.Path, sidecars.Select(entry => entry.Unit), angularMoves);
        using var intermediateLayout = TemporaryDirectory.Create("quality-studio-intermediate-layout");
        WritePublishedLayout(repositoryRoot, intermediateLayout.Path, sidecars.Select(entry => entry.Unit), angularMoves,
            intermediateBackendLayout: true);
        var publishedIdentifiers = Flatten(RepositoryHierarchyBuilder.Build(publishedLayout.Path))
            .Concat(Flatten(RepositoryHierarchyBuilder.Build(intermediateLayout.Path)))
            .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var movedCount = 0;
        var unmovedCount = 0;
        foreach (var (sidecar, unit) in sidecars)
        {
            if (KnownOrphanedUnits.Contains(unit.Id, StringComparer.Ordinal)) continue;
            var currentPath = CurrentSourcePath(unit.Path, angularMoves);
            if (currentPath == unit.Path)
            {
                unmovedCount++;
                Assert.True(identifiers.Contains(unit.Id), $"Published unit no longer resolves: {sidecar} -> {unit.Id}");
                continue;
            }

            movedCount++;
            Assert.Contains(nodes, node => node.Path == currentPath && node.Level == unit.Level);
            Assert.True(publishedIdentifiers.Contains(unit.Id),
                $"Moved unit no longer derives in its published layout: {sidecar} -> {unit.Id}");
        }

        Assert.True(movedCount > 0, "The explicit layout migration must exercise published moved units.");
        Assert.True(unmovedCount > 0, "Unchanged paths must retain strict published-ID coverage.");
    }

    private sealed record PublishedUnit(string Id, string Path, ReviewLevel Level);

    private static PublishedUnit? ReadPublishedUnit(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("unit", out var unit) ||
            !unit.TryGetProperty("id", out var id)) return null;
        return new PublishedUnit(id.GetString()!, unit.GetProperty("path").GetString()!,
            Enum.Parse<ReviewLevel>(unit.GetProperty("level").GetString()!, ignoreCase: true));
    }

    private static string CurrentSourcePath(string publishedPath, IReadOnlyDictionary<string, string> angularMoves)
    {
        if (angularMoves.TryGetValue(publishedPath, out var currentPath)) return currentPath;
        if (publishedPath.StartsWith("backend/src/", StringComparison.Ordinal))
            return "backend/" + publishedPath["backend/src/".Length..];
        return publishedPath.StartsWith("src/AgentOrchestrator.CodeQuality/", StringComparison.Ordinal) ||
               publishedPath.StartsWith("src/QualityStudio.Api/", StringComparison.Ordinal) ||
               publishedPath.StartsWith("src/quality-cli/", StringComparison.Ordinal)
            ? "backend/" + publishedPath["src/".Length..]
            : publishedPath;
    }

    private static void WritePublishedLayout(
        string repositoryRoot, string fixtureRoot, IEnumerable<PublishedUnit> units,
        IReadOnlyDictionary<string, string> angularMoves, bool intermediateBackendLayout = false)
    {
        var sourcePrefix = intermediateBackendLayout ? "backend/src" : "src";
        var solution = File.ReadAllText(Path.Combine(repositoryRoot, "QualityStudio.slnx"));
        foreach (var project in new[] { "AgentOrchestrator.CodeQuality", "QualityStudio.Api", "quality-cli" })
            solution = solution.Replace($"backend/{project}/", $"{sourcePrefix}/{project}/", StringComparison.Ordinal);
        if (!intermediateBackendLayout)
            solution = solution.Replace("backend/tests/", "tests/", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(fixtureRoot, "QualityStudio.slnx"), solution);
        CopySource("frontend/angular.json", "frontend/angular.json");
        foreach (var project in new[] { "AgentOrchestrator.CodeQuality", "QualityStudio.Api", "quality-cli" })
            CopySource($"backend/{project}/{project}.csproj", $"{sourcePrefix}/{project}/{project}.csproj");
        foreach (var unit in units.DistinctBy(unit => unit.Path))
            CopySource(CurrentSourcePath(unit.Path, angularMoves), unit.Path);

        void CopySource(string currentPath, string publishedPath)
        {
            var target = Path.GetFullPath(Path.Combine(fixtureRoot, publishedPath));
            Assert.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(repositoryRoot, currentPath), target, overwrite: true);
        }
    }

    private static IEnumerable<string> EnumerateReviewSidecars(string repositoryRoot)
    {
        var pending = new Stack<string>();
        pending.Push(repositoryRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.Replace('\\', '/').EndsWith("/.quality/reviews", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var file in Directory.EnumerateFiles(current, "*.review-meta.*.json", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (Path.GetFileName(directory) is not ("node_modules" or "bin" or "obj" or ".git" or "dist"))
                {
                    pending.Push(directory);
                }
            }
        }
    }

    private HierarchyNode SingleModule() =>
        Assert.Single(Assert.Single(RepositoryHierarchyBuilder.BuildDotNet(root)).Children);

    private void CreateSolution()
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "Demo"));
        File.WriteAllText(Path.Combine(root, "Demo.slnx"),
            "<Solution><Project Path=\"src/Demo/Demo.csproj\" /></Solution>");
        WriteProject("src/Demo/Demo.csproj");
    }

    private void WriteProject(string relativePath, string body = "")
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\">\n{body}\n</Project>\n");
    }

    private void WriteSource(string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string[] Namespaces(HierarchyNode module) =>
        module.Children.Select(node => node.Name).ToArray();

    private static IEnumerable<HierarchyNode> Files(HierarchyNode module) =>
        module.Children.SelectMany(ns => ns.Children)
            .Where(node => node.Level == ReviewLevel.File)
            .DistinctBy(node => node.Id, StringComparer.Ordinal)
            .OrderBy(node => node.Path, StringComparer.Ordinal);

    private static IEnumerable<HierarchyNode> Functions(HierarchyNode module) =>
        Files(module).SelectMany(file => file.Children);

    private static IEnumerable<HierarchyNode> Flatten(IEnumerable<HierarchyNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    public void Dispose()
    {
        TemporaryDirectory.Delete(root);
        GC.SuppressFinalize(this);
    }
}
