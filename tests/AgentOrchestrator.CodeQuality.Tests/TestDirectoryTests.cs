namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class TestDirectoryTests
{
    [Fact]
    public void Delete_removes_a_directory_with_read_only_files()
    {
        var root = Directory.CreateTempSubdirectory("quality-test-cleanup-").FullName;
        var file = Path.Combine(root, "object");
        File.WriteAllText(file, "fixture");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        TestDirectory.Delete(root);

        Assert.False(Directory.Exists(root));
    }
}
