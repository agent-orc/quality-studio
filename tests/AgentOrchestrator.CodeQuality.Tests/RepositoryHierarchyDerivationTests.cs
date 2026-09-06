using System.Text.Json;
using AgentOrchestrator.CodeQuality;

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

    [Fact]
    public async Task RuntimeReviewSidecarUnitIdResolvesInTheCurrentHierarchy()
    {
        CreateSolution();
        WriteSource("src/Demo/Greeter.cs", "namespace Demo;\npublic sealed class Greeter { }\n");
        var result = await new ReviewRunner(new HierarchyReviewAgent()).ReviewAsync(
            new ReviewRequest("src/Demo/Greeter.cs", RepositoryRoot: root),
            TestContext.Current.CancellationToken);
        var identifiers = Flatten(RepositoryHierarchyBuilder.Build(root))
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            result.MetaPath, TestContext.Current.CancellationToken));

        Assert.Contains(document.RootElement.GetProperty("unit").GetProperty("id").GetString()!, identifiers);
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

    private sealed class HierarchyReviewAgent : IReviewAgent
    {
        public string AgentName => "hierarchy-test";
        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default) => Task.FromResult(new ReviewAgentResult(
            "hierarchy-run", $"```json\n{ReviewResponseParserTests.ValidResponse}\n```",
            new TokenUsage(1, 1, 0, 0, 1), Model));
    }

    public void Dispose()
    {
        TestDirectory.Delete(root);
        GC.SuppressFinalize(this);
    }
}
