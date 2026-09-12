using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewMetaIndexTests
{
    [Fact]
    public void Read_returns_the_sidecars_of_a_unit()
    {
        using var fixture = TemporaryDirectory.Create("quality-studio-meta-index");
        WriteSidecar(fixture, "Sample.cs", ReviewKind.Code);

        using var index = new ReviewMetaIndex();

        var documents = index.Read(fixture.Path, "Sample.cs");
        Assert.Equal("code", Assert.Single(documents).GetProperty("kind").GetString());
    }

    [Fact]
    public void Find_returns_the_sidecar_path_of_a_kind()
    {
        using var fixture = TemporaryDirectory.Create("quality-studio-meta-index");
        var expected = WriteSidecar(fixture, "Sample.cs", ReviewKind.Security);

        using var index = new ReviewMetaIndex();

        Assert.Equal(expected, index.Find(fixture.Path, "Sample.cs", "security"));
    }

    [Fact]
    public void Release_drops_the_repository_and_a_later_read_rebuilds_it_from_disk()
    {
        using var fixture = TemporaryDirectory.Create("quality-studio-meta-index");
        WriteSidecar(fixture, "Sample.cs", ReviewKind.Code);
        using var index = new ReviewMetaIndex();
        Assert.Single(index.Read(fixture.Path, "Sample.cs"));

        index.Release(fixture.Path);
        WriteSidecar(fixture, "Second.cs", ReviewKind.Code);

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
        var path = WriteSidecar(fixture, "Sample.cs", ReviewKind.Code);
        using var index = new ReviewMetaIndex();
        Assert.Equal(path, index.Find(fixture.Path, "Sample.cs", "code"));

        var payload = File.ReadAllText(path);
        for (var revision = 0; revision < 8; revision++)
        {
            // The product's own write path, so this also covers indexing a sidecar while it
            // is being replaced.
            AtomicFile.WriteAllText(path, payload);

            Assert.Equal(path, index.Find(fixture.Path, "Sample.cs", "code"));
        }
    }

    [Fact]
    public void Release_and_Dispose_are_idempotent()
    {
        using var fixture = TemporaryDirectory.Create("quality-studio-meta-index");
        WriteSidecar(fixture, "Sample.cs", ReviewKind.Code);
        var index = new ReviewMetaIndex();
        Assert.Single(index.Read(fixture.Path, "Sample.cs"));

        index.Release(fixture.Path);
        index.Release(fixture.Path);
        index.Dispose();
        index.Dispose();
    }

    private static string WriteSidecar(TemporaryDirectory fixture, string unitPath, ReviewKind kind)
    {
        var path = ReviewMetaPath.ForFile(fixture.Path, unitPath, kind.ToString().ToLowerInvariant());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var grade = new ReviewGrade(80, GradeBand.B, "Fixture grade.");
        File.WriteAllText(path, ReviewMetaJson.Serialize(new ReviewMetaDocument
        {
            Unit = new ReviewUnit("qs-v1/generic/file/" + new string('a', 64), ReviewAdapter.Generic,
                ReviewLevel.File, unitPath, unitPath),
            ReviewedAt = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero),
            Kind = kind,
            Reviewer = new ReviewerIdentity("test", "test"),
            ReviewedHash = ManifestHash.Subject(new string('b', 64)),
            SubjectInputs = [new SubjectInputHash(unitPath, "file", "sha256:" + new string('c', 64))],
            ReviewInputs = new ReviewInputs(
                ManifestHash.ReviewInput(new string('e', 64)), true, [], [],
                new PromptReference($"file-{kind.ToString().ToLowerInvariant()}-review", "1.0.0",
                    "sha256:" + new string('f', 64))),
            Grade = grade,
            Summary = "Fixture review.",
            Aspects = [new ReviewAspect("correctness", "Correctness", grade)],
            Findings = [],
        }));
        return path;
    }
}
