namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ProcessBoundaryTaxonomyTests
{
    [Fact]
    public void Test_process_creation_is_centralized_and_machine_bound_tests_remain_explicit()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var testRoot = Path.Combine(root, "tests");
        var sources = Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        var creationTokens = new[] { "new " + "Process", "Process" + ".Start" };
        var undeclared = sources.Where(path =>
                !path.EndsWith(Path.Combine("TestSupport", "TestToolProcess.cs"), StringComparison.Ordinal) &&
                creationTokens.Any(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToArray();

        Assert.Empty(undeclared);

        var machineTraits = sources.Sum(path => Count(
            File.ReadAllText(path), "[Trait(\"Category\", \"MachineBound\")]"));
        Assert.Equal(2, machineTraits);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }
}
