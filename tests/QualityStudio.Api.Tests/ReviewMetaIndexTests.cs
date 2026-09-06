using System.Text.Json;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewMetaIndexTests
{
    [Fact]
    public void Read_returns_the_sidecars_of_a_unit()
    {
        using var fixture = TemporaryDirectory.Create("quality-studio-meta-index");
        WriteSidecar(fixture, "Sample.cs", "code");

        using var index = new ReviewMetaIndex();

        var documents = index.Read(fixture.Path, "Sample.cs");
        Assert.Equal("code", Assert.Single(documents).GetProperty("kind").GetString());
    }

    [Fact]
    public void Find_returns_the_sidecar_path_of_a_kind()
    {
        using var fixture = TemporaryDirectory.Create("quality-studio-meta-index");
        var expected = WriteSidecar(fixture, "Sample.cs", "security");

        using var index = new ReviewMetaIndex();

        Assert.Equal(expected, index.Find(fixture.Path, "Sample.cs", "security"));
    }

    [Fact]
    public void Forget_releases_the_repository_and_a_later_read_rebuilds_it_from_disk()
    {
        using var fixture = TemporaryDirectory.Create("quality-studio-meta-index");
        WriteSidecar(fixture, "Sample.cs", "code");
        using var index = new ReviewMetaIndex();
        Assert.Single(index.Read(fixture.Path, "Sample.cs"));

        index.Forget(fixture.Path);
        WriteSidecar(fixture, "Second.cs", "code");

        // No watcher is left to observe the new sidecar, so the rebuild has to come from
        // the rescan the next read triggers.
        Assert.Single(index.Read(fixture.Path, "Second.cs"));
    }

    [Fact]
    public void Replacing_a_sidecar_atomically_never_leaves_the_unit_without_metadata()
    {
        // Sidecars are written with File.Move(overwrite: true). Windows reports that as a delete
        // of the destination followed by a rename onto it, and dropping the document on the
        // delete used to make a read in that window fail for a file that never left the disk.
        using var fixture = TemporaryDirectory.Create("quality-studio-meta-index");
        var path = WriteSidecar(fixture, "Sample.cs", "code");
        using var index = new ReviewMetaIndex();
        Assert.Equal(path, index.Find(fixture.Path, "Sample.cs", "code"));

        var payload = File.ReadAllText(path);
        for (var revision = 0; revision < 8; revision++)
        {
            var temporary = path + ".tmp-" + revision;
            File.WriteAllText(temporary, payload);
            ReplaceAtomically(temporary, path);

            Assert.Equal(path, index.Find(fixture.Path, "Sample.cs", "code"));
        }
    }

    /// <summary>
    /// File.Move(overwrite: true) is what the product uses, but on Windows a virus scanner can
    /// hold a moment's handle on a freshly written file and make the replace fail. That is host
    /// noise, not the behaviour under test, so retry briefly before giving up.
    /// </summary>
    private static void ReplaceAtomically(string temporary, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                attempt < 20 && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }
    }

    [Fact]
    public void Forget_and_Dispose_are_idempotent()
    {
        using var fixture = TemporaryDirectory.Create("quality-studio-meta-index");
        WriteSidecar(fixture, "Sample.cs", "code");
        var index = new ReviewMetaIndex();
        Assert.Single(index.Read(fixture.Path, "Sample.cs"));

        index.Forget(fixture.Path);
        index.Forget(fixture.Path);
        index.Dispose();
        index.Dispose();
    }

    private static string WriteSidecar(TemporaryDirectory fixture, string unitPath, string kind)
    {
        var directory = fixture.CreateSubdirectory(".quality", "reviews", "files");
        var path = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(unitPath)}.review-meta.{kind}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            unit = new { path = unitPath },
            kind,
            summary = "Fixture sidecar.",
        }));
        return path;
    }
}
