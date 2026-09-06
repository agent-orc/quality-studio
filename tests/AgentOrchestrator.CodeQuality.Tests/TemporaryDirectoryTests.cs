using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class TemporaryDirectoryTests
{
    [Fact]
    public void Delete_removes_a_directory_with_read_only_files()
    {
        // Git marks loose objects read-only on Windows, which is what made a plain
        // recursive delete fail with UnauthorizedAccessException in git-backed fixtures.
        var fixture = TemporaryDirectory.Create("quality-test-cleanup");
        var file = Path.Combine(fixture.Path, "object");
        File.WriteAllText(file, "fixture");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        fixture.Dispose();

        Assert.False(Directory.Exists(fixture.Path));
    }

    [Fact]
    public void Delete_gives_up_quietly_when_a_handle_is_still_open()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Only Windows refuses to remove a directory whose file handle is still open.");

        using var fixture = TemporaryDirectory.Create("quality-test-locked");
        var file = Path.Combine(fixture.Path, "held");
        using var handle = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        // Abandoning a temporary fixture is preferable to turning a passing assertion red.
        TemporaryDirectory.Delete(fixture.Path);

        Assert.True(Directory.Exists(fixture.Path));
    }

    [Fact]
    public void Create_and_Combine_produce_paths_below_the_fixture_root()
    {
        using var fixture = TemporaryDirectory.Create("quality-test-layout");

        var nested = fixture.CreateSubdirectory("src", "nested");

        Assert.True(Directory.Exists(nested));
        Assert.StartsWith(fixture.Path, fixture.Combine("src", "file.cs"), StringComparison.Ordinal);
    }
}
