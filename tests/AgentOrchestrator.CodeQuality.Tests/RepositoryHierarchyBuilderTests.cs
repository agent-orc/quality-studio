using AgentOrchestrator.CodeQuality;
using System.Diagnostics;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RepositoryHierarchyBuilderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"quality-studio-{Guid.NewGuid():N}");

    [Fact]
    public void DerivesAllFiveLevelsFromDotNetSources()
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "Demo"));
        File.WriteAllText(Path.Combine(root, "Demo.slnx"),
            "<Solution><Project Path=\"src/Demo/Demo.csproj\" /></Solution>");
        File.WriteAllText(Path.Combine(root, "src", "Demo", "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root, "src", "Demo", "Greeter.cs"),
            "namespace Demo.Greetings; public sealed class Greeter\n{\n    public string SayHello() => \"hello\";\n}");

        var project = Assert.Single(RepositoryHierarchyBuilder.BuildDotNet(root));
        var module = Assert.Single(project.Children);
        var ns = Assert.Single(module.Children);
        var file = Assert.Single(ns.Children);
        var function = Assert.Single(file.Children);

        Assert.Equal(ReviewLevel.Project, project.Level);
        Assert.Equal("Demo.Greetings", ns.Name);
        Assert.NotEqual(module.Path, ns.Path);
        Assert.Contains("/.namespaces/Demo.Greetings", ns.Path, StringComparison.Ordinal);
        Assert.Equal("Greeter.cs", file.Name);
        Assert.Equal(new FileInfo(Path.Combine(root, "src", "Demo", "Greeter.cs")).Length, file.SizeBytes);
        Assert.Equal(4, file.LineCount);
        Assert.Equal("SayHello", function.Name);
        Assert.StartsWith("qs-v1/dotnet/function/", function.Id, StringComparison.Ordinal);

        var selected = Assert.Single(RepositoryHierarchyBuilder.Build(root));
        Assert.Equal(project.Id, selected.Id);
        Assert.Equal(module.Id, Assert.Single(selected.Children).Id);
    }

    [Fact]
    public void DerivesAngularWorkspaceWithStandaloneComponents()
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "app", "orders"));
        File.WriteAllText(Path.Combine(root, "angular.json"),
            "{\"projects\":{\"demo\":{\"root\":\"\",\"sourceRoot\":\"src\"}}}");
        File.WriteAllText(Path.Combine(root, "src", "app", "app.component.ts"),
            "@Component({standalone: true}) export class AppComponent {}");
        File.WriteAllText(Path.Combine(root, "src", "app", "orders", "order-card.component.ts"),
            "@Component({standalone: true}) export class OrderCardComponent {}");
        File.WriteAllText(Path.Combine(root, "src", "app", "orders", "order-card.component.html"), "<p>order</p>");
        File.WriteAllText(Path.Combine(root, "src", "app", "orders", "order-card.component.spec.ts"), "ignored");

        var project = Assert.Single(RepositoryHierarchyBuilder.Build(root));
        Assert.StartsWith("qs-v1/angular/project/", project.Id, StringComparison.Ordinal);
        var module = Assert.Single(project.Children);
        Assert.Equal(".", module.Path);
        var files = Flatten([project]).Where(node => node.Level == ReviewLevel.File).ToArray();

        Assert.Equal(3, files.Length);
        Assert.Contains(files, file => file.Path == "src/app/orders/order-card.component.ts");
        Assert.DoesNotContain(files, file => file.Path.EndsWith(".spec.ts", StringComparison.Ordinal));
        Assert.All(files, file => Assert.StartsWith("qs-v1/angular/file/", file.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void MixedRepositoryEmitsDotNetAndAngularProjects()
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "Api"));
        Directory.CreateDirectory(Path.Combine(root, "frontend", "src", "app"));
        File.WriteAllText(Path.Combine(root, "Quality.slnx"),
            "<Solution><Project Path=\"src/Api/Api.csproj\" /></Solution>");
        File.WriteAllText(Path.Combine(root, "src", "Api", "Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root, "src", "Api", "Program.cs"), "namespace Api; public class Program;");
        File.WriteAllText(Path.Combine(root, "frontend", "angular.json"),
            "{\"projects\":{\"frontend\":{\"root\":\"\",\"sourceRoot\":\"src\"}}}");
        File.WriteAllText(Path.Combine(root, "frontend", "src", "app", "app.component.ts"),
            "@Component({standalone: true}) export class AppComponent {}");

        var projects = RepositoryHierarchyBuilder.Build(root);

        Assert.Equal(2, projects.Count);
        Assert.Contains(Flatten(projects), node => node.Path == "src/Api/Program.cs");
        Assert.Contains(Flatten(projects), node => node.Path == "frontend/src/app/app.component.ts");
    }

    [Fact]
    public void GenericFallbackHonoursGitignoreAndBuildOutputs()
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "tools"));
        Directory.CreateDirectory(Path.Combine(root, "dist"));
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n!keep.log\n");
        File.WriteAllText(Path.Combine(root, "src", "tools", "main.py"), "print('hello')\n");
        File.WriteAllText(Path.Combine(root, "debug.log"), "ignored\n");
        File.WriteAllText(Path.Combine(root, "keep.log"), "included\n");
        File.WriteAllText(Path.Combine(root, "dist", "bundle.js"), "ignored\n");
        File.WriteAllText(Path.Combine(root, "obj", "generated.txt"), "ignored\n");

        var project = Assert.Single(RepositoryHierarchyBuilder.Build(root));
        var files = Flatten([project]).Where(node => node.Level == ReviewLevel.File).ToArray();

        Assert.StartsWith("qs-v1/generic/project/", project.Id, StringComparison.Ordinal);
        Assert.Contains(files, file => file.Path == "src/tools/main.py");
        Assert.Contains(files, file => file.Path == "keep.log");
        Assert.DoesNotContain(files, file => file.Path is "debug.log" or "dist/bundle.js" or "obj/generated.txt");
        Assert.Contains(Flatten([project]), node => node.Level == ReviewLevel.Namespace && node.Path == "src/tools");
    }

    [Fact]
    public void PackageWorkspacesAndTsconfigReferencesSelectAngularAdapter()
    {
        Directory.CreateDirectory(Path.Combine(root, "packages", "web", "src"));
        File.WriteAllText(Path.Combine(root, "package.json"), "{\"workspaces\":[\"packages/*\"]}");
        File.WriteAllText(Path.Combine(root, "packages", "web", "package.json"), "{\"name\":\"web\"}");
        File.WriteAllText(Path.Combine(root, "packages", "web", "src", "main.ts"), "export const main = true;");

        var workspaceProject = Assert.Single(RepositoryHierarchyBuilder.Build(root));
        Assert.StartsWith("qs-v1/angular/project/", workspaceProject.Id, StringComparison.Ordinal);
        Assert.Contains(Flatten([workspaceProject]), node => node.Path == "packages/web/src/main.ts");

        Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(Path.Combine(root, "apps", "portal", "src"));
        File.WriteAllText(Path.Combine(root, "tsconfig.json"), "{\"references\":[{\"path\":\"./apps/portal\"}]}");
        File.WriteAllText(Path.Combine(root, "apps", "portal", "tsconfig.json"), "{\"include\":[\"src/**/*.ts\"]}");
        File.WriteAllText(Path.Combine(root, "apps", "portal", "src", "main.ts"), "export const main = true;");

        var referencedProject = Assert.Single(RepositoryHierarchyBuilder.Build(root));
        Assert.StartsWith("qs-v1/angular/project/", referencedProject.Id, StringComparison.Ordinal);
        Assert.Contains(Flatten([referencedProject]), node => node.Path == "apps/portal/src/main.ts");
    }

    [Fact]
    public void CacheReusesGitStateAndInvalidatesOnWorktreeContent()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.py"), "print(1)\n");
        RunGit("init", "--quiet");
        var cache = new RepositoryHierarchyCache();

        var first = cache.Get(root);
        var warm = cache.Get(root);
        File.WriteAllText(Path.Combine(root, "main.py"), "print(2)\n");
        var changed = cache.Get(root);

        Assert.Same(first, warm);
        Assert.NotSame(first, changed);
        Assert.NotEqual(first.ETag, changed.ETag);
    }

    [Fact]
    public void GenericFiveThousandFileScanStaysWithinBudget()
    {
        var source = Path.Combine(root, "src");
        Directory.CreateDirectory(source);
        for (var index = 0; index < 5_000; index++)
        {
            File.WriteAllText(Path.Combine(source, $"file-{index:D4}.txt"), $"line {index}\n");
        }

        var stopwatch = Stopwatch.StartNew();
        var project = Assert.Single(RepositoryHierarchyBuilder.Build(root));
        stopwatch.Stop();
        Console.WriteLine($"5,000-file generic hierarchy: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");

        Assert.Equal(5_000, Flatten([project]).Count(node => node.Level == ReviewLevel.File));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"5,000-file hierarchy took {stopwatch.Elapsed}.");
    }

    private void RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

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
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
