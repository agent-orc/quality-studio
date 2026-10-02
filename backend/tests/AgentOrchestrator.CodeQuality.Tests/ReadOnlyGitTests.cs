using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", "ToolBound")]
public sealed class ReadOnlyGitTests
{
    [Theory]
    [InlineData("hierarchy-cache")]
    [InlineData("hierarchy-adapter")]
    [InlineData("staleness")]
    public async Task Repository_reads_do_not_execute_configured_fsmonitor_or_rewrite_the_index(string surface)
    {
        var root = Directory.CreateTempSubdirectory("quality-readonly-git-").FullName;
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await GitTestRepository.InitializeAsync(root, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "main.py"), "print(1)\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored/\n", cancellationToken);
            Directory.CreateDirectory(Path.Combine(root, "ignored"));
            await File.WriteAllTextAsync(Path.Combine(root, "ignored", "hidden.py"), "ignored\n", cancellationToken);
            await GitTestRepository.RunAsync(root, cancellationToken, "add", "main.py", ".gitignore");
            var hook = Path.Combine(root, ".git", "fsmonitor-probe.sh");
            var marker = Path.Combine(root, ".git", "fsmonitor-called");
            await File.WriteAllTextAsync(hook,
                "#!/bin/sh\nprintf observed > .git/fsmonitor-called\nexit 1\n", cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await GitTestRepository.RunAsync(root, cancellationToken, "config", "core.fsmonitor", hook.Replace('\\', '/'));

            // Establish that this real Git/OS fixture actually executes the harmless hook.
            await GitTestRepository.RunAsync(root, cancellationToken, "status", "--porcelain=v1");
            Assert.True(File.Exists(marker), "The control invocation must execute the fixture-local hook.");
            File.Delete(marker);
            var indexPath = Path.Combine(root, ".git", "index");
            var originalIndex = await File.ReadAllBytesAsync(indexPath, cancellationToken);

            switch (surface)
            {
                case "hierarchy-cache":
                    var cache = new RepositoryHierarchyCache(TimeSpan.Zero);
                    var original = cache.GetMeasured(root).Snapshot;
                    Assert.Equal(RepositoryGitState.OkStatus, original.GitStateStatus);
                    Assert.Contains(Flatten(original.Roots), node => node.Path == "main.py");
                    await File.WriteAllTextAsync(Path.Combine(root, "main.py"), "print(2)\n", cancellationToken);
                    var changed = cache.GetMeasured(root).Snapshot;
                    Assert.NotEqual(original.ETag, changed.ETag);
                    break;
                case "hierarchy-adapter":
                    var nodes = Flatten(RepositoryHierarchyBuilder.Build(root)).ToArray();
                    Assert.Contains(nodes, node => node.Path == "main.py");
                    Assert.DoesNotContain(nodes, node => node.Path == "ignored/hidden.py");
                    break;
                case "staleness":
                    var report = await new StalenessEvaluator().ScanAsync(root,
                        new StalenessEvaluatorOptions { IncludeGlobs = ["**/*.py"] }, cancellationToken);
                    Assert.Equal("main.py", Assert.Single(report.Files).RelativePath);
                    break;
            }

            Assert.False(File.Exists(marker), "A read-only product call executed repository-configured fsmonitor.");
            Assert.Equal(originalIndex, await File.ReadAllBytesAsync(indexPath, cancellationToken));
        }
        finally
        {
            TemporaryDirectory.Delete(QualityDataRoot.For(root));
            TemporaryDirectory.Delete(root);
        }
    }

    private static IEnumerable<HierarchyNode> Flatten(IEnumerable<HierarchyNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children)) yield return child;
        }
    }
}
