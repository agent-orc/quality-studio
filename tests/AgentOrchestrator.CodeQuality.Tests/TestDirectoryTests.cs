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

    [Fact]
    public void Git_fixture_pins_identity_timestamps_and_line_endings()
    {
        using var repository = GitTestRepository.Create("quality-test-git-fixture-");
        repository.Write("sample.txt", "one\n").CommitAll("first");

        var log = repository.Git("log", "-1", "--pretty=format:%an|%ae|%aI");

        Assert.Equal("Quality Studio Test|tests@quality-studio.invalid|2026-01-02T03:04:05+00:00", log.Trim());
        Assert.Equal("false", repository.Git("config", "core.autocrlf").Trim());
        Assert.Equal(Directory.GetParent(repository.Root)!.FullName, repository.AllowedRoot);
    }

    [Fact]
    public void Git_fixture_reports_the_failing_command_with_captured_output()
    {
        using var repository = GitTestRepository.Create("quality-test-git-diag-");

        var failure = Assert.Throws<InvalidOperationException>(() => repository.Git("cat-file", "-p", "does-not-exist"));

        Assert.Contains("git cat-file -p does-not-exist failed with exit code", failure.Message, StringComparison.Ordinal);
        Assert.Contains(repository.Root, failure.Message, StringComparison.Ordinal);
        Assert.Contains("--- stderr ---", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Git_fixture_removes_its_working_directory_on_dispose()
    {
        var repository = GitTestRepository.Create("quality-test-git-cleanup-");
        var root = repository.Root;
        repository.Write("sample.txt", "one\n").CommitAll("first");

        repository.Dispose();

        Assert.False(Directory.Exists(root));
    }
}
